namespace NetEvolve.FrameShift.Equivalence;

using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using NetEvolve.FrameShift.Mutations;
using NetEvolve.FrameShift.Mutations.RegularExpressions;

/// <summary>
/// Decides whether a candidate <see cref="Mutation" /> is trivial, meaning that no test could ever
/// observe the difference between the mutant and the original code.
/// </summary>
/// <remarks>
/// <para>
/// The classification is deliberately one-sided. A wrong <see cref="EquivalenceVerdict.NotTrivial" />
/// verdict only costs a warning that a reviewer can dismiss, while a wrong trivial verdict silently
/// hides a real testing gap, which is exactly what FrameShift exists to find. Every check therefore
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
    private const string ConfigureAwaitArgumentReason =
        "the mutation only flips the captured-context argument of ConfigureAwait, which no test can observe";
    private const string WellKnownMemberReason = "the containing member is a well known infrastructure member";
    private const string CompilerGeneratedReason = "the containing member is compiler generated";
    private const string ExcludedMemberReason = "the containing member is excluded from coverage";
    private const string ObsoleteMemberReason = "the containing member is marked obsolete";

    private const string RegexExactOneQuantifierReason =
        "the quantifier repeats its atom exactly once, which leaving the quantifier out already does";
    private const string RegexOptionalQuantifierShorthandReason =
        "the counted quantifier is the same as the optional operator";
    private const string RegexOneOrMoreQuantifierShorthandReason =
        "the counted quantifier is the same as the one-or-more operator";
    private const string RegexZeroOrMoreQuantifierShorthandReason =
        "the counted quantifier is the same as the zero-or-more operator";

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
    /// <param name="unreachableCodeDiagnosticsCache">
    /// The cache memoising the compiler diagnostics used to detect unreachable code, shared by every
    /// mutation candidate of the same compilation, or <see langword="null" /> to compute the
    /// diagnostics without memoization, which every existing caller not yet passing one still does.
    /// </param>
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
        CancellationToken cancellationToken,
        UnreachableCodeDiagnosticsCache? unreachableCodeDiagnosticsCache = null
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
            ?? ClassifyRegexQuantifierShorthand(mutation, semanticModel, cancellationToken)
            ?? ClassifyUnreachableCode(mutation, semanticModel, unreachableCodeDiagnosticsCache, cancellationToken)
            ?? ClassifyDiscardedResult(mutation, semanticModel, cancellationToken)
            ?? ClassifyConstantOnlyContext(mutation)
            ?? ClassifyConfigureAwaitArgument(mutation, semanticModel, cancellationToken)
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
    /// Folds the arithmetic operators for the operand types FrameShift can fold without any risk of
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
    /// Determines whether the mutation only rewrites a regular expression quantifier into one of the
    /// four shorthand spellings .NET documents as exactly equivalent to a counted form: an exact count
    /// of one is the same as no quantifier at all, and <c>{0,1}</c>, <c>{1,}</c> and <c>{0,}</c> are
    /// the counted spellings of <c>?</c>, <c>+</c> and <c>*</c> respectively, greedy or lazy alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is deliberately the only place the classifier reasons about what a regular expression
    /// pattern matches, and it stays narrow on purpose. The four rewrites above are not an
    /// approximation, they are the exact semantics .NET documents for a quantifier, independent of every
    /// other <see cref="RegexOptions" /> flag and of everything else in the pattern, because a
    /// quantifier's own repetition count and laziness have no interaction with capture numbering or
    /// backreferences. Proving that two different patterns describe the same language in general - the
    /// kind of question <c>a+</c> and <c>aa*</c> ask - is out of scope and stays out of scope. This check
    /// exists for a pattern that already spells one of the four forms out - hand-written or produced by a
    /// rewrite outside this family - and proves the rewrite between it and its shorthand or bare
    /// counterpart is a no-op; it deliberately does not, and must not, call a bound shift such as
    /// <c>RegexQuantifierMutator</c>'s own <c>{0,1}</c> to <c>{1,1}</c> rewrite trivial, because narrowing
    /// "zero or one" to "exactly one" is an observable change - the empty string matches the former and
    /// not the latter - and such a mutant is a genuine testing gap, not a no-op.
    /// </para>
    /// <para>
    /// Both patterns are re-tokenized under the very same options, which have to be statically known in
    /// the first place: a pattern whose options cannot be resolved is left to the conservative default,
    /// because the options change the grammar a token belongs to. Every non-quantifier token is copied
    /// through unchanged, and each of the four rules is tried on its own canonical form, because each
    /// proves a different fact and has to report its own reason. A rule that leaves the canonicalized
    /// pattern identical to the one it started from proved nothing and is never reported for it.
    /// </para>
    /// </remarks>
    /// <param name="mutation">The mutation to inspect.</param>
    /// <param name="semanticModel">The semantic model of the original tree.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>A trivial verdict, or <see langword="null" /> if the check does not apply.</returns>
    private static EquivalenceVerdict? ClassifyRegexQuantifierShorthand(
        Mutation mutation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (
            !TryTokenizeRegexRewrite(
                mutation,
                semanticModel,
                cancellationToken,
                out var originalPattern,
                out var originalTokens,
                out var mutatedPattern,
                out var mutatedTokens
            )
        )
        {
            return null;
        }

        return ClassifyQuantifierCanonicalization(
                originalPattern,
                originalTokens,
                mutatedPattern,
                mutatedTokens,
                CanonicalizeExactOne,
                RegexExactOneQuantifierReason
            )
            ?? ClassifyQuantifierCanonicalization(
                originalPattern,
                originalTokens,
                mutatedPattern,
                mutatedTokens,
                CanonicalizeOptional,
                RegexOptionalQuantifierShorthandReason
            )
            ?? ClassifyQuantifierCanonicalization(
                originalPattern,
                originalTokens,
                mutatedPattern,
                mutatedTokens,
                CanonicalizeOneOrMore,
                RegexOneOrMoreQuantifierShorthandReason
            )
            ?? ClassifyQuantifierCanonicalization(
                originalPattern,
                originalTokens,
                mutatedPattern,
                mutatedTokens,
                CanonicalizeZeroOrMore,
                RegexZeroOrMoreQuantifierShorthandReason
            );
    }

    /// <summary>
    /// Resolves <paramref name="mutation" /> as a regex pattern rewrite and tokenizes both sides of it
    /// under the site's own options, which is the shared precondition every quantifier canonicalization
    /// rule needs before it can compare the two patterns.
    /// </summary>
    /// <param name="mutation">The mutation to inspect.</param>
    /// <param name="semanticModel">The semantic model of the original tree.</param>
    /// <param name="cancellationToken">A token to observe while resolving the site.</param>
    /// <param name="originalPattern">The original pattern text.</param>
    /// <param name="originalTokens">The tokens of the original pattern.</param>
    /// <param name="mutatedPattern">The mutated pattern text.</param>
    /// <param name="mutatedTokens">The tokens of the mutated pattern.</param>
    /// <returns>
    /// <see langword="true" /> if <paramref name="mutation" /> is a regex pattern rewrite whose options are
    /// statically known and whose patterns both tokenize; otherwise <see langword="false" />.
    /// </returns>
    private static bool TryTokenizeRegexRewrite(
        Mutation mutation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        out string originalPattern,
        out ImmutableArray<RegexToken> originalTokens,
        out string mutatedPattern,
        out ImmutableArray<RegexToken> mutatedTokens
    )
    {
        originalPattern = string.Empty;
        originalTokens = ImmutableArray<RegexToken>.Empty;
        mutatedPattern = string.Empty;
        mutatedTokens = ImmutableArray<RegexToken>.Empty;

        if (
            mutation.Original is not LiteralExpressionSyntax { Token.Value: string } original
            || mutation.Replacement is not LiteralExpressionSyntax { Token.Value: string } replacement
            || !CanQuery(original, semanticModel)
        )
        {
            return false;
        }

        var site = RegexPatternLocator.TryLocate(original, semanticModel, cancellationToken);

        if (site?.Options is not { } siteOptions)
        {
            return false;
        }

        var options = siteOptions & ~RegexOptions.Compiled;

        originalPattern = site.Pattern;
        mutatedPattern = replacement.Token.ValueText;

        return RegexPatternTokenizer.TryTokenize(originalPattern, options, out originalTokens, out _, out _)
            && RegexPatternTokenizer.TryTokenize(mutatedPattern, options, out mutatedTokens, out _, out _);
    }

    /// <summary>
    /// Applies one quantifier canonicalization rule to both patterns and proves triviality when the
    /// canonical forms agree and the rule actually changed something.
    /// </summary>
    /// <param name="originalPattern">The original pattern text.</param>
    /// <param name="originalTokens">The tokens of the original pattern.</param>
    /// <param name="mutatedPattern">The mutated pattern text.</param>
    /// <param name="mutatedTokens">The tokens of the mutated pattern.</param>
    /// <param name="canonicalize">The rule turning a quantifier token into its canonical text.</param>
    /// <param name="reason">The reason reported when the rule proves the two patterns equal.</param>
    /// <returns>A trivial verdict, or <see langword="null" /> if the rule does not prove equality.</returns>
    private static EquivalenceVerdict? ClassifyQuantifierCanonicalization(
        string originalPattern,
        ImmutableArray<RegexToken> originalTokens,
        string mutatedPattern,
        ImmutableArray<RegexToken> mutatedTokens,
        Func<RegexToken, string> canonicalize,
        string reason
    )
    {
        var canonicalOriginal = CanonicalizeQuantifiers(originalTokens, canonicalize);
        var canonicalMutated = CanonicalizeQuantifiers(mutatedTokens, canonicalize);

        if (!string.Equals(canonicalOriginal, canonicalMutated, StringComparison.Ordinal))
        {
            return null;
        }

        // A rule that leaves both patterns exactly as written proved nothing about this particular
        // difference; some other rule, or no rule at all, is the correct explanation for it.
        if (
            string.Equals(canonicalOriginal, originalPattern, StringComparison.Ordinal)
            && string.Equals(canonicalMutated, mutatedPattern, StringComparison.Ordinal)
        )
        {
            return null;
        }

        return EquivalenceVerdict.Trivial(reason);
    }

    /// <summary>
    /// Rebuilds a pattern by copying every token verbatim except a quantifier, which is rewritten by
    /// <paramref name="canonicalize" />.
    /// </summary>
    /// <param name="tokens">The tokens of the pattern.</param>
    /// <param name="canonicalize">The rule turning a quantifier token into its canonical text.</param>
    /// <returns>The canonicalized pattern text.</returns>
    private static string CanonicalizeQuantifiers(
        ImmutableArray<RegexToken> tokens,
        Func<RegexToken, string> canonicalize
    )
    {
        var builder = new StringBuilder();

        foreach (var token in tokens)
        {
            _ = builder.Append(token.Kind == RegexTokenKind.Quantifier ? canonicalize(token) : token.Text);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Canonicalizes an exact-one quantifier, <c>{1}</c> or <c>{1,1}</c>, to the empty string, because
    /// leaving the quantifier out repeats the atom exactly once as well and an exact count leaves the
    /// engine no laziness to differ on.
    /// </summary>
    /// <param name="token">The quantifier token.</param>
    /// <returns>The canonical text, unchanged when the token is not an exact-one quantifier.</returns>
    private static string CanonicalizeExactOne(RegexToken token)
    {
        var core = SplitQuantifierCore(token.Text, out _);

        return IsExactCount(core, out var count) && count == 1 ? string.Empty : token.Text;
    }

    /// <summary>
    /// Canonicalizes the counted <c>{0,1}</c> quantifier to <c>?</c>, keeping its laziness marker.
    /// </summary>
    /// <param name="token">The quantifier token.</param>
    /// <returns>The canonical text, unchanged when the token is not this counted form.</returns>
    private static string CanonicalizeOptional(RegexToken token) => CanonicalizeBoundedCounted(token, 0, 1, "?");

    /// <summary>
    /// Canonicalizes the open-ended <c>{1,}</c> quantifier to <c>+</c>, keeping its laziness marker.
    /// </summary>
    /// <param name="token">The quantifier token.</param>
    /// <returns>The canonical text, unchanged when the token is not this counted form.</returns>
    private static string CanonicalizeOneOrMore(RegexToken token) => CanonicalizeOpenEndedCounted(token, 1, "+");

    /// <summary>
    /// Canonicalizes the open-ended <c>{0,}</c> quantifier to <c>*</c>, keeping its laziness marker.
    /// </summary>
    /// <param name="token">The quantifier token.</param>
    /// <returns>The canonical text, unchanged when the token is not this counted form.</returns>
    private static string CanonicalizeZeroOrMore(RegexToken token) => CanonicalizeOpenEndedCounted(token, 0, "*");

    /// <summary>
    /// Canonicalizes a counted quantifier stating both an exact minimum and an exact maximum, e.g.
    /// <c>{0,1}</c>.
    /// </summary>
    /// <param name="token">The quantifier token.</param>
    /// <param name="minimum">The exact minimum the rule matches.</param>
    /// <param name="maximum">The exact maximum the rule matches.</param>
    /// <param name="shorthand">The shorthand symbol replacing the counted core.</param>
    /// <returns>The canonical text, unchanged when the token does not match the rule's shape.</returns>
    private static string CanonicalizeBoundedCounted(RegexToken token, int minimum, int maximum, string shorthand)
    {
        var core = SplitQuantifierCore(token.Text, out var marker);

        if (!IsBoundedCount(core, out var actualMinimum, out var actualMaximum))
        {
            return token.Text;
        }

        return actualMinimum == minimum && actualMaximum == maximum ? shorthand + marker : token.Text;
    }

    /// <summary>
    /// Canonicalizes an open-ended counted quantifier, e.g. <c>{1,}</c>, whose upper bound is left
    /// unstated.
    /// </summary>
    /// <param name="token">The quantifier token.</param>
    /// <param name="minimum">The exact minimum the rule matches.</param>
    /// <param name="shorthand">The shorthand symbol replacing the counted core.</param>
    /// <returns>The canonical text, unchanged when the token does not match the rule's shape.</returns>
    private static string CanonicalizeOpenEndedCounted(RegexToken token, int minimum, string shorthand)
    {
        var core = SplitQuantifierCore(token.Text, out var marker);

        if (!IsOpenEndedCount(core, out var actualMinimum))
        {
            return token.Text;
        }

        return actualMinimum == minimum ? shorthand + marker : token.Text;
    }

    /// <summary>
    /// Splits a quantifier token into its core and its lazy marker, the same way
    /// <c>RegexQuantifierMutator.SplitCore</c> does: a one character core is <c>*</c>, <c>+</c> or
    /// <c>?</c>, so anything behind the first character is the marker; a counted core ends with
    /// <c>}</c>, so a trailing <c>?</c> behind it is the marker.
    /// </summary>
    /// <param name="text">The text of the quantifier token.</param>
    /// <param name="marker">The lazy marker the token carries, either <c>?</c> or the empty string.</param>
    /// <returns>The core of the quantifier, without the marker.</returns>
    private static string SplitQuantifierCore(string text, out string marker)
    {
        if (text[0] is '*' or '+' or '?')
        {
            marker = text.Length > 1 ? "?" : string.Empty;

            return text.Substring(0, 1);
        }

        if (text[text.Length - 1] is '?')
        {
            marker = "?";

            return text.Substring(0, text.Length - 1);
        }

        marker = string.Empty;

        return text;
    }

    /// <summary>
    /// Determines whether a counted core states a single, exact repetition count, either <c>{n}</c> or
    /// <c>{n,n}</c>, and parses that count.
    /// </summary>
    /// <param name="core">The core of a quantifier, without its lazy marker.</param>
    /// <param name="count">The exact count, or zero when the core does not state one.</param>
    /// <returns><see langword="true" /> if the core states a single, exact repetition count.</returns>
    private static bool IsExactCount(string core, out int count)
    {
        count = 0;

        if (core[0] is not '{' || core[core.Length - 1] is not '}')
        {
            return false;
        }

        var bounds = core.Substring(1, core.Length - 2);
        var separator = bounds.IndexOf(',');

        if (separator < 0)
        {
            return TryParseQuantifierBound(bounds, out count);
        }

        var minimumText = bounds.Substring(0, separator);
        var maximumText = bounds.Substring(separator + 1);

        if (
            !TryParseQuantifierBound(minimumText, out var minimum)
            || !TryParseQuantifierBound(maximumText, out var maximum)
            || minimum != maximum
        )
        {
            return false;
        }

        count = minimum;

        return true;
    }

    /// <summary>
    /// Determines whether a counted core states both an exact minimum and an exact maximum, e.g.
    /// <c>{0,1}</c>, and parses both bounds.
    /// </summary>
    /// <param name="core">The core of a quantifier, without its lazy marker.</param>
    /// <param name="minimum">The lower bound, or zero when the core does not state this shape.</param>
    /// <param name="maximum">The upper bound, or zero when the core does not state this shape.</param>
    /// <returns><see langword="true" /> if the core states both an exact minimum and maximum.</returns>
    private static bool IsBoundedCount(string core, out int minimum, out int maximum)
    {
        minimum = 0;
        maximum = 0;

        if (core[0] is not '{' || core[core.Length - 1] is not '}')
        {
            return false;
        }

        var bounds = core.Substring(1, core.Length - 2);
        var separator = bounds.IndexOf(',');

        if (separator < 0)
        {
            return false;
        }

        var maximumText = bounds.Substring(separator + 1);

        if (maximumText.Length == 0)
        {
            return false;
        }

        var minimumText = bounds.Substring(0, separator);

        return TryParseQuantifierBound(minimumText, out minimum) && TryParseQuantifierBound(maximumText, out maximum);
    }

    /// <summary>
    /// Determines whether a counted core states an open-ended lower bound, e.g. <c>{1,}</c>, and parses
    /// that bound.
    /// </summary>
    /// <param name="core">The core of a quantifier, without its lazy marker.</param>
    /// <param name="minimum">The lower bound, or zero when the core does not state this shape.</param>
    /// <returns><see langword="true" /> if the core states an open-ended lower bound.</returns>
    private static bool IsOpenEndedCount(string core, out int minimum)
    {
        minimum = 0;

        if (core[0] is not '{' || core[core.Length - 1] is not '}')
        {
            return false;
        }

        var bounds = core.Substring(1, core.Length - 2);
        var separator = bounds.IndexOf(',');

        if (separator < 0 || separator != bounds.Length - 1)
        {
            return false;
        }

        var minimumText = bounds.Substring(0, separator);

        return TryParseQuantifierBound(minimumText, out minimum);
    }

    /// <summary>
    /// Parses one bound of a counted quantifier the same way <c>RegexQuantifierMutator</c> does: no
    /// sign, no group separator and no culture specific digit is ever accepted.
    /// </summary>
    /// <param name="text">The digits of the bound, as the token spells them.</param>
    /// <param name="value">The parsed bound, or zero when the text does not parse.</param>
    /// <returns><see langword="false" /> when the bound does not fit into an <see cref="int" />.</returns>
    private static bool TryParseQuantifierBound(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Determines whether the mutation sits in code the compiler already knows can never run.
    /// </summary>
    /// <param name="mutation">The mutation to inspect.</param>
    /// <param name="semanticModel">The semantic model of the original tree.</param>
    /// <param name="unreachableCodeDiagnosticsCache">
    /// The cache memoising the compiler diagnostics per member, or <see langword="null" /> to compute
    /// them without memoization.
    /// </param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>A trivial verdict, or <see langword="null" /> if the check does not apply.</returns>
    private static EquivalenceVerdict? ClassifyUnreachableCode(
        Mutation mutation,
        SemanticModel semanticModel,
        UnreachableCodeDiagnosticsCache? unreachableCodeDiagnosticsCache,
        CancellationToken cancellationToken
    )
    {
        if (HasThrowOnlyBody(mutation.Original))
        {
            return EquivalenceVerdict.Trivial(ThrowOnlyBodyReason);
        }

        return IsInUnreachableStatement(mutation, semanticModel, unreachableCodeDiagnosticsCache, cancellationToken)
            ? EquivalenceVerdict.Trivial(UnreachableStatementReason)
            : null;
    }

    /// <summary>
    /// Determines whether the compiler reports unreachable code covering the mutation location.
    /// </summary>
    /// <param name="mutation">The mutation to inspect.</param>
    /// <param name="semanticModel">The semantic model of the original tree.</param>
    /// <param name="unreachableCodeDiagnosticsCache">
    /// The cache memoising the compiler diagnostics per member, so that every candidate mutation of
    /// the same member shares one <see cref="SemanticModel.GetDiagnostics(TextSpan?, CancellationToken)" />
    /// call, or <see langword="null" /> to compute them without memoization.
    /// </param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns><see langword="true" /> if the mutation is unreachable; otherwise <see langword="false" />.</returns>
    private static bool IsInUnreachableStatement(
        Mutation mutation,
        SemanticModel semanticModel,
        UnreachableCodeDiagnosticsCache? unreachableCodeDiagnosticsCache,
        CancellationToken cancellationToken
    )
    {
        if (!CanQuery(mutation.Original, semanticModel))
        {
            return false;
        }

        var scope = mutation.Original.FirstAncestorOrSelf<MemberDeclarationSyntax>();
        var diagnostics =
            unreachableCodeDiagnosticsCache?.GetDiagnostics(semanticModel, scope?.Span, cancellationToken)
            ?? semanticModel.GetDiagnostics(scope?.Span, cancellationToken);
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
    private static SyntaxNode? FindBodyOwner(SyntaxNode node)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (
                current
                is BaseMethodDeclarationSyntax
                    or AccessorDeclarationSyntax
                    or PropertyDeclarationSyntax
                    or IndexerDeclarationSyntax
                    or LocalFunctionStatementSyntax
            )
            {
                return current;
            }
        }

        return null;
    }

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
        foreach (var ancestor in mutation.Original.Ancestors())
        {
            var reason = GetConstantOnlyContextReason(ancestor);
            if (reason is not null)
            {
                return EquivalenceVerdict.Trivial(reason);
            }
        }

        return null;
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
    /// Determines whether the mutation flips the captured-context argument of a
    /// <see cref="System.Threading.Tasks.Task.ConfigureAwait(bool)" /> or
    /// <see cref="System.Threading.Tasks.ValueTask.ConfigureAwait(bool)" /> call.
    /// </summary>
    /// <remarks>
    /// That argument only decides which synchronization context the continuation resumes on; it
    /// changes no return value, throws nothing new, and touches no other state a test could assert
    /// on. Unlike every other check in this classifier, this is not a proof from the shape of a
    /// particular fixture - it holds for every call site, because the documented contract of the
    /// parameter itself is scheduling, not behaviour. The method is resolved through the semantic
    /// model, exactly like the culture-sensitivity family does, so a same-named method on a type of
    /// your own is never mistaken for it.
    /// </remarks>
    /// <param name="mutation">The mutation to inspect.</param>
    /// <param name="semanticModel">The semantic model of the original tree.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>A trivial verdict, or <see langword="null" /> if the check does not apply.</returns>
    private static EquivalenceVerdict? ClassifyConfigureAwaitArgument(
        Mutation mutation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (mutation.Kind != MutationKind.BooleanLiteral)
        {
            return null;
        }

        if (
            mutation.Original.Parent is not ArgumentSyntax { NameColon: null } argument
            || argument.Parent is not ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation }
            || !CanQuery(invocation, semanticModel)
        )
        {
            return null;
        }

        if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
        {
            return null;
        }

        return IsConfigureAwaitMethod(method) ? EquivalenceVerdict.Trivial(ConfigureAwaitArgumentReason) : null;
    }

    /// <summary>
    /// Determines whether a method is the single-argument <c>ConfigureAwait</c> overload of
    /// <see cref="System.Threading.Tasks.Task" />, <see cref="System.Threading.Tasks.Task{TResult}" />,
    /// <see cref="System.Threading.Tasks.ValueTask" /> or <see cref="System.Threading.Tasks.ValueTask{TResult}" />.
    /// </summary>
    /// <param name="method">The invoked method to inspect.</param>
    /// <returns><see langword="true" /> if the method is that overload; otherwise <see langword="false" />.</returns>
    private static bool IsConfigureAwaitMethod(IMethodSymbol method) =>
        string.Equals(method.Name, "ConfigureAwait", StringComparison.Ordinal)
        && method.ContainingType is { Name: "Task" or "ValueTask" } containingType
        && string.Equals(
            containingType.ContainingNamespace?.ToDisplayString(),
            "System.Threading.Tasks",
            StringComparison.Ordinal
        );

    /// <summary>
    /// Determines whether the member containing the mutation is one FrameShift never treats as a
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
