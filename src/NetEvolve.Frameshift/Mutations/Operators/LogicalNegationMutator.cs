namespace NetEvolve.Frameshift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Removes an existing logical negation (<c>!x</c> becomes <c>x</c>) and introduces a negation around
/// the boolean condition of an <c>if</c>, <c>while</c> or <c>do</c> statement and of a conditional
/// expression (<c>x</c> becomes <c>!(x)</c>).
/// </summary>
internal sealed class LogicalNegationMutator : MutationOperatorBase
{
    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds =
    [
        SyntaxKind.LogicalNotExpression,
        SyntaxKind.IfStatement,
        SyntaxKind.WhileStatement,
        SyntaxKind.DoStatement,
        SyntaxKind.ConditionalExpression,
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="LogicalNegationMutator" /> class.
    /// </summary>
    public LogicalNegationMutator()
        : base("negation", MutationKind.LogicalNegation, _supportedSyntaxKinds) { }

    /// <inheritdoc />
    /// <remarks>
    /// The <see cref="MutationOperatorBase.CreateMutations" /> of the base class only forwards nodes whose
    /// kind is one of the <see cref="MutationOperatorBase.SupportedSyntaxKinds" />, so every node that
    /// arrives here is either the logical not expression or one of the four nodes that carry a condition.
    /// The last arm is therefore the conditional expression, and no node without a condition exists.
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
            PrefixUnaryExpressionSyntax negation => CreateRemoval(negation, semanticModel, cancellationToken),
            IfStatementSyntax ifStatement => CreateWrapping(ifStatement.Condition, semanticModel, cancellationToken),
            WhileStatementSyntax whileStatement => CreateWrapping(
                whileStatement.Condition,
                semanticModel,
                cancellationToken
            ),
            DoStatementSyntax doStatement => CreateWrapping(doStatement.Condition, semanticModel, cancellationToken),
            _ => CreateWrapping(((ConditionalExpressionSyntax)node).Condition, semanticModel, cancellationToken),
        };
    }

    private IEnumerable<Mutation> CreateRemoval(
        PrefixUnaryExpressionSyntax negation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        var operand = negation.Operand;
        if (!IsBoolean(semanticModel.GetTypeInfo(operand, cancellationToken).Type))
        {
            return [];
        }

        return [CreateMutation(negation, operand, "remove-negation", "!x => x")];
    }

    private IEnumerable<Mutation> CreateWrapping(
        ExpressionSyntax condition,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (condition.IsKind(SyntaxKind.LogicalNotExpression))
        {
            return [];
        }

        if (semanticModel.GetTypeInfo(condition, cancellationToken).Type?.SpecialType != SpecialType.System_Boolean)
        {
            return [];
        }

        var inner = condition.WithLeadingTrivia().WithTrailingTrivia();
        var replacement = SyntaxFactory.PrefixUnaryExpression(
            SyntaxKind.LogicalNotExpression,
            SyntaxFactory.ParenthesizedExpression(inner)
        );

        return [CreateMutation(condition, replacement, "negate-condition", "x => !(x)")];
    }

    private static bool IsBoolean(ITypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        if (type.SpecialType == SpecialType.System_Boolean)
        {
            return true;
        }

        return type is INamedTypeSymbol named
            && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            && named.TypeArguments.Length == 1
            && named.TypeArguments[0].SpecialType == SpecialType.System_Boolean;
    }
}
