namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Removes an existing logical negation (<c>!x</c> becomes <c>x</c>) and introduces a negation around
/// the boolean condition of an <see langword="if"/>, <see langword="while"/> or <see langword="do"/> statement and of a conditional
/// expression (<c>x</c> becomes <c>!(x)</c>).
/// </summary>
/// <remarks>
/// <para>
/// The two directions treat <c>bool?</c> deliberately differently. Removing a negation accepts it,
/// because <c>!x</c> on a <c>bool?</c> is itself a <c>bool?</c>, so the operand can take the place of the
/// whole expression without changing the type of the position. Introducing one rejects it, because the
/// wrapped <c>!(x)</c> would again be a <c>bool?</c>, and a <c>bool?</c> is not a valid condition of an
/// <see langword="if"/>, <see langword="while"/> or <see langword="do"/> statement nor of a conditional expression, so the mutant would
/// never compile.
/// </para>
/// <para>
/// The three states of a <c>bool?</c> are reached by <see cref="NullableLiteralMutator" /> instead.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Determines whether <paramref name="type" /> is <see cref="bool"/> or <c>bool?</c>, which are the two
    /// operand types the removal of a negation leaves in a well typed position.
    /// </summary>
    /// <param name="type">The type to inspect, which may be <see langword="null" /> for an unresolved node.</param>
    /// <returns><see langword="true" /> if the type is <see cref="bool"/> or <c>bool?</c>.</returns>
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
