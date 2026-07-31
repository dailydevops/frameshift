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

        if (IsConstantRequired(literal))
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
