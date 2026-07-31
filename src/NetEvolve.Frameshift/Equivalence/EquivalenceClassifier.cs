namespace NetEvolve.Frameshift.Equivalence;

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using NetEvolve.Frameshift.Mutations;

/// <summary>
/// Decides whether a candidate <see cref="Mutation" /> is trivial, meaning that no test could ever
/// observe the difference between the mutant and the original code.
/// </summary>
/// <remarks>
/// <para>
/// The classification is deliberately one-sided. A wrong <see cref="EquivalenceVerdict.NotTrivial" />
/// verdict only costs a warning that a reviewer can dismiss, while a wrong trivial verdict silently
/// hides a real testing gap, which is exactly what Frameshift exists to find. Every check therefore
/// only reports triviality when it can actually prove it and returns
/// <see cref="EquivalenceVerdict.NotTrivial" /> in every uncertain case, including unsupported
/// constant types, foreign syntax trees and every shape a check does not fully understand.
/// </para>
/// <para>
/// The checks run cheapest first, from pure syntax comparisons over local constant folding to
/// symbol and diagnostic based inspection, so that the common uninteresting mutant is rejected
/// without touching the semantic model.
/// </para>
/// </remarks>
internal static class EquivalenceClassifier
{
    private const string UnreachableCodeDiagnosticId = "CS0162";

    private const string NoOpReason = "the mutant is syntactically identical to the original code";
    private const string ConstantFoldingReason = "the mutated expression folds to the same constant";
    private const string UnreachableStatementReason = "the mutated statement is already unreachable";
    private const string ThrowOnlyBodyReason = "the containing member does nothing but throw";
    private const string DiscardedStatementReason = "the mutated value is never consumed by its statement";
    private const string DiscardAssignmentReason = "the mutated value is assigned to a discard";
    private const string AttributeArgumentReason = "the mutation only changes a compile-time attribute argument";
    private const string ConstantDeclarationReason = "the mutation only changes a compile-time constant";
    private const string DefaultParameterReason = "the mutation only changes a default parameter value";
    private const string CaseLabelReason = "the mutation only changes a compile-time case label";
    private const string WellKnownMemberReason = "the containing member is a well known infrastructure member";
    private const string CompilerGeneratedReason = "the containing member is compiler generated";
    private const string ExcludedMemberReason = "the containing member is excluded from coverage";
    private const string ObsoleteMemberReason = "the containing member is marked obsolete";

    /// <summary>
    /// Classifies <paramref name="mutation" /> as trivial or as a mutant whose survival would be
    /// meaningful.
    /// </summary>
    /// <param name="mutation">The candidate mutation to classify.</param>
    /// <param name="semanticModel">
    /// The semantic model of the syntax tree <see cref="Mutation.Original" /> belongs to. Checks that
    /// need semantic information are skipped when the node belongs to a different tree.
    /// </param>
    /// <param name="cancellationToken">A token observed between the individual checks.</param>
    /// <returns>
    /// A trivial <see cref="EquivalenceVerdict" /> with a precise reason, or
    /// <see cref="EquivalenceVerdict.NotTrivial" /> when triviality could not be proven.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="mutation" /> or <paramref name="semanticModel" /> is <see langword="null" />.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    public static EquivalenceVerdict Classify(
        Mutation mutation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (mutation is null)
        {
            throw new ArgumentNullException(nameof(mutation));
        }

        if (semanticModel is null)
        {
            throw new ArgumentNullException(nameof(semanticModel));
        }

        cancellationToken.ThrowIfCancellationRequested();

