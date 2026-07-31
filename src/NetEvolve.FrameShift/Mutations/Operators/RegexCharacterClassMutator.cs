namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using System.Globalization;
using NetEvolve.FrameShift.Mutations.RegularExpressions;

/// <summary>
/// Mutates what a character class or a shorthand escape actually matches: swapping one shorthand class
/// for another, toggling the negation of a class, widening a range by one character at either end,
/// removing a standalone member, and rewriting <c>.</c> against its character-class equivalent
/// <c>[\s\S]</c> in both directions.
/// </summary>
/// <remarks>
/// <para>
/// A character class is where a pattern states which characters are acceptable at a position, and it is
/// the construct a test suite most often gets only approximately right: a suite that exercises
/// <c>\d</c> with a handful of digits never notices that <c>\w</c> would have accepted the very same
/// inputs, a suite that exercises <c>[b-y]</c> never notices that the bound could have been one
/// character narrower, and a suite that exercises <c>[abc]</c> never notices that one of its members is
/// entirely redundant for every input the suite tries. Every rewrite below moves exactly one such
/// boundary, so a surviving mutant names precisely the missing test rather than a vague weakness of the
/// pattern.
/// </para>
/// <para>
/// <b>Shorthand class swap.</b> The six shorthand escapes <c>\d</c>, <c>\D</c>, <c>\w</c>, <c>\W</c>,
/// <c>\s</c> and <c>\S</c> each stand for a fixed character class, and any one of them is a plausible
/// typo or copy-paste slip for any other - they are all two characters long and differ only in the
/// letter and its case. Every occurrence, whether it sits bare in the pattern or as a member inside a
/// class such as <c>[\d.]</c>, is rewritten to each of the other five in a fixed order, because the token
/// kind and text of a shorthand escape are identical in both positions and the substitution is just as
/// meaningful in either. Nothing distinguishes "more likely" swaps from "less likely" ones here on
/// purpose: a mutation operator does not guess which slip is realistic, it offers every one a developer
/// could make and lets the test suite prove it would have caught it.
/// </para>
/// <para>
/// <b>Class negation toggle.</b> A class open token is exactly <c>[</c> or the negated <c>[^</c>, and
/// flipping between them is the single character that separates "one of these" from "none of these" -
/// arguably the most consequential slip a character class invites. The toggle is offered for every class
/// open token regardless of nesting, including the nested class that is the right hand side of a
/// subtraction such as the second <c>[</c> in <c>[\w-[\d]]</c>: the rewrite only ever replaces that one
/// token's own span, so what encloses it is irrelevant and needs no special case.
/// </para>
/// <para>
/// <b>Range widening.</b> A range such as <c>[b-y]</c> states both an inclusive lower and an inclusive
/// upper bound, and a test suite that only tries characters well inside the range never notices that
/// either bound could have been one character narrower. Each bound is nudged towards the other by
/// exactly one code unit - the start is decremented, the end is incremented - which is the smallest
/// change that could ever be observed. Only a range whose two ends are each a single plain character is
/// touched: an escape such as <c>\x41</c> or a shorthand class is never decoded as a bound, because
/// guessing the character it denotes would not be a token level rewrite any more. A decrement or
/// increment that would produce <c>]</c>, <c>\</c>, <c>^</c> or <c>-</c> is skipped, because a blind
/// substitution must never manufacture a character that is special inside a class - producing one would
/// change the shape of the class rather than merely widen it, and might not even be the mutation it
/// claims to be once the engine re-parses it.
/// </para>
/// <para>
/// <b>Member removal.</b> A standalone member of a class - one that is not the endpoint of a range, such
/// as every character of <c>[abc]</c> - is offered as a deletion, because a test suite that never
/// supplies the character that member alone accepts cannot tell whether it was needed at all. A member
/// that participates in a range is left alone here on purpose: deleting one endpoint of <c>[a-z]</c>
/// would not remove a member, it would silently change the shape of the range, which is already covered,
/// more precisely, by the widening rewrites above.
/// </para>
/// <para>
/// <b>Dot equivalence.</b> Outside a character class, <c>.</c> and <c>[\s\S]</c> accept exactly the same
/// single character - every character is either whitespace or not - regardless of
/// <see cref="System.Text.RegularExpressions.RegexOptions.Singleline" />, which only changes what
/// <c>.</c> itself matches and never touches an explicit class. A developer who does not know the
/// <c>[\s\S]</c> idiom, or who copies it needlessly, produces exactly the other spelling, so each
/// direction is offered as a rewrite of the other: <c>.</c> becomes the six character class
/// <c>[\s\S]</c>, and the exact four token run <c>[</c>, <c>\s</c>, <c>\S</c>, <c>]</c> collapses back
/// to <c>.</c>. Only that exact run is recognised for the collapse - <c>[\S\s]</c> or a class with
/// anything else inside is left alone - because the rewrite is a token level splice and not an attempt to
/// reason about what an arbitrary class accepts.
/// </para>
/// <para>
/// The pass walks the tokens once, left to right, in a single indexed loop: the range and the removal
/// rules each need to see a token's immediate neighbours, and the class open dispatch additionally peeks
/// three tokens ahead for the dot-equivalence run, so an indexed loop is simplest and keeps every lookup
/// a plain array access. Dispatch is by token kind, and within one token the rewrites are produced in the
/// fixed order documented above: the shorthand swap, then the negation toggle together with the
/// dot-equivalence collapse it may trigger, then the range widening, then the member removal, then the
/// dot-to-class expansion.
/// </para>
/// </remarks>
internal sealed class RegexCharacterClassMutator : RegexPatternMutatorBase
{
    /// <summary>
    /// The six shorthand character classes, in the fixed order every swap is offered in, paired with the
    /// name the operator suffix uses for each of them.
    /// </summary>
    private static readonly (string Text, string Name)[] _shorthands =
    [
        (@"\d", "digit"),
        (@"\D", "non-digit"),
        (@"\w", "word"),
        (@"\W", "non-word"),
        (@"\s", "space"),
        (@"\S", "non-space"),
    ];

