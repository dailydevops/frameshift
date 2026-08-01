namespace NetEvolve.FrameShift.Mutations.Operators;

using Microsoft.CodeAnalysis;

/// <summary>
/// The one answer the bitwise mutation family asks about an operand's type: whether it is integral
/// enough to take part in a bitwise or shift mutation.
/// </summary>
/// <remarks>
/// Shared by <see cref="BitwiseOperatorMutator" /> and <see cref="BitwiseAssignmentMutator" />, so the
/// two operators agree on which operands belong to this family.
/// </remarks>
internal static class IntegralTypeCheck
{
    /// <summary>
    /// Decides whether <paramref name="type" /> is integral enough to take part in a bitwise or shift
    /// mutation.
    /// </summary>
    /// <param name="type">The converted type of the operand, or <see langword="null" /> if unknown.</param>
    /// <param name="allowEnum">Whether an enum operand, resolved to its underlying type, is accepted.</param>
    /// <returns><see langword="true" /> if the operand is integral.</returns>
    internal static bool IsIntegral(ITypeSymbol? type, bool allowEnum)
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
