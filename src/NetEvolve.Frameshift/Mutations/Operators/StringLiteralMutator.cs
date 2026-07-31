namespace NetEvolve.Frameshift.Mutations.Operators;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Replaces a non-empty string literal by the empty string and an empty string literal by a
/// non-empty one. Verbatim and raw literals are supported, the replacement is always a plain literal.
/// </summary>
internal sealed class StringLiteralMutator : MutationOperatorBase
{
    private const string NonEmptyReplacement = "Frameshift";

    /// <summary>
    /// Initializes a new instance of the <see cref="StringLiteralMutator" /> class.
    /// </summary>
    public StringLiteralMutator()
        : base("string-literal", MutationKind.StringLiteral, [SyntaxKind.StringLiteralExpression]) { }

    /// <inheritdoc />
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // MutationOperatorBase.CreateMutations only hands over nodes of one of the SupportedSyntaxKinds,
        // and SyntaxKind.StringLiteralExpression is always a literal expression, so the cast cannot fail
        // and no type test is needed here.
        var literal = (LiteralExpressionSyntax)node;

        if (IsConstantRequired(literal) || IsNameOfArgument(literal))
        {
            yield break;
        }

        if (literal.Token.ValueText.Length == 0)
        {
            yield return CreateMutation(
                literal,
                CreateStringLiteral(NonEmptyReplacement),
                "empty-to-non-empty",
                "\"\" => \"" + NonEmptyReplacement + "\""
            );
        }
        else
        {
            yield return CreateMutation(literal, CreateStringLiteral(string.Empty), "to-empty", "\"...\" => \"\"");
        }
    }

    private static LiteralExpressionSyntax CreateStringLiteral(string value) =>
        SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value));

    /// <summary>
    /// Determines whether <paramref name="node" /> is the argument of a <c>nameof</c> expression.
    /// </summary>
    /// <param name="node">The node to inspect.</param>
    /// <returns><see langword="true" /> if the node is a <c>nameof</c> argument.</returns>
    private static bool IsNameOfArgument(SyntaxNode node) =>
        node.Parent is ArgumentSyntax argument
        && argument.Parent is ArgumentListSyntax argumentList
        && argumentList.Parent is InvocationExpressionSyntax invocation
        && invocation.Expression is IdentifierNameSyntax identifier
        && string.Equals(identifier.Identifier.ValueText, "nameof", StringComparison.Ordinal);

    /// <summary>
    /// Determines whether <paramref name="node" /> sits in a position that only accepts a compile
    /// time constant, such as an attribute argument, a <see langword="const" /> initializer, a default
    /// parameter value, a <c>case</c> label, a <c>goto case</c> statement or a constant pattern.
    /// </summary>
    /// <param name="node">The node to inspect.</param>
    /// <returns><see langword="true" /> if the node must stay a constant expression.</returns>
    private static bool IsConstantRequired(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case AttributeSyntax:
                case AttributeArgumentSyntax:
                case CaseSwitchLabelSyntax:
                case ConstantPatternSyntax:
                case RelationalPatternSyntax:
                case ParameterSyntax:
                case EnumMemberDeclarationSyntax:
                    return true;

                case GotoStatementSyntax gotoStatement when gotoStatement.IsKind(SyntaxKind.GotoCaseStatement):
                case FieldDeclarationSyntax field when field.Modifiers.Any(SyntaxKind.ConstKeyword):
                case LocalDeclarationStatementSyntax local when local.Modifiers.Any(SyntaxKind.ConstKeyword):
                    return true;

                case MemberDeclarationSyntax:
                case CompilationUnitSyntax:
                    return false;

                default:
                    continue;
            }
        }

        return false;
    }
}
