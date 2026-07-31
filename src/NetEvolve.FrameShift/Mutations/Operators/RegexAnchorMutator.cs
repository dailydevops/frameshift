namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using NetEvolve.FrameShift.Mutations.RegularExpressions;

/// <summary>
/// Mutates the zero width position assertions of a regular expression pattern by removing an anchor that
/// pins a position in the input and by flipping a word boundary assertion into its negation.
/// </summary>
/// <remarks>
/// <para>
/// An anchor is the one construct of a pattern that constrains where a match may sit without consuming a
/// single character, which makes it invisible to every test that only asserts that some substring was
/// found. A test suite that never states which input must be rejected therefore passes just as happily
/// against <c>\d+</c> as it does against <c>^\d+$</c>. Removing the anchor is exactly the defect a
/// developer produces by forgetting it, and the surviving mutant names the missing test: an input that
/// carries the expected shape somewhere in the middle rather than at the demanded position.
/// </para>
/// <para>
/// The removals are offered for <c>^</c>, <c>$</c>, <c>\A</c>, <c>\z</c> and <c>\Z</c>. All five describe
/// a position inside the input, so dropping one widens the set of accepted inputs and nothing else. The
/// three string anchors are kept apart from the two line anchors, because <c>^</c> and <c>$</c> change
/// meaning under <c>RegexOptions.Multiline</c> while <c>\A</c>, <c>\z</c> and <c>\Z</c> do not, and a
/// surviving mutant of either kind points at a different missing test.
/// </para>
/// <para>
/// The word boundary assertions are not removed but negated: <c>\b</c> becomes <c>\B</c> and <c>\B</c>
/// becomes <c>\b</c>. Removing them would usually widen the pattern in a way a test observes only for the
/// few inputs that sit right at a boundary, whereas the negation inverts the assertion for every input
/// and is thus the stronger mutant. It is also the defect that actually occurs, the two constructs being
/// one keystroke apart.
/// </para>
/// <para>
/// <c>\G</c> is deliberately left alone and produces no rewrite at all. It pins the start of the match to
/// the position the previous match ended at rather than describing a position inside the input, so
/// removing it changes where matching may begin instead of which input matches. A mutant of it therefore
/// belongs to the iteration behaviour of a scanning loop and not to the pattern family this operator
/// covers, and it is out of scope for this package.
/// </para>
/// <para>
/// The operator answers only for tokens of kind <see cref="RegexTokenKind.Anchor" />, which is what keeps
/// character classes out without a single look at the surrounding characters. Inside a class <c>\b</c> is
/// the backspace character and arrives as <see cref="RegexTokenKind.Escape" />, so <c>[\b]</c> is never
/// touched, and the <c>^</c> that negates a class is part of a
/// <see cref="RegexTokenKind.CharacterClassOpen" /> token, so <c>[^a]</c> is never touched either. The
/// same holds for a <c>$</c> that is merely a class member and arrives as
/// <see cref="RegexTokenKind.CharacterClassContent" />.
/// </para>
/// <para>
/// Removing an anchor can leave a pattern the engine rejects, for instance when a quantifier loses the
/// atom it repeated, and it can leave an empty pattern. Neither case is guarded here: the empty pattern
/// is a legal regular expression and a legitimate mutant, and the rejected one is discarded by
/// <see cref="RegexPatternMutatorBase" /> together with every other rewrite that does not parse.
/// </para>
/// <para>
/// The rewrites follow the token order of the pattern, one rewrite per anchor token. An anchor may occur
/// several times, and every occurrence is a mutation point of its own, so two occurrences of the same
/// anchor in one pattern yield two mutations that carry the same operator suffix at the same literal.
/// That is correct - they are different mutants of the same pattern, distinguished by the position the
/// mutation applies to.
/// </para>
/// </remarks>
internal sealed class RegexAnchorMutator : RegexPatternMutatorBase
{
    /// <summary>
    /// The word boundary assertion, negated by <see cref="NonWordBoundary" />.
    /// </summary>
    private const string WordBoundary = @"\b";

    /// <summary>
    /// The negated word boundary assertion, negated by <see cref="WordBoundary" />.
    /// </summary>
    private const string NonWordBoundary = @"\B";

    /// <summary>
    /// Initializes a new instance of the <see cref="RegexAnchorMutator" /> class.
    /// </summary>
    public RegexAnchorMutator()
        : base("regex.anchor", MutationKind.RegexAnchor) { }

    /// <inheritdoc />
    protected override IEnumerable<RegexPatternRewrite> CreateRewrites(
        string pattern,
        ImmutableArray<RegexToken> tokens,
        CancellationToken cancellationToken
    )
    {
        foreach (var token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (token.Kind != RegexTokenKind.Anchor)
            {
                continue;
            }

            var rewrite = TryCreateRewrite(pattern, token);

            if (rewrite is not null)
            {
                yield return rewrite;
            }
        }
    }

    /// <summary>
    /// Creates the one rewrite the anchor <paramref name="token" /> offers.
    /// </summary>
    /// <param name="pattern">The pattern the token belongs to.</param>
    /// <param name="token">The anchor token to mutate.</param>
    /// <returns>
    /// The rewrite, or <see langword="null" /> for an anchor the operator deliberately leaves alone.
    /// </returns>
    private static RegexPatternRewrite? TryCreateRewrite(string pattern, RegexToken token)
    {
        var suffix = GetOperatorSuffix(token.Text);

        if (suffix is null)
        {
            return null;
        }

        return new RegexPatternRewrite(Replace(pattern, token, GetReplacementText(token.Text)), suffix);
    }

    /// <summary>
    /// Selects the identifier suffix the mutation of an anchor carries.
    /// </summary>
    /// <param name="anchor">The exact text of the anchor token.</param>
    /// <returns>
    /// The suffix, or <see langword="null" /> when the anchor is out of scope. The names describe the
    /// assertion rather than its spelling, so that a report stays readable without the pattern next to it.
    /// </returns>
    private static string? GetOperatorSuffix(string anchor) =>
        anchor switch
        {
            "^" => "remove-caret",
            "$" => "remove-dollar",
            @"\A" => "remove-string-start",
            @"\z" => "remove-string-end",
            @"\Z" => "remove-string-end-before-final-newline",
            WordBoundary => "word-boundary-to-non-word-boundary",
            NonWordBoundary => "non-word-boundary-to-word-boundary",

            // `\G` pins the start of the match and is out of scope, and an unknown anchor text cannot be
            // reasoned about at all, so both are skipped.
            _ => null,
        };

    /// <summary>
    /// Selects the text taking the place of an anchor: the opposite assertion for a word boundary and
    /// nothing at all for every anchor that is removed.
    /// </summary>
    /// <param name="anchor">The exact text of the anchor token.</param>
    /// <returns>The replacement text, which is empty for a removal.</returns>
    private static string GetReplacementText(string anchor)
    {
        if (string.Equals(anchor, WordBoundary, StringComparison.Ordinal))
        {
            return NonWordBoundary;
        }

        if (string.Equals(anchor, NonWordBoundary, StringComparison.Ordinal))
        {
            return WordBoundary;
        }

        return string.Empty;
    }
}
