namespace NetEvolve.FrameShift.Tests.Unit.Mutations.RegularExpressions;

using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using NetEvolve.FrameShift.Mutations.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// An adversarial second opinion on <see cref="RegexPatternTokenizer" />. Where
/// <c>RegexPatternTokenizerTests</c> asserts the token sequence of the constructs the tokenizer was written
/// for, this file attacks the corners: the character class whose first member is <c>]</c>, class
/// subtraction, an escape at the end of the pattern, a brace that starts no quantifier, every
/// <c>(?</c> form, the grammar switch of <see cref="RegexOptions.IgnorePatternWhitespace" />, surrogate
/// pairs, and a pattern long enough to expose quadratic behaviour.
/// </summary>
/// <remarks>
/// <para>
/// Two oracles are used throughout, because a lexer that merely agrees with itself is worthless to a
/// rewriter. The first is the real parser: every pattern is handed to <see cref="Regex" />, and a pattern
/// the tokenizer accepts must construct while a pattern it rejects must throw - with the single documented
/// exception of a pattern whose only fault is semantic. The second is reconstruction: concatenating the
/// token texts must return the original pattern character for character, and every token's text must equal
/// the substring its own span names. That second property is what catches an off-by-one span, which a
/// kind-only assertion happily ignores; a rewriter splices a replacement into exactly that span, so a span
/// that is one character short silently produces an invalid mutant.
/// </para>
/// <para>
/// A <see cref="Regex" /> is always constructed with an explicit match timeout. None of these patterns is
/// ever matched against a hostile input, but the analyzer rule set requires the timeout, and passing one
/// cannot change whether the pattern parses.
/// </para>
/// <para>
/// The tests whose name ends in a behaviour the tokenizer does not have yet are marked in their own
/// documentation: they describe what .NET actually does, measured against the real parser on this
/// machine, and they are expected to fail until the tokenizer is corrected. Every fact they assert was
/// verified on .NET 10 and on .NET Framework 4.7.2, so none of them depends on the runtime under test.
/// </para>
/// </remarks>
internal sealed class RegexPatternTokenizerAdversarialTests
{
    internal const RegexOptions Strict = RegexOptions.None;
    internal const RegexOptions Extended = RegexOptions.IgnorePatternWhitespace;
    private static readonly TimeSpan _matchTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The reconstruction property over a broad table of patterns .NET accepts: the tokens tile the pattern
    /// with no gap, no overlap and no missing tail, every token's text is exactly the substring its span
    /// names, and concatenating the texts returns the pattern. A single off-by-one anywhere in the tokenizer
    /// fails here.
    /// </summary>
    /// <param name="pattern">The pattern to tokenize.</param>
    /// <param name="options">The options that decide the grammar.</param>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    [Arguments("a", Strict)]
    [Arguments("abc", Strict)]
    [Arguments(@"^\d+$", Strict)]
    [Arguments("a+b*c?", Strict)]
    [Arguments("a{2,4}", Strict)]
    [Arguments("a{2,}?", Strict)]
    [Arguments("a{0,2147483647}", Strict)]
    [Arguments("a??", Strict)]
    [Arguments("a{{2}", Strict)]
    [Arguments("a{*", Strict)]
    [Arguments("a{2 }", Strict)]
    [Arguments("a{2, 3}", Strict)]
    [Arguments("a{,5}", Strict)]
    [Arguments("a{}", Strict)]
    [Arguments("{a}", Strict)]
    [Arguments("a{2", Strict)]
    [Arguments("a{2,", Strict)]
    [Arguments("[]]", Strict)]
    [Arguments("[^]]", Strict)]
    [Arguments("[]a]", Strict)]
    [Arguments("[^]a]", Strict)]
    [Arguments("[a]]", Strict)]
    [Arguments("[]-]", Strict)]
    [Arguments("[-]", Strict)]
    [Arguments("[^-]", Strict)]
    [Arguments("[--]", Strict)]
    [Arguments("[---]", Strict)]
    [Arguments("[-a]", Strict)]
    [Arguments("[a-]", Strict)]
    [Arguments("[[a]", Strict)]
    [Arguments("[a-b-c]", Strict)]
    [Arguments("[a-z-[aeiou]]", Strict)]
    [Arguments("[a-[b-[c]]]", Strict)]
    [Arguments("[a-[]]]", Strict)]
    [Arguments("[]-[a]]", Strict)]
    [Arguments("[^]-[a]]", Strict)]
    [Arguments("[a-[b]]]", Strict)]
    [Arguments("[a-[b]]*", Strict)]
    [Arguments("[a-[b]]{2}", Strict)]
    [Arguments("[abc-[b]]", Strict)]
    [Arguments(@"[\w-[\d]]", Strict)]
    [Arguments(@"[\p{Lu}-[a]]", Strict)]
    [Arguments(@"[\s-[\r\n]]", Strict)]
    [Arguments(@"[\d-[5]]", Strict)]
    [Arguments(@"[\d-z]", Strict)]
    [Arguments(@"[\x41-\x5A]", Strict)]
    [Arguments(@"[\--a]", Strict)]
    [Arguments(@"[\b]", Strict)]
    [Arguments(@"[\1]", Strict)]
    [Arguments(@"[\77]", Strict)]
    [Arguments(@"[\777]", Strict)]
    [Arguments(@"[\c[]", Strict)]
    [Arguments(@"\d\D\w\W\s\S", Strict)]
    [Arguments(@"\a\e\f\n\r\t\v", Strict)]
    [Arguments(@"\A\z\Z\G\b\B", Strict)]
    [Arguments(@"\p{IsGreek}", Strict)]
    [Arguments(@"\x41", Strict)]
    [Arguments(@"\u0041", Strict)]
    [Arguments(@"\cA", Strict)]
    [Arguments(@"\c@", Strict)]
    [Arguments(@"\c_", Strict)]
    [Arguments(@"\c^", Strict)]
    [Arguments(@"\ca", Strict)]
    [Arguments(@"\012", Strict)]
    [Arguments(@"\0", Strict)]
    [Arguments(@"\08", Strict)]
    [Arguments(@"\09", Strict)]
    [Arguments(@"\0777", Strict)]
    [Arguments(@"\-\+\#\<", Strict)]
    [Arguments(@"\]\}\{\/", Strict)]
    [Arguments(@"\ ", Strict)]
    [Arguments("(a)", Strict)]
    [Arguments("()", Strict)]
    [Arguments("(?:a)", Strict)]
    [Arguments("(?>a)", Strict)]
    [Arguments("(?=a)(?!a)(?<=a)(?<!a)", Strict)]
    [Arguments("(?<name>a)", Strict)]
    [Arguments("(?'name'a)", Strict)]
    [Arguments("(?<n>a)(?<-n>)", Strict)]
    [Arguments("(?<n>a)(?'-n'b)", Strict)]
    [Arguments("(?<1>a)", Strict)]
    [Arguments("(?<a1>a)", Strict)]
    [Arguments("(?<_a>a)", Strict)]
    [Arguments("(?<n1>a)(?<n2>b)(?<n2-n1>c)", Strict)]
    [Arguments("(?#comment)", Strict)]
    [Arguments("(?#)", Strict)]
    [Arguments("(?# a ) b", Strict)]
    [Arguments("(?i)a", Strict)]
    [Arguments("(?i:a)", Strict)]
    [Arguments("(?-i)a", Strict)]
    [Arguments("(?imnsx-imnsx:a)", Strict)]
    [Arguments("(?-)", Strict)]
    [Arguments("(?i-)", Strict)]
    [Arguments("(?ii)a", Strict)]
    [Arguments("(?x-x)a b", Strict)]
    [Arguments("((?i))", Strict)]
    [Arguments("(?im:a)", Strict)]
    [Arguments("(?(a)b|c)", Strict)]
    [Arguments("(a)(?(1)b|c)", Strict)]
    [Arguments("(?<n>)(?(n)b|c)", Strict)]
    [Arguments("(?(?=a)b|c)", Strict)]
    [Arguments("(|)", Strict)]
    [Arguments("(||)", Strict)]
    [Arguments("a||b", Strict)]
    [Arguments("a|", Strict)]
    [Arguments("|a", Strict)]
    [Arguments("^*", Strict)]
    [Arguments("$*", Strict)]
    [Arguments(@"\b*", Strict)]
    [Arguments("(?=a)*", Strict)]
    [Arguments("(?:)*", Strict)]
    [Arguments("(?:a{2})*", Strict)]
    [Arguments("(a(?i))*", Strict)]
    [Arguments("a(?#c)*", Strict)]
    [Arguments("x(?#c)(?#d)*", Strict)]
    [Arguments("\U0001F600", Strict)]
    [Arguments("\U0001F600*", Strict)]
    [Arguments("[\U0001F600]", Strict)]
    [Arguments("[\uD83D]", Strict)]
    [Arguments("a\nb", Strict)]
    [Arguments("a\r\nb", Strict)]
    [Arguments("(?m)^a$\nb", Strict)]
    [Arguments(@"(a)(b)(c)(d)(e)(f)(g)(h)(i)(j)\10", Strict)]
    [Arguments(@"(?<10>a)\10", Strict)]
    [Arguments(@"(?<a>x)(z)\2", Strict)]
    [Arguments(@"(?<n>a)\k<n>x", Strict)]
    [Arguments(@"(?'a'x)\k'a'", Strict)]
    [Arguments("a b", Extended)]
    [Arguments("a\tb", Extended)]
    [Arguments("a\nb", Extended)]
    [Arguments("[ ]", Extended)]
    [Arguments("[#]", Extended)]
    [Arguments("[a#b]", Extended)]
    [Arguments("[a\nb]", Extended)]
    [Arguments("[a b]", Extended)]
    [Arguments("a#c", Extended)]
    [Arguments("a#c\nb", Extended)]
    [Arguments("a#c\r\nb", Extended)]
    [Arguments("x#", Extended)]
    [Arguments("#\nx", Extended)]
    [Arguments(@"\ b", Extended)]
    [Arguments("a *?", Extended)]
    [Arguments("a ?", Extended)]
    [Arguments("a {2}", Extended)]
    [Arguments("a (?# c) *", Extended)]
    [Arguments("(a #c\n)", Extended)]
    [Arguments("a b#c", Extended)]
    [Arguments("(?-x)a b", Extended)]
    [Arguments("(?-x:a b)", Extended)]
    [Arguments("[a-[b]]", Extended)]
    [Arguments("(?x)a b", Strict)]
    [Arguments("(?x)a#c", Strict)]
    [Arguments("(?x)a#c\r\nb", Strict)]
    [Arguments("(?x)(?-x)a b", Strict)]
    [Arguments("(?x:a b)c", Strict)]
    [Arguments("(?x)a{ 2}", Strict)]
    [Arguments("(?x)(a\n#c\n)", Strict)]
    [Arguments("(?x)  ", Strict)]
    [Arguments("(?x)#only", Strict)]
    public async Task Tokenize_PatternRealParserAccepts_TokensReconstructItExactly(string pattern, RegexOptions options)
    {
        var tokenized = RegexPatternTokenizer.TryTokenize(
            pattern,
            options,
            out var tokens,
            out var index,
            out var error
        );

        _ = await Assert.That(IsAcceptedByRegex(pattern, options)).IsTrue();
        _ = await Assert.That($"{tokenized}:{index}:{error}").IsEqualTo("True:-1:");
        _ = await Assert.That(Reconstruct(tokens)).IsEqualTo(pattern);
        _ = await Assert.That(DescribeTiling(pattern, tokens)).IsEqualTo("tiles");
    }

