namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using System.Globalization;
using NetEvolve.FrameShift.Mutations.RegularExpressions;

/// <summary>
/// Mutates the alternations of a regular expression pattern by removing one branch at a time and by
/// swapping every pair of adjacent branches.
/// </summary>
/// <remarks>
/// <para>
/// Both mutations target a defect a pattern with an alternation invites. Removing a branch asks whether
/// the test suite exercises that branch at all: a branch no test reaches is indistinguishable from a
/// branch that was never written, so a surviving removal names an alternative nobody covers. Swapping two
/// adjacent branches asks a question that only exists because .NET's alternation is leftmost-first rather
/// than longest-match - the engine takes the first branch that lets the overall match succeed, so for
/// input that two branches both match the <em>order</em> of those branches decides which one wins, and
/// with it the length of the match and the contents of every capture inside them. A pattern such as
/// <c>(a|ab)</c> matching <c>ab</c> is the textbook case: it captures <c>a</c>, and the swapped pattern
/// captures <c>ab</c>.
/// </para>
/// <para>
/// A branch is not a token. It is the range between two <see cref="RegexTokenKind.Alternation" /> tokens,
/// or between the start of the enclosing scope's content and the first bar, or between the last bar and
/// the end of that content. The operator therefore reconstructs the alternation scopes of the pattern in a
/// single left-to-right walk over the tokens: a <see cref="RegexTokenKind.GroupOpen" /> or a
/// <see cref="RegexTokenKind.Lookaround" /> opens a scope whose content begins behind the opening token,
/// a <see cref="RegexTokenKind.GroupClose" /> ends the innermost open one, an alternation token belongs to
/// the innermost open one, and the whole pattern is the root scope. Because the operator receives the
/// tokens of a pattern the tokenizer accepted, and the tokenizer rejects an unbalanced pattern, the walk
/// cannot see a closing token without a matching opening one; the stack is therefore used without an
/// underflow guard.
/// </para>
/// <para>
/// A <c>|</c> inside a character class is not an alternation at all - it is an ordinary member and arrives
/// as <see cref="RegexTokenKind.CharacterClassContent" />. Every construct inside a character class
/// carries such a <c>CharacterClass*</c> kind, so the walk ignores those kinds and needs no notion of
/// where a class starts or ends. Nothing inside a character class is ever touched, and that follows from
/// the token kind alone rather than from an inspection of the surroundings.
/// </para>
/// <para>
/// One scope is deliberately excluded: the conditional <c>(?(...)yes|no)</c>. Its opening token is exactly
/// <c>(?</c>, because the tokenizer shares the parenthesis of the condition with the construct and
/// tokenizes the condition as the group that immediately follows. The condition therefore sits inside what
/// a naive reading would call the first branch, so removing or reordering these pseudo-branches would move
/// the condition rather than an alternative and would not be an alternation mutation at all. A scope whose
/// opening token text is <c>(?</c> is skipped for that reason; the alternations nested inside its
/// <c>yes</c> and <c>no</c> parts are ordinary scopes of their own and are mutated normally.
/// </para>
/// <para>
/// Every rewrite is produced by splicing whole ranges rather than by concatenating the tokens a branch
/// consists of. That matters under <see cref="System.Text.RegularExpressions.RegexOptions.IgnorePatternWhitespace" />,
/// where a branch may hold <see cref="RegexTokenKind.WhitespaceIgnored" /> and
/// <see cref="RegexTokenKind.Comment" /> tokens: a range carries them along untouched, so the mutant
/// keeps the layout and the comments of the branch it moved and only the alternation itself changes.
/// </para>
/// <para>
/// An empty branch is legal - <c>a|</c> has one - and needs no special handling, because an empty range is
/// spliced like any other. Removing a branch from a two-branch alternation can reproduce the original
/// pattern, for instance when the removal leaves an empty branch next to a bar; the base class discards a
/// rewrite that equals the original, so the operator offers it without checking.
/// </para>
/// <para>
/// The identifier suffix of every rewrite ends with the index the scope's content starts at, because a
/// pattern may alternate in several places - <c>(a|b)(c|d)</c> alternates twice - and the branch indices
/// alone would then not be unique within a single literal. This is the only place in the regular
/// expression family where a position becomes part of an identifier, and it is a position in the pattern
/// rather than in the source file, so it stays stable when the literal moves.
/// </para>
/// </remarks>
internal sealed class RegexAlternationMutator : RegexPatternMutatorBase
{
    /// <summary>
    /// The exact opening token text of a conditional <c>(?(...)yes|no)</c>, whose pseudo-branches are not
    /// alternatives and are therefore never mutated.
    /// </summary>
    private const string ConditionalOpenText = "(?";

