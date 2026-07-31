namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using NetEvolve.FrameShift.Mutations.RegularExpressions;

/// <summary>
/// Mutates an escaped literal dot of a regular expression pattern into an unescaped dot, turning "match a
/// literal dot" into "match any character".
/// </summary>
/// <remarks>
/// <para>
/// <c>\.</c> and <c>.</c> are one keystroke apart and mean opposite things: the first matches exactly the
/// dot character, the second matches any character other than a line terminator (or truly any character
/// under <see cref="System.Text.RegularExpressions.RegexOptions.Singleline" />). Forgetting the backslash
/// is one of the most common regular expression defects there is, for instance in a pattern meant to match
/// a file extension or a version number such as <c>\d+\.\d+</c>. A test suite built only from inputs that
/// happen to contain a literal dot at that position cannot tell the two apart, because every such input
/// satisfies both the correct pattern and the mutant. The surviving mutant names exactly the missing test:
/// an input that carries some other character where the pattern demands a dot.
/// </para>
/// <para>
/// Only the escaped dot is mutated. Every other shorthand escape, such as <c>\d</c>, <c>\w</c> or <c>\n</c>,
/// already denotes a character class or a specific character rather than a literal punctuation mark, and
/// widening it into "any character" is not the keystroke-away defect this operator targets; those
/// constructs are left to other operators of the family, if any exist. Likewise an escaped character other
/// than the dot, such as <c>\-</c> or <c>\+</c>, is left alone: unescaping most of them either changes the
/// pattern's grammar (turning a literal <c>+</c> into a quantifier, which almost always makes the pattern
/// invalid and is discarded by the base class) or is inert in ways that carry no comparable defect signal.
/// The dot is the one escape whose unescaped form is both always legal and always a real semantic
/// broadening, which is why it alone is worth a dedicated operator.
/// </para>
/// <para>
/// A depth counter tracks whether the walk is currently inside a character class, incrementing on every
/// <see cref="RegexTokenKind.CharacterClassOpen" /> and decrementing on every
/// <see cref="RegexTokenKind.CharacterClassClose" />, including a nested class opened by a subtraction such
/// as <c>[\w-[\.]]</c>. Inside a character class both <c>\.</c> and <c>.</c> already mean the literal dot -
/// a class member is never "any character", the class itself is what supplies the alternative meanings a
/// dot would otherwise have outside one. Unescaping <c>\.</c> there would therefore be a silent no-op that
/// the base class's own equality check would discard anyway, but skipping it here instead of letting it be
/// discarded documents the reason: there is no defect being modelled inside a class, not merely a
/// coincidentally identical rewrite.
/// </para>
/// <para>
/// The operator answers only for tokens of kind <see cref="RegexTokenKind.Escape" /> whose text is exactly
/// the two characters <c>\.</c>. An escaped dot inside a character class arrives as the very same token
/// kind, which is exactly why the depth counter - and not the token kind alone - is what decides whether a
/// rewrite is offered.
/// </para>
/// </remarks>
internal sealed class RegexEscapeMutator : RegexPatternMutatorBase
{
    /// <summary>
    /// The escaped literal dot this operator looks for.
    /// </summary>
    private const string EscapedDot = @"\.";

    /// <summary>
    /// The unescaped replacement, matching any character instead of only the dot.
    /// </summary>
    private const string AnyCharacter = ".";

    /// <summary>
    /// Initializes a new instance of the <see cref="RegexEscapeMutator" /> class.
    /// </summary>
    public RegexEscapeMutator()
        : base("regex.escape", MutationKind.RegexEscape) { }

    /// <inheritdoc />
    protected override IEnumerable<RegexPatternRewrite> CreateRewrites(
        string pattern,
        ImmutableArray<RegexToken> tokens,
        CancellationToken cancellationToken
    )
    {
        var characterClassDepth = 0;

        foreach (var token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (token.Kind)
            {
                case RegexTokenKind.CharacterClassOpen:
                    characterClassDepth++;
                    break;
                case RegexTokenKind.CharacterClassClose:
                    characterClassDepth--;
                    break;
                case RegexTokenKind.Escape
                    when characterClassDepth == 0 && string.Equals(token.Text, EscapedDot, StringComparison.Ordinal):
                    yield return new RegexPatternRewrite(
                        Replace(pattern, token, AnyCharacter),
                        "literal-dot-to-any-character"
                    );
                    break;
            }
        }
    }
}