    /// <summary>
    /// The other half of the oracle: a pattern .NET rejects for a lexical reason must be rejected, and the
    /// reported index must point into the pattern. The reason string is deliberately not asserted here -
    /// <c>RegexPatternTokenizerTests</c> pins those - so that this table can stay a broad sweep.
    /// </summary>
    /// <param name="pattern">The malformed pattern.</param>
    /// <param name="options">The options that decide the grammar.</param>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    [Arguments(@"a\", Strict)]
    [Arguments(@"[a\", Strict)]
    [Arguments(@"(a\", Strict)]
    [Arguments(@"a\Qb", Strict)]
    [Arguments(@"[\Q]", Strict)]
    [Arguments(@"\Qab\E", Strict)]
    [Arguments("[", Strict)]
    [Arguments("[^", Strict)]
    [Arguments("[a", Strict)]
    [Arguments("(?<", Strict)]
    [Arguments("(?<)", Strict)]
    [Arguments("(?<a)", Strict)]
    [Arguments("(?'a)", Strict)]
    [Arguments("(?()", Strict)]
    [Arguments("(?#a(b)c)", Strict)]
    [Arguments("a*(?#c)*", Strict)]
    [Arguments("a???", Strict)]
    [Arguments("a*{2}", Strict)]
    [Arguments("[\\800]", Strict)]
    [Arguments("a * * ", Extended)]
    [Arguments("a ** ", Extended)]
    [Arguments("(?# c)?", Extended)]
    [Arguments("(?# c) *", Extended)]
    public async Task TryTokenize_PatternRealParserRejects_IsRejectedWithAnIndexInsideThePattern(
        string pattern,
        RegexOptions options
    )
    {
        var tokenized = RegexPatternTokenizer.TryTokenize(
            pattern,
            options,
            out var tokens,
            out var index,
            out var error
        );

        _ = await Assert.That(IsAcceptedByRegex(pattern, options)).IsFalse();
        _ = await Assert.That(tokenized).IsFalse();
        _ = await Assert.That(tokens.IsEmpty).IsTrue();
        _ = await Assert.That(index >= 0 && index < pattern.Length).IsTrue();
        _ = await Assert.That(string.IsNullOrWhiteSpace(error)).IsFalse();
    }

    /// <summary>
    /// New members of the documented semantic gap: the tokenizer is a lexer, so these tokenize even though
    /// <see cref="Regex" /> rejects them. They are pinned because each one is a different reason for the
    /// rejection - a reversed surrogate range, a backreference that <see cref="RegexOptions.ExplicitCapture" />
    /// removed the group for, a balancing group naming an undefined capture, an empty Unicode property, a
    /// conditional with three branches and a conditional whose condition is a named group.
    /// </summary>
    /// <param name="pattern">The pattern that tokenizes although .NET rejects it.</param>
    /// <param name="options">The options that decide the grammar.</param>
    /// <param name="expected">The expected token sequence.</param>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    [Arguments(
        "[\U0001F600-\U0001F64F]",
        Strict,
        "CharacterClassOpen:0:[|CharacterClassContent:1:\uD83D|CharacterClassContent:2:\uDE00"
            + "|CharacterClassRange:3:-|CharacterClassContent:4:\uD83D|CharacterClassContent:5:\uDE4F"
            + "|CharacterClassClose:6:]"
    )]
    [Arguments(@"(a)\1", RegexOptions.ExplicitCapture, @"GroupOpen:0:(|Literal:1:a|GroupClose:2:)|Backreference:3:\1")]
    [Arguments(
        @"(?n)(a)\1",
        Strict,
        @"InlineOptions:0:(?n)|GroupOpen:4:(|Literal:5:a|GroupClose:6:)|Backreference:7:\1"
    )]
    [Arguments("(?<-a>x)", Strict, "GroupOpen:0:(?<-a>|Literal:6:x|GroupClose:7:)")]
    [Arguments("(?<name1-name2>a)", Strict, "GroupOpen:0:(?<name1-name2>|Literal:15:a|GroupClose:16:)")]
    [Arguments(@"\p{}", Strict, @"Escape:0:\p{}")]
    [Arguments(
        @"(?<a>x)(z)\3",
        Strict,
        @"GroupOpen:0:(?<a>|Literal:5:x|GroupClose:6:)|GroupOpen:7:(|Literal:8:z|GroupClose:9:)|Backreference:10:\3"
    )]
    [Arguments(
        "(?(a)b|c|d)",
        Strict,
        "GroupOpen:0:(?|GroupOpen:2:(|Literal:3:a|GroupClose:4:)|Literal:5:b|Alternation:6:||Literal:7:c"
            + "|Alternation:8:||Literal:9:d|GroupClose:10:)"
    )]
    [Arguments(
        "(?(?<n>a)b|c)",
        Strict,
        "GroupOpen:0:(?|GroupOpen:2:(?<n>|Literal:7:a|GroupClose:8:)|Literal:9:b|Alternation:10:||Literal:11:c"
            + "|GroupClose:12:)"
    )]
    public async Task Tokenize_SemanticallyInvalidPattern_TokenizesAlthoughTheRealParserRejectsIt(
        string pattern,
        RegexOptions options,
        string expected
    )
    {
        var tokens = RegexPatternTokenizer.Tokenize(pattern, options);

        _ = await Assert.That(Describe(tokens)).IsEqualTo(expected);
        _ = await Assert.That(Reconstruct(tokens)).IsEqualTo(pattern);
        _ = await Assert.That(IsAcceptedByRegex(pattern, options)).IsFalse();
    }

    /// <summary>
    /// Corners whose token structure nothing else pins: the <c>]</c> that closes a class and the <c>]</c>
    /// that is a member, a subtraction whose base is a single <c>]</c>, a subtraction whose nested class
    /// opens with <c>]</c>, and the literal <c>]</c> behind a class.
    /// </summary>
    /// <param name="pattern">The pattern to tokenize.</param>
    /// <param name="options">The options that decide the grammar.</param>
    /// <param name="expected">The expected token sequence.</param>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    [Arguments("[a]]", Strict, "CharacterClassOpen:0:[|CharacterClassContent:1:a|CharacterClassClose:2:]|Literal:3:]")]
    [Arguments(
        "[]-[a]]",
        Strict,
        "CharacterClassOpen:0:[|CharacterClassContent:1:]|CharacterClassSubtraction:2:-|CharacterClassOpen:3:["
            + "|CharacterClassContent:4:a|CharacterClassClose:5:]|CharacterClassClose:6:]"
    )]
    [Arguments(
        "[a-[]]]",
        Strict,
        "CharacterClassOpen:0:[|CharacterClassContent:1:a|CharacterClassSubtraction:2:-|CharacterClassOpen:3:["
            + "|CharacterClassContent:4:]|CharacterClassClose:5:]|CharacterClassClose:6:]"
    )]
    [Arguments(
        "[a-[b]]]",
        Strict,
        "CharacterClassOpen:0:[|CharacterClassContent:1:a|CharacterClassSubtraction:2:-|CharacterClassOpen:3:["
            + "|CharacterClassContent:4:b|CharacterClassClose:5:]|CharacterClassClose:6:]|Literal:7:]"
    )]
    [Arguments(
        "[--]",
        Strict,
        "CharacterClassOpen:0:[|CharacterClassContent:1:-|CharacterClassContent:2:-|CharacterClassClose:3:]"
    )]
    [Arguments(
        "[---]",
        Strict,
        "CharacterClassOpen:0:[|CharacterClassContent:1:-|CharacterClassRange:2:-|CharacterClassContent:3:-"
            + "|CharacterClassClose:4:]"
    )]
    [Arguments("a{{2}", Strict, "Literal:0:a|Literal:1:{|Quantifier:2:{2}")]
    [Arguments("a{*", Strict, "Literal:0:a|Literal:1:{|Quantifier:2:*")]
    [Arguments("a{2,", Strict, "Literal:0:a|Literal:1:{|Literal:2:2|Literal:3:,")]
    [Arguments("a{2 }", Strict, "Literal:0:a|Literal:1:{|Literal:2:2|Literal:3: |Literal:4:}")]
    [Arguments("(?# a ) b", Strict, "Comment:0:(?# a )|Literal:7: |Literal:8:b")]
    [Arguments("(?x-x)a b", Strict, "InlineOptions:0:(?x-x)|Literal:6:a|Literal:7: |Literal:8:b")]
    [Arguments("((?i))", Strict, "GroupOpen:0:(|InlineOptions:1:(?i)|GroupClose:5:)")]
    [Arguments("(||)", Strict, "GroupOpen:0:(|Alternation:1:||Alternation:2:||GroupClose:3:)")]
    [Arguments("(a(?i))*", Strict, "GroupOpen:0:(|Literal:1:a|InlineOptions:2:(?i)|GroupClose:6:)|Quantifier:7:*")]
    [Arguments("x(?#c)(?#d)*", Strict, "Literal:0:x|Comment:1:(?#c)|Comment:6:(?#d)|Quantifier:11:*")]
    [Arguments("\U0001F600*", Strict, "Literal:0:\uD83D|Literal:1:\uDE00|Quantifier:2:*")]
    [Arguments("a#c\r\nb", Extended, "Literal:0:a|Comment:1:#c\r|WhitespaceIgnored:4:\n|Literal:5:b")]
    [Arguments("x#", Extended, "Literal:0:x|Comment:1:#")]
    [Arguments("#\nx", Extended, "Comment:0:#|WhitespaceIgnored:1:\n|Literal:2:x")]
    [Arguments(
        "[a#b]",
        Extended,
        "CharacterClassOpen:0:[|CharacterClassContent:1:a|CharacterClassContent:2:#|CharacterClassContent:3:b"
            + "|CharacterClassClose:4:]"
    )]
    [Arguments(
        "[a\nb]",
        Extended,
        "CharacterClassOpen:0:[|CharacterClassContent:1:a|CharacterClassContent:2:\n|CharacterClassContent:3:b"
            + "|CharacterClassClose:4:]"
    )]
    [Arguments("a {2}", Extended, "Literal:0:a|WhitespaceIgnored:1: |Quantifier:2:{2}")]
    [Arguments(
        "a (?# c) *",
        Extended,
        "Literal:0:a|WhitespaceIgnored:1: |Comment:2:(?# c)|WhitespaceIgnored:8: |Quantifier:9:*"
    )]
    public async Task Tokenize_CornerConstruct_ProducesTheExpectedTokens(
        string pattern,
        RegexOptions options,
        string expected
    )
    {
        var tokens = RegexPatternTokenizer.Tokenize(pattern, options);

        _ = await Assert.That(Describe(tokens)).IsEqualTo(expected);
        _ = await Assert.That(Reconstruct(tokens)).IsEqualTo(pattern);
        _ = await Assert.That(IsAcceptedByRegex(pattern, options)).IsTrue();
    }

    /// <summary>
    /// .NET has no <c>\Q ... \E</c> quoting at all, unlike Java and PCRE: both escapes are simply
    /// unrecognized, wherever they appear. The answer is pinned here so that nobody ever teaches the
    /// tokenizer to treat <c>\Q</c> as the start of a quoted run - which would make every construct behind
    /// it a literal and every mutant of that run wrong.
    /// </summary>
    /// <param name="pattern">A pattern containing <c>\Q</c> or <c>\E</c>.</param>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    [Arguments(@"\Q")]
    [Arguments(@"\E")]
    [Arguments(@"\Qa+b\E")]
    [Arguments(@"a\Qb")]
    [Arguments(@"[\Q]")]
    [Arguments(@"[a\E]")]
    [Arguments(@"(\Q)")]
    public async Task Tokenize_QuotingEscape_IsUnrecognizedByBothTheTokenizerAndTheRealParser(string pattern)
    {
        var expected = pattern.Contains(@"\Q", StringComparison.Ordinal) ? @"\Q" : @"\E";

        var tokenized = RegexPatternTokenizer.TryTokenize(pattern, Strict, out _, out var index, out var error);

        _ = await Assert.That(IsAcceptedByRegex(pattern, Strict)).IsFalse();
        _ = await Assert.That(tokenized).IsFalse();
        _ = await Assert.That(error).Contains("is not recognized by .NET");
        _ = await Assert.That(pattern.Substring(index, 2)).IsEqualTo(expected);
    }

    /// <summary>
    /// DEFECT - the tokenizer produces the wrong tokens today. A digit escape is a backreference only when
    /// the <em>whole</em> digit run names a capture group the pattern defines. When it does not, .NET does
    /// not shorten the backreference: it re-reads the run as an octal escape of up to three octal digits and
    /// leaves any further digit a literal. Measured against the real parser on .NET 10 and .NET Framework
    /// 4.7.2: <c>(a)\10</c> matches <c>"a" + (char)8</c> and not <c>"aa0"</c>, and with ten groups
    /// <c>\100</c> matches <c>(char)64</c> and not group ten followed by <c>'0'</c>. The tokenizer instead
    /// trims the run digit by digit, so it reports a <see cref="RegexTokenKind.Backreference" /> where the
    /// pattern holds an <see cref="RegexTokenKind.Escape" /> - and a rewriter that renumbers a
    /// backreference would corrupt a character escape.
    /// </summary>
    /// <param name="pattern">The pattern to tokenize.</param>
    /// <param name="expected">The token sequence .NET's reading demands.</param>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    [Arguments(@"\10", @"Escape:0:\10")]
    [Arguments(@"\11", @"Escape:0:\11")]
    [Arguments(@"\77", @"Escape:0:\77")]
    [Arguments(@"\100", @"Escape:0:\100")]
    [Arguments(@"\400", @"Escape:0:\400")]
    [Arguments(@"\18", @"Escape:0:\1|Literal:2:8")]
    [Arguments(@"\7777", @"Escape:0:\777|Literal:4:7")]
    [Arguments(@"\4000", @"Escape:0:\400|Literal:4:0")]
    [Arguments(@"(a)\10", @"GroupOpen:0:(|Literal:1:a|GroupClose:2:)|Escape:3:\10")]
    [Arguments(@"(a)\18", @"GroupOpen:0:(|Literal:1:a|GroupClose:2:)|Escape:3:\1|Literal:5:8")]
    [Arguments(
        @"(a)(b)\11",
        @"GroupOpen:0:(|Literal:1:a|GroupClose:2:)|GroupOpen:3:(|Literal:4:b|GroupClose:5:)|Escape:6:\11"
    )]
    public async Task Tokenize_DigitEscapeThatNamesNoCaptureGroup_IsAnOctalEscapeAndNotAShortenedBackreference(
        string pattern,
        string expected
    )
    {
        var tokens = RegexPatternTokenizer.Tokenize(pattern, Strict);

        _ = await Assert.That(IsAcceptedByRegex(pattern, Strict)).IsTrue();
        _ = await Assert.That(Describe(tokens)).IsEqualTo(expected);
        _ = await Assert.That(Reconstruct(tokens)).IsEqualTo(pattern);
    }

    /// <summary>
    /// The control for the defect above: where the whole digit run <em>does</em> name a group, the token is
    /// a backreference and the tokenizer is already right. Ten groups make <c>\10</c> group ten; eleven make
    /// <c>\11</c> group eleven.
    /// </summary>
    /// <param name="pattern">The pattern to tokenize.</param>
    /// <param name="expectedLastToken">The rendered final token.</param>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    [Arguments(@"(a)(b)(c)(d)(e)(f)(g)(h)(i)(j)\10", @"Backreference:30:\10")]
    [Arguments(@"(a)(b)(c)(d)(e)(f)(g)(h)(i)(j)(k)\11", @"Backreference:33:\11")]
    [Arguments(@"(a)\1", @"Backreference:3:\1")]
    [Arguments(@"(?<10>a)\10", @"Backreference:8:\10")]
    public async Task Tokenize_DigitEscapeThatNamesACaptureGroup_IsABackreference(
        string pattern,
        string expectedLastToken
    )
    {
        var tokens = RegexPatternTokenizer.Tokenize(pattern, Strict);
        var last = tokens[tokens.Length - 1];

        _ = await Assert.That(IsAcceptedByRegex(pattern, Strict)).IsTrue();
        _ = await Assert.That($"{last.Kind}:{last.Start}:{last.Text}").IsEqualTo(expectedLastToken);
        _ = await Assert.That(Reconstruct(tokens)).IsEqualTo(pattern);
    }

    /// <summary>
    /// DEFECT - the tokenizer rejects these patterns today. .NET skips the same blanks between a quantifier
    /// and its lazy <c>?</c> as it does anywhere else: a <c>(?#...)</c> comment always, and whitespace plus a
    /// <c>#</c> comment under <see cref="RegexOptions.IgnorePatternWhitespace" />. So <c>a*(?#c)?</c> and
    /// <c>a * ?</c> are lazy quantifiers, not nested ones, and the real parser accepts every pattern below.
    /// The tokenizer only looks at the character immediately behind the quantifier, so it reports a nested
    /// quantifier and refuses a legal pattern - which would make the analyzer skip the pattern entirely.
    /// </summary>
    /// <param name="pattern">The pattern to tokenize.</param>
    /// <param name="options">The options that decide the grammar.</param>
    /// <param name="lazyMarkerIndex">The index of the <c>?</c> that makes the quantifier lazy.</param>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    [Arguments("a*(?#c)?", Strict, 7)]
    [Arguments("a{2}(?#c)?", Strict, 9)]
    [Arguments("a * ?", Extended, 4)]
    [Arguments("a+ ?", Extended, 3)]
    [Arguments("a{2,3} ?", Extended, 7)]
    [Arguments("a*\n?", Extended, 3)]
    [Arguments("a*\t?", Extended, 3)]
    [Arguments("a*#c\n?", Extended, 5)]
    [Arguments("a* (?#c) ?", Extended, 9)]
    [Arguments("(?x)a * ?", Strict, 8)]
    [Arguments("(?x)a{2} ?", Strict, 9)]
    [Arguments("(?x:a * ?)", Strict, 8)]
    public async Task Tokenize_LazyMarkerBehindASkippableBlank_IsAcceptedAsAQuantifier(
        string pattern,
        RegexOptions options,
        int lazyMarkerIndex
    )
    {
        var tokenized = RegexPatternTokenizer.TryTokenize(
            pattern,
            options,
            out var tokens,
            out var index,
            out var error
        );
        var lazyMarker = tokenized ? tokens.FirstOrDefault(token => token.Start == lazyMarkerIndex) : null;
        var expectedMarker = $"Quantifier[{lazyMarkerIndex}..{lazyMarkerIndex + 1})='?'";

        _ = await Assert.That(IsAcceptedByRegex(pattern, options)).IsTrue();
        _ = await Assert.That($"{tokenized}:{index}:{error}").IsEqualTo("True:-1:");
        _ = await Assert.That(Reconstruct(tokens)).IsEqualTo(pattern);
        _ = await Assert.That(lazyMarker?.ToString()).IsEqualTo(expectedMarker);
    }

    /// <summary>
    /// The control for the defect above: a second <em>quantifier</em> behind a blank really is a nested
    /// quantifier, and both parsers reject it. Only the lazy marker is skipped over, so fixing the case
    /// above must not open this door.
    /// </summary>
    /// <param name="pattern">The malformed pattern.</param>
    /// <param name="options">The options that decide the grammar.</param>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    [Arguments("a*(?#c)*", Strict)]
    [Arguments("a*(?#c)+", Strict)]
    [Arguments("a * *", Extended)]
    [Arguments("a*#c\n*", Extended)]
    [Arguments("a* {2}", Extended)]
    public async Task TryTokenize_SecondQuantifierBehindASkippableBlank_StaysRejected(
        string pattern,
        RegexOptions options
    )
    {
        var tokenized = RegexPatternTokenizer.TryTokenize(pattern, options, out _, out _, out var error);

        _ = await Assert.That(IsAcceptedByRegex(pattern, options)).IsFalse();
        _ = await Assert.That(tokenized).IsFalse();
        _ = await Assert.That(error).Contains("nested quantifier");
    }

    /// <summary>
    /// DEFECT - the tokenizer accepts these patterns today. A class subtraction has to be the last thing in
    /// the class it subtracts from: .NET rejects every pattern below, whether the leftover is an ordinary
    /// member or a second subtraction. The tokenizer treats the nested class as just another member and
    /// keeps scanning, so it hands a rewriter a token sequence for a pattern that does not compile - and a
    /// mutant built from it would be discarded for a reason that has nothing to do with the mutation.
    /// </summary>
    /// <param name="pattern">The pattern the real parser rejects.</param>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    [Arguments("[a-[b]c]")]
    [Arguments("[a-z-[aeiou]x]")]
    [Arguments(@"[\w-[\d]x]")]
    [Arguments("[a-[b]-[c]]")]
    [Arguments("[a-z-[aeiou]-[b]]")]
    [Arguments(@"[\w-[\d]-[a]]")]
    [Arguments("[a-[b-[c]]d]")]
    public async Task TryTokenize_ClassSubtractionFollowedByAnotherMember_IsRejected(string pattern)
    {
        var tokenized = RegexPatternTokenizer.TryTokenize(
            pattern,
            Strict,
            out var tokens,
            out var index,
            out var error
        );

        _ = await Assert.That(IsAcceptedByRegex(pattern, Strict)).IsFalse();
        _ = await Assert.That(tokenized).IsFalse();
        _ = await Assert.That(tokens.IsEmpty).IsTrue();
        _ = await Assert.That(index >= 0 && index < pattern.Length).IsTrue();
        _ = await Assert.That(string.IsNullOrWhiteSpace(error)).IsFalse();
    }

    /// <summary>
    /// DEFECT - the tokenizer misreads these patterns today. A <c>-[</c> only starts a subtraction when the
    /// class already holds a member; a <c>-</c> in the very first position - and <c>[^</c> does not count as
    /// a member - is an ordinary member and the <c>[</c> behind it is one too. That is why <c>[-[a]</c> is a
    /// complete, legal class of <c>-</c>, <c>[</c> and <c>a</c>, and why <c>[-[a]*</c> quantifies it. The
    /// tokenizer reads the <c>-[</c> as a subtraction, so it waits for a second <c>]</c> and rejects a legal
    /// pattern; where a second <c>]</c> happens to be present it accepts the pattern but reports a class
    /// that reaches two characters too far, which is the worst case for a rewriter.
    /// </summary>
    /// <param name="pattern">The pattern to tokenize.</param>
    /// <param name="expected">The token sequence .NET's reading demands.</param>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    [Arguments(
        "[-[a]",
        "CharacterClassOpen:0:[|CharacterClassContent:1:-|CharacterClassContent:2:[|CharacterClassContent:3:a"
            + "|CharacterClassClose:4:]"
    )]
    [Arguments(
        "[-[a]*",
        "CharacterClassOpen:0:[|CharacterClassContent:1:-|CharacterClassContent:2:[|CharacterClassContent:3:a"
            + "|CharacterClassClose:4:]|Quantifier:5:*"
    )]
    [Arguments(
        "[^-[a]",
        "CharacterClassOpen:0:[^|CharacterClassContent:2:-|CharacterClassContent:3:[|CharacterClassContent:4:a"
            + "|CharacterClassClose:5:]"
    )]
    [Arguments(
        "[-[a]b]",
        "CharacterClassOpen:0:[|CharacterClassContent:1:-|CharacterClassContent:2:[|CharacterClassContent:3:a"
            + "|CharacterClassClose:4:]|Literal:5:b|Literal:6:]"
    )]
    [Arguments(
        "[-[a]]",
        "CharacterClassOpen:0:[|CharacterClassContent:1:-|CharacterClassContent:2:[|CharacterClassContent:3:a"
            + "|CharacterClassClose:4:]|Literal:5:]"
    )]
    public async Task Tokenize_DashBeforeANestedClassInTheFirstPosition_IsAnOrdinaryMember(
        string pattern,
        string expected
    )
    {
        var tokenized = RegexPatternTokenizer.TryTokenize(
            pattern,
            Strict,
            out var tokens,
            out var index,
            out var error
        );

        _ = await Assert.That(IsAcceptedByRegex(pattern, Strict)).IsTrue();
        _ = await Assert.That($"{tokenized}:{index}:{error}").IsEqualTo("True:-1:");
        _ = await Assert.That(Describe(tokens)).IsEqualTo(expected);
        _ = await Assert.That(Reconstruct(tokens)).IsEqualTo(pattern);
    }

