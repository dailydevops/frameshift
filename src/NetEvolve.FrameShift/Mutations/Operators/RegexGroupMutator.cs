namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using NetEvolve.FrameShift.Mutations.RegularExpressions;

/// <summary>
/// Mutates the grouping constructs of a regular expression pattern by turning a capturing group into a
/// non-capturing one and a non-capturing group into a capturing one.
/// </summary>
/// <remarks>
/// <para>
/// The mutation is worth making even though the mutated pattern still matches exactly the same input as
/// the original: what breaks is the code that reads a capture, by number through
/// <c>match.Groups[2]</c> or by name through <c>match.Groups["year"]</c>. A test that only asserts that
/// a pattern matched, or that only compares <c>match.Value</c>, cannot tell the mutant from the
/// original - and that is precisely the gap the operator asks a test to close. The reverse direction,
/// promoting <c>(?:</c> to <c>(</c>, is the same defect seen from the other side: it inserts a capture
/// where the pattern deliberately had none and shifts the number of every group behind it.
/// </para>
/// <para>
/// Only four opening forms are answered for, each with exactly one rewrite: the plain <c>(</c> and the
/// two named forms <c>(?&lt;name&gt;</c> and <c>(?'name'</c> lose their capture and become <c>(?:</c>,
/// and <c>(?:</c> gains one and becomes <c>(</c>. Only the group OPENING is rewritten. The matching
/// <c>)</c> closes a non-capturing group just as well as a capturing one, so no second token has to be
/// touched and the operator never has to find the partner of an opening.
/// </para>
/// <para>
/// Renumbering is deliberately not tracked. Dropping a capture renumbers every later numbered group, so
/// a pattern containing a backreference such as <c>\2</c> may become invalid, and turning a named
/// capture into <c>(?:</c> leaves any <c>\k&lt;name&gt;</c> reference undefined. Both outcomes are
/// rejected by the validity check of <see cref="RegexPatternMutatorBase" />, which is exactly the right
/// behaviour: such a mutant would throw in every single test that reaches it instead of failing an
/// assertion, so it says nothing about the test suite. The operator therefore needs no capture
/// bookkeeping of its own and may offer a rewrite without proving that the references still resolve.
/// </para>
/// <para>
/// Every other opening form produces nothing, for a reason of its own.
/// <c>(?&gt;</c>, the atomic group, changes when the engine is allowed to backtrack and not what is
/// captured, which puts it outside this operator.
/// The scoped inline options forms <c>(?i:</c>, <c>(?-i:</c> and their siblings carry option flags, and
/// the option flags of a matcher are already covered by the <c>RegexOptions</c> family; rewriting them
/// here would duplicate those mutants and additionally drop the flags they set.
/// The bare <c>(?</c> is the opening of a conditional <c>(?(...)yes|no)</c>, whose condition is
/// tokenized as the group that follows it. The conditional captures nothing, so there is no capture to
/// take away.
/// A balancing group such as <c>(?&lt;close-open&gt;</c> or <c>(?&lt;-open&gt;</c> is a construct with
/// its own stack semantics rather than a plain named capture, so it is left alone; it is recognized by
/// the <c>-</c> inside its name, which is the only thing that separates it from a name that merely
/// contains other punctuation.
/// A lookaround finally arrives as <see cref="RegexTokenKind.Lookaround" /> and therefore never reaches
/// this operator at all.
/// </para>
/// <para>
/// Every decision is made from <see cref="RegexToken.Kind" /> and <see cref="RegexToken.Text" /> alone,
/// and every rewrite is a splice into a token span. A parenthesis inside a character class - where
/// <c>(</c> is an ordinary member and opens nothing - arrives as
/// <see cref="RegexTokenKind.CharacterClassContent" /> and is skipped by the kind test, so the operator
/// never has to look at what surrounds a token to know that it is a real group opening.
/// </para>
/// <para>
/// The rewrites are produced in one pass over the tokens, in pattern order, at most one per opening.
/// That order is fixed and reproducible, which is what lets the produced identifiers be compared across
/// runs.
/// </para>
/// </remarks>
internal sealed class RegexGroupMutator : RegexPatternMutatorBase
{
    /// <summary>
    /// The opening of a plain capturing group.
    /// </summary>
    private const string CapturingOpen = "(";

