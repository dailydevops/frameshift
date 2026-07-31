namespace NetEvolve.Frameshift.Mutations.Operators;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Mutates the ternary conditional operator by swapping its two branches and by negating its
/// condition. Conditionals with syntactically equivalent branches are left untouched.
/// </summary>
internal sealed class ConditionalExpressionMutator : MutationOperatorBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConditionalExpressionMutator" /> class.
    /// </summary>
    public ConditionalExpressionMutator()
        : base("conditional-expression", MutationKind.ConditionalExpression, [SyntaxKind.ConditionalExpression]) { }

    /// <inheritdoc />
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is not ConditionalExpressionSyntax conditional)
        {
            yield break;
        }

        if (SyntaxFactory.AreEquivalent(conditional.WhenTrue, conditional.WhenFalse))
        {
            yield break;
        }

        var swapped = conditional
            .WithWhenTrue(conditional.WhenFalse.WithTriviaFrom(conditional.WhenTrue))
            .WithWhenFalse(conditional.WhenTrue.WithTriviaFrom(conditional.WhenFalse));

        yield return CreateMutation(conditional, swapped, "swap-branches", "c ? a : b => c ? b : a");

        var negated = conditional.WithCondition(Negate(conditional.Condition));

        yield return CreateMutation(conditional, negated, "negate-condition", "c ? a : b => !c ? a : b");
    }

    /// <summary>
    /// Negates <paramref name="condition" />, either by removing an existing logical negation or by
    /// adding one, parenthesizing the condition where the operator precedence requires it.
    /// </summary>
    /// <param name="condition">The condition to negate.</param>
    /// <returns>The negated condition, carrying the leading trivia of <paramref name="condition" />.</returns>
    private static ExpressionSyntax Negate(ExpressionSyntax condition)
    {
        var leadingTrivia = condition.GetLeadingTrivia();

        if (condition is PrefixUnaryExpressionSyntax negation && negation.IsKind(SyntaxKind.LogicalNotExpression))
        {
            return negation.Operand.WithLeadingTrivia(leadingTrivia).WithTrailingTrivia(negation.GetTrailingTrivia());
        }

        // The trailing trivia has to stay outside of the added parentheses, otherwise the whitespace
        // that separated the condition from the question mark ends up in front of the closing
        // parenthesis and the mutant no longer reads like the code it replaces.
        var operand = RequiresParentheses(condition)
            ? SyntaxFactory
                .ParenthesizedExpression(condition.WithoutTrivia())
                .WithTrailingTrivia(condition.GetTrailingTrivia())
            : condition.WithLeadingTrivia(SyntaxTriviaList.Empty);

        var operatorToken = SyntaxFactory.Token(leadingTrivia, SyntaxKind.ExclamationToken, SyntaxTriviaList.Empty);

        return SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, operatorToken, operand);
    }

    /// <summary>
    /// Determines whether <paramref name="condition" /> has to be wrapped in parentheses before the
    /// logical negation operator can be applied to it.
    /// </summary>
    /// <param name="condition">The condition to inspect.</param>
    /// <returns><see langword="true" /> if parentheses are required.</returns>
    private static bool RequiresParentheses(ExpressionSyntax condition) =>
        condition.Kind() switch
        {
            SyntaxKind.IdentifierName => false,
            SyntaxKind.SimpleMemberAccessExpression => false,
            SyntaxKind.PointerMemberAccessExpression => false,
            SyntaxKind.MemberBindingExpression => false,
            SyntaxKind.ConditionalAccessExpression => false,
            SyntaxKind.InvocationExpression => false,
            SyntaxKind.ElementAccessExpression => false,
            SyntaxKind.ParenthesizedExpression => false,
            SyntaxKind.TrueLiteralExpression => false,
            SyntaxKind.FalseLiteralExpression => false,
            SyntaxKind.LogicalNotExpression => false,
            SyntaxKind.ThisExpression => false,
            SyntaxKind.BaseExpression => false,
            SyntaxKind.SuppressNullableWarningExpression => false,
            _ => true,
        };
}
