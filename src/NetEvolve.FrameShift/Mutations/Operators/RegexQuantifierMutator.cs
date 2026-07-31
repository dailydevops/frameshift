namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using System.Globalization;
using NetEvolve.FrameShift.Mutations.RegularExpressions;

/// <summary>
/// Mutates the repetition suffixes of a regular expression pattern: it swaps <c>*</c> and <c>+</c>,
/// removes an optional <c>?</c>, shifts the bounds of a <c>{n}</c>, <c>{n,}</c> or <c>{n,m}</c>
/// quantifier by one, and toggles the laziness of every quantifier that has a choice to make.
/// </summary>
/// <remarks>
/// <para>
/// The quantifier is where a pattern states how often something may repeat, and it is the construct a
/// test suite most often pins down only halfway: a suite that exercises <c>a+</c> with one <c>a</c> and
/// with three never notices that <c>a*</c> would have done as well, and a suite that exercises
/// <c>\d{4}</c> with a four digit year never notices that <c>\d{3}</c> or <c>\d{5}</c> would have been
/// accepted too. Every rewrite of this operator therefore moves exactly one boundary of the accepted
/// language by the smallest possible step, which is what makes a surviving mutant a precise statement
/// about a missing test case rather than a vague one about a weak pattern.
/// </para>
/// <para>
/// The mutations follow the shape of the quantifier. <c>*</c> becomes <c>+</c> and thereby demands the
/// repetition a test with zero occurrences has to detect, while <c>+</c> becomes <c>*</c> and thereby
/// permits an empty run. An optional <c>?</c> is removed altogether, which makes the quantified atom
/// mandatory, and is the only rewrite that deletes the token instead of replacing it. A counted
/// quantifier has each of its bounds decreased and increased by one, so that both edges of the accepted
/// count are probed; the lower bound is only decreased when it is at least one, because a negative
/// bound is not a quantifier at all.
/// </para>
/// <para>
/// A shape mutation always keeps the laziness of the original token: <c>*?</c> becomes <c>+?</c> and
/// <c>{2,3}?</c> becomes <c>{1,3}?</c>. Mixing the two dimensions in a single mutant would produce a
/// mutant that two unrelated test cases could kill, which is precisely the ambiguity a mutation score is
/// supposed to resolve. The laziness toggle is therefore a rewrite of its own and always the last one
/// produced for a token: a greedy core gains the <c>?</c> marker, a lazy core loses it. The one
/// exception is the exact <c>{n}</c> core. An exact repetition count leaves the engine no choice about
/// how many times to repeat, so the marker cannot change a single match, and the mutant would be
/// equivalent to the original by construction - such a mutant can never be killed and would only depress
/// the score. It is therefore not offered in either direction.
/// </para>
/// <para>
/// A bound is read from the token text and parsed with <see cref="NumberStyles.None" /> and
/// <see cref="CultureInfo.InvariantCulture" />, so neither a sign, a group separator nor a culture
/// specific digit is ever accepted. A bound that does not parse - which in practice means it overflows
/// <see cref="int" />, exactly as the parser of the regular expression engine itself rejects it -
/// produces no bound mutation for that token; the laziness toggle of the same token is still produced. A
/// bound already at <see cref="int.MaxValue" /> is not increased, because there is no larger bound to
/// write down. A bound spelled with leading zeros is re-emitted without them, as in <c>{007}</c>
/// becoming <c>{6}</c>; that is a harmless normalization of a token that is being changed anyway and
/// never alters what the mutant matches.
/// </para>
/// <para>
/// The operator offers a rewrite whose bounds cross, such as <c>{3,3}</c> becoming <c>{4,3}</c> or
/// <c>{2,2}</c> becoming <c>{2,1}</c>, without any ordering check of its own. Such a pattern is not
/// legal, and the base class discards every rewrite that is not a valid pattern under the options of the
/// site. Repeating that check here would duplicate a rule the family already owns and would risk
/// disagreeing with the engine about where the boundary lies.
/// </para>
/// <para>
/// Only tokens of kind <see cref="RegexTokenKind.Quantifier" /> are answered for, and the kind alone
/// decides. A <c>*</c>, <c>+</c> or <c>?</c> inside a character class is a
/// <see cref="RegexTokenKind.CharacterClassContent" /> member matching itself and is therefore never
/// touched, and a <c>{</c> that starts no quantifier - as in <c>a{x}</c> or <c>a{</c> - is a
/// <see cref="RegexTokenKind.Literal" /> and never reaches this operator either. Neither case needs a
/// look at the characters around the token, and none is taken.
/// </para>
/// <para>
/// The rewrites are produced token by token in pattern order and, inside a token, in the fixed order
/// documented above, so the mutants of a given pattern are always the same and always in the same
/// sequence.
/// </para>
/// </remarks>
internal sealed class RegexQuantifierMutator : RegexPatternMutatorBase
{
    /// <summary>
    /// The lazy marker a quantifier token may end with, which is also the greedy optional core - the
    /// reason core and marker are split by length and never by a test on the last character.
    /// </summary>
    private const string LazyMarker = "?";

