namespace NetEvolve.FrameShift.Mutations;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// The single place where all known <see cref="IMutationOperator" /> implementations are registered
/// and indexed by the syntax kinds they support.
/// </summary>
internal static class MutationOperatorRegistry
{
    private static readonly ImmutableArray<IMutationOperator> _all = CreateOperators().ToImmutableArray();

    private static readonly ImmutableDictionary<SyntaxKind, ImmutableArray<IMutationOperator>> _bySyntaxKind =
        BuildLookup(_all);

    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds = _bySyntaxKind
        .Keys.OrderBy(kind => (int)kind)
        .ToImmutableArray();

    /// <summary>
    /// Gets all registered mutation operators.
    /// </summary>
    public static ImmutableArray<IMutationOperator> All => _all;

    /// <summary>
    /// Gets all syntax kinds at least one registered operator supports.
    /// </summary>
    public static ImmutableArray<SyntaxKind> SupportedSyntaxKinds => _supportedSyntaxKinds;

    /// <summary>
    /// Gets all registered operators supporting <paramref name="kind" />.
    /// </summary>
    /// <param name="kind">The syntax kind to look up.</param>
    /// <returns>The matching operators, or an empty array if no operator supports the kind.</returns>
    public static ImmutableArray<IMutationOperator> ForSyntaxKind(SyntaxKind kind) =>
        _bySyntaxKind.TryGetValue(kind, out var operators) ? operators : [];

    private static IEnumerable<IMutationOperator> CreateOperators()
    {
        // >>> operator registrations
        yield return new Operators.ArithmeticAssignmentMutator();
        yield return new Operators.ArithmeticOperatorMutator();
        yield return new Operators.BitwiseAssignmentMutator();
        yield return new Operators.BitwiseOperatorMutator();
        yield return new Operators.BooleanLiteralMutator();
        yield return new Operators.CaseConversionMutator();
        yield return new Operators.ConditionalExpressionMutator();
        yield return new Operators.CultureInfoMutator();
        yield return new Operators.EqualityOperatorMutator();
        yield return new Operators.FormatProviderArgumentMutator();
        yield return new Operators.IncrementDecrementMutator();
        yield return new Operators.LogicalNegationMutator();
        yield return new Operators.LogicalOperatorMutator();
        yield return new Operators.NullableBooleanLiteralMutator();
        yield return new Operators.NullCoalescingMutator();
        yield return new Operators.NumericLiteralMutator();
        yield return new Operators.RegexAlternationMutator();
        yield return new Operators.RegexAnchorMutator();
        yield return new Operators.RegexBackreferenceMutator();
        yield return new Operators.RegexCharacterClassMutator();
        yield return new Operators.RegexEscapeMutator();
        yield return new Operators.RegexGroupMutator();
        yield return new Operators.RegexLookaroundMutator();
        yield return new Operators.RegexOptionsMutator();
        yield return new Operators.RegexQuantifierMutator();
        yield return new Operators.RelationalOperatorMutator();
        yield return new Operators.StringComparerMutator();
        yield return new Operators.StringComparisonMutator();
        yield return new Operators.StringLiteralMutator();
        yield return new Operators.UnaryOperatorMutator();
        // <<< operator registrations
    }

    private static ImmutableDictionary<SyntaxKind, ImmutableArray<IMutationOperator>> BuildLookup(
        ImmutableArray<IMutationOperator> operators
    )
    {
        var builder = ImmutableDictionary.CreateBuilder<SyntaxKind, ImmutableArray<IMutationOperator>>();
        var grouped = operators
            .SelectMany(op => op.SupportedSyntaxKinds.Select(kind => (Kind: kind, Operator: op)))
            .GroupBy(entry => entry.Kind);

        foreach (var group in grouped)
        {
            builder[group.Key] = group.Select(entry => entry.Operator).ToImmutableArray();
        }

        return builder.ToImmutable();
    }
}
