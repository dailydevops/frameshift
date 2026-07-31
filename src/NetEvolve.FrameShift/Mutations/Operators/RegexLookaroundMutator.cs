namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using NetEvolve.FrameShift.Mutations.RegularExpressions;

/// <summary>
/// Mutates a lookaround assertion of a regular expression pattern by negating it while keeping its
/// direction: a lookahead stays a lookahead and a lookbehind stays a lookbehind, only the polarity of the
/// assertion flips.
/// </summary>
/// <remarks>
/// <para>
/// A lookaround constrains what surrounds a position without consuming a single character of it, which
/// makes it invisible to a test that only inspects the substring the whole match consumed. A test suite
/// that never states which input must be rejected because of what follows or precedes the match passes
/// just as happily against <c>a(?=b)</c> as it does against <c>a(?!b)</c>, provided both are only ever fed
/// an input the two agree on. Negating the assertion is exactly the defect a developer produces by
/// forgetting the <c>!</c> or writing it where none belongs, and the surviving mutant names the missing
/// test: an input for which the original and the negated assertion disagree.
/// </para>
/// <para>
/// The mutation negates rather than removes the assertion, mirroring how <see cref="RegexAnchorMutator" />
/// negates <c>\b</c> and <c>\B</c> instead of deleting them. Removing a lookaround would delete the entire
/// group along with its content, which is a much larger rewrite than the one-token substitution this
/// operator performs, and would conflate two different mutants: "the assertion is missing" and "the
/// assertion is inverted". The negation is also the stronger mutant, because it flips the assertion for
/// every input the group is reached for, not only for the few inputs where dropping it happens to matter.
/// </para>
/// <para>
/// The four opening forms are paired by direction rather than by polarity: <c>(?=</c> (lookahead) swaps
/// with <c>(?!</c> (negative lookahead), and <c>(?&lt;=</c> (lookbehind) swaps with <c>(?&lt;!</c> (negative
/// lookbehind). A lookahead never becomes a lookbehind or vice versa, because that would change which
/// neighbourhood of the position is inspected - a different construct entirely - rather than merely
/// inverting the answer the same construct gives.
/// </para>
/// <para>
/// The rewrite touches only the opening token and never looks at the group's content or its closing
/// <see cref="RegexTokenKind.GroupClose" /> token: negating a lookaround changes what the assertion demands
/// of its content, not the content itself, and the <c>)</c> that ends the group is identical in every
/// polarity of the same direction. A rewrite is therefore a single token substitution, exactly like
/// <see cref="RegexAnchorMutator" />'s treatment of a word boundary.
/// </para>
/// <para>
/// No other opening form ever reaches this operator, because the tokenizer itself tells the four spellings
/// apart from everything else that starts with <c>(?</c>: an ordinary group such as <c>(?:</c>, an atomic
/// group <c>(?&gt;</c>, a named group <c>(?&lt;name&gt;</c> or <c>(?'name'</c>, a scoped inline-options
/// group such as <c>(?i:</c>, and a standalone inline-options construct such as <c>(?i)</c> are all
/// tokenized as <see cref="RegexTokenKind.GroupOpen" /> or <see cref="RegexTokenKind.InlineOptions" />
/// rather than <see cref="RegexTokenKind.Lookaround" />. Answering only for
/// <see cref="RegexTokenKind.Lookaround" /> therefore keeps every one of those constructs out without this
/// operator inspecting a single surrounding character.
/// </para>
/// <para>
/// Negating a lookaround can never turn a legal pattern into an illegal one: the replacement is always one
/// of the same four fixed strings the tokenizer already accepted at that exact position, so the rest of
/// the pattern's grammar - the group's content, its nesting, everything after the closing parenthesis - is
/// left completely untouched. <see cref="RegexPatternMutatorBase" /> still validates every rewrite, because
/// that is a guarantee the base class makes uniformly across the whole family rather than one this operator
/// needs to earn on its own.
/// </para>
/// <para>
/// The rewrites follow the token order of the pattern, one rewrite per lookaround occurrence. A pattern may
/// contain several lookarounds, and every occurrence is a mutation point of its own, so two occurrences of
/// the same form in one pattern yield two mutations that carry the same operator suffix at the same
/// literal - they are different mutants of the same pattern, distinguished by the position the mutation
/// applies to.
/// </para>
/// </remarks>
internal sealed class RegexLookaroundMutator : RegexPatternMutatorBase
{
    /// <summary>
    /// The lookahead opening, negated by <see cref="NegativeLookahead" />.
    /// </summary>
    private const string Lookahead = "(?=";

    /// <summary>
    /// The negative lookahead opening, negated by <see cref="Lookahead" />.
    /// </summary>
    private const string NegativeLookahead = "(?!";

    /// <summary>
    /// The lookbehind opening, negated by <see cref="NegativeLookbehind" />.
    /// </summary>
    private const string Lookbehind = "(?<=";

    /// <summary>
    /// The negative lookbehind opening, negated by <see cref="Lookbehind" />.
    /// </summary>
    private const string NegativeLookbehind = "(?<!";

    /// <summary>
    /// Initializes a new instance of the <see cref="RegexLookaroundMutator" /> class.
    /// </summary>
    public RegexLookaroundMutator()
        : base("regex.lookaround", MutationKind.RegexLookaround) { }

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

            if (token.Kind != RegexTokenKind.Lookaround)
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
    /// Creates the one rewrite the lookaround <paramref name="token" /> offers.
    /// </summary>
    /// <param name="pattern">The pattern the token belongs to.</param>
    /// <param name="token">The lookaround opening token to mutate.</param>
    /// <returns>
    /// The rewrite, or <see langword="null" /> for a token text the tokenizer never produces for this kind.
    /// </returns>
    private static RegexPatternRewrite? TryCreateRewrite(string pattern, RegexToken token)
    {
        var (replacement, suffix) = GetNegation(token.Text);

        if (suffix is null)
        {
            return null;
        }

        return new RegexPatternRewrite(Replace(pattern, token, replacement!), suffix);
    }

    /// <summary>
    /// Selects the negated opening and the identifier suffix for a lookaround opening.
    /// </summary>
    /// <param name="opening">The exact text of the lookaround token.</param>
    /// <returns>
    /// The replacement text and the suffix, both <see langword="null" /> for an opening the tokenizer never
    /// produces as <see cref="RegexTokenKind.Lookaround" />. The suffix names both the direction and the
    /// polarity change, so that a report stays readable without the pattern next to it.
    /// </returns>
    private static (string? Replacement, string? Suffix) GetNegation(string opening) =>
        opening switch
        {
            Lookahead => (NegativeLookahead, "lookahead-to-negative-lookahead"),
            NegativeLookahead => (Lookahead, "negative-lookahead-to-lookahead"),
            Lookbehind => (NegativeLookbehind, "lookbehind-to-negative-lookbehind"),
            NegativeLookbehind => (Lookbehind, "negative-lookbehind-to-lookbehind"),

            // The tokenizer only ever produces one of the four forms above for this kind, so an unknown
            // text cannot occur; it is handled defensively rather than assumed away.
            _ => (null, null),
        };
}