    /// <summary>
    /// The control for the defect above: once the class holds a member, <c>-[</c> really is a subtraction -
    /// including when that member is the <c>]</c> that a first position makes a literal. Fixing the first
    /// position must not turn these into members.
    /// </summary>
    /// <param name="pattern">The pattern to tokenize.</param>
    /// <param name="subtractionIndex">The index of the <c>-</c> that subtracts.</param>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    [Arguments("[a-[b]]", 2)]
    [Arguments("[]-[a]]", 2)]
    [Arguments("[^]-[a]]", 3)]
    [Arguments("[a-z-[q]]", 4)]
    [Arguments(@"[\w-[\d]]", 3)]
    public async Task Tokenize_DashBeforeANestedClassBehindAMember_IsASubtraction(string pattern, int subtractionIndex)
    {
        var tokens = RegexPatternTokenizer.Tokenize(pattern, Strict);
        var subtraction = tokens.Single(token => token.Start == subtractionIndex);

        _ = await Assert.That(IsAcceptedByRegex(pattern, Strict)).IsTrue();
        _ = await Assert.That(subtraction.Kind).IsEqualTo(RegexTokenKind.CharacterClassSubtraction);
        _ = await Assert.That(subtraction.Text).IsEqualTo("-");
        _ = await Assert.That(Reconstruct(tokens)).IsEqualTo(pattern);
    }

