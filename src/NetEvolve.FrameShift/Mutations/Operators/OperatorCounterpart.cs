namespace NetEvolve.FrameShift.Mutations.Operators;

using Microsoft.CodeAnalysis;

/// <summary>
/// The one answer every operator mutator asks before rewriting a call bound to a user defined operator:
/// whether the declaring type also provides the operator the mutant would bind to, so that the mutant
/// still compiles.
/// </summary>
internal static class OperatorCounterpart
{
    /// <summary>
    /// Determines whether <paramref name="userDefinedOperator" />'s containing type also declares a user
    /// defined operator named <paramref name="metadataName" /> with the same number of parameters.
    /// </summary>
    /// <param name="userDefinedOperator">The user defined operator the mutant would replace.</param>
    /// <param name="metadataName">The metadata name of the operator the mutant would bind to, such as
    /// <c>op_Addition</c> or <c>op_Increment</c>.</param>
    /// <returns><see langword="true" /> if the containing type declares a matching counterpart.</returns>
    internal static bool HasCounterpart(IMethodSymbol userDefinedOperator, string metadataName)
    {
        var containingType = userDefinedOperator.ContainingType;
        if (containingType is null)
        {
            return false;
        }

        foreach (var member in containingType.GetMembers(metadataName))
        {
            if (
                member is IMethodSymbol candidate
                && candidate.MethodKind == MethodKind.UserDefinedOperator
                && candidate.Parameters.Length == userDefinedOperator.Parameters.Length
            )
            {
                return true;
            }
        }

        return false;
    }
}