    /// <summary>
    /// Initializes a new instance of the <see cref="RegexAlternationMutator" /> class.
    /// </summary>
    public RegexAlternationMutator()
        : base("regex.alternation", MutationKind.RegexAlternation) { }

    /// <inheritdoc />
    protected override IEnumerable<RegexPatternRewrite> CreateRewrites(
        string pattern,
        ImmutableArray<RegexToken> tokens,
        CancellationToken cancellationToken
    )
    {
        foreach (var scope in CollectScopes(pattern, tokens, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (scope.Bars.Count == 0 || string.Equals(scope.OpenText, ConditionalOpenText, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var rewrite in CreateScopeRewrites(pattern, scope, cancellationToken))
            {
                yield return rewrite;
            }
        }
    }

    /// <summary>
    /// Reconstructs the alternation scopes of the pattern in one left-to-right walk over the tokens.
    /// </summary>
    /// <param name="pattern">The pattern the tokens tile completely.</param>
    /// <param name="tokens">The tokens of the pattern, in order.</param>
    /// <param name="cancellationToken">A token to observe while walking.</param>
    /// <returns>The scopes, in the order they are opened in, the root scope first.</returns>
    private static ImmutableArray<AlternationScope> CollectScopes(
        string pattern,
        ImmutableArray<RegexToken> tokens,
        CancellationToken cancellationToken
    )
    {
        var scopes = ImmutableArray.CreateBuilder<AlternationScope>();
        var openScopes = new Stack<AlternationScope>();

        // The whole pattern is a scope of its own, opened by nothing and ending with the pattern.
        var root = new AlternationScope(0, string.Empty) { ContentEnd = pattern.Length };
        scopes.Add(root);
        openScopes.Push(root);

        foreach (var token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (token.Kind)
            {
                case RegexTokenKind.GroupOpen:
                case RegexTokenKind.Lookaround:
                    var opened = new AlternationScope(token.End, token.Text);
                    scopes.Add(opened);
                    openScopes.Push(opened);

                    break;
                case RegexTokenKind.GroupClose:
                    // The tokenizer rejects an unbalanced pattern, so a closing token always has an
                    // opening one on the stack and the pop cannot underflow.
                    openScopes.Pop().ContentEnd = token.Start;

                    break;
                case RegexTokenKind.Alternation:
                    openScopes.Peek().Bars.Add(token);

                    break;
                default:
                    // Every other kind is irrelevant to the structure of an alternation, including all of
                    // the CharacterClass* kinds a construct inside a character class carries.
                    break;
            }
        }

        return scopes.ToImmutable();
    }

    /// <summary>
    /// Produces the rewrites of one alternation scope: first the removal of every branch, then the swap of
    /// every pair of adjacent branches, both in ascending branch index.
    /// </summary>
    /// <param name="pattern">The pattern to rewrite.</param>
    /// <param name="scope">The scope whose branches are mutated.</param>
    /// <param name="cancellationToken">A token to observe while producing the rewrites.</param>
    /// <returns>The rewrites of the scope.</returns>
    private static IEnumerable<RegexPatternRewrite> CreateScopeRewrites(
        string pattern,
        AlternationScope scope,
        CancellationToken cancellationToken
    )
    {
        var branches = GetBranches(scope);
        var bars = scope.Bars;
        var position = Format(scope.ContentStart);

        for (var index = 0; index < branches.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A branch is removed together with one adjacent bar, so that the remaining branches stay
            // separated by exactly one bar each. The first branch takes the bar behind it, every other
            // branch takes the bar in front of it.
            var removal =
                index == 0
                    ? Splice(pattern, branches[0].Start, bars[0].End, string.Empty)
                    : Splice(pattern, bars[index - 1].Start, branches[index].End, string.Empty);

            yield return new RegexPatternRewrite(removal, $"remove-branch-{Format(index + 1)}-at-{position}");
        }

        for (var index = 0; index < bars.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var left = branches[index];
            var right = branches[index + 1];
            var swapped = Splice(
                pattern,
                left.Start,
                right.End,
                GetText(pattern, right) + bars[index].Text + GetText(pattern, left)
            );

            yield return new RegexPatternRewrite(
                swapped,
                $"swap-branches-{Format(index + 1)}-{Format(index + 2)}-at-{position}"
            );
        }
    }

    /// <summary>
    /// Derives the branches of a scope with <c>k</c> alternation tokens, which are the <c>k + 1</c> ranges
    /// the bars leave between the start and the end of the scope's content.
    /// </summary>
    /// <param name="scope">The scope to split, which holds at least one alternation token.</param>
    /// <returns>
    /// The branch ranges, in source order, each of them half open. A range may be empty, in which case its
    /// end equals its start.
    /// </returns>
    private static ImmutableArray<(int Start, int End)> GetBranches(AlternationScope scope)
    {
        var bars = scope.Bars;
        var branches = ImmutableArray.CreateBuilder<(int Start, int End)>(bars.Count + 1);

        branches.Add((scope.ContentStart, bars[0].Start));

        for (var index = 1; index < bars.Count; index++)
        {
            branches.Add((bars[index - 1].End, bars[index].Start));
        }

        branches.Add((bars[bars.Count - 1].End, scope.ContentEnd));

        return branches.MoveToImmutable();
    }

    /// <summary>
    /// Reads the text a branch range covers, which is the only way the operator obtains branch text.
    /// </summary>
    /// <param name="pattern">The pattern the range lies in.</param>
    /// <param name="branch">The range to read.</param>
    /// <returns>The covered text, possibly empty.</returns>
    private static string GetText(string pattern, (int Start, int End) branch) =>
        pattern.Substring(branch.Start, branch.End - branch.Start);

    /// <summary>
    /// Formats a number for an identifier suffix, culture independently so that the produced identifier is
    /// the same on every machine.
    /// </summary>
    /// <param name="value">The number to format.</param>
    /// <returns>The invariant decimal form of the number.</returns>
    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// One alternation scope: the range a group or the whole pattern encloses, together with the
    /// alternation tokens that split it into branches.
    /// </summary>
    private sealed class AlternationScope
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AlternationScope" /> class.
        /// </summary>
        /// <param name="contentStart">The index the content of the scope starts at.</param>
        /// <param name="openText">
        /// The text of the token that opened the scope, or an empty string for the root scope.
        /// </param>
        public AlternationScope(int contentStart, string openText)
        {
            ContentStart = contentStart;
            OpenText = openText;
        }

        /// <summary>
        /// Gets the index the content of the scope starts at, which is behind the opening token.
        /// </summary>
        public int ContentStart { get; }

        /// <summary>
        /// Gets the text of the opening token, which is what identifies a conditional construct.
        /// </summary>
        public string OpenText { get; }

        /// <summary>
        /// Gets or sets the index one past the last character of the content, which is only known once the
        /// closing token has been seen.
        /// </summary>
        public int ContentEnd { get; set; }

        /// <summary>
        /// Gets the alternation tokens of the scope, in source order.
        /// </summary>
        public List<RegexToken> Bars { get; } = [];
    }
}