    /// <summary>
    /// DEFECT - the tokenizer rejects these patterns today. An inline options construct is a run of mode
    /// switches, not one optional <c>-</c>: a <c>-</c> turns the options behind it off, a <c>+</c> turns them
    /// on again, and either may appear as often as it likes. The real parser accepts every pattern below.
    /// The tokenizer's option scanner latches the first <c>-</c> and does not know <c>+</c> at all, so it
    /// stops in the middle of the construct and reports an unrecognized grouping construct.
    /// </summary>
    /// <param name="pattern">The pattern the real parser accepts.</param>
    /// <param name="expected">The expected token sequence.</param>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    [Arguments("(?-i-s)", "InlineOptions:0:(?-i-s)")]
    [Arguments("(?i-s-x)", "InlineOptions:0:(?i-s-x)")]
    [Arguments("(?-i+s)", "InlineOptions:0:(?-i+s)")]
    [Arguments("(?+i)", "InlineOptions:0:(?+i)")]
    [Arguments("(?i+)", "InlineOptions:0:(?i+)")]
    [Arguments("(?+)", "InlineOptions:0:(?+)")]
    [Arguments("(?i-i-i)a", "InlineOptions:0:(?i-i-i)|Literal:8:a")]
    [Arguments("(?-i-s:a)", "GroupOpen:0:(?-i-s:|Literal:7:a|GroupClose:8:)")]
    [Arguments("(?-i+s:a)", "GroupOpen:0:(?-i+s:|Literal:7:a|GroupClose:8:)")]
    [Arguments("(?imnsx-imnsx-imnsx:a)", "GroupOpen:0:(?imnsx-imnsx-imnsx:|Literal:20:a|GroupClose:21:)")]
    public async Task Tokenize_InlineOptionsWithSeveralModeSwitches_IsAnOptionsConstruct(
        string pattern,
        string expected
    )
    {
        var tokenized = RegexPatternTokenizer.TryTokenize(
            pattern,
            Strict,
            out var tokens,
            out var index,
            out var error
        );

        _ = await Assert.That(IsAcceptedByRegex(pattern, Strict)).IsTrue();
        _ = await Assert.That($"{tokenized}:{index}:{error}").IsEqualTo("True:-1:");
        _ = await Assert.That(Describe(tokens)).IsEqualTo(expected);
        _ = await Assert.That(Reconstruct(tokens)).IsEqualTo(pattern);
    }

