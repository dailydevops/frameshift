namespace NetEvolve.FrameShift.Mutations.RegularExpressions;

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

/// <summary>
/// The one-time answer to the three questions every operator of the regular expression pattern family
/// asks about a candidate string literal: whether it is a pattern site at all, whether the pattern is a
/// legal regular expression under the site's options, and how the pattern tokenizes.
/// </summary>
/// <remarks>
/// <para>
/// This is the exact work <see cref="RegexPatternLocator.TryLocate" />,
/// <see cref="RegexPatternValidity.IsValid" /> and <see cref="RegexPatternTokenizer.TryTokenize" /> used
/// to perform independently inside every one of the eight operators of the family for the very same
/// literal. Factoring it out does not change what is computed, only how often: a caller that visits the
/// same node once per operator - as <see cref="Mutations.MutantGenerator" /> does - can now compute this
/// once and hand the result to every operator through <see cref="RegexPatternCache" />, instead of
/// re-locating, re-validating and re-tokenizing the same text eight times over.
/// </para>
/// <para>
/// A <see langword="null" /> result means the literal is not a viable pattern site for any of the three
/// reasons a site can be rejected: it is not a pattern at all, its options are not statically
/// determinable, or the pattern is malformed - either lexically, per the tokenizer, or semantically, per
/// the <see cref="Regex" /> constructor. Every one of those is a "no mutation from here" answer, and the
/// three checks are cheap to short-circuit in that order because each one is more expensive than the one
/// before it.
/// </para>
/// </remarks>
internal sealed class RegexPatternResolution
{
    private RegexPatternResolution(RegexPatternSite site, RegexOptions options, ImmutableArray<RegexToken> tokens)
    {
        Site = site;
        Options = options;
        Tokens = tokens;
    }

    /// <summary>
    /// Gets the located pattern site.
    /// </summary>
    public RegexPatternSite Site { get; }

    /// <summary>
    /// Gets the options the pattern was validated and tokenized with, which is <see cref="Site" />'s own
    /// options with <see cref="RegexOptions.Compiled" /> dropped.
    /// </summary>
    public RegexOptions Options { get; }

    /// <summary>
    /// Gets the tokens of <see cref="RegexPatternSite.Pattern" />, in order and covering it completely.
    /// </summary>
    public ImmutableArray<RegexToken> Tokens { get; }

    /// <summary>
    /// Locates, validates and tokenizes the pattern behind <paramref name="node" />, if it is one.
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
    public static RegexPatternResolution? TryResolve(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var site = RegexPatternLocator.TryLocate(node, semanticModel, cancellationToken);

        if (site is null || !site.AreOptionsKnown)
        {
            return null;
        }

        var options = ToParseOptions(site.Options!.Value);

        if (!RegexPatternValidity.IsValid(site.Pattern, options, out _))
        {
            return null;
        }

        if (!RegexPatternTokenizer.TryTokenize(site.Pattern, options, out var tokens, out _, out _))
        {
            return null;
        }

        return new RegexPatternResolution(site, options, tokens);
    }

    /// <summary>
    /// Turns the resolved options of a site into the options the validity check and the tokenizer are
    /// allowed to run with.
    /// </summary>
    /// <param name="options">The options the site resolved.</param>
    /// <returns>The options without <see cref="RegexOptions.Compiled" />.</returns>
    /// <remarks>
    /// Only <see cref="RegexOptions.Compiled" /> is dropped, and it has to be: it makes the constructor
    /// emit IL for an object that is thrown away immediately, which an analyzer running inside the
    /// compiler must not do. It cannot change the answer either, because it selects how the engine is
    /// built and not which patterns are legal. Every other flag is kept, including the ones that change
    /// the grammar and the ones that shrink it, because dropping those would answer a different
    /// question than the one the site asks.
    /// </remarks>
    private static RegexOptions ToParseOptions(RegexOptions options) => options & ~RegexOptions.Compiled;
}
