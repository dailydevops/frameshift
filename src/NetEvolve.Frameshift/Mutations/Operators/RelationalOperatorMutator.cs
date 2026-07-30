namespace NetEvolve.Frameshift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Mutates the relational operators <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c> and <c>&gt;=</c> into each
/// other, including the boundary flipping combinations that expose off-by-one errors.
/// </summary>
internal sealed class RelationalOperatorMutator : MutationOperatorBase
{
    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds =
    [
        SyntaxKind.LessThanExpression,
        SyntaxKind.LessThanOrEqualExpression,
        SyntaxKind.GreaterThanExpression,
        SyntaxKind.GreaterThanOrEqualExpression,
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="RelationalOperatorMutator" /> class.
    /// </summary>
    public RelationalOperatorMutator()
        : base("relational", MutationKind.RelationalOperator, _supportedSyntaxKinds) { }

    /// <inheritdoc />
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (node is not BinaryExpressionSyntax binary)
        {
            yield break;
        }

        var originalKind = binary.Kind();
        foreach (var candidate in _supportedSyntaxKinds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (candidate == originalKind)
            {
                continue;
            }

            var replacement = SyntaxFactory.BinaryExpression(
                candidate,
                binary.Left,
                CreateOperatorToken(binary.OperatorToken, candidate),
                binary.Right
            );

            yield return CreateMutation(
                binary,
                replacement,
                $"{GetSlug(originalKind)}-to-{GetSlug(candidate)}",
                $"{GetText(originalKind)} => {GetText(candidate)}"
            );
        }
    }

    private static SyntaxToken CreateOperatorToken(SyntaxToken originalToken, SyntaxKind expressionKind) =>
        SyntaxFactory.Token(
            originalToken.LeadingTrivia,
            GetOperatorTokenKind(expressionKind),
            originalToken.TrailingTrivia
        );

    private static SyntaxKind GetOperatorTokenKind(SyntaxKind expressionKind) =>
        expressionKind switch
        {
            SyntaxKind.LessThanExpression => SyntaxKind.LessThanToken,
            SyntaxKind.LessThanOrEqualExpression => SyntaxKind.LessThanEqualsToken,
            SyntaxKind.GreaterThanExpression => SyntaxKind.GreaterThanToken,
            _ => SyntaxKind.GreaterThanEqualsToken,
        };

    private static string GetSlug(SyntaxKind expressionKind) =>
        expressionKind switch
        {
            SyntaxKind.LessThanExpression => "less-than",
            SyntaxKind.LessThanOrEqualExpression => "less-than-or-equal",
            SyntaxKind.GreaterThanExpression => "greater-than",
            _ => "greater-than-or-equal",
        };

    private static string GetText(SyntaxKind expressionKind) =>
        expressionKind switch
        {
            SyntaxKind.LessThanExpression => "<",
            SyntaxKind.LessThanOrEqualExpression => "<=",
            SyntaxKind.GreaterThanExpression => ">",
            _ => ">=",
        };
}
