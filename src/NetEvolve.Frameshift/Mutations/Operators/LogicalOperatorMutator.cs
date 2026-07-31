namespace NetEvolve.Frameshift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Mutates <c>&amp;&amp;</c> into <c>||</c> and back, as well as <c>&amp;</c> into <c>|</c> and back
/// whenever both operands are boolean. Bitwise expressions over integral operands are left to the
/// bitwise mutation operator.
/// </summary>
internal sealed class LogicalOperatorMutator : MutationOperatorBase
{
    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds =
    [
        SyntaxKind.LogicalAndExpression,
        SyntaxKind.LogicalOrExpression,
        SyntaxKind.BitwiseAndExpression,
        SyntaxKind.BitwiseOrExpression,
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="LogicalOperatorMutator" /> class.
    /// </summary>
    public LogicalOperatorMutator()
        : base("logical", MutationKind.LogicalOperator, _supportedSyntaxKinds) { }

    /// <inheritdoc />
    /// <remarks>
    /// No type test is needed: <see cref="MutationOperatorBase.CreateMutations" /> only forwards nodes
    /// whose kind is one of the <see cref="MutationOperatorBase.SupportedSyntaxKinds" />, and every kind
    /// this operator supports is a <see cref="BinaryExpressionSyntax" />.
    /// </remarks>
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        var binary = (BinaryExpressionSyntax)node;

        cancellationToken.ThrowIfCancellationRequested();

        var originalKind = binary.Kind();
        var isBitwise = originalKind is SyntaxKind.BitwiseAndExpression or SyntaxKind.BitwiseOrExpression;

        if (isBitwise && !HasBooleanOperands(binary, semanticModel, cancellationToken))
        {
            return [];
        }

        var targetKind = originalKind switch
        {
            SyntaxKind.LogicalAndExpression => SyntaxKind.LogicalOrExpression,
            SyntaxKind.LogicalOrExpression => SyntaxKind.LogicalAndExpression,
            SyntaxKind.BitwiseAndExpression => SyntaxKind.BitwiseOrExpression,
            _ => SyntaxKind.BitwiseAndExpression,
        };

        var replacement = SyntaxFactory.BinaryExpression(
            targetKind,
            binary.Left,
            SyntaxFactory.Token(
                binary.OperatorToken.LeadingTrivia,
                GetOperatorTokenKind(targetKind),
                binary.OperatorToken.TrailingTrivia
            ),
            binary.Right
        );

        return
        [
            CreateMutation(
                binary,
                replacement,
                $"{GetSlug(originalKind)}-to-{GetSlug(targetKind)}",
                $"{GetText(originalKind)} => {GetText(targetKind)}"
            ),
        ];
    }

    private static bool HasBooleanOperands(
        BinaryExpressionSyntax binary,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    ) =>
        IsBoolean(semanticModel.GetTypeInfo(binary.Left, cancellationToken).ConvertedType)
        && IsBoolean(semanticModel.GetTypeInfo(binary.Right, cancellationToken).ConvertedType);

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

    private static SyntaxKind GetOperatorTokenKind(SyntaxKind expressionKind) =>
        expressionKind switch
        {
            SyntaxKind.LogicalAndExpression => SyntaxKind.AmpersandAmpersandToken,
            SyntaxKind.LogicalOrExpression => SyntaxKind.BarBarToken,
            SyntaxKind.BitwiseAndExpression => SyntaxKind.AmpersandToken,
            _ => SyntaxKind.BarToken,
        };

    private static string GetSlug(SyntaxKind expressionKind) =>
        expressionKind switch
        {
            SyntaxKind.LogicalAndExpression => "conditional-and",
            SyntaxKind.LogicalOrExpression => "conditional-or",
            SyntaxKind.BitwiseAndExpression => "boolean-and",
            _ => "boolean-or",
        };

    private static string GetText(SyntaxKind expressionKind) =>
        expressionKind switch
        {
            SyntaxKind.LogicalAndExpression => "&&",
            SyntaxKind.LogicalOrExpression => "||",
            SyntaxKind.BitwiseAndExpression => "&",
            _ => "|",
        };
}