    /// <summary>
    /// DEFECT, and the consequence of the one above: the mode a run of switches leaves behind decides the
    /// grammar of the rest of the group, so the tokenizer has to end up with the same mode as the real
    /// parser. The last switch that mentions <c>x</c> wins - <c>(?x-x-x)</c> leaves it off and
    /// <c>(?x-x+x)</c> leaves it on - and the oracle here is a match rather than a construction, because
    /// only a match can tell whether the space was ignored.
    /// </summary>
    /// <param name="pattern">The pattern whose trailing space is or is not ignored.</param>
    /// <param name="expectedKind">The kind the space between <c>a</c> and <c>b</c> has to be reported as.</param>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    [Arguments("(?x-x-x)a b", RegexTokenKind.Literal)]
    [Arguments("(?x-x+x)a b", RegexTokenKind.WhitespaceIgnored)]
    [Arguments("(?-x+x)a b", RegexTokenKind.WhitespaceIgnored)]
    [Arguments("(?+x)a b", RegexTokenKind.WhitespaceIgnored)]
    [Arguments("(?x-x)a b", RegexTokenKind.Literal)]
    [Arguments("(?x)a b", RegexTokenKind.WhitespaceIgnored)]
    public async Task Tokenize_ModeSwitchRun_LeavesTheSameWhitespaceGrammarAsTheRealParser(
        string pattern,
        RegexTokenKind expectedKind
    )
    {
        var tokenized = RegexPatternTokenizer.TryTokenize(pattern, Strict, out var tokens, out _, out _);
        var space = tokenized
            ? tokens.Single(token => string.Equals(token.Text, " ", StringComparison.Ordinal)).Kind
            : RegexTokenKind.Comment;
        var oracle = Matches(pattern, Strict, "ab") ? RegexTokenKind.WhitespaceIgnored : RegexTokenKind.Literal;

        _ = await Assert.That(oracle).IsEqualTo(expectedKind);
        _ = await Assert.That(tokenized).IsTrue();
        _ = await Assert.That(space).IsEqualTo(expectedKind);
    }

