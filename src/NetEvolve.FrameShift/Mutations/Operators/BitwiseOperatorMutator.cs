namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Replaces a bitwise operator (<c>&amp;</c>, <c>|</c>, <c>^</c>) by each of the two remaining
/// bitwise operators and swaps the shift operators <c>&lt;&lt;</c> and <c>&gt;&gt;</c>.
/// </summary>
/// <remarks>
/// Only expressions whose operands are integral according to the semantic model are mutated. Boolean
/// operands are never mutated here, they belong to the logical operators. Shift expressions
/// additionally require both operands to be non-enum integral types.
/// </remarks>
internal sealed class BitwiseOperatorMutator : MutationOperatorBase
{
    private static readonly ImmutableArray<SyntaxKind> _bitwiseExpressionKinds =
    [
        SyntaxKind.BitwiseAndExpression,
        SyntaxKind.BitwiseOrExpression,
        SyntaxKind.ExclusiveOrExpression,
    ];

    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds =
    [
        SyntaxKind.BitwiseAndExpression,
        SyntaxKind.BitwiseOrExpression,
        SyntaxKind.ExclusiveOrExpression,
        SyntaxKind.LeftShiftExpression,
        SyntaxKind.RightShiftExpression,
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="BitwiseOperatorMutator" /> class.
    /// </summary>
    public BitwiseOperatorMutator()
        : base("bitwise", MutationKind.BitwiseOperator, _supportedSyntaxKinds) { }

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

        var leftType = semanticModel.GetTypeInfo(binary.Left, cancellationToken).ConvertedType;
        var rightType = semanticModel.GetTypeInfo(binary.Right, cancellationToken).ConvertedType;
        var isShift = binary.IsKind(SyntaxKind.LeftShiftExpression) || binary.IsKind(SyntaxKind.RightShiftExpression);

        if (!IsIntegral(leftType, allowEnum: !isShift) || !IsIntegral(rightType, allowEnum: !isShift))
        {
            return [];
        }

        return isShift ? CreateShiftMutation(binary) : CreateBitwiseMutations(binary, cancellationToken);
    }

    private IEnumerable<Mutation> CreateBitwiseMutations(
        BinaryExpressionSyntax binary,
        CancellationToken cancellationToken
    )
    {
        var originalKind = binary.Kind();
        foreach (var targetKind in _bitwiseExpressionKinds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (targetKind == originalKind)
            {
                continue;
            }

            yield return CreateMutation(
                binary,
                Rewrite(binary, targetKind, GetOperatorToken(targetKind)),
                $"{GetName(originalKind)}-to-{GetName(targetKind)}",
                $"{GetSymbol(originalKind)} => {GetSymbol(targetKind)}"
            );
        }
    }

    private IEnumerable<Mutation> CreateShiftMutation(BinaryExpressionSyntax binary)
    {
        var isLeftShift = binary.IsKind(SyntaxKind.LeftShiftExpression);
        var targetKind = isLeftShift ? SyntaxKind.RightShiftExpression : SyntaxKind.LeftShiftExpression;
        var suffix = isLeftShift ? "left-shift-to-right-shift" : "right-shift-to-left-shift";

        return
        [
            new Mutation(
                MutationKind.ShiftOperator,
                $"{Id}.{suffix}",
                isLeftShift ? "<< => >>" : ">> => <<",
                binary,
                Rewrite(binary, targetKind, GetOperatorToken(targetKind))
            ),
        ];
    }

    private static BinaryExpressionSyntax Rewrite(
        BinaryExpressionSyntax binary,
        SyntaxKind targetKind,
        SyntaxKind targetToken
    )
    {
        var operatorToken = SyntaxFactory.Token(
            binary.OperatorToken.LeadingTrivia,
            targetToken,
            binary.OperatorToken.TrailingTrivia
        );

        return SyntaxFactory
            .BinaryExpression(targetKind, binary.Left, operatorToken, binary.Right)
            .WithTriviaFrom(binary);
    }

    /// <summary>
    /// Maps a bitwise expression kind to the name its mutation identifier is built from.
    /// </summary>
    /// <param name="expressionKind">One of the <see cref="_bitwiseExpressionKinds" />, which are the only
    /// kinds both callers pass.</param>
    /// <returns>The name of the operator, so the last arm is the exclusive or.</returns>
    private static string GetName(SyntaxKind expressionKind) =>
        expressionKind switch
        {
            SyntaxKind.BitwiseAndExpression => "and",
            SyntaxKind.BitwiseOrExpression => "or",
            _ => "xor",
        };

    /// <summary>
    /// Maps a bitwise expression kind to the symbol its display name is built from.
    /// </summary>
    /// <param name="expressionKind">One of the <see cref="_bitwiseExpressionKinds" />, which are the only
    /// kinds both callers pass.</param>
    /// <returns>The symbol of the operator, so the last arm is the exclusive or.</returns>
    private static string GetSymbol(SyntaxKind expressionKind) =>
        expressionKind switch
        {
            SyntaxKind.BitwiseAndExpression => "&",
            SyntaxKind.BitwiseOrExpression => "|",
            _ => "^",
        };

    /// <summary>
    /// Maps an expression kind to the token the rewritten expression is written with.
    /// </summary>
    /// <param name="expressionKind">One of the <see cref="_bitwiseExpressionKinds" /> or one of the two
    /// shift kinds, which are the only kinds both callers pass.</param>
    /// <returns>The operator token, so the last arm is the right shift.</returns>
    private static SyntaxKind GetOperatorToken(SyntaxKind expressionKind) =>
        expressionKind switch
        {
            SyntaxKind.BitwiseAndExpression => SyntaxKind.AmpersandToken,
            SyntaxKind.BitwiseOrExpression => SyntaxKind.BarToken,
            SyntaxKind.ExclusiveOrExpression => SyntaxKind.CaretToken,
            SyntaxKind.LeftShiftExpression => SyntaxKind.LessThanLessThanToken,
            _ => SyntaxKind.GreaterThanGreaterThanToken,
        };

    private static bool IsIntegral(ITypeSymbol? type, bool allowEnum)
    {
        if (type is null)
        {
            return false;
        }

        var effective = type;
        if (
            effective is INamedTypeSymbol nullable
            && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            && nullable.TypeArguments.Length == 1
        )
        {
            effective = nullable.TypeArguments[0];
        }

        if (effective.TypeKind == TypeKind.Enum)
        {
            if (!allowEnum)
            {
                return false;
            }

            var underlyingType = (effective as INamedTypeSymbol)?.EnumUnderlyingType;
            if (underlyingType is null)
            {
                return false;
            }

            effective = underlyingType;
        }

        return effective.SpecialType
            is SpecialType.System_SByte
                or SpecialType.System_Byte
                or SpecialType.System_Int16
                or SpecialType.System_UInt16
                or SpecialType.System_Int32
                or SpecialType.System_UInt32
                or SpecialType.System_Int64
                or SpecialType.System_UInt64
                or SpecialType.System_Char;
    }
}