    /// <summary>
    /// The opening of a non-capturing group.
    /// </summary>
    private const string NonCapturingOpen = "(?:";

    /// <summary>
    /// The prefix of a named group whose name is delimited by angle brackets.
    /// </summary>
    private const string AngleNamePrefix = "(?<";

    /// <summary>
    /// The prefix of a named group whose name is delimited by single quotes.
    /// </summary>
    private const string QuoteNamePrefix = "(?'";

    /// <summary>
    /// The suffix identifying a mutation that takes a capture away.
    /// </summary>
    private const string ToNonCapturingSuffix = "capturing-to-non-capturing";

    /// <summary>
    /// The suffix identifying a mutation that introduces a capture.
    /// </summary>
    private const string ToCapturingSuffix = "non-capturing-to-capturing";

    /// <summary>
    /// Initializes a new instance of the <see cref="RegexGroupMutator" /> class.
    /// </summary>
    public RegexGroupMutator()
        : base("regex.group", MutationKind.RegexGroup) { }

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

            if (token.Kind != RegexTokenKind.GroupOpen)
            {
                continue;
            }

            if (IsCapturingOpen(token.Text))
            {
                yield return new RegexPatternRewrite(Replace(pattern, token, NonCapturingOpen), ToNonCapturingSuffix);

                continue;
            }

            if (string.Equals(token.Text, NonCapturingOpen, StringComparison.Ordinal))
            {
                yield return new RegexPatternRewrite(Replace(pattern, token, CapturingOpen), ToCapturingSuffix);
            }
        }
    }

    /// <summary>
    /// Determines whether <paramref name="text" /> opens a group that defines a capture this operator
    /// takes away, meaning the plain <c>(</c> or one of the two plain named forms.
    /// </summary>
    /// <param name="text">The text of a <see cref="RegexTokenKind.GroupOpen" /> token.</param>
    /// <returns><see langword="true" /> when the opening defines such a capture.</returns>
    /// <remarks>
    /// A plain <c>(</c> is a capture even under <c>RegexOptions.ExplicitCapture</c>, where it silently
    /// behaves like <c>(?:</c>. The rewrite then reproduces the pattern in effect but not in text, and the
    /// base class drops it only if the text is unchanged - which it is not - so the mutant survives as an
    /// equivalent one. That is accepted deliberately: recognizing the case would mean carrying the option
    /// state of every enclosing group, and the mutant is harmless where a genuine capture would be
    /// missed if the check were tightened the other way.
    /// </remarks>
    private static bool IsCapturingOpen(string text) =>
        string.Equals(text, CapturingOpen, StringComparison.Ordinal)
        || IsNamedCaptureOpen(text, AngleNamePrefix, '>')
        || IsNamedCaptureOpen(text, QuoteNamePrefix, '\'');

    /// <summary>
    /// Determines whether <paramref name="text" /> is a plain named capture opening delimited by
    /// <paramref name="prefix" /> and <paramref name="closer" />, as opposed to a balancing group.
    /// </summary>
    /// <param name="text">The text of a <see cref="RegexTokenKind.GroupOpen" /> token.</param>
    /// <param name="prefix">The three character prefix that opens the name.</param>
    /// <param name="closer">The character that closes the name.</param>
    /// <returns><see langword="true" /> when the opening is a plain named capture.</returns>
    /// <remarks>
    /// The name itself is not validated any further: the tokenizer has already accepted it, and a second
    /// opinion here could only disagree with the engine. The one thing that is inspected is the presence
    /// of a <c>-</c>, which turns the construct into a balancing group and therefore into something this
    /// operator does not touch.
    /// </remarks>
    private static bool IsNamedCaptureOpen(string text, string prefix, char closer)
    {
        // The shortest form is a prefix, a single character name and the closer.
        if (text.Length < prefix.Length + 2)
        {
            return false;
        }

        if (!text.StartsWith(prefix, StringComparison.Ordinal) || text[text.Length - 1] != closer)
        {
            return false;
        }

        var name = text.Substring(prefix.Length, text.Length - prefix.Length - 1);

        return name.IndexOf('-') < 0;
    }
}