    /// <summary>
    /// DEFECT for every row whose expectation is <see cref="RegexTokenKind.Literal" />. Under
    /// <see cref="RegexOptions.IgnorePatternWhitespace" /> .NET ignores exactly five characters - space,
    /// tab, line feed, form feed and carriage return - and nothing else, not even a vertical tab and none of
    /// the Unicode separators. The tokenizer asks <see cref="char.IsWhiteSpace(char)" /> instead, which is a
    /// much wider set, so it reports characters as ignored that the pattern actually has to match. The
    /// oracle is a match against <c>"ab"</c>: it succeeds only if .NET really dropped the character.
    /// </summary>
    /// <param name="candidate">The whitespace candidate between <c>a</c> and <c>b</c>.</param>
    /// <param name="expectedKind">The kind the character has to be reported as.</param>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    [Arguments(" ", RegexTokenKind.WhitespaceIgnored)]
    [Arguments("\t", RegexTokenKind.WhitespaceIgnored)]
    [Arguments("\n", RegexTokenKind.WhitespaceIgnored)]
    [Arguments("\f", RegexTokenKind.WhitespaceIgnored)]
    [Arguments("\r", RegexTokenKind.WhitespaceIgnored)]
    [Arguments("\v", RegexTokenKind.Literal)]
    [Arguments("\u0085", RegexTokenKind.Literal)]
    [Arguments("\u00a0", RegexTokenKind.Literal)]
    [Arguments("\u1680", RegexTokenKind.Literal)]
    [Arguments("\u2000", RegexTokenKind.Literal)]
    [Arguments("\u2028", RegexTokenKind.Literal)]
    [Arguments("\u2029", RegexTokenKind.Literal)]
    [Arguments("\u202f", RegexTokenKind.Literal)]
    [Arguments("\u205f", RegexTokenKind.Literal)]
    [Arguments("\u3000", RegexTokenKind.Literal)]
    public async Task Tokenize_WhitespaceCandidate_IsIgnoredOnlyWhereTheRealParserIgnoresIt(
        string candidate,
        RegexTokenKind expectedKind
    )
    {
        var pattern = "a" + candidate + "b";

        var tokens = RegexPatternTokenizer.Tokenize(pattern, Extended);
        var middle = tokens.Single(token => token.Start == 1);
        var oracle = Matches(pattern, Extended, "ab") ? RegexTokenKind.WhitespaceIgnored : RegexTokenKind.Literal;

        _ = await Assert.That(oracle).IsEqualTo(expectedKind);
        _ = await Assert.That(middle.Kind).IsEqualTo(expectedKind);
        _ = await Assert.That(middle.Text).IsEqualTo(candidate);
        _ = await Assert.That(Reconstruct(tokens)).IsEqualTo(pattern);
    }

