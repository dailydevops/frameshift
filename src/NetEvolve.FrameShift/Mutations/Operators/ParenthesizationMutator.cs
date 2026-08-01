namespace NetEvolve.FrameShift.Mutations.Operators;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Reassociates a parenthesized additive sub-expression (<c>+</c>, <c>-</c>) whose parentheses are
/// load-bearing for operator precedence inside an enclosing multiplicative expression (<c>*</c>,
/// <c>/</c>, <c>%</c>), replacing the smallest enclosing expression whose shape actually changes once
/// the parentheses are read away.
/// </summary>
/// <remarks>
/// <para>
/// Simply deleting the <see cref="ParenthesizedExpressionSyntax" /> wrapper node and splicing its
/// inner expression back into the same tree position would not change anything: precedence and
/// associativity are already baked into the shape of the <see cref="BinaryExpressionSyntax" /> tree
/// beneath the parentheses, so <c>(a + b) * c</c> and <c>a + b * c</c> are two differently shaped
/// trees, not the same tree with one wrapper node removed. This operator therefore builds the
/// differently associated <see cref="BinaryExpressionSyntax" /> tree directly and replaces the
/// enclosing multiplicative expression with it.
/// </para>
/// <para>
/// Only the pairing of the additive and the multiplicative family is covered, kept narrow on purpose.
/// A parenthesized multiplicative sub-expression inside an additive one, such as <c>a + (b * c)</c> or
/// <c>(a * b) + c</c>, is left alone: multiplication already binds tighter than addition, so those
/// parentheses are already redundant and removing them would not change the tree at all. Likewise,
/// parentheses around an expression of the very same precedence tier, such as <c>(a + b) + c</c> or
/// <c>(a * b) * c</c>, group identically to the unparenthesized reading because of left-associativity,
/// so offering them would only ever produce an equivalent mutant.
/// </para>
/// <para>
/// A mutation is only offered when every operator involved - the parenthesized expression, its
/// enclosing expression, and every operand of both - is a genuine, non user-defined arithmetic
/// operator, the same way <see cref="ArithmeticOperatorMutator" /> decides that. Reassociating a user
/// defined operator's precedence is out of scope, because there is no general way to know whether that
/// is even meaningful for the type it is declared on.
/// </para>
/// </remarks>
internal sealed class ParenthesizationMutator : MutationOperatorBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ParenthesizationMutator" /> class.
    /// </summary>
    public ParenthesizationMutator()
        : base("parenthesization", MutationKind.Parenthesization, [SyntaxKind.ParenthesizedExpression]) { }

    /// <inheritdoc />
    /// <remarks>
    /// No type check is needed: the base class only forwards a node whose kind is one of
    /// <see cref="MutationOperatorBase.SupportedSyntaxKinds" />, and the only kind this operator
    /// supports is a parenthesized expression, so the cast cannot fail.
    /// </remarks>
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var parenthesized = (ParenthesizedExpressionSyntax)node;

        if (
            parenthesized.Parent is not BinaryExpressionSyntax outer
            || !IsMultiplicative(outer.Kind())
            || parenthesized.Expression is not BinaryExpressionSyntax inner
            || !IsAdditive(inner.Kind())
        )
        {
            yield break;
        }

        var isLeftOperand = ReferenceEquals(outer.Left, parenthesized);
        if (!isLeftOperand && !ReferenceEquals(outer.Right, parenthesized))
        {
            yield break;
        }

        if (ConstantContext.IsRequired(outer))
        {
            yield break;
        }

        var first = isLeftOperand ? inner.Left : outer.Left;
        var second = isLeftOperand ? inner.Right : inner.Left;
        var third = isLeftOperand ? outer.Right : inner.Right;

        if (
            !IsGenuineArithmeticOperand(first, semanticModel, cancellationToken)
            || !IsGenuineArithmeticOperand(second, semanticModel, cancellationToken)
            || !IsGenuineArithmeticOperand(third, semanticModel, cancellationToken)
            || IsUserDefinedOperator(inner, semanticModel, cancellationToken)
            || IsUserDefinedOperator(outer, semanticModel, cancellationToken)
        )
        {
            yield break;
        }

        var (replacement, originalPattern, mutatedPattern) = isLeftOperand
            ? ReassociateLeftOperand(outer, inner)
            : ReassociateRightOperand(outer, inner);

        var positionSuffix = isLeftOperand ? "left" : "right";

        yield return CreateMutation(
            outer,
            replacement.WithTriviaFrom(outer),
            $"{GetName(inner.Kind())}-in-{GetName(outer.Kind())}-{positionSuffix}",
            $"{originalPattern} => {mutatedPattern}"
        );
    }

    /// <summary>
    /// Reassociates <c>(a op1 b) op2 c</c> into <c>a op1 (b op2 c)</c>, which is how the expression
    /// reads without the parentheses: <c>a op1 b op2 c</c>.
    /// </summary>
    private static (
        BinaryExpressionSyntax Replacement,
        string OriginalPattern,
        string MutatedPattern
    ) ReassociateLeftOperand(BinaryExpressionSyntax outer, BinaryExpressionSyntax inner)
    {
        var regroupedRight = SyntaxFactory.BinaryExpression(
            outer.Kind(),
            inner.Right.WithoutTrivia(),
            SpacedToken(outer.OperatorToken.Kind()),
            outer.Right.WithoutTrivia()
        );
        var replacement = SyntaxFactory.BinaryExpression(
            inner.Kind(),
            inner.Left.WithoutTrivia(),
            SpacedToken(inner.OperatorToken.Kind()),
            regroupedRight
        );

        return (
            replacement,
            $"(a {GetSymbol(inner.Kind())} b) {GetSymbol(outer.Kind())} c",
            $"a {GetSymbol(inner.Kind())} b {GetSymbol(outer.Kind())} c"
        );
    }

    /// <summary>
    /// Reassociates <c>a op2 (b op1 c)</c> into <c>(a op2 b) op1 c</c>, which is how the expression
    /// reads without the parentheses: <c>a op2 b op1 c</c>.
    /// </summary>
    private static (
        BinaryExpressionSyntax Replacement,
        string OriginalPattern,
        string MutatedPattern
    ) ReassociateRightOperand(BinaryExpressionSyntax outer, BinaryExpressionSyntax inner)
    {
        var regroupedLeft = SyntaxFactory.BinaryExpression(
            outer.Kind(),
            outer.Left.WithoutTrivia(),
            SpacedToken(outer.OperatorToken.Kind()),
            inner.Left.WithoutTrivia()
        );
        var replacement = SyntaxFactory.BinaryExpression(
            inner.Kind(),
            regroupedLeft,
            SpacedToken(inner.OperatorToken.Kind()),
            inner.Right.WithoutTrivia()
        );

        return (
            replacement,
            $"a {GetSymbol(outer.Kind())} (b {GetSymbol(inner.Kind())} c)",
            $"a {GetSymbol(outer.Kind())} b {GetSymbol(inner.Kind())} c"
        );
    }

    private static bool IsAdditive(SyntaxKind expressionKind) =>
        expressionKind is SyntaxKind.AddExpression or SyntaxKind.SubtractExpression;

    private static bool IsMultiplicative(SyntaxKind expressionKind) =>
        expressionKind is SyntaxKind.MultiplyExpression or SyntaxKind.DivideExpression or SyntaxKind.ModuloExpression;

    private static SyntaxToken SpacedToken(SyntaxKind tokenKind) =>
        SyntaxFactory.Token(
            SyntaxTriviaList.Create(SyntaxFactory.Space),
            tokenKind,
            SyntaxTriviaList.Create(SyntaxFactory.Space)
        );

    private static string GetName(SyntaxKind expressionKind) =>
        expressionKind switch
        {
            SyntaxKind.AddExpression => "add",
            SyntaxKind.SubtractExpression => "subtract",
            SyntaxKind.MultiplyExpression => "multiply",
            SyntaxKind.DivideExpression => "divide",
            SyntaxKind.ModuloExpression => "modulo",
            _ => throw NotArithmetic(expressionKind),
        };

    private static string GetSymbol(SyntaxKind expressionKind) =>
        expressionKind switch
        {
            SyntaxKind.AddExpression => "+",
            SyntaxKind.SubtractExpression => "-",
            SyntaxKind.MultiplyExpression => "*",
            SyntaxKind.DivideExpression => "/",
            SyntaxKind.ModuloExpression => "%",
            _ => throw NotArithmetic(expressionKind),
        };

    private static ArgumentOutOfRangeException NotArithmetic(SyntaxKind expressionKind) =>
        new ArgumentOutOfRangeException(
            nameof(expressionKind),
            expressionKind,
            "The syntax kind is not a binary arithmetic expression."
        );

    /// <summary>
    /// Decides whether an operand can take part in a parenthesization mutation, the same way
    /// <see cref="ArithmeticOperatorMutator" /> decides it for its own operands: a <see cref="string" />
    /// or an <see cref="object" /> operand means a string concatenation, a delegate operand means a
    /// delegate combination, and neither belongs to the arithmetic family this operator covers.
    /// </summary>
    private static bool IsGenuineArithmeticOperand(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        var type = semanticModel.GetTypeInfo(expression, cancellationToken).ConvertedType;

        if (type is null || type.TypeKind is TypeKind.Error or TypeKind.Delegate or TypeKind.Pointer)
        {
            return false;
        }

        return type.SpecialType is not (SpecialType.System_String or SpecialType.System_Object);
    }

    /// <summary>
    /// Decides whether <paramref name="binary" /> is bound to a user defined operator. Reassociating a
    /// user defined operator's precedence is out of scope, since there is no general way to know whether
    /// that is even meaningful for the type it is declared on.
    /// </summary>
    private static bool IsUserDefinedOperator(
        BinaryExpressionSyntax binary,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    ) =>
        semanticModel.GetSymbolInfo(binary, cancellationToken).Symbol
            is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator };
}