    /// <summary>
    /// The characters that are special inside a character class and must never be produced by a blind
    /// decrement or increment of a range bound.
    /// </summary>
    private static readonly char[] _forbiddenRangeBoundCharacters = [']', '\\', '^', '-'];

    /// <summary>
    /// The pattern text <c>.</c> is rewritten to, equivalent to it outside a character class under every
    /// combination of options.
    /// </summary>
    private const string AnyCharacterClass = @"[\s\S]";

    /// <summary>
    /// The pattern text the exact four token run of <see cref="AnyCharacterClass" /> collapses back to.
    /// </summary>
    private const string Dot = ".";

    /// <summary>
    /// Initializes a new instance of the <see cref="RegexCharacterClassMutator" /> class.
    /// </summary>
    public RegexCharacterClassMutator()
        : base("regex.character-class", MutationKind.RegexCharacterClass) { }

    /// <inheritdoc />
    protected override IEnumerable<RegexPatternRewrite> CreateRewrites(
        string pattern,
        ImmutableArray<RegexToken> tokens,
        CancellationToken cancellationToken
    )
    {
        for (var index = 0; index < tokens.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var rewrite in CreateRewritesForToken(pattern, tokens, index))
            {
                yield return rewrite;
            }
        }
    }

    /// <summary>
    /// Dispatches to the rewrite rule matching the kind of the token at <paramref name="index" />.
    /// </summary>
    /// <param name="pattern">The pattern being rewritten.</param>
    /// <param name="tokens">The tokens of the pattern, in order.</param>
    /// <param name="index">The index of the token to produce rewrites for.</param>
    /// <returns>The rewrites of that token, possibly none.</returns>
    private static IEnumerable<RegexPatternRewrite> CreateRewritesForToken(
        string pattern,
        ImmutableArray<RegexToken> tokens,
        int index
    )
    {
        var token = tokens[index];

        switch (token.Kind)
        {
            case RegexTokenKind.Escape:
                return CreateShorthandSwaps(pattern, token);
            case RegexTokenKind.CharacterClassOpen:
                return CreateOpenBracketRewrites(pattern, tokens, index);
            case RegexTokenKind.CharacterClassRange:
                return CreateRangeWidenings(pattern, tokens, index);
            case RegexTokenKind.CharacterClassContent:
                return AsRewrites(TryCreateMemberRemoval(pattern, tokens, index));
            case RegexTokenKind.Literal:
                return AsRewrites(TryCreateDotExpansion(pattern, token));
            default:
                // Every other kind carries no rewrite this operator offers.
                return [];
        }
    }

    /// <summary>
    /// Produces the rewrites of a <see cref="RegexTokenKind.CharacterClassOpen" /> token: its negation
    /// toggle, and the collapse into <see cref="Dot" /> when it opens the exact <see cref="AnyCharacterClass" />
    /// token run.
    /// </summary>
    /// <param name="pattern">The pattern being rewritten.</param>
    /// <param name="tokens">The tokens of the pattern, in order.</param>
    /// <param name="index">The index of the opening token.</param>
    /// <returns>The rewrites of that token, possibly none.</returns>
    private static IEnumerable<RegexPatternRewrite> CreateOpenBracketRewrites(
        string pattern,
        ImmutableArray<RegexToken> tokens,
        int index
    )
    {
        var negation = CreateNegationToggle(pattern, tokens[index]);

        if (negation is not null)
        {
            yield return negation;
        }

        var collapse = TryCreateAnyClassCollapse(pattern, tokens, index);

        if (collapse is not null)
        {
            yield return collapse;
        }
    }

    /// <summary>
    /// Wraps a possibly absent single rewrite into a sequence, so a single-rewrite rule can be dispatched
    /// the same way as a multi-rewrite one.
    /// </summary>
    /// <param name="rewrite">The rewrite to wrap, or <see langword="null" />.</param>
    /// <returns>A sequence holding <paramref name="rewrite" />, or an empty one.</returns>
    private static IEnumerable<RegexPatternRewrite> AsRewrites(RegexPatternRewrite? rewrite) =>
        rewrite is null ? [] : [rewrite];

    /// <summary>
    /// Produces the swap of a shorthand escape to each of the other five, in the fixed order the class
    /// documents, or nothing at all when the escape is not one of the six shorthands.
    /// </summary>
    /// <param name="pattern">The pattern being rewritten.</param>
    /// <param name="token">The escape token to swap.</param>
    /// <returns>The swaps of the token, in the fixed shorthand order.</returns>
    private static IEnumerable<RegexPatternRewrite> CreateShorthandSwaps(string pattern, RegexToken token)
    {
        var ownIndex = -1;

        for (var i = 0; i < _shorthands.Length; i++)
        {
            if (string.Equals(_shorthands[i].Text, token.Text, StringComparison.Ordinal))
            {
                ownIndex = i;

                break;
            }
        }

        if (ownIndex < 0)
        {
            yield break;
        }

        var ownName = _shorthands[ownIndex].Name;

        for (var i = 0; i < _shorthands.Length; i++)
        {
            if (i == ownIndex)
            {
                continue;
            }

            var (text, name) = _shorthands[i];

            yield return new RegexPatternRewrite(Replace(pattern, token, text), $"{ownName}-to-{name}");
        }
    }

    /// <summary>
    /// Produces the negation toggle of a class open token.
    /// </summary>
    /// <param name="pattern">The pattern being rewritten.</param>
    /// <param name="token">The class open token to toggle.</param>
    /// <returns>The toggle rewrite, or <see langword="null" /> for an unrecognised class open text.</returns>
    private static RegexPatternRewrite? CreateNegationToggle(string pattern, RegexToken token)
    {
        if (string.Equals(token.Text, "[", StringComparison.Ordinal))
        {
            return new RegexPatternRewrite(Replace(pattern, token, "[^"), "negate-class");
        }

        if (string.Equals(token.Text, "[^", StringComparison.Ordinal))
        {
            return new RegexPatternRewrite(Replace(pattern, token, "["), "un-negate-class");
        }

        return null;
    }

    /// <summary>
    /// Checks whether the class open token at <paramref name="index" /> opens the exact four token run
    /// <c>[\s\S]</c> and, if so, produces its collapse to <c>.</c>.
    /// </summary>
    /// <param name="pattern">The pattern being rewritten.</param>
    /// <param name="tokens">The tokens of the pattern.</param>
    /// <param name="index">The index of the class open token.</param>
    /// <returns>The collapse rewrite, or <see langword="null" /> when the run does not match exactly.</returns>
    private static RegexPatternRewrite? TryCreateAnyClassCollapse(
        string pattern,
        ImmutableArray<RegexToken> tokens,
        int index
    )
    {
        if (index + 3 >= tokens.Length)
        {
            return null;
        }

        var open = tokens[index];
        var first = tokens[index + 1];
        var second = tokens[index + 2];
        var close = tokens[index + 3];

        if (
            !string.Equals(open.Text, "[", StringComparison.Ordinal)
            || first.Kind != RegexTokenKind.Escape
            || !string.Equals(first.Text, @"\s", StringComparison.Ordinal)
            || second.Kind != RegexTokenKind.Escape
            || !string.Equals(second.Text, @"\S", StringComparison.Ordinal)
            || close.Kind != RegexTokenKind.CharacterClassClose
            || !string.Equals(close.Text, "]", StringComparison.Ordinal)
        )
        {
            return null;
        }

        return new RegexPatternRewrite(Splice(pattern, open.Start, close.End, Dot), "any-character-class-to-dot");
    }

    /// <summary>
    /// Produces the widening of both ends of a range, for the range token at <paramref name="index" />.
    /// </summary>
    /// <param name="pattern">The pattern being rewritten.</param>
    /// <param name="tokens">The tokens of the pattern.</param>
    /// <param name="index">The index of the range token.</param>
    /// <returns>
    /// The widening of the start followed by the widening of the end, either or both possibly absent.
    /// </returns>
    private static IEnumerable<RegexPatternRewrite> CreateRangeWidenings(
        string pattern,
        ImmutableArray<RegexToken> tokens,
        int index
    )
    {
        if (index - 1 < 0 || index + 1 >= tokens.Length)
        {
            yield break;
        }

        var start = tokens[index - 1];
        var end = tokens[index + 1];

        if (
            start.Kind != RegexTokenKind.CharacterClassContent
            || end.Kind != RegexTokenKind.CharacterClassContent
            || start.Length != 1
            || end.Length != 1
        )
        {
            yield break;
        }

        var startCharacter = start.Text[0];

        if (startCharacter > 0 && TryWiden(startCharacter, -1, out var widenedStart))
        {
            yield return new RegexPatternRewrite(
                Replace(pattern, start, widenedStart.ToString()),
                $"widen-range-start-at-{Format(start.Start)}"
            );
        }

        var endCharacter = end.Text[0];

        if (endCharacter < char.MaxValue && TryWiden(endCharacter, 1, out var widenedEnd))
        {
            yield return new RegexPatternRewrite(
                Replace(pattern, end, widenedEnd.ToString()),
                $"widen-range-end-at-{Format(end.Start)}"
            );
        }
    }

    /// <summary>
    /// Shifts a range bound by one code unit and rejects the result when it would be one of the
    /// characters that are special inside a character class.
    /// </summary>
    /// <param name="character">The bound to shift.</param>
    /// <param name="delta">Either <c>1</c> or <c>-1</c>.</param>
    /// <param name="widened">The shifted character, valid only when the method returns <see langword="true" />.</param>
    /// <returns><see langword="true" /> when the shifted character is safe to splice in.</returns>
    private static bool TryWiden(char character, int delta, out char widened)
    {
        widened = (char)(character + delta);

        return Array.IndexOf(_forbiddenRangeBoundCharacters, widened) < 0;
    }

    /// <summary>
    /// Produces the removal of a class content token, when it is a standalone member rather than the
    /// endpoint of a range.
    /// </summary>
    /// <param name="pattern">The pattern being rewritten.</param>
    /// <param name="tokens">The tokens of the pattern.</param>
    /// <param name="index">The index of the class content token.</param>
    /// <returns>The removal rewrite, or <see langword="null" /> when the member is part of a range.</returns>
    private static RegexPatternRewrite? TryCreateMemberRemoval(
        string pattern,
        ImmutableArray<RegexToken> tokens,
        int index
    )
    {
        var token = tokens[index];

        var precededByRange = index - 1 >= 0 && tokens[index - 1].Kind == RegexTokenKind.CharacterClassRange;
        var followedByRange = index + 1 < tokens.Length && tokens[index + 1].Kind == RegexTokenKind.CharacterClassRange;

        if (precededByRange || followedByRange)
        {
            return null;
        }

        return new RegexPatternRewrite(
            Replace(pattern, token, string.Empty),
            $"remove-member-at-{Format(token.Start)}"
        );
    }

    /// <summary>
    /// Produces the expansion of a literal dot to <see cref="AnyCharacterClass" />.
    /// </summary>
    /// <param name="pattern">The pattern being rewritten.</param>
    /// <param name="token">The literal token to expand.</param>
    /// <returns>The expansion rewrite, or <see langword="null" /> when the literal is not a dot.</returns>
    private static RegexPatternRewrite? TryCreateDotExpansion(string pattern, RegexToken token)
    {
        if (!string.Equals(token.Text, Dot, StringComparison.Ordinal))
        {
            return null;
        }

        return new RegexPatternRewrite(Replace(pattern, token, AnyCharacterClass), "dot-to-any-character-class");
    }

    /// <summary>
    /// Formats a number for an identifier suffix, culture independently so that the produced identifier
    /// is the same on every machine.
    /// </summary>
    /// <param name="value">The number to format.</param>
    /// <returns>The invariant decimal form of the number.</returns>
    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
}