    /// <summary>
    /// A whitespace character inside a character class is a member under every option, because the class
    /// grammar does not skip blanks. Pinned separately from the table above, since the fix for that table
    /// must not start dropping class members.
    /// </summary>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    public async Task Tokenize_WhitespaceInsideACharacterClass_StaysAMember()
    {
        const string pattern = "[a \t\n#b]";
        RegexTokenKind[] expectedKinds =
        [
            RegexTokenKind.CharacterClassOpen,
            RegexTokenKind.CharacterClassContent,
            RegexTokenKind.CharacterClassClose,
        ];

        var tokens = RegexPatternTokenizer.Tokenize(pattern, Extended);
        var kinds = tokens.Select(token => token.Kind).Distinct().OrderBy(kind => kind).ToArray();

        _ = await Assert.That(Matches(pattern, Extended, " ")).IsTrue();
        _ = await Assert.That(Matches(pattern, Extended, "#")).IsTrue();
        _ = await Assert.That(tokens.Length).IsEqualTo(8);
        _ = await Assert.That(kinds).IsEquivalentTo(expectedKinds);
        _ = await Assert.That(Reconstruct(tokens)).IsEqualTo(pattern);
    }

    /// <summary>
    /// A surrogate pair is two tokens, because a .NET pattern is a sequence of UTF-16 code units and a
    /// quantifier behind an astral character therefore repeats its low surrogate alone. That is .NET's
    /// behaviour, not a defect, and it is pinned so that a rewriter never splices between the two halves.
    /// </summary>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    public async Task Tokenize_SurrogatePair_ProducesOneTokenPerCodeUnit()
    {
        const string pattern = "\U0001F600+";

        var tokens = RegexPatternTokenizer.Tokenize(pattern, Strict);

        _ = await Assert.That(Describe(tokens)).IsEqualTo("Literal:0:\uD83D|Literal:1:\uDE00|Quantifier:2:+");
        _ = await Assert.That(char.IsHighSurrogate(tokens[0].Text[0])).IsTrue();
        _ = await Assert.That(char.IsLowSurrogate(tokens[1].Text[0])).IsTrue();
        _ = await Assert.That(Matches(pattern, Strict, "\U0001F600\uDE00")).IsTrue();
        _ = await Assert.That(Reconstruct(tokens)).IsEqualTo(pattern);
    }

    /// <summary>
    /// A very long pattern must be tokenized without a stack overflow and without quadratic behaviour. The
    /// budget is deliberately far above the measured cost - the whole set is a few milliseconds - so the
    /// test only fails on a genuinely pathological change, never on a slow machine.
    /// </summary>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    public async Task Tokenize_VeryLongPattern_ScalesLinearlyAndDoesNotRecurse()
    {
        var literals = Repeat("abcd", 5000);
        var groups = Repeat("(a)", 2000);
        var classes = Repeat("[a-z]", 4000);
        var alternations = Repeat("(?:a|b)", 2000);
        var watch = Stopwatch.StartNew();

        var literalTokens = RegexPatternTokenizer.Tokenize(literals, Strict);
        var groupTokens = RegexPatternTokenizer.Tokenize(groups, Strict);
        var classTokens = RegexPatternTokenizer.Tokenize(classes, Strict);
        var alternationTokens = RegexPatternTokenizer.Tokenize(alternations, Strict);
        watch.Stop();

        _ = await Assert.That(literalTokens.Length).IsEqualTo(20000);
        _ = await Assert.That(groupTokens.Length).IsEqualTo(6000);
        _ = await Assert.That(classTokens.Length).IsEqualTo(20000);
        _ = await Assert.That(alternationTokens.Length).IsEqualTo(10000);
        _ = await Assert.That(Reconstruct(literalTokens)).IsEqualTo(literals);
        _ = await Assert.That(Reconstruct(classTokens)).IsEqualTo(classes);
        _ = await Assert.That(DescribeTiling(alternations, alternationTokens)).IsEqualTo("tiles");
        _ = await Assert.That(watch.ElapsedMilliseconds).IsLessThan(30_000L);
    }