    /// <summary>
    /// The marker of a greedy quantifier, which carries no marker at all.
    /// </summary>
    private const string GreedyMarker = "";

    /// <summary>
    /// Initializes a new instance of the <see cref="RegexQuantifierMutator" /> class.
    /// </summary>
    public RegexQuantifierMutator()
        : base("regex.quantifier", MutationKind.RegexQuantifier) { }

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

            if (token.Kind != RegexTokenKind.Quantifier)
            {
                continue;
            }

            var core = SplitCore(token.Text, out var marker);

            foreach (var rewrite in CreateShapeRewrites(pattern, token, core, marker))
            {
                yield return rewrite;
            }

            var laziness = CreateLazinessRewrite(pattern, token, core, marker);

            if (laziness is not null)
            {
                yield return laziness;
            }
        }
    }

    /// <summary>
    /// Splits a quantifier token into its core and its lazy marker.
    /// </summary>
    /// <param name="text">The text of the quantifier token, e.g. <c>+?</c> or <c>{2,3}</c>.</param>
    /// <param name="marker">
    /// The lazy marker the token carries, either <see cref="LazyMarker" /> or <see cref="GreedyMarker" />.
    /// </param>
    /// <returns>The core of the quantifier, without the marker.</returns>
    /// <remarks>
    /// The split is decided by length and by the shape of the core, never by asking whether the text ends
    /// with a <c>?</c>: the text <c>?</c> is the greedy optional core, and only the second <c>?</c> of
    /// <c>??</c> is a marker. A one character core is <c>*</c>, <c>+</c> or <c>?</c>, so anything behind
    /// the first character is the marker; a counted core ends with <c>}</c>, so a trailing <c>?</c> behind
    /// it is the marker.
    /// </remarks>
    private static string SplitCore(string text, out string marker)
    {
        if (text[0] is '*' or '+' or '?')
        {
            marker = text.Length > 1 ? LazyMarker : GreedyMarker;

            return text.Substring(0, 1);
        }

        if (text[text.Length - 1] is '?')
        {
            marker = LazyMarker;

            return text.Substring(0, text.Length - 1);
        }

        marker = GreedyMarker;

        return text;
    }

    /// <summary>
    /// Produces the rewrites that change what the quantifier counts, keeping its laziness.
    /// </summary>
    /// <param name="pattern">The pattern being rewritten.</param>
    /// <param name="token">The quantifier token being rewritten.</param>
    /// <param name="core">The core of the quantifier.</param>
    /// <param name="marker">The lazy marker the token carries.</param>
    /// <returns>The shape rewrites of the token, in the documented order.</returns>
    private static IEnumerable<RegexPatternRewrite> CreateShapeRewrites(
        string pattern,
        RegexToken token,
        string core,
        string marker
    )
    {
        switch (core[0])
        {
            case '*':
                yield return new RegexPatternRewrite(Replace(pattern, token, "+" + marker), "star-to-plus");

                break;
            case '+':
                yield return new RegexPatternRewrite(Replace(pattern, token, "*" + marker), "plus-to-star");

                break;
            case '?':
                // The optional quantifier has no counterpart to swap it with, so making the atom
                // mandatory means dropping core and marker together: '??' loses both characters.
                yield return new RegexPatternRewrite(Replace(pattern, token, string.Empty), "remove-optional");

                break;
            default:
                foreach (var rewrite in CreateBoundRewrites(pattern, token, core, marker))
                {
                    yield return rewrite;
                }

                break;
        }
    }

    /// <summary>
    /// Produces the rewrites that shift the bounds of a counted quantifier by one.
    /// </summary>
    /// <param name="pattern">The pattern being rewritten.</param>
    /// <param name="token">The quantifier token being rewritten.</param>
    /// <param name="core">The counted core, e.g. <c>{2,3}</c>.</param>
    /// <param name="marker">The lazy marker the token carries.</param>
    /// <returns>
    /// The bound rewrites of the token, in the documented order, or none when a bound does not parse.
    /// </returns>
    private static IEnumerable<RegexPatternRewrite> CreateBoundRewrites(
        string pattern,
        RegexToken token,
        string core,
        string marker
    )
    {
        // The core is '{', the bounds and '}', so the bounds are the text between the braces. They come
        // from the token itself, which is why no part of the pattern is ever searched for a digit.
        var bounds = core.Substring(1, core.Length - 2);
        var separator = bounds.IndexOf(',');
        var statesMaximum = separator >= 0;
        var minimumText = statesMaximum ? bounds.Substring(0, separator) : bounds;

        if (!TryParseBound(minimumText, out var minimum))
        {
            return [];
        }

        if (!statesMaximum)
        {
            return CreateExactRewrites(pattern, token, minimum, marker);
        }

        var maximumText = bounds.Substring(separator + 1);

        if (maximumText.Length == 0)
        {
            // The open ended form keeps its separator and states no upper bound behind it, which is what
            // the empty upper bound text stands for.
            return CreateMinimumRewrites(pattern, token, minimum, string.Empty, marker);
        }

        if (!TryParseBound(maximumText, out var maximum))
        {
            return [];
        }

        return CreateRangeRewrites(pattern, token, minimum, maximum, marker);
    }

    /// <summary>
    /// Produces the two rewrites of an exact <c>{n}</c> quantifier.
    /// </summary>
    /// <param name="pattern">The pattern being rewritten.</param>
    /// <param name="token">The quantifier token being rewritten.</param>
    /// <param name="count">The exact repetition count.</param>
    /// <param name="marker">The lazy marker the token carries.</param>
    /// <returns>The decrease of the count followed by its increase.</returns>
    private static IEnumerable<RegexPatternRewrite> CreateExactRewrites(
        string pattern,
        RegexToken token,
        int count,
        string marker
    )
    {
        if (count >= 1)
        {
            yield return CreateCountedRewrite(pattern, token, count - 1, null, marker, "decrease-exact");
        }

        if (count != int.MaxValue)
        {
            yield return CreateCountedRewrite(pattern, token, count + 1, null, marker, "increase-exact");
        }
    }

    /// <summary>
    /// Produces the two rewrites that shift the lower bound of a quantifier that states a separator.
    /// </summary>
    /// <param name="pattern">The pattern being rewritten.</param>
    /// <param name="token">The quantifier token being rewritten.</param>
    /// <param name="minimum">The lower bound of the quantifier.</param>
    /// <param name="maximum">
    /// The upper bound as the rewrites keep it, empty for the open ended <c>{n,}</c> form.
    /// </param>
    /// <param name="marker">The lazy marker the token carries.</param>
    /// <returns>The decrease of the lower bound followed by its increase.</returns>
    private static IEnumerable<RegexPatternRewrite> CreateMinimumRewrites(
        string pattern,
        RegexToken token,
        int minimum,
        string maximum,
        string marker
    )
    {
        if (minimum >= 1)
        {
            yield return CreateCountedRewrite(pattern, token, minimum - 1, maximum, marker, "decrease-minimum");
        }

        if (minimum != int.MaxValue)
        {
            yield return CreateCountedRewrite(pattern, token, minimum + 1, maximum, marker, "increase-minimum");
        }
    }

    /// <summary>
    /// Produces the four rewrites of a <c>{n,m}</c> quantifier: both shifts of the lower bound, then both
    /// shifts of the upper one.
    /// </summary>
    /// <param name="pattern">The pattern being rewritten.</param>
    /// <param name="token">The quantifier token being rewritten.</param>
    /// <param name="minimum">The lower bound of the quantifier.</param>
    /// <param name="maximum">The upper bound of the quantifier.</param>
    /// <param name="marker">The lazy marker the token carries.</param>
    /// <returns>The bound rewrites of the token, in the documented order.</returns>
    private static IEnumerable<RegexPatternRewrite> CreateRangeRewrites(
        string pattern,
        RegexToken token,
        int minimum,
        int maximum,
        string marker
    )
    {
        foreach (var rewrite in CreateMinimumRewrites(pattern, token, minimum, FormatBound(maximum), marker))
        {
            yield return rewrite;
        }

        if (maximum >= 1)
        {
            yield return CreateCountedRewrite(
                pattern,
                token,
                minimum,
                FormatBound(maximum - 1),
                marker,
                "decrease-maximum"
            );
        }

        if (maximum != int.MaxValue)
        {
            yield return CreateCountedRewrite(
                pattern,
                token,
                minimum,
                FormatBound(maximum + 1),
                marker,
                "increase-maximum"
            );
        }
    }

    /// <summary>
    /// Creates one rewrite replacing the quantifier token by a counted quantifier with the given bounds.
    /// </summary>
    /// <param name="pattern">The pattern being rewritten.</param>
    /// <param name="token">The quantifier token being rewritten.</param>
    /// <param name="minimum">The lower bound of the rewritten quantifier.</param>
    /// <param name="maximum">
    /// The upper bound as it is spelled out, <see langword="null" /> for the exact <c>{n}</c> form and
    /// empty for the open ended <c>{n,}</c> form.
    /// </param>
    /// <param name="marker">The lazy marker the token carries and the rewrite keeps.</param>
    /// <param name="operatorSuffix">The suffix identifying the mutation.</param>
    /// <returns>The created rewrite.</returns>
    private static RegexPatternRewrite CreateCountedRewrite(
        string pattern,
        RegexToken token,
        int minimum,
        string? maximum,
        string marker,
        string operatorSuffix
    ) => new(Replace(pattern, token, FormatCounted(minimum, maximum) + marker), operatorSuffix);

    /// <summary>
    /// Spells out a counted quantifier core.
    /// </summary>
    /// <param name="minimum">The lower bound.</param>
    /// <param name="maximum">
    /// The upper bound as it is spelled out, <see langword="null" /> for the exact form and empty for the
    /// open ended one.
    /// </param>
    /// <returns>The core, e.g. <c>{2}</c>, <c>{2,}</c> or <c>{2,3}</c>.</returns>
    /// <remarks>
    /// The exact form states no separator at all, while the open ended form states the separator and
    /// nothing behind it. That difference cannot be read off the bounds, which is why the absent upper
    /// bound and the empty one are told apart.
    /// </remarks>
    private static string FormatCounted(int minimum, string? maximum)
    {
        var lower = FormatBound(minimum);

        return maximum is null ? "{" + lower + "}" : "{" + lower + "," + maximum + "}";
    }

    /// <summary>
    /// Spells out one bound of a counted quantifier.
    /// </summary>
    /// <param name="bound">The bound to format.</param>
    /// <returns>The decimal digits of the bound, without a sign and without a group separator.</returns>
    private static string FormatBound(int bound) => bound.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Creates the rewrite that toggles the laziness of the quantifier, if it has one to toggle.
    /// </summary>
    /// <param name="pattern">The pattern being rewritten.</param>
    /// <param name="token">The quantifier token being rewritten.</param>
    /// <param name="core">The core of the quantifier.</param>
    /// <param name="marker">The lazy marker the token carries.</param>
    /// <returns>
    /// The rewrite, or <see langword="null" /> for an exact <c>{n}</c> core, whose repetition count leaves
    /// the engine no choice and whose marker therefore cannot change a single match.
    /// </returns>
    private static RegexPatternRewrite? CreateLazinessRewrite(
        string pattern,
        RegexToken token,
        string core,
        string marker
    )
    {
        if (IsExactCount(core))
        {
            return null;
        }

        if (marker.Length == 0)
        {
            return new RegexPatternRewrite(Replace(pattern, token, core + LazyMarker), "greedy-to-lazy");
        }

        return new RegexPatternRewrite(Replace(pattern, token, core), "lazy-to-greedy");
    }

    /// <summary>
    /// Determines whether a core states an exact repetition count, meaning the <c>{n}</c> form.
    /// </summary>
    /// <param name="core">The core of a quantifier, without its lazy marker.</param>
    /// <returns><see langword="true" /> when the core is a counted form without a separator.</returns>
    private static bool IsExactCount(string core) => core[0] is '{' && core.IndexOf(',') < 0;

    /// <summary>
    /// Parses one bound of a counted quantifier.
    /// </summary>
    /// <param name="text">The digits of the bound, as the token spells them.</param>
    /// <param name="value">The parsed bound, or zero when the text does not parse.</param>
    /// <returns>
    /// <see langword="false" /> when the bound does not fit into an <see cref="int" />, which is the same
    /// answer the parser of the regular expression engine gives it.
    /// </returns>
    private static bool TryParseBound(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
