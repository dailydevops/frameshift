namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Swaps <c>checked</c> and <c>unchecked</c>, in both the expression form (<c>checked(expr)</c> /
/// <c>unchecked(expr)</c>) and the statement form (<c>checked { }</c> / <c>unchecked { }</c>).
/// </summary>
/// <remarks>
/// The keyword only decides how arithmetic overflow is handled at run time, never the type or shape of
/// the expression or statement, so swapping it always compiles and no operand or type guard is needed.
/// </remarks>
internal sealed class CheckedContextMutator : MutationOperatorBase
{
    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds =
    [
        SyntaxKind.CheckedExpression,
        SyntaxKind.UncheckedExpression,
        SyntaxKind.CheckedStatement,
        SyntaxKind.UncheckedStatement,
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="CheckedContextMutator" /> class.
    /// </summary>
    public CheckedContextMutator()
        : base("checked-context", MutationKind.CheckedContext, _supportedSyntaxKinds) { }

    /// <inheritdoc />
    /// <remarks>
    /// No type test is needed: <see cref="MutationOperatorBase.CreateMutations" /> only forwards nodes
    /// whose kind is one of the <see cref="MutationOperatorBase.SupportedSyntaxKinds" />, and every kind
    /// this operator supports is either a <see cref="CheckedExpressionSyntax" /> or a
    /// <see cref="CheckedStatementSyntax" />.
    /// </remarks>
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        return node switch
        {
            CheckedExpressionSyntax expression => [CreateExpressionMutation(expression)],
            CheckedStatementSyntax statement => [CreateStatementMutation(statement)],
            _ => [],
        };
    }

    private Mutation CreateExpressionMutation(CheckedExpressionSyntax expression)
    {
        var isChecked = expression.IsKind(SyntaxKind.CheckedExpression);
        var targetKind = isChecked ? SyntaxKind.UncheckedExpression : SyntaxKind.CheckedExpression;
        var suffix = isChecked ? "checked-to-unchecked-expression" : "unchecked-to-checked-expression";
        var displayName = isChecked ? "checked(...) => unchecked(...)" : "unchecked(...) => checked(...)";

        var keyword = SyntaxFactory.Token(
            expression.Keyword.LeadingTrivia,
            targetKind == SyntaxKind.CheckedExpression ? SyntaxKind.CheckedKeyword : SyntaxKind.UncheckedKeyword,
            expression.Keyword.TrailingTrivia
        );

        var replacement = SyntaxFactory
            .CheckedExpression(
                targetKind,
                keyword,
                expression.OpenParenToken,
                expression.Expression,
                expression.CloseParenToken
            )
            .WithTriviaFrom(expression);

        return CreateMutation(expression, replacement, suffix, displayName);
    }

    private Mutation CreateStatementMutation(CheckedStatementSyntax statement)
    {
        var isChecked = statement.IsKind(SyntaxKind.CheckedStatement);
        var targetKind = isChecked ? SyntaxKind.UncheckedStatement : SyntaxKind.CheckedStatement;
        var suffix = isChecked ? "checked-to-unchecked-statement" : "unchecked-to-checked-statement";
        var displayName = isChecked ? "checked { } => unchecked { }" : "unchecked { } => checked { }";

        var keyword = SyntaxFactory.Token(
            statement.Keyword.LeadingTrivia,
            targetKind == SyntaxKind.CheckedStatement ? SyntaxKind.CheckedKeyword : SyntaxKind.UncheckedKeyword,
            statement.Keyword.TrailingTrivia
        );

        var replacement = SyntaxFactory
            .CheckedStatement(targetKind, keyword, statement.Block)
            .WithTriviaFrom(statement);

        return CreateMutation(statement, replacement, suffix, displayName);
    }
}