    /// <summary>
    /// Deep nesting is the other way a hand written parser dies. The tokenizer keeps its group and class
    /// state on explicit stacks, so two hundred nested groups and sixty nested subtraction classes have to
    /// cost nothing; the depths stay modest because the real parser recurses and is the more fragile of the
    /// two.
    /// </summary>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    public async Task Tokenize_DeeplyNestedConstructs_DoesNotOverflowTheStack()
    {
        var groups = Repeat("(", 200) + "a" + Repeat(")", 200);
        var classes = Repeat("[a-[", 60) + "b" + Repeat("]]", 60);

        var groupTokens = RegexPatternTokenizer.Tokenize(groups, Strict);
        var classTokens = RegexPatternTokenizer.Tokenize(classes, Strict);

        _ = await Assert.That(IsAcceptedByRegex(groups, Strict)).IsTrue();
        _ = await Assert.That(IsAcceptedByRegex(classes, Strict)).IsTrue();
        _ = await Assert.That(groupTokens.Length).IsEqualTo(401);
        _ = await Assert.That(Reconstruct(groupTokens)).IsEqualTo(groups);
        _ = await Assert.That(Reconstruct(classTokens)).IsEqualTo(classes);
        _ = await Assert.That(DescribeTiling(classes, classTokens)).IsEqualTo("tiles");
    }

    /// <summary>
    /// The digit escape path rewrites tokens after the fact, which is the one place in the tokenizer that
    /// could turn quadratic. Three hundred long digit runs are enough to expose it.
    /// </summary>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    public async Task Tokenize_ManyLongDigitRuns_StaysFast()
    {
        var pattern = "(a)" + Repeat(@"\1" + new string('0', 50), 300);
        var watch = Stopwatch.StartNew();

        var tokens = RegexPatternTokenizer.Tokenize(pattern, Strict);
        watch.Stop();

        _ = await Assert.That(tokens.Length).IsGreaterThan(300);
        _ = await Assert.That(Reconstruct(tokens)).IsEqualTo(pattern);
        _ = await Assert.That(DescribeTiling(pattern, tokens)).IsEqualTo("tiles");
        _ = await Assert.That(watch.ElapsedMilliseconds).IsLessThan(30_000L);
    }

    /// <summary>
    /// An empty pattern and a pattern that is nothing but one construct are the degenerate inputs a
    /// rewriter still has to survive.
    /// </summary>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    public async Task Tokenize_DegeneratePattern_ProducesAConsistentResult()
    {
        var empty = RegexPatternTokenizer.Tokenize(string.Empty, Extended);
        var whitespaceOnly = RegexPatternTokenizer.Tokenize(" \t\n", Extended);
        var commentOnly = RegexPatternTokenizer.Tokenize("(?#)", Strict);

        _ = await Assert.That(empty.IsEmpty).IsTrue();
        _ = await Assert.That(Describe(whitespaceOnly)).IsEqualTo("WhitespaceIgnored:0: \t\n");
        _ = await Assert.That(Describe(commentOnly)).IsEqualTo("Comment:0:(?#)");
        _ = await Assert.That(Reconstruct(whitespaceOnly)).IsEqualTo(" \t\n");
    }

    /// <summary>
    /// Renders a token sequence as <c>Kind:start:text</c> entries, matching the rendering the sibling test
    /// file uses so that an expectation can be moved between the two.
    /// </summary>
    /// <param name="tokens">The tokens to render.</param>
    /// <returns>The rendered sequence.</returns>
    private static string Describe(ImmutableArray<RegexToken> tokens) =>
        string.Join("|", tokens.Select(token => $"{token.Kind}:{token.Start}:{token.Text}"));

    /// <summary>
    /// Concatenates the token texts, which has to return the original pattern.
    /// </summary>
    /// <param name="tokens">The tokens to concatenate.</param>
    /// <returns>The reconstructed pattern.</returns>
    private static string Reconstruct(ImmutableArray<RegexToken> tokens)
    {
        if (tokens.IsDefaultOrEmpty)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var token in tokens)
        {
            _ = builder.Append(token.Text);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Checks that the tokens tile the pattern and that every token's text is the substring its own span
    /// names. A description rather than a boolean, so that a failure names the offending token instead of
    /// only saying <c>false</c>.
    /// </summary>
    /// <param name="pattern">The tokenized pattern.</param>
    /// <param name="tokens">The tokens of the pattern.</param>
    /// <returns><c>tiles</c> if the tokens tile the pattern; otherwise a description of the first offence.</returns>
    private static string DescribeTiling(string pattern, ImmutableArray<RegexToken> tokens)
    {
        var position = 0;
        foreach (var token in tokens)
        {
            if (token.Start != position)
            {
                return $"{token} starts at {token.Start} instead of {position}";
            }

            if (token.Length < 1 || token.End > pattern.Length)
            {
                return $"{token} does not fit into a pattern of length {pattern.Length}";
            }

            if (!string.Equals(token.Text, pattern.Substring(token.Start, token.Length), StringComparison.Ordinal))
            {
                return $"{token} does not match the pattern at its own span";
            }

            position = token.End;
        }

        return position == pattern.Length ? "tiles" : $"the tokens stop at {position} of {pattern.Length}";
    }

    /// <summary>
    /// Asks the real parser whether a pattern is legal. A malformed pattern raises
    /// <see cref="ArgumentException" />, which covers the <c>RegexParseException</c> of the modern runtimes
    /// and the plain <see cref="ArgumentException" /> of .NET Framework alike.
    /// </summary>
    /// <param name="pattern">The pattern to compile.</param>
    /// <param name="options">The options to compile it with.</param>
    /// <returns><see langword="true" /> if <see cref="Regex" /> accepts the pattern.</returns>
    private static bool IsAcceptedByRegex(string pattern, RegexOptions options)
    {
        try
        {
            _ = new Regex(pattern, options, _matchTimeout);

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Asks the real parser what a pattern <em>means</em>, which is the only oracle that can tell an ignored
    /// character from a matched one.
    /// </summary>
    /// <param name="pattern">The pattern to match with.</param>
    /// <param name="options">The options to compile it with.</param>
    /// <param name="input">The input to match.</param>
    /// <returns><see langword="true" /> if the pattern matches the input.</returns>
    private static bool Matches(string pattern, RegexOptions options, string input) =>
        new Regex(pattern, options, _matchTimeout).IsMatch(input);

    /// <summary>
    /// Repeats a unit, which builds the stress patterns without a loop in the test body.
    /// </summary>
    /// <param name="unit">The unit to repeat.</param>
    /// <param name="count">How often to repeat it.</param>
    /// <returns>The repeated string.</returns>
    private static string Repeat(string unit, int count)
    {
        var builder = new StringBuilder(unit.Length * count);
        for (var index = 0; index < count; index++)
        {
            _ = builder.Append(unit);
        }

        return builder.ToString();
    }
}
