namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Replaces a compound bitwise assignment (<c>&amp;=</c>, <c>|=</c>, <c>^=</c>) by each of the two
/// remaining compound bitwise assignments, and swaps the compound shift assignments <c>&lt;&lt;=</c>
/// and <c>&gt;&gt;=</c>.
/// </summary>
/// <remarks>
/// Only operands that are integral according to the semantic model are mutated, mirroring the guard
/// <see cref="BitwiseOperatorMutator" /> already applies to the non-compound operators. Shift
/// assignments additionally require both operands to be non-enum integral types.
/// </remarks>
internal sealed class BitwiseAssignmentMutator : MutationOperatorBase
{
    private static readonly ImmutableArray<SyntaxKind> _bitwiseAssignmentKinds =
    [
        SyntaxKind.AndAssignmentExpression,
        SyntaxKind.OrAssignmentExpression,
        SyntaxKind.ExclusiveOrAssignmentExpression,
    ];

    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds =
    [
        SyntaxKind.AndAssignmentExpression,
        SyntaxKind.OrAssignmentExpression,
        SyntaxKind.ExclusiveOrAssignmentExpression,
        SyntaxKind.LeftShiftAssignmentExpression,
        SyntaxKind.RightShiftAssignmentExpression,
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="BitwiseAssignmentMutator" /> class.
    /// </summary>
    public BitwiseAssignmentMutator()
        : base("bitwise-assignment", MutationKind.BitwiseOperator, _supportedSyntaxKinds) { }

    /// <inheritdoc />
    /// <remarks>
    /// No type test is needed: the base class only forwards a node whose kind is one of
    /// <see cref="MutationOperatorBase.SupportedSyntaxKinds" />, and every kind this operator supports is
    /// an assignment expression, so the cast cannot fail.
    /// </remarks>
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        var assignment = (AssignmentExpressionSyntax)node;
        var originalKind = assignment.Kind();
        var isShift =
            originalKind is SyntaxKind.LeftShiftAssignmentExpression or SyntaxKind.RightShiftAssignmentExpression;

        var leftType = semanticModel.GetTypeInfo(assignment.Left, cancellationToken).ConvertedType;
        var rightType = semanticModel.GetTypeInfo(assignment.Right, cancellationToken).ConvertedType;

        if (!IsIntegral(leftType, allowEnum: !isShift) || !IsIntegral(rightType, allowEnum: !isShift))
        {
            return [];
        }

        return isShift
            ? CreateShiftMutation(assignment, originalKind)
            : CreateBitwiseMutations(assignment, cancellationToken);
    }

    private IEnumerable<Mutation> CreateBitwiseMutations(
        AssignmentExpressionSyntax assignment,
        CancellationToken cancellationToken
    )
    {
        var originalKind = assignment.Kind();

        foreach (var targetKind in _bitwiseAssignmentKinds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (targetKind == originalKind)
            {
                continue;
            }

            yield return CreateMutation(
                assignment,
                Rewrite(assignment, targetKind, GetOperatorToken(targetKind)),
                $"{GetName(originalKind)}-to-{GetName(targetKind)}",
                $"{GetSymbol(originalKind)} => {GetSymbol(targetKind)}"
            );
        }
    }

    private static IEnumerable<Mutation> CreateShiftMutation(
        AssignmentExpressionSyntax assignment,
        SyntaxKind originalKind
    )
    {
        var isLeftShift = originalKind == SyntaxKind.LeftShiftAssignmentExpression;
        var targetKind = isLeftShift
            ? SyntaxKind.RightShiftAssignmentExpression
            : SyntaxKind.LeftShiftAssignmentExpression;
        var suffix = isLeftShift
            ? "left-shift-assign-to-right-shift-assign"
            : "right-shift-assign-to-left-shift-assign";

        return
        [
            new Mutation(
                MutationKind.ShiftOperator,
                $"bitwise-assignment.{suffix}",
                isLeftShift ? "<<= => >>=" : ">>= => <<=",
                assignment,
                Rewrite(assignment, targetKind, GetOperatorToken(targetKind))
            ),
        ];
    }

    private static AssignmentExpressionSyntax Rewrite(
        AssignmentExpressionSyntax assignment,
        SyntaxKind targetKind,
        SyntaxKind targetToken
    )
    {
        var operatorToken = SyntaxFactory.Token(
            assignment.OperatorToken.LeadingTrivia,
            targetToken,
            assignment.OperatorToken.TrailingTrivia
        );

        return SyntaxFactory
            .AssignmentExpression(targetKind, assignment.Left, operatorToken, assignment.Right)
            .WithTriviaFrom(assignment);
    }

    private static string GetName(SyntaxKind expressionKind) =>
        expressionKind switch
        {
            SyntaxKind.AndAssignmentExpression => "and-assign",
            SyntaxKind.OrAssignmentExpression => "or-assign",
            _ => "xor-assign",
        };

    private static string GetSymbol(SyntaxKind expressionKind) =>
        expressionKind switch
        {
            SyntaxKind.AndAssignmentExpression => "&=",
            SyntaxKind.OrAssignmentExpression => "|=",
            _ => "^=",
        };

    private static SyntaxKind GetOperatorToken(SyntaxKind expressionKind) =>
        expressionKind switch
        {
            SyntaxKind.AndAssignmentExpression => SyntaxKind.AmpersandEqualsToken,
            SyntaxKind.OrAssignmentExpression => SyntaxKind.BarEqualsToken,
            SyntaxKind.ExclusiveOrAssignmentExpression => SyntaxKind.CaretEqualsToken,
            SyntaxKind.LeftShiftAssignmentExpression => SyntaxKind.LessThanLessThanEqualsToken,
            _ => SyntaxKind.GreaterThanGreaterThanEqualsToken,
        };

    /// <summary>
    /// Decides whether a side of the assignment is integral enough to take part in a bitwise or shift
    /// assignment mutation. Mirrors <c>BitwiseOperatorMutator.IsIntegral</c>, so the two operators agree on
    /// which operands belong to this family.
    /// </summary>
    /// <param name="type">The converted type of the side, or <see langword="null" /> if unknown.</param>
    /// <param name="allowEnum">Whether an enum operand, resolved to its underlying type, is accepted.</param>
    /// <returns><see langword="true" /> if the side is integral.</returns>
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
