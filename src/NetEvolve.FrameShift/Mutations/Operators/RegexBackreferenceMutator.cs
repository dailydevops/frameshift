namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using System.Globalization;
using NetEvolve.FrameShift.Mutations.RegularExpressions;

/// <summary>
/// Mutates a numbered backreference of a regular expression pattern by shifting which capturing group it
/// refers to, one group up and one group down.
/// </summary>
/// <remarks>
/// <para>
/// A backreference does not describe a character or a position; it demands that the input repeat whatever
/// an earlier group captured. A test suite that only ever exercises the "happy" repetition, and never an
/// input where the second occurrence differs from the first, cannot tell <c>(\w+)\1</c> apart from
/// <c>(\w+)\2</c> whenever a plausible neighbour group exists - the two patterns behave identically on
/// every input that never reaches the second group at all. Shifting the referenced group by one is exactly
/// the defect a developer produces by miscounting parentheses, and the surviving mutant names the missing
/// test: an input that would only fail once the reference points at the intended group.
/// </para>
/// <para>
/// The operator answers only for tokens of kind <see cref="RegexTokenKind.Backreference" /> whose text is a
/// run of ASCII digits behind the backslash, e.g. <c>\1</c> or <c>\12</c>. The tokenizer resolves the
/// octal-versus-backreference ambiguity of a bare digit run before this operator ever sees a token, by its
/// own <c>ResolveNumberedBackreferences</c> step (see <see cref="RegexPatternTokenizer" />), so every token
/// of this kind offered here is guaranteed to be a genuine backreference and never a re-interpreted octal
/// escape - this operator does not need to, and does not, re-derive that distinction.
/// </para>
/// <para>
/// A named backreference such as <c>\k&lt;year&gt;</c> or <c>\k'year'</c> is deliberately left alone and
/// produces no rewrite at all, which this operator recognises by the token text starting with <c>\k</c>
/// rather than with a digit. "Which group a name refers to" is decided by matching it against the group
/// names the pattern defines elsewhere, and that association is not available at the token-stream level
/// this family works at: an operator that only ever looks at one token at a time has no name table to
/// consult and no honest way to choose a different name to substitute. Mutating a named backreference is
/// therefore a natural extension of this operator, but it needs semantic information this package
/// deliberately does not carry, exactly as <see cref="RegexAnchorMutator" /> leaves <c>\G</c> alone because
/// the information it would need lives outside the pattern token stream.
/// </para>
/// <para>
/// The increase is always offered, for every numbered backreference: replacing <c>\<em>n</em></c> by
/// <c>\<em>n</em>+1</c>. Whether the pattern defines a group numbered <c>n+1</c> at all is not checked here
/// - if it does not, the rewritten pattern is not a valid regular expression, and
/// <see cref="RegexPatternMutatorBase" /> discards it the same way it discards every other invalid rewrite.
/// This mirrors how <c>RegexGroupMutator</c> deliberately does not track renumbering when it inserts or
/// removes a group: tracking which numbers are in scope is exactly the bookkeeping this operator declines
/// to duplicate, because the base class already proves the answer for free.
/// </para>
/// <para>
/// The decrease is offered only when <c>n</c> is at least <c>2</c>, replacing <c>\<em>n</em></c> by
/// <c>\<em>n</em>-1</c>. It is never offered for <c>\1</c>, and this is not merely because group <c>0</c>
/// does not exist: a digit run of <c>\0</c> is not a backreference at all under .NET's grammar, it is the
/// octal escape for the NUL character. Decrementing <c>\1</c> would therefore not shift which group is
/// referenced, it would silently change what <em>kind</em> of construct the text denotes - the very
/// ambiguity the tokenizer already resolved on this operator's behalf - and reintroducing it is out of
/// scope.
/// </para>
/// <para>
/// A digit run so long that it overflows <see cref="int" /> is never observed in a pattern that could ever
/// match, but it is not rejected upstream either, so this operator yields no rewrite for it rather than
/// throwing.
/// </para>
/// </remarks>
internal sealed class RegexBackreferenceMutator : RegexPatternMutatorBase
{
    /// <summary>
    /// The prefix that marks a token as a named backreference rather than a numbered one.
    /// </summary>
    private const string NamedBackreferencePrefix = @"\k";

    /// <summary>
    /// Initializes a new instance of the <see cref="RegexBackreferenceMutator" /> class.
    /// </summary>
    public RegexBackreferenceMutator()
        : base("regex.backreference", MutationKind.RegexBackreference) { }

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

            if (
                token.Kind != RegexTokenKind.Backreference
                || token.Text.StartsWith(NamedBackreferencePrefix, StringComparison.Ordinal)
            )
            {
                continue;
            }

            if (!int.TryParse(token.Text.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                continue;
            }

            yield return new RegexPatternRewrite(
                Replace(pattern, token, @"\" + (number + 1).ToString(CultureInfo.InvariantCulture)),
                "increase-referenced-group"
            );

            if (number >= 2)
            {
                yield return new RegexPatternRewrite(
                    Replace(pattern, token, @"\" + (number - 1).ToString(CultureInfo.InvariantCulture)),
                    "decrease-referenced-group"
                );
            }
        }
    }
}
