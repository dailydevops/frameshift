namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Swaps the increment and decrement operators, in prefix as well as in postfix form.
/// </summary>
/// <remarks>
/// Only the operator itself changes, the fixity is preserved: <c>x++</c> becomes <c>x--</c> and
/// <c>++x</c> becomes <c>--x</c>, a postfix form is never turned into a prefix form or vice versa.
/// </remarks>
internal sealed class IncrementDecrementMutator : MutationOperatorBase
{
    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds =
    [
        SyntaxKind.PreIncrementExpression,
        SyntaxKind.PreDecrementExpression,
        SyntaxKind.PostIncrementExpression,
        SyntaxKind.PostDecrementExpression,
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="IncrementDecrementMutator" /> class.
    /// </summary>
    public IncrementDecrementMutator()
        : base("increment-decrement", MutationKind.Increment, _supportedSyntaxKinds) { }

    /// <inheritdoc />
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var isIncrement =
            node.IsKind(SyntaxKind.PreIncrementExpression) || node.IsKind(SyntaxKind.PostIncrementExpression);
        var boundOperator = semanticModel.GetSymbolInfo(node, cancellationToken).Symbol as IMethodSymbol;
        if (
            boundOperator?.MethodKind == MethodKind.UserDefinedOperator
            && !OperatorCounterpart.HasCounterpart(boundOperator, isIncrement ? "op_Decrement" : "op_Increment")
        )
        {
            return [];
        }

        // The base class only forwards nodes of the supported kinds: both pre-forms are a prefix unary
        // expression and both post-forms are a postfix unary expression, so no other shape arrives here.
        return node is PrefixUnaryExpressionSyntax prefix
            ? CreatePrefixMutations(prefix, isIncrement)
            : CreatePostfixMutations((PostfixUnaryExpressionSyntax)node, isIncrement);
    }

    /// <summary>
    /// Creates the mutation for a prefix <c>++x</c> or <c>--x</c> expression.
    /// </summary>
    /// <param name="prefix">The prefix expression to mutate.</param>
    /// <param name="isIncrement"><see langword="true" /> for <c>++x</c>; otherwise <see langword="false" />.</param>
    /// <returns>The single mutation swapping the operator.</returns>
    private IEnumerable<Mutation> CreatePrefixMutations(PrefixUnaryExpressionSyntax prefix, bool isIncrement)
    {
        var operatorToken = SyntaxFactory.Token(
            prefix.OperatorToken.LeadingTrivia,
            isIncrement ? SyntaxKind.MinusMinusToken : SyntaxKind.PlusPlusToken,
            prefix.OperatorToken.TrailingTrivia
        );
        var replacement = SyntaxFactory
            .PrefixUnaryExpression(
                isIncrement ? SyntaxKind.PreDecrementExpression : SyntaxKind.PreIncrementExpression,
                operatorToken,
                prefix.Operand
            )
            .WithTriviaFrom(prefix);

        return
        [
            new Mutation(
                isIncrement ? MutationKind.Increment : MutationKind.Decrement,
                $"{Id}.{(isIncrement ? "prefix-increment-to-decrement" : "prefix-decrement-to-increment")}",
                isIncrement ? "++x => --x" : "--x => ++x",
                prefix,
                replacement
            ),
        ];
    }

    /// <summary>
    /// Creates the mutation for a postfix <c>x++</c> or <c>x--</c> expression.
    /// </summary>
    /// <param name="postfix">The postfix expression to mutate.</param>
    /// <param name="isIncrement"><see langword="true" /> for <c>x++</c>; otherwise <see langword="false" />.</param>
    /// <returns>The single mutation swapping the operator.</returns>
    private IEnumerable<Mutation> CreatePostfixMutations(PostfixUnaryExpressionSyntax postfix, bool isIncrement)
    {
        var operatorToken = SyntaxFactory.Token(
            postfix.OperatorToken.LeadingTrivia,
            isIncrement ? SyntaxKind.MinusMinusToken : SyntaxKind.PlusPlusToken,
            postfix.OperatorToken.TrailingTrivia
        );
        var replacement = SyntaxFactory
            .PostfixUnaryExpression(
                isIncrement ? SyntaxKind.PostDecrementExpression : SyntaxKind.PostIncrementExpression,
                postfix.Operand,
                operatorToken
            )
            .WithTriviaFrom(postfix);

        return
        [
            new Mutation(
                isIncrement ? MutationKind.Increment : MutationKind.Decrement,
                $"{Id}.{(isIncrement ? "postfix-increment-to-decrement" : "postfix-decrement-to-increment")}",
                isIncrement ? "x++ => x--" : "x-- => x++",
                postfix,
                replacement
            ),
        ];
    }
}
