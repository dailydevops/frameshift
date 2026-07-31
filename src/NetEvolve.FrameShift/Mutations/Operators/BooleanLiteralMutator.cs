namespace NetEvolve.FrameShift.Mutations.Operators;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Replaces the boolean literal <see langword="true" /> with <see langword="false" /> and vice versa,
/// skipping every position that requires a compile time constant.
/// </summary>
internal sealed class BooleanLiteralMutator : MutationOperatorBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BooleanLiteralMutator" /> class.
    /// </summary>
    public BooleanLiteralMutator()
        : base(
            "boolean-literal",
            MutationKind.BooleanLiteral,
            [SyntaxKind.TrueLiteralExpression, SyntaxKind.FalseLiteralExpression]
        ) { }

    /// <inheritdoc />
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // MutationOperatorBase.CreateMutations only hands over nodes of one of the SupportedSyntaxKinds,
        // and both boolean literal kinds are literal expressions, so the cast cannot fail and no type test
        // is needed here.
        var literal = (LiteralExpressionSyntax)node;

        if (ConstantContext.IsRequired(literal))
        {
            yield break;
        }

        if (literal.IsKind(SyntaxKind.TrueLiteralExpression))
        {
            yield return CreateMutation(
                literal,
                SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression),
                "true-to-false",
                "true => false"
            );
        }
        else
        {
            yield return CreateMutation(
                literal,
                SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression),
                "false-to-true",
                "false => true"
            );
        }
    }
}