        return ClassifyNoOpRewrite(mutation)
            ?? ClassifyConstantFolding(mutation, semanticModel, cancellationToken)
            ?? ClassifyUnreachableCode(mutation, semanticModel, cancellationToken)
            ?? ClassifyDiscardedResult(mutation, semanticModel, cancellationToken)
            ?? ClassifyConstantOnlyContext(mutation)
            ?? ClassifyExcludedMember(mutation, semanticModel, cancellationToken)
            ?? EquivalenceVerdict.NotTrivial;
    }

    /// <summary>
    /// Determines whether the replacement is syntactically the same code as the original.
    /// </summary>
    /// <param name="mutation">The mutation to inspect.</param>
    /// <returns>A trivial verdict, or <see langword="null" /> if the check does not apply.</returns>
    private static EquivalenceVerdict? ClassifyNoOpRewrite(Mutation mutation) =>
        SyntaxFactory.AreEquivalent(mutation.Original, mutation.Replacement, topLevel: false)
            ? EquivalenceVerdict.Trivial(NoOpReason)
            : null;

    /// <summary>
    /// Determines whether the original and the mutated expression fold to the very same constant.
    /// </summary>
    /// <param name="mutation">The mutation to inspect.</param>
    /// <param name="semanticModel">The semantic model of the original tree.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>A trivial verdict, or <see langword="null" /> if the check does not apply.</returns>
    private static EquivalenceVerdict? ClassifyConstantFolding(
        Mutation mutation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (!CanQuery(mutation.Original, semanticModel))
        {
            return null;
        }

        var original = GetConstant(mutation.Original, semanticModel, cancellationToken);
        if (original is null || !TryEvaluateReplacement(mutation, semanticModel, cancellationToken, out var mutated))
        {
            return null;
        }

        if (mutated is null || !IsSameConstant(original, mutated))
        {
            return null;
        }

        return EquivalenceVerdict.Trivial(ConstantFoldingReason);
    }

    /// <summary>
    /// Determines whether two folded constants are indistinguishable at run time.
    /// </summary>
    /// <remarks>
    /// <see cref="object.Equals(object)" /> is not enough for floating point values, because positive
    /// and negative zero compare equal while they are observably different, for example through
    /// <c>1.0 / value</c>. Every not-a-number value on the other hand is treated as the same constant,
    /// because its sign bit does not survive any arithmetic the mutated code could perform on it.
    /// </remarks>
    /// <param name="original">The constant value of the original expression.</param>
    /// <param name="mutated">The folded value of the mutated expression.</param>
    /// <returns><see langword="true" /> if both constants are the same; otherwise <see langword="false" />.</returns>
    private static bool IsSameConstant(object original, object mutated) =>
        (original, mutated) switch
        {
            (double left, double right) => IsSameFloatingPoint(left, right),
            (float left, float right) => IsSameFloatingPoint(left, right),
            _ => original.Equals(mutated),
        };

    /// <summary>
    /// Compares two floating point constants by their bit pattern, which is what tells the two zeros
    /// apart. Every not-a-number value counts as the same constant.
    /// </summary>
    /// <param name="left">The left value.</param>
    /// <param name="right">The right value.</param>
    /// <returns><see langword="true" /> if both values are the same; otherwise <see langword="false" />.</returns>
    private static bool IsSameFloatingPoint(double left, double right) =>
        double.IsNaN(left)
            ? double.IsNaN(right)
            : BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right);

    /// <summary>
    /// Evaluates the constant value of <see cref="Mutation.Replacement" /> by folding the mutated
    /// operator over the constant values of the operands of <see cref="Mutation.Original" />, which is
    /// the only way to reach the replacement, because it is not part of any compiled tree.
    /// </summary>
    /// <param name="mutation">The mutation to evaluate.</param>
    /// <param name="semanticModel">The semantic model of the original tree.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <param name="value">The folded value, or <see langword="null" /> when folding was not possible.</param>
    /// <returns><see langword="true" /> if a value could be proven; otherwise <see langword="false" />.</returns>
    private static bool TryEvaluateReplacement(
        Mutation mutation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        out object? value
    )
    {
        value = null;

        switch (mutation.Original, mutation.Replacement)
        {
            case (BinaryExpressionSyntax original, BinaryExpressionSyntax replacement)
                when HasSameOperands(original, replacement):
                return TryFoldBinary(
                    replacement.Kind(),
                    GetConstant(original.Left, semanticModel, cancellationToken),
                    GetConstant(original.Right, semanticModel, cancellationToken),
                    out value
                );

            case (PrefixUnaryExpressionSyntax original, PrefixUnaryExpressionSyntax replacement)
                when SyntaxFactory.AreEquivalent(original.Operand, replacement.Operand, topLevel: false):
                return TryFoldUnary(
                    replacement.Kind(),
                    GetConstant(original.Operand, semanticModel, cancellationToken),
                    out value
                );

            case (LiteralExpressionSyntax, LiteralExpressionSyntax replacement):
                value = replacement.Token.Value;
                return value is not null;

            default:
                return false;
        }
    }

    /// <summary>
    /// Determines whether both binary expressions use the very same left and right operand.
    /// </summary>
    /// <param name="original">The original expression.</param>
    /// <param name="replacement">The mutated expression.</param>
    /// <returns><see langword="true" /> if only the operator differs; otherwise <see langword="false" />.</returns>
    private static bool HasSameOperands(BinaryExpressionSyntax original, BinaryExpressionSyntax replacement) =>
        SyntaxFactory.AreEquivalent(original.Left, replacement.Left, topLevel: false)
        && SyntaxFactory.AreEquivalent(original.Right, replacement.Right, topLevel: false);

    /// <summary>
    /// Reads the compile-time constant value of <paramref name="node" />.
    /// </summary>
    /// <param name="node">The node to read.</param>
    /// <param name="semanticModel">The semantic model of the tree <paramref name="node" /> belongs to.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The boxed constant value, or <see langword="null" /> if there is none.</returns>
    private static object? GetConstant(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        var constant = semanticModel.GetConstantValue(node, cancellationToken);
        return constant.HasValue ? constant.Value : null;
    }

    /// <summary>
    /// Folds a binary operator over two constant operand values.
    /// </summary>
    /// <param name="kind">The syntax kind of the mutated binary expression.</param>
    /// <param name="left">The constant value of the left operand.</param>
    /// <param name="right">The constant value of the right operand.</param>
    /// <param name="value">The folded value, or <see langword="null" /> when folding was not possible.</param>
    /// <returns><see langword="true" /> if the operator could be folded; otherwise <see langword="false" />.</returns>
    private static bool TryFoldBinary(SyntaxKind kind, object? left, object? right, out object? value)
    {
        value = null;
        if (left is null || right is null)
        {
            return false;
        }

        switch (kind)
        {
            case SyntaxKind.AddExpression:
            case SyntaxKind.SubtractExpression:
            case SyntaxKind.MultiplyExpression:
            case SyntaxKind.DivideExpression:
            case SyntaxKind.ModuloExpression:
                return TryFoldArithmetic(kind, left, right, out value);

            case SyntaxKind.LessThanExpression:
            case SyntaxKind.LessThanOrEqualExpression:
            case SyntaxKind.GreaterThanExpression:
            case SyntaxKind.GreaterThanOrEqualExpression:
                return TryFoldRelational(kind, left, right, out value);

            case SyntaxKind.EqualsExpression:
            case SyntaxKind.NotEqualsExpression:
                return TryFoldEquality(kind, left, right, out value);

            case SyntaxKind.LeftShiftExpression:
            case SyntaxKind.RightShiftExpression:
                return TryFoldShift(kind, left, right, out value);

            default:
                return TryFoldBitwiseOrLogical(kind, left, right, out value);
        }
    }

    /// <summary>
    /// Folds the arithmetic operators for the operand types Frameshift can fold without any risk of
    /// an exception, which are <see cref="int" /> and <see cref="double" />.
    /// </summary>
    /// <param name="kind">The syntax kind of the mutated binary expression.</param>
    /// <param name="left">The constant value of the left operand.</param>
    /// <param name="right">The constant value of the right operand.</param>
    /// <param name="value">The folded value, or <see langword="null" /> when folding was not possible.</param>
    /// <returns><see langword="true" /> if the operator could be folded; otherwise <see langword="false" />.</returns>
    private static bool TryFoldArithmetic(SyntaxKind kind, object left, object right, out object? value)
    {
        value = null;

        if (left is int leftInteger && right is int rightInteger)
        {
            return TryFoldInt32(kind, leftInteger, rightInteger, out value);
        }

        if (left is double leftDouble && right is double rightDouble)
        {
            value = kind switch
            {
                SyntaxKind.AddExpression => leftDouble + rightDouble,
                SyntaxKind.SubtractExpression => leftDouble - rightDouble,
                SyntaxKind.MultiplyExpression => leftDouble * rightDouble,
                SyntaxKind.DivideExpression => leftDouble / rightDouble,
                SyntaxKind.ModuloExpression => leftDouble % rightDouble,
                _ => null,
            };

            return value is not null;
        }

        return false;
    }

    /// <summary>
    /// Folds an arithmetic operator over two <see cref="int" /> operands, computing in
    /// <see cref="long" /> so that an overflowing result can be detected and rejected instead of
    /// being folded to a wrong value. Division and modulo by zero are never folded, because they
    /// would throw at run time and are therefore observable.
    /// </summary>
    /// <param name="kind">The syntax kind of the mutated binary expression.</param>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <param name="value">The folded value, or <see langword="null" /> when folding was not possible.</param>
    /// <returns><see langword="true" /> if the operator could be folded; otherwise <see langword="false" />.</returns>
    private static bool TryFoldInt32(SyntaxKind kind, int left, int right, out object? value)
    {
        value = null;

        long? result = kind switch
        {
            SyntaxKind.AddExpression => (long)left + right,
            SyntaxKind.SubtractExpression => (long)left - right,
            SyntaxKind.MultiplyExpression => (long)left * right,
            SyntaxKind.DivideExpression when right != 0 => (long)left / right,
            SyntaxKind.ModuloExpression when right != 0 => (long)left % right,
            _ => null,
        };

        if (result is null || result.Value < int.MinValue || result.Value > int.MaxValue)
        {
            return false;
        }

        value = (int)result.Value;
        return true;
    }

    /// <summary>
    /// Folds the relational operators for <see cref="int" /> and <see cref="double" /> operands.
    /// </summary>
    /// <param name="kind">The syntax kind of the mutated binary expression.</param>
    /// <param name="left">The constant value of the left operand.</param>
    /// <param name="right">The constant value of the right operand.</param>
    /// <param name="value">The folded value, or <see langword="null" /> when folding was not possible.</param>
    /// <returns><see langword="true" /> if the operator could be folded; otherwise <see langword="false" />.</returns>
    private static bool TryFoldRelational(SyntaxKind kind, object left, object right, out object? value)
    {
        value = null;

        if (left is int leftInteger && right is int rightInteger)
        {
            value = Compare(kind, leftInteger.CompareTo(rightInteger));
            return value is not null;
        }

        if (left is double leftDouble && right is double rightDouble)
        {
            if (double.IsNaN(leftDouble) || double.IsNaN(rightDouble))
            {
                return false;
            }

            value = Compare(kind, leftDouble.CompareTo(rightDouble));
            return value is not null;
        }

        return false;
    }

    /// <summary>
    /// Turns the sign of a comparison into the result of the relational operator.
    /// </summary>
    /// <param name="kind">The syntax kind of the mutated binary expression.</param>
    /// <param name="comparison">The comparison result of the two operands.</param>
    /// <returns>The boxed result, or <see langword="null" /> for an unsupported operator.</returns>
    private static object? Compare(SyntaxKind kind, int comparison) =>
        kind switch
        {
            SyntaxKind.LessThanExpression => comparison < 0,
            SyntaxKind.LessThanOrEqualExpression => comparison <= 0,
            SyntaxKind.GreaterThanExpression => comparison > 0,
            SyntaxKind.GreaterThanOrEqualExpression => comparison >= 0,
            _ => null,
        };

    /// <summary>
    /// Folds the equality operators, which is only done for operands of the very same runtime type,
    /// so that no widening or user defined conversion has to be reasoned about.
    /// </summary>
    /// <param name="kind">The syntax kind of the mutated binary expression.</param>
    /// <param name="left">The constant value of the left operand.</param>
    /// <param name="right">The constant value of the right operand.</param>
    /// <param name="value">The folded value, or <see langword="null" /> when folding was not possible.</param>
    /// <returns><see langword="true" /> if the operator could be folded; otherwise <see langword="false" />.</returns>
    private static bool TryFoldEquality(SyntaxKind kind, object left, object right, out object? value)
    {
        value = null;
        if (left.GetType() != right.GetType())
        {
            return false;
        }

        // A not-a-number operand makes both operators false respectively true, which is exactly what
        // Equals does not model: it reports two not-a-number values as equal, while == reports them
        // as different. Folding it through Equals would silently swallow an observable mutation.
        var areEqual = !IsNotANumber(left) && left.Equals(right);
        value = kind == SyntaxKind.EqualsExpression ? areEqual : !areEqual;
        return true;
    }

    /// <summary>
    /// Determines whether a constant is a floating point not-a-number value.
    /// </summary>
    /// <param name="value">The constant to inspect.</param>
    /// <returns><see langword="true" /> if the value is not a number; otherwise <see langword="false" />.</returns>
    private static bool IsNotANumber(object value) =>
        value switch
        {
            double number => double.IsNaN(number),
            float number => float.IsNaN(number),
            _ => false,
        };

    /// <summary>
    /// Folds the shift operators for <see cref="int" /> operands, applying the same shift count
    /// masking the language uses.
    /// </summary>
    /// <param name="kind">The syntax kind of the mutated binary expression.</param>
    /// <param name="left">The constant value of the left operand.</param>
    /// <param name="right">The constant value of the right operand.</param>
    /// <param name="value">The folded value, or <see langword="null" /> when folding was not possible.</param>
    /// <returns><see langword="true" /> if the operator could be folded; otherwise <see langword="false" />.</returns>
    private static bool TryFoldShift(SyntaxKind kind, object left, object right, out object? value)
    {
        value = null;
        if (left is not int leftInteger || right is not int rightInteger)
        {
            return false;
        }

        var count = rightInteger & 31;
        value = kind switch
        {
            SyntaxKind.LeftShiftExpression => leftInteger << count,
            SyntaxKind.RightShiftExpression => leftInteger >> count,
            _ => null,
        };

        return value is not null;
    }

    /// <summary>
    /// Folds the bitwise and logical operators for <see cref="bool" /> and <see cref="int" /> operands.
    /// </summary>
    /// <param name="kind">The syntax kind of the mutated binary expression.</param>
    /// <param name="left">The constant value of the left operand.</param>
    /// <param name="right">The constant value of the right operand.</param>
    /// <param name="value">The folded value, or <see langword="null" /> when folding was not possible.</param>
    /// <returns><see langword="true" /> if the operator could be folded; otherwise <see langword="false" />.</returns>
    private static bool TryFoldBitwiseOrLogical(SyntaxKind kind, object left, object right, out object? value)
    {
        value = null;

        if (left is bool leftBoolean && right is bool rightBoolean)
        {
            value = kind switch
            {
                SyntaxKind.LogicalAndExpression or SyntaxKind.BitwiseAndExpression => leftBoolean && rightBoolean,
                SyntaxKind.LogicalOrExpression or SyntaxKind.BitwiseOrExpression => leftBoolean || rightBoolean,
                SyntaxKind.ExclusiveOrExpression => leftBoolean ^ rightBoolean,
                _ => null,
            };

            return value is not null;
        }

        if (left is int leftInteger && right is int rightInteger)
        {
            value = kind switch
            {
                SyntaxKind.BitwiseAndExpression => leftInteger & rightInteger,
                SyntaxKind.BitwiseOrExpression => leftInteger | rightInteger,
                SyntaxKind.ExclusiveOrExpression => leftInteger ^ rightInteger,
                _ => null,
            };

            return value is not null;
        }

        return false;
    }

    /// <summary>
    /// Folds the unary operators for <see cref="bool" />, <see cref="int" /> and
    /// <see cref="double" /> operands, rejecting the negation of <see cref="int.MinValue" />, which
    /// would overflow.
    /// </summary>
    /// <param name="kind">The syntax kind of the mutated unary expression.</param>
    /// <param name="operand">The constant value of the operand.</param>
    /// <param name="value">The folded value, or <see langword="null" /> when folding was not possible.</param>
    /// <returns><see langword="true" /> if the operator could be folded; otherwise <see langword="false" />.</returns>
    private static bool TryFoldUnary(SyntaxKind kind, object? operand, out object? value)
    {
        value = operand switch
        {
            bool boolean when kind == SyntaxKind.LogicalNotExpression => !boolean,
            int integer when kind == SyntaxKind.UnaryPlusExpression => integer,
            int integer when kind == SyntaxKind.UnaryMinusExpression && integer != int.MinValue => -integer,
            int integer when kind == SyntaxKind.BitwiseNotExpression => ~integer,
            double number when kind == SyntaxKind.UnaryPlusExpression => number,
            double number when kind == SyntaxKind.UnaryMinusExpression => -number,
            _ => null,
        };

        return value is not null;
    }

    /// <summary>
    /// Determines whether the mutation sits in code the compiler already knows can never run.
    /// </summary>
    /// <param name="mutation">The mutation to inspect.</param>
    /// <param name="semanticModel">The semantic model of the original tree.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>A trivial verdict, or <see langword="null" /> if the check does not apply.</returns>
    private static EquivalenceVerdict? ClassifyUnreachableCode(
        Mutation mutation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (HasThrowOnlyBody(mutation.Original))
        {
            return EquivalenceVerdict.Trivial(ThrowOnlyBodyReason);
        }

        return IsInUnreachableStatement(mutation, semanticModel, cancellationToken)
            ? EquivalenceVerdict.Trivial(UnreachableStatementReason)
            : null;
    }

    /// <summary>
    /// Determines whether the compiler reports unreachable code covering the mutation location.
    /// </summary>
    /// <param name="mutation">The mutation to inspect.</param>
    /// <param name="semanticModel">The semantic model of the original tree.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns><see langword="true" /> if the mutation is unreachable; otherwise <see langword="false" />.</returns>
    private static bool IsInUnreachableStatement(
        Mutation mutation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (!CanQuery(mutation.Original, semanticModel))
        {
            return false;
        }

        var scope = mutation.Original.FirstAncestorOrSelf<MemberDeclarationSyntax>();
        var diagnostics = semanticModel.GetDiagnostics(scope?.Span, cancellationToken);
        var mutationSpan = mutation.Location.SourceSpan;

        return diagnostics.Any(diagnostic =>
            string.Equals(diagnostic.Id, UnreachableCodeDiagnosticId, StringComparison.Ordinal)
            && CoversMutation(diagnostic.Location.SourceSpan, mutationSpan, semanticModel, cancellationToken)
        );
    }

    /// <summary>
    /// Determines whether an unreachable code diagnostic reported at
    /// <paramref name="diagnosticSpan" /> covers <paramref name="mutationSpan" />, either directly or
    /// through the statement the diagnostic is reported on.
    /// </summary>
    /// <param name="diagnosticSpan">The span of the reported diagnostic.</param>
    /// <param name="mutationSpan">The span of the mutation location.</param>
    /// <param name="semanticModel">The semantic model of the original tree.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns><see langword="true" /> if the span covers the mutation; otherwise <see langword="false" />.</returns>
    private static bool CoversMutation(
        TextSpan diagnosticSpan,
        TextSpan mutationSpan,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (diagnosticSpan.Contains(mutationSpan))
        {
            return true;
        }

        var root = semanticModel.SyntaxTree.GetRoot(cancellationToken);
        if (!root.FullSpan.Contains(diagnosticSpan))
        {
            return false;
        }

        var statement = root.FindNode(diagnosticSpan).FirstAncestorOrSelf<StatementSyntax>();
        return statement is not null && statement.Span.Contains(mutationSpan);
    }

    /// <summary>
    /// Determines whether the body containing the mutation consists of nothing but a
    /// <see langword="throw" />, in which case the member never produces a result a test could
    /// compare.
    /// </summary>
    /// <param name="node">The mutated node.</param>
    /// <returns><see langword="true" /> if the body only throws; otherwise <see langword="false" />.</returns>
    private static bool HasThrowOnlyBody(SyntaxNode node) =>
        FindBodyOwner(node) switch
        {
            BaseMethodDeclarationSyntax method => IsThrowOnly(method.Body, method.ExpressionBody),
            AccessorDeclarationSyntax accessor => IsThrowOnly(accessor.Body, accessor.ExpressionBody),
            PropertyDeclarationSyntax property => IsThrowOnly(body: null, property.ExpressionBody),
            IndexerDeclarationSyntax indexer => IsThrowOnly(body: null, indexer.ExpressionBody),
            LocalFunctionStatementSyntax localFunction => IsThrowOnly(localFunction.Body, localFunction.ExpressionBody),
            _ => false,
        };

    /// <summary>
    /// Finds the innermost declaration owning the body the mutated node lives in.
    /// </summary>
    /// <param name="node">The mutated node.</param>
    /// <returns>The owning declaration, or <see langword="null" /> if there is none.</returns>
    private static SyntaxNode? FindBodyOwner(SyntaxNode node) =>
        node.AncestorsAndSelf()
            .FirstOrDefault(ancestor =>
                ancestor
                    is BaseMethodDeclarationSyntax
                        or AccessorDeclarationSyntax
                        or PropertyDeclarationSyntax
                        or IndexerDeclarationSyntax
                        or LocalFunctionStatementSyntax
            );

    /// <summary>
    /// Determines whether a body is a single <see langword="throw" />.
    /// </summary>
    /// <param name="body">The block body, or <see langword="null" />.</param>
    /// <param name="expressionBody">The expression body, or <see langword="null" />.</param>
    /// <returns><see langword="true" /> if the body only throws; otherwise <see langword="false" />.</returns>
    private static bool IsThrowOnly(BlockSyntax? body, ArrowExpressionClauseSyntax? expressionBody)
    {
        if (expressionBody is not null)
        {
            return expressionBody.Expression is ThrowExpressionSyntax;
        }

        return body is not null && body.Statements.Count == 1 && body.Statements[0] is ThrowStatementSyntax;
    }

    /// <summary>
    /// Determines whether the mutated value is thrown away instead of being consumed.
    /// </summary>
    /// <param name="mutation">The mutation to inspect.</param>
    /// <param name="semanticModel">The semantic model of the original tree.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>A trivial verdict, or <see langword="null" /> if the check does not apply.</returns>
    private static EquivalenceVerdict? ClassifyDiscardedResult(
        Mutation mutation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (HasSideEffect(mutation.Original))
        {
            return null;
        }

        var outermost = FindOutermostPureExpression(mutation.Original);
        if (outermost is null)
        {
            return null;
        }

        if (outermost.Parent is ExpressionStatementSyntax)
        {
            return EquivalenceVerdict.Trivial(DiscardedStatementReason);
        }

        return IsDiscardAssignment(outermost, semanticModel, cancellationToken)
            ? EquivalenceVerdict.Trivial(DiscardAssignmentReason)
            : null;
    }

    /// <summary>
    /// Walks outwards from the mutated node as long as the parent only forwards the value of its
    /// child, so that the resulting expression has exactly the value the mutation changes.
    /// </summary>
    /// <param name="node">The mutated node.</param>
    /// <returns>The outermost pure expression, or <see langword="null" /> if there is none.</returns>
    private static ExpressionSyntax? FindOutermostPureExpression(SyntaxNode node)
    {
        if (node is not ExpressionSyntax expression)
        {
            return null;
        }

        while (expression.Parent is ExpressionSyntax parent && IsValueForwarding(parent))
        {
            expression = parent;
        }

        return expression;
    }

    /// <summary>
    /// Determines whether an expression only computes a value from its children, without any effect
    /// beyond that value.
    /// </summary>
    /// <param name="expression">The expression to inspect.</param>
    /// <returns><see langword="true" /> if it is side effect free; otherwise <see langword="false" />.</returns>
    private static bool IsValueForwarding(ExpressionSyntax expression) =>
        expression
            is ParenthesizedExpressionSyntax
                or BinaryExpressionSyntax
                or ConditionalExpressionSyntax
                or CastExpressionSyntax
                or CheckedExpressionSyntax
        || (expression is PrefixUnaryExpressionSyntax prefix && !IsIncrementOrDecrement(prefix.Kind()));

    /// <summary>
    /// Determines whether a syntax kind describes an increment or decrement, which changes state.
    /// </summary>
    /// <param name="kind">The syntax kind to inspect.</param>
    /// <returns><see langword="true" /> if the kind changes state; otherwise <see langword="false" />.</returns>
    private static bool IsIncrementOrDecrement(SyntaxKind kind) =>
        kind
            is SyntaxKind.PreIncrementExpression
                or SyntaxKind.PreDecrementExpression
                or SyntaxKind.PostIncrementExpression
                or SyntaxKind.PostDecrementExpression;

    /// <summary>
    /// Determines whether the mutated subtree can do anything besides computing a value, in which
    /// case dropping that value does not make the mutant unobservable.
    /// </summary>
    /// <param name="node">The mutated node.</param>
    /// <returns><see langword="true" /> if the subtree has a side effect; otherwise <see langword="false" />.</returns>
    private static bool HasSideEffect(SyntaxNode node) =>
        node.DescendantNodesAndSelf()
            .Any(descendant =>
                descendant
                    is InvocationExpressionSyntax
                        or AssignmentExpressionSyntax
                        or ObjectCreationExpressionSyntax
                        or ImplicitObjectCreationExpressionSyntax
                        or AwaitExpressionSyntax
                        or PostfixUnaryExpressionSyntax
                || (descendant is PrefixUnaryExpressionSyntax prefix && IsIncrementOrDecrement(prefix.Kind()))
            );

    /// <summary>
    /// Determines whether <paramref name="expression" /> is assigned to a discard in a statement of
    /// its own, e.g. <c>_ = value;</c>.
    /// </summary>
    /// <param name="expression">The expression carrying the mutated value.</param>
    /// <param name="semanticModel">The semantic model of the original tree.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns><see langword="true" /> if the value is discarded; otherwise <see langword="false" />.</returns>
    private static bool IsDiscardAssignment(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (
            expression.Parent is not AssignmentExpressionSyntax assignment
            || assignment.Right != expression
            || assignment.Parent is not ExpressionStatementSyntax
            || !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
        )
        {
            return false;
        }

        if (!CanQuery(assignment.Left, semanticModel))
        {
            return assignment.Left is IdentifierNameSyntax fallback
                && string.Equals(fallback.Identifier.ValueText, "_", StringComparison.Ordinal);
        }

        return semanticModel.GetSymbolInfo(assignment.Left, cancellationToken).Symbol is IDiscardSymbol;
    }

    /// <summary>
    /// Determines whether the mutation only changes a value that is baked in at compile time.
    /// </summary>
    /// <param name="mutation">The mutation to inspect.</param>
    /// <returns>A trivial verdict, or <see langword="null" /> if the check does not apply.</returns>
    private static EquivalenceVerdict? ClassifyConstantOnlyContext(Mutation mutation)
    {
        var reason = mutation
            .Original.Ancestors()
            .Select(GetConstantOnlyContextReason)
            .FirstOrDefault(candidate => candidate is not null);

        return reason is null ? null : EquivalenceVerdict.Trivial(reason);
    }

    /// <summary>
    /// Maps an ancestor of the mutated node to the reason it makes the mutation constant-only.
    /// </summary>
    /// <param name="ancestor">The ancestor to inspect.</param>
    /// <returns>The reason clause, or <see langword="null" /> if the ancestor is not constant-only.</returns>
    private static string? GetConstantOnlyContextReason(SyntaxNode ancestor) =>
        ancestor switch
        {
            AttributeArgumentSyntax or AttributeSyntax => AttributeArgumentReason,
            EqualsValueClauseSyntax clause when clause.Parent is ParameterSyntax => DefaultParameterReason,
            SwitchLabelSyntax => CaseLabelReason,
            LocalDeclarationStatementSyntax local when IsConstant(local.Modifiers) => ConstantDeclarationReason,
            FieldDeclarationSyntax field when IsConstant(field.Modifiers) => ConstantDeclarationReason,
            EnumMemberDeclarationSyntax => ConstantDeclarationReason,
            _ => null,
        };

    /// <summary>
    /// Determines whether a modifier list declares a compile-time constant.
    /// </summary>
    /// <param name="modifiers">The modifiers of a declaration.</param>
    /// <returns><see langword="true" /> if the declaration is a constant; otherwise <see langword="false" />.</returns>
    private static bool IsConstant(SyntaxTokenList modifiers) => modifiers.Any(SyntaxKind.ConstKeyword);

    /// <summary>
    /// Determines whether the member containing the mutation is one Frameshift never treats as a
    /// testing gap.
    /// </summary>
    /// <param name="mutation">The mutation to inspect.</param>
    /// <param name="semanticModel">The semantic model of the original tree.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>A trivial verdict, or <see langword="null" /> if the check does not apply.</returns>
    private static EquivalenceVerdict? ClassifyExcludedMember(
        Mutation mutation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (!CanQuery(mutation.Original, semanticModel))
        {
            return null;
        }

        var declaration = FindDeclaration(mutation.Original);
        if (declaration is null)
        {
            return null;
        }

        var symbol = semanticModel.GetDeclaredSymbol(declaration, cancellationToken);
        return symbol is null ? null : ClassifySymbol(symbol);
    }

    /// <summary>
    /// Maps the symbol of the containing member to the reason it is excluded.
    /// </summary>
    /// <param name="symbol">The symbol of the member containing the mutation.</param>
    /// <returns>A trivial verdict, or <see langword="null" /> if the member is not excluded.</returns>
    private static EquivalenceVerdict? ClassifySymbol(ISymbol symbol)
    {
        if (IsWellKnownMember(symbol))
        {
            return EquivalenceVerdict.Trivial(WellKnownMemberReason);
        }

        if (symbol.IsImplicitlyDeclared)
        {
            return EquivalenceVerdict.Trivial(CompilerGeneratedReason);
        }

        if (HasAttribute(symbol, "ExcludeFromCodeCoverageAttribute", "GeneratedCodeAttribute"))
        {
            return EquivalenceVerdict.Trivial(ExcludedMemberReason);
        }

        return HasAttribute(symbol, "ObsoleteAttribute") ? EquivalenceVerdict.Trivial(ObsoleteMemberReason) : null;
    }

    /// <summary>
    /// Finds the innermost declaration the mutated node belongs to, using the variable declarator of
    /// a field, because only the declarator carries a symbol.
    /// </summary>
    /// <param name="node">The mutated node.</param>
    /// <returns>The declaration to ask for a symbol, or <see langword="null" /> if there is none.</returns>
    private static SyntaxNode? FindDeclaration(SyntaxNode node)
    {
        var declaration = node.AncestorsAndSelf()
            .FirstOrDefault(ancestor =>
                ancestor is MemberDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax
            );

        if (declaration is BaseFieldDeclarationSyntax field)
        {
            return field.Declaration.Variables.Count > 0 ? field.Declaration.Variables[0] : null;
        }

        return declaration;
    }

    /// <summary>
    /// Determines whether the member is one of the infrastructure members whose mutants carry no
    /// information about the tested behaviour.
    /// </summary>
    /// <param name="symbol">The symbol of the member containing the mutation.</param>
    /// <returns><see langword="true" /> if the member is well known; otherwise <see langword="false" />.</returns>
    private static bool IsWellKnownMember(ISymbol symbol) =>
        symbol.Kind is SymbolKind.Method or SymbolKind.Property
        && symbol.Name is "ToString" or "GetHashCode" or "Equals" or "Dispose";

    /// <summary>
    /// Determines whether the member or any of its containing types carries one of
    /// <paramref name="names" />.
    /// </summary>
    /// <param name="symbol">The symbol of the member containing the mutation.</param>
    /// <param name="names">The metadata names of the attributes to look for.</param>
    /// <returns><see langword="true" /> if an attribute is present; otherwise <see langword="false" />.</returns>
    private static bool HasAttribute(ISymbol symbol, params string[] names)
    {
        for (var current = symbol; current is not null; current = current.ContainingType)
        {
            if (current.GetAttributes().Any(attribute => IsNamed(attribute, names)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether an attribute is one of <paramref name="names" />.
    /// </summary>
    /// <param name="attribute">The attribute to inspect.</param>
    /// <param name="names">The metadata names of the attributes to look for.</param>
    /// <returns><see langword="true" /> if the attribute matches; otherwise <see langword="false" />.</returns>
    private static bool IsNamed(AttributeData attribute, string[] names)
    {
        var name = attribute.AttributeClass?.Name;
        return name is not null && names.Contains(name, StringComparer.Ordinal);
    }

    /// <summary>
    /// Determines whether <paramref name="semanticModel" /> can answer questions about
    /// <paramref name="node" />, which requires the node to belong to the model's syntax tree.
    /// </summary>
    /// <remarks>
    /// <see cref="SyntaxNode.SyntaxTree" /> is never <see langword="null" /> — every node belongs to a
    /// tree, even one that was only parsed — so the tree identity is the whole test.
    /// </remarks>
    /// <param name="node">The node to query.</param>
    /// <param name="semanticModel">The semantic model to query.</param>
    /// <returns><see langword="true" /> if the node can be queried; otherwise <see langword="false" />.</returns>
    private static bool CanQuery(SyntaxNode node, SemanticModel semanticModel) =>
        ReferenceEquals(node.SyntaxTree, semanticModel.SyntaxTree);
}
