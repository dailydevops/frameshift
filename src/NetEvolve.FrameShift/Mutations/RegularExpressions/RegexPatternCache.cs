namespace NetEvolve.FrameShift.Mutations.RegularExpressions;

using Microsoft.CodeAnalysis;

/// <summary>
/// Memoizes <see cref="RegexPatternResolution.TryResolve" /> per candidate pattern literal, so that a
/// syntax node visited by every operator of the regular expression pattern family is located, validated
/// and tokenized once instead of once per operator.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Mutations.MutantGenerator" /> asks all eight operators of the family about the very same
/// string literal, one after another, because that is how <see cref="MutationOperatorRegistry" /> groups
/// operators by the syntax kind they claim. Without this cache each of the eight would independently
/// call <see cref="RegexPatternLocator.TryLocate" />, construct a throwaway <see cref="System.Text.RegularExpressions.Regex" />
/// through <see cref="RegexPatternValidity.IsValid" /> and tokenize the pattern through
/// <see cref="RegexPatternTokenizer.TryTokenize" />, for a compilation with many patterns doing that work
/// up to eight times over for the same text.
/// </para>
/// <para>
/// A cache instance is scoped to a single walk of one syntax tree - one call to
/// <see cref="Mutations.MutantGenerator" />'s tree walker - and is never shared beyond it, which keeps
/// its lifetime and its memory bounded by the number of candidate literals in that one tree. The type is
/// therefore deliberately not thread-safe: nothing in the walk that owns an instance visits a node from
/// more than one thread at a time, and sharing an instance across concurrent walks would require
/// synchronisation this cache does not provide.
/// </para>
/// </remarks>
internal sealed class RegexPatternCache
{
    private readonly Dictionary<SyntaxNode, RegexPatternResolution?> _resolutions = [];

    /// <summary>
    /// Returns the cached resolution of <paramref name="node" />, computing and caching it first if this
    /// is the first time it is asked for.
    /// </summary>
    /// <param name="node">The candidate pattern literal.</param>
    /// <param name="semanticModel">The semantic model of the tree <paramref name="node" /> belongs to.</param>
    /// <param name="cancellationToken">A token to observe while resolving.</param>
    /// <returns>
    /// The resolution, or <see langword="null" /> when <paramref name="node" /> is no viable pattern site.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="node" /> or <paramref name="semanticModel" /> is <see langword="null" />.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    public RegexPatternResolution? GetOrResolve(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (node is null)
        {
            throw new ArgumentNullException(nameof(node));
        }

        if (semanticModel is null)
        {
            throw new ArgumentNullException(nameof(semanticModel));
        }

        if (_resolutions.TryGetValue(node, out var cached))
        {
            return cached;
        }

        var resolution = RegexPatternResolution.TryResolve(node, semanticModel, cancellationToken);
        _resolutions[node] = resolution;

        return resolution;
    }
}
