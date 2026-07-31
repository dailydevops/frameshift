namespace NetEvolve.FrameShift.Mutations.RegularExpressions;

/// <summary>
/// The lexical category of a <see cref="RegexToken" />, meaning the role the token text plays inside a
/// .NET regular expression pattern.
/// </summary>
/// <remarks>
/// The categories describe the grammar of <see cref="System.Text.RegularExpressions.Regex" /> patterns,
/// not the grammar of any other regular expression dialect. A rewriter that only splices token text
/// therefore never has to look at the surrounding characters again.
/// </remarks>
internal enum RegexTokenKind
{
    /// <summary>
    /// A single character that matches itself, e.g. <c>a</c> in <c>a+</c>. Outside a character class every
    /// character that starts no other construct is a literal, including an unquantified <c>{</c> such as
    /// the one in <c>a{x}</c>.
    /// </summary>
    Literal = 0,

    /// <summary>
    /// A backslash escape that contributes a character or a character class, e.g. <c>\n</c>, <c>\-</c>,
    /// <c>\d</c>, <c>\p{Lu}</c>, <c>\x41</c>, <c>\u0041</c>, <c>\cA</c> or the octal <c>\012</c>.
    /// </summary>
    Escape,

    /// <summary>
    /// A zero width assertion about a position rather than a character: <c>^</c>, <c>$</c>, <c>\A</c>,
    /// <c>\z</c>, <c>\Z</c>, <c>\b</c>, <c>\B</c> or <c>\G</c>. Inside a character class <c>\b</c> is the
    /// backspace character and therefore an <see cref="Escape" /> instead.
    /// </summary>
    Anchor,

    /// <summary>
    /// A repetition suffix including its lazy marker, e.g. <c>*</c>, <c>+?</c>, <c>{2}</c>, <c>{2,}</c> or
    /// <c>{2,3}?</c>. The lazy <c>?</c> belongs to the token it makes lazy and is never a token of its own.
    /// </summary>
    Quantifier,

    /// <summary>
    /// The alternation operator <c>|</c>.
    /// </summary>
    Alternation,

    /// <summary>
    /// The opening of a group: <c>(</c>, <c>(?:</c>, <c>(?&gt;</c>, <c>(?&lt;name&gt;</c>,
    /// <c>(?'name'</c>, a balancing group such as <c>(?&lt;close-open&gt;</c>, a scoped options group such
    /// as <c>(?i:</c>, or the <c>(?</c> that starts a conditional <c>(?(...)yes|no)</c>, whose condition is
    /// tokenized as the group that follows.
    /// </summary>
    GroupOpen,

    /// <summary>
    /// The <c>)</c> that closes a group, a lookaround or a scoped options group.
    /// </summary>
    GroupClose,

    /// <summary>
    /// The opening of a lookaround group: <c>(?=</c>, <c>(?!</c>, <c>(?&lt;=</c> or <c>(?&lt;!</c>. It is
    /// closed by a <see cref="GroupClose" /> like any other group.
    /// </summary>
    Lookaround,

    /// <summary>
    /// A standalone inline options construct such as <c>(?i)</c>, <c>(?-i)</c> or <c>(?imnsx-imnsx)</c>,
    /// which changes the options for the remainder of the enclosing group. The scoped form <c>(?i:</c> is a
    /// <see cref="GroupOpen" />, because it opens a group that a <see cref="GroupClose" /> ends.
    /// </summary>
    InlineOptions,

    /// <summary>
    /// A comment, either the group form <c>(?#comment)</c> or, under
    /// <see cref="System.Text.RegularExpressions.RegexOptions.IgnorePatternWhitespace" />, an unescaped
    /// <c>#</c> and everything up to but excluding the next line feed.
    /// </summary>
    Comment,

    /// <summary>
    /// A run of whitespace that
    /// <see cref="System.Text.RegularExpressions.RegexOptions.IgnorePatternWhitespace" /> removes from the
    /// pattern. Whitespace inside a character class is never ignored and stays
    /// <see cref="CharacterClassContent" />.
    /// </summary>
    WhitespaceIgnored,

    /// <summary>
    /// A reference to an earlier capture, either numbered such as <c>\1</c> or named such as
    /// <c>\k&lt;name&gt;</c> and <c>\k'name'</c>. Inside a character class a digit escape is octal and
    /// therefore an <see cref="Escape" /> instead.
    /// </summary>
    Backreference,

    /// <summary>
    /// The opening of a character class, either <c>[</c> or the negated <c>[^</c>, including the nested
    /// class of a subtraction such as the second <c>[</c> in <c>[\w-[\d]]</c>.
    /// </summary>
    CharacterClassOpen,

    /// <summary>
    /// The <c>]</c> that closes a character class.
    /// </summary>
    CharacterClassClose,

    /// <summary>
    /// A single character member of a character class, including a <c>]</c> that appears first and is
    /// therefore literal, as in <c>[]]</c> and <c>[^]]</c>, and including a <c>-</c> that separates no two
    /// members, as in <c>[-a]</c> and <c>[a-]</c>.
    /// </summary>
    CharacterClassContent,

    /// <summary>
    /// The <c>-</c> between the two ends of a character range, e.g. the <c>-</c> in <c>[a-z]</c>.
    /// </summary>
    CharacterClassRange,

    /// <summary>
    /// The <c>-</c> that subtracts a nested class, e.g. the <c>-</c> in <c>[\w-[\d]]</c>.
    /// </summary>
    CharacterClassSubtraction,
}
