namespace NetEvolve.FrameShift.Tests.Unit.Mutations.RegularExpressions;

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using NetEvolve.FrameShift.Mutations.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the regular expression tokenizer: the token sequence of every construct a .NET pattern knows,
/// the way <see cref="RegexOptions.IgnorePatternWhitespace" /> changes the grammar, the reported index and
/// reason for a malformed pattern, and the boundary between the lexical checks the tokenizer performs and
/// the semantic ones it leaves to a validity check.
/// </summary>
/// <remarks>
/// Every accepted pattern is additionally handed to <see cref="Regex" />, and every rejected one as well:
/// the real parser is the strongest available oracle, so a table entry that disagrees with it fails here
/// instead of surviving until an operator produces an invalid mutant.
/// </remarks>
internal sealed class RegexPatternTokenizerTests
{
    private const string CoverageWithOptions = @"(?x)^(a)\1 # c";
    private const string CoverageWithClass = @"[a-z-[q]]|\d*(?=x)(?#c)";
    private static readonly TimeSpan _parseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Every declared <see cref="RegexTokenKind" />. The generic overload only exists on the modern targets,
    /// so the classic ones keep the reflection based call the analyzers accept there.
    /// </summary>
    /// <returns>All declared kinds.</returns>
    private static RegexTokenKind[] AllKinds() =>
#if NET5_0_OR_GREATER
        Enum.GetValues<RegexTokenKind>();
#else
        (RegexTokenKind[])Enum.GetValues(typeof(RegexTokenKind));
#endif

    [Test]
    [Arguments("a", RegexOptions.None, "Literal:0:a")]
    [Arguments("abc", RegexOptions.None, "Literal:0:a|Literal:1:b|Literal:2:c")]
    [Arguments("a+", RegexOptions.None, "Literal:0:a|Quantifier:1:+")]
    [Arguments("a*?", RegexOptions.None, "Literal:0:a|Quantifier:1:*?")]
    [Arguments("a{2,3}?", RegexOptions.None, "Literal:0:a|Quantifier:1:{2,3}?")]
    [Arguments("^a$", RegexOptions.None, "Anchor:0:^|Literal:1:a|Anchor:2:$")]
    [Arguments("a|b", RegexOptions.None, "Literal:0:a|Alternation:1:||Literal:2:b")]
    [Arguments("a|", RegexOptions.None, "Literal:0:a|Alternation:1:|")]
    [Arguments("|a", RegexOptions.None, "Alternation:0:||Literal:1:a")]
    [Arguments("[]]", RegexOptions.None, "CharacterClassOpen:0:[|CharacterClassContent:1:]|CharacterClassClose:2:]")]
    [Arguments("[^]]", RegexOptions.None, "CharacterClassOpen:0:[^|CharacterClassContent:2:]|CharacterClassClose:3:]")]
    [Arguments(
        "[a+b]",
        RegexOptions.None,
        "CharacterClassOpen:0:[|CharacterClassContent:1:a|CharacterClassContent:2:+|CharacterClassContent:3:b|CharacterClassClose:4:]"
    )]
    [Arguments(
        "[a-z]",
        RegexOptions.None,
        "CharacterClassOpen:0:[|CharacterClassContent:1:a|CharacterClassRange:2:-|CharacterClassContent:3:z|CharacterClassClose:4:]"
    )]
    [Arguments(
        "[\\d-[a]]",
        RegexOptions.None,
        "CharacterClassOpen:0:[|Escape:1:\\d|CharacterClassSubtraction:3:-|CharacterClassOpen:4:[|CharacterClassContent:5:a|CharacterClassClose:6:]|CharacterClassClose:7:]"
    )]
    [Arguments(
        "[\\w-[\\d]]",
        RegexOptions.None,
        "CharacterClassOpen:0:[|Escape:1:\\w|CharacterClassSubtraction:3:-|CharacterClassOpen:4:[|Escape:5:\\d|CharacterClassClose:7:]|CharacterClassClose:8:]"
    )]
    [Arguments(
        "[a-[b-[c]]]",
        RegexOptions.None,
        "CharacterClassOpen:0:[|CharacterClassContent:1:a|CharacterClassSubtraction:2:-|CharacterClassOpen:3:[|CharacterClassContent:4:b|CharacterClassSubtraction:5:-|CharacterClassOpen:6:[|CharacterClassContent:7:c|CharacterClassClose:8:]|CharacterClassClose:9:]|CharacterClassClose:10:]"
    )]
    [Arguments(
        "[[a]",
        RegexOptions.None,
        "CharacterClassOpen:0:[|CharacterClassContent:1:[|CharacterClassContent:2:a|CharacterClassClose:3:]"
    )]
    [Arguments(
        "[-a]",
        RegexOptions.None,
        "CharacterClassOpen:0:[|CharacterClassContent:1:-|CharacterClassContent:2:a|CharacterClassClose:3:]"
    )]
    [Arguments(
        "[a-]",
        RegexOptions.None,
        "CharacterClassOpen:0:[|CharacterClassContent:1:a|CharacterClassContent:2:-|CharacterClassClose:3:]"
    )]
    [Arguments(
        "[a-b-c]",
        RegexOptions.None,
        "CharacterClassOpen:0:[|CharacterClassContent:1:a|CharacterClassRange:2:-|CharacterClassContent:3:b|CharacterClassContent:4:-|CharacterClassContent:5:c|CharacterClassClose:6:]"
    )]
    [Arguments(
        "[]-]",
        RegexOptions.None,
        "CharacterClassOpen:0:[|CharacterClassContent:1:]|CharacterClassContent:2:-|CharacterClassClose:3:]"
    )]
    [Arguments(
        "[^]a]",
        RegexOptions.None,
        "CharacterClassOpen:0:[^|CharacterClassContent:2:]|CharacterClassContent:3:a|CharacterClassClose:4:]"
    )]
    [Arguments(
        "\\d\\D\\w\\W\\s\\S",
        RegexOptions.None,
        "Escape:0:\\d|Escape:2:\\D|Escape:4:\\w|Escape:6:\\W|Escape:8:\\s|Escape:10:\\S"
    )]
    [Arguments("\\p{Lu}\\P{Lu}", RegexOptions.None, "Escape:0:\\p{Lu}|Escape:6:\\P{Lu}")]
    [Arguments("\\p{IsGreek}", RegexOptions.None, "Escape:0:\\p{IsGreek}")]
    [Arguments("\\x41", RegexOptions.None, "Escape:0:\\x41")]
    [Arguments("\\u0041", RegexOptions.None, "Escape:0:\\u0041")]
    [Arguments("\\cA", RegexOptions.None, "Escape:0:\\cA")]
    [Arguments("\\c@", RegexOptions.None, "Escape:0:\\c@")]
    [Arguments("\\012", RegexOptions.None, "Escape:0:\\012")]
    [Arguments("\\0", RegexOptions.None, "Escape:0:\\0")]
    [Arguments("\\08", RegexOptions.None, "Escape:0:\\0|Literal:2:8")]
    [Arguments(
        "\\a\\e\\f\\n\\r\\t\\v",
        RegexOptions.None,
        "Escape:0:\\a|Escape:2:\\e|Escape:4:\\f|Escape:6:\\n|Escape:8:\\r|Escape:10:\\t|Escape:12:\\v"
    )]
    [Arguments("\\-\\+\\#\\<", RegexOptions.None, "Escape:0:\\-|Escape:2:\\+|Escape:4:\\#|Escape:6:\\<")]
    [Arguments(
        "\\A\\z\\Z\\G\\b\\B",
        RegexOptions.None,
        "Anchor:0:\\A|Anchor:2:\\z|Anchor:4:\\Z|Anchor:6:\\G|Anchor:8:\\b|Anchor:10:\\B"
    )]
    [Arguments("[\\b]", RegexOptions.None, "CharacterClassOpen:0:[|Escape:1:\\b|CharacterClassClose:3:]")]
    [Arguments("[\\1]", RegexOptions.None, "CharacterClassOpen:0:[|Escape:1:\\1|CharacterClassClose:3:]")]
    [Arguments(
        "(?<n>a)\\k<n>",
        RegexOptions.None,
        "GroupOpen:0:(?<n>|Literal:5:a|GroupClose:6:)|Backreference:7:\\k<n>"
    )]
    [Arguments(
        "(?<n>a)\\k'n'",
        RegexOptions.None,
        "GroupOpen:0:(?<n>|Literal:5:a|GroupClose:6:)|Backreference:7:\\k'n'"
    )]
    [Arguments("(a)\\1", RegexOptions.None, "GroupOpen:0:(|Literal:1:a|GroupClose:2:)|Backreference:3:\\1")]
    [Arguments("(a)\\10", RegexOptions.None, "GroupOpen:0:(|Literal:1:a|GroupClose:2:)|Escape:3:\\10")]
    [Arguments("(?#c)a", RegexOptions.None, "Comment:0:(?#c)|Literal:5:a")]
    [Arguments("(?i)a", RegexOptions.None, "InlineOptions:0:(?i)|Literal:4:a")]
    [Arguments("(?i:a)", RegexOptions.None, "GroupOpen:0:(?i:|Literal:4:a|GroupClose:5:)")]
    [Arguments("(?-i)a", RegexOptions.None, "InlineOptions:0:(?-i)|Literal:5:a")]
    [Arguments("(?imnsx-imnsx:a)", RegexOptions.None, "GroupOpen:0:(?imnsx-imnsx:|Literal:14:a|GroupClose:15:)")]
    [Arguments("(?<name>a)", RegexOptions.None, "GroupOpen:0:(?<name>|Literal:8:a|GroupClose:9:)")]
    [Arguments("(?'name'a)", RegexOptions.None, "GroupOpen:0:(?'name'|Literal:8:a|GroupClose:9:)")]
    [Arguments(
        "(?<n>a)(?<-n>)",
        RegexOptions.None,
        "GroupOpen:0:(?<n>|Literal:5:a|GroupClose:6:)|GroupOpen:7:(?<-n>|GroupClose:13:)"
    )]
    [Arguments(
        "(?<n1>a)(?<n2>b)(?<n2-n1>c)",
        RegexOptions.None,
        "GroupOpen:0:(?<n1>|Literal:6:a|GroupClose:7:)|GroupOpen:8:(?<n2>|Literal:14:b|GroupClose:15:)|GroupOpen:16:(?<n2-n1>|Literal:25:c|GroupClose:26:)"
    )]
    [Arguments(
        "(?=a)(?!a)(?<=a)(?<!a)",
        RegexOptions.None,
        "Lookaround:0:(?=|Literal:3:a|GroupClose:4:)|Lookaround:5:(?!|Literal:8:a|GroupClose:9:)|Lookaround:10:(?<=|Literal:14:a|GroupClose:15:)|Lookaround:16:(?<!|Literal:20:a|GroupClose:21:)"
    )]
    [Arguments("(?>a)", RegexOptions.None, "GroupOpen:0:(?>|Literal:3:a|GroupClose:4:)")]
    [Arguments("(?:a)", RegexOptions.None, "GroupOpen:0:(?:|Literal:3:a|GroupClose:4:)")]
    [Arguments("()", RegexOptions.None, "GroupOpen:0:(|GroupClose:1:)")]
    [Arguments("a{", RegexOptions.None, "Literal:0:a|Literal:1:{")]
    [Arguments("a{2", RegexOptions.None, "Literal:0:a|Literal:1:{|Literal:2:2")]
    [Arguments("a{}", RegexOptions.None, "Literal:0:a|Literal:1:{|Literal:2:}")]
    [Arguments("{a}", RegexOptions.None, "Literal:0:{|Literal:1:a|Literal:2:}")]
    [Arguments("a{,3}", RegexOptions.None, "Literal:0:a|Literal:1:{|Literal:2:,|Literal:3:3|Literal:4:}")]
    [Arguments("a{ 2}", RegexOptions.None, "Literal:0:a|Literal:1:{|Literal:2: |Literal:3:2|Literal:4:}")]
    [Arguments("^*", RegexOptions.None, "Anchor:0:^|Quantifier:1:*")]
    [Arguments("(?<=a)*", RegexOptions.None, "Lookaround:0:(?<=|Literal:4:a|GroupClose:5:)|Quantifier:6:*")]
    [Arguments("(?n)(a)", RegexOptions.None, "InlineOptions:0:(?n)|GroupOpen:4:(|Literal:5:a|GroupClose:6:)")]
    [Arguments(
        "(?(?=a)b|c)",
        RegexOptions.None,
        "GroupOpen:0:(?|Lookaround:2:(?=|Literal:5:a|GroupClose:6:)|Literal:7:b|Alternation:8:||Literal:9:c|GroupClose:10:)"
    )]
    [Arguments(
        "(a)(?(1)a|b)",
        RegexOptions.None,
        "GroupOpen:0:(|Literal:1:a|GroupClose:2:)|GroupOpen:3:(?|GroupOpen:5:(|Literal:6:1|GroupClose:7:)|Literal:8:a|Alternation:9:||Literal:10:b|GroupClose:11:)"
    )]
    [Arguments(
        "(?<n>a)(?<-n>)(?(n)a|b)",
        RegexOptions.None,
        "GroupOpen:0:(?<n>|Literal:5:a|GroupClose:6:)|GroupOpen:7:(?<-n>|GroupClose:13:)|GroupOpen:14:(?|GroupOpen:16:(|Literal:17:n|GroupClose:18:)|Literal:19:a|Alternation:20:||Literal:21:b|GroupClose:22:)"
    )]
    [Arguments("#c", RegexOptions.None, "Literal:0:#|Literal:1:c")]
    [Arguments("a#c", RegexOptions.None, "Literal:0:a|Literal:1:#|Literal:2:c")]
    [Arguments("[#]", RegexOptions.None, "CharacterClassOpen:0:[|CharacterClassContent:1:#|CharacterClassClose:2:]")]
    [Arguments("\\ ", RegexOptions.None, "Escape:0:\\ ")]
    [Arguments("a\\ b", RegexOptions.None, "Literal:0:a|Escape:1:\\ |Literal:3:b")]
    [Arguments("[ ]", RegexOptions.None, "CharacterClassOpen:0:[|CharacterClassContent:1: |CharacterClassClose:2:]")]
    [Arguments("a b", RegexOptions.IgnorePatternWhitespace, "Literal:0:a|WhitespaceIgnored:1: |Literal:2:b")]
    [Arguments("a b", RegexOptions.None, "Literal:0:a|Literal:1: |Literal:2:b")]
    [Arguments("(?x)a b", RegexOptions.None, "InlineOptions:0:(?x)|Literal:4:a|WhitespaceIgnored:5: |Literal:6:b")]
    [Arguments(
        "(?x)a#c\nb",
        RegexOptions.None,
        "InlineOptions:0:(?x)|Literal:4:a|Comment:5:#c|WhitespaceIgnored:7:\n|Literal:8:b"
    )]
    [Arguments("(?x)#only", RegexOptions.None, "InlineOptions:0:(?x)|Comment:4:#only")]
    [Arguments("(?x)  ", RegexOptions.None, "InlineOptions:0:(?x)|WhitespaceIgnored:4:  ")]
    [Arguments(
        "(?x)[ a ]",
        RegexOptions.None,
        "InlineOptions:0:(?x)|CharacterClassOpen:4:[|CharacterClassContent:5: |CharacterClassContent:6:a|CharacterClassContent:7: |CharacterClassClose:8:]"
    )]
    [Arguments(
        "(?x)(?-x)a b",
        RegexOptions.None,
        "InlineOptions:0:(?x)|InlineOptions:4:(?-x)|Literal:9:a|Literal:10: |Literal:11:b"
    )]
    [Arguments(
        "(?-x:a b)",
        RegexOptions.IgnorePatternWhitespace,
        "GroupOpen:0:(?-x:|Literal:5:a|Literal:6: |Literal:7:b|GroupClose:8:)"
    )]
    [Arguments("(?x)a *", RegexOptions.None, "InlineOptions:0:(?x)|Literal:4:a|WhitespaceIgnored:5: |Quantifier:6:*")]
    [Arguments("(?x)\\ ", RegexOptions.None, "InlineOptions:0:(?x)|Escape:4:\\ ")]
    [Arguments(
        "(?x)a{ 2}",
        RegexOptions.None,
        "InlineOptions:0:(?x)|Literal:4:a|Literal:5:{|WhitespaceIgnored:6: |Literal:7:2|Literal:8:}"
    )]
    [Arguments(
        "(?x)(a\n#c\n)",
        RegexOptions.None,
        "InlineOptions:0:(?x)|GroupOpen:4:(|Literal:5:a|WhitespaceIgnored:6:\n|Comment:7:#c|WhitespaceIgnored:9:\n|GroupClose:10:)"
    )]
    [Arguments("#c", RegexOptions.IgnorePatternWhitespace, "Comment:0:#c")]
    [Arguments(
        "a#c\nb",
        RegexOptions.IgnorePatternWhitespace,
        "Literal:0:a|Comment:1:#c|WhitespaceIgnored:3:\n|Literal:4:b"
    )]
    [Arguments(
        "[#]",
        RegexOptions.IgnorePatternWhitespace,
        "CharacterClassOpen:0:[|CharacterClassContent:1:#|CharacterClassClose:2:]"
    )]
    [Arguments(
        "[ ]",
        RegexOptions.IgnorePatternWhitespace,
        "CharacterClassOpen:0:[|CharacterClassContent:1: |CharacterClassClose:2:]"
    )]
    [Arguments("\\ ", RegexOptions.IgnorePatternWhitespace, "Escape:0:\\ ")]
    [Arguments("a\\ b", RegexOptions.IgnorePatternWhitespace, "Literal:0:a|Escape:1:\\ |Literal:3:b")]
    [Arguments(
        "(?-x)a b",
        RegexOptions.IgnorePatternWhitespace,
        "InlineOptions:0:(?-x)|Literal:5:a|Literal:6: |Literal:7:b"
    )]
    [Arguments(
        "(?x:a b)c",
        RegexOptions.None,
        "GroupOpen:0:(?x:|Literal:4:a|WhitespaceIgnored:5: |Literal:6:b|GroupClose:7:)|Literal:8:c"
    )]
    [Arguments("\\p{IsBasicLatin}", RegexOptions.None, "Escape:0:\\p{IsBasicLatin}")]
    [Arguments(
        "[\\p{Lu}-[a]]",
        RegexOptions.None,
        "CharacterClassOpen:0:[|Escape:1:\\p{Lu}|CharacterClassSubtraction:7:-|CharacterClassOpen:8:[|CharacterClassContent:9:a|CharacterClassClose:10:]|CharacterClassClose:11:]"
    )]
    [Arguments(
        "[\\s-[\\r\\n]]",
        RegexOptions.None,
        "CharacterClassOpen:0:[|Escape:1:\\s|CharacterClassSubtraction:3:-|CharacterClassOpen:4:[|Escape:5:\\r|Escape:7:\\n|CharacterClassClose:9:]|CharacterClassClose:10:]"
    )]
    [Arguments("a{2}?", RegexOptions.None, "Literal:0:a|Quantifier:1:{2}?")]
    [Arguments("a{10,}?", RegexOptions.None, "Literal:0:a|Quantifier:1:{10,}?")]
    [Arguments("(?#)", RegexOptions.None, "Comment:0:(?#)")]
    [Arguments(
        "(?x)(?i)a b",
        RegexOptions.None,
        "InlineOptions:0:(?x)|InlineOptions:4:(?i)|Literal:8:a|WhitespaceIgnored:9: |Literal:10:b"
    )]
    [Arguments("(?xi)a b", RegexOptions.None, "InlineOptions:0:(?xi)|Literal:5:a|WhitespaceIgnored:6: |Literal:7:b")]
    [Arguments("(?-)a", RegexOptions.None, "InlineOptions:0:(?-)|Literal:4:a")]
    [Arguments("(?i-)a", RegexOptions.None, "InlineOptions:0:(?i-)|Literal:5:a")]
    [Arguments("(?<1>a)", RegexOptions.None, "GroupOpen:0:(?<1>|Literal:5:a|GroupClose:6:)")]
    [Arguments("(?<a1>a)", RegexOptions.None, "GroupOpen:0:(?<a1>|Literal:6:a|GroupClose:7:)")]
    [Arguments("(?<_a>a)", RegexOptions.None, "GroupOpen:0:(?<_a>|Literal:6:a|GroupClose:7:)")]
    [Arguments(
        "(a)(?<-1>b)",
        RegexOptions.None,
        "GroupOpen:0:(|Literal:1:a|GroupClose:2:)|GroupOpen:3:(?<-1>|Literal:9:b|GroupClose:10:)"
    )]
    [Arguments("(?<-0>a)", RegexOptions.None, "GroupOpen:0:(?<-0>|Literal:6:a|GroupClose:7:)")]
    [Arguments("a(?#c)*", RegexOptions.None, "Literal:0:a|Comment:1:(?#c)|Quantifier:6:*")]
    [Arguments("(?x)a\nb", RegexOptions.None, "InlineOptions:0:(?x)|Literal:4:a|WhitespaceIgnored:5:\n|Literal:6:b")]
    [Arguments(
        "[\\x41-\\x5A]",
        RegexOptions.None,
        "CharacterClassOpen:0:[|Escape:1:\\x41|CharacterClassRange:5:-|Escape:6:\\x5A|CharacterClassClose:10:]"
    )]
    [Arguments(
        "[\\--a]",
        RegexOptions.None,
        "CharacterClassOpen:0:[|Escape:1:\\-|CharacterClassRange:3:-|CharacterClassContent:4:a|CharacterClassClose:5:]"
    )]
    [Arguments(
        "(?<n>a)[\\0]",
        RegexOptions.None,
        "GroupOpen:0:(?<n>|Literal:5:a|GroupClose:6:)|CharacterClassOpen:7:[|Escape:8:\\0|CharacterClassClose:10:]"
    )]
    [Arguments("a b#c", RegexOptions.None, "Literal:0:a|Literal:1: |Literal:2:b|Literal:3:#|Literal:4:c")]
    [Arguments(
        "a b#c",
        RegexOptions.IgnorePatternWhitespace,
        "Literal:0:a|WhitespaceIgnored:1: |Literal:2:b|Comment:3:#c"
    )]
    [Arguments("# nothing but a comment", RegexOptions.IgnorePatternWhitespace, "Comment:0:# nothing but a comment")]
    [Arguments("^\\d{2,4}$", RegexOptions.None, "Anchor:0:^|Escape:1:\\d|Quantifier:3:{2,4}|Anchor:8:$")]
    [Arguments("^\\d{2,}$", RegexOptions.None, "Anchor:0:^|Escape:1:\\d|Quantifier:3:{2,}|Anchor:7:$")]
    [Arguments(
        "(?x)^(a)\\1 # c",
        RegexOptions.None,
        "InlineOptions:0:(?x)|Anchor:4:^|GroupOpen:5:(|Literal:6:a|GroupClose:7:)|Backreference:8:\\1|WhitespaceIgnored:10: |Comment:11:# c"
    )]
    [Arguments(
        "[a-z-[q]]|\\d*(?=x)(?#c)",
        RegexOptions.None,
        "CharacterClassOpen:0:[|CharacterClassContent:1:a|CharacterClassRange:2:-|CharacterClassContent:3:z|CharacterClassSubtraction:4:-|CharacterClassOpen:5:[|CharacterClassContent:6:q|CharacterClassClose:7:]|CharacterClassClose:8:]|Alternation:9:||Escape:10:\\d|Quantifier:12:*|Lookaround:13:(?=|Literal:16:x|GroupClose:17:)|Comment:18:(?#c)"
    )]
    [Arguments(
        "[\\w-[\\d]]{2,}?",
        RegexOptions.None,
        "CharacterClassOpen:0:[|Escape:1:\\w|CharacterClassSubtraction:3:-|CharacterClassOpen:4:[|Escape:5:\\d|CharacterClassClose:7:]|CharacterClassClose:8:]|Quantifier:9:{2,}?"
    )]
    public async Task Tokenize_WellFormedPattern_ProducesTheExpectedTokens(
        string pattern,
        RegexOptions options,
        string expected
    )
    {
        var tokens = RegexPatternTokenizer.Tokenize(pattern, options);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Describe(tokens)).IsEqualTo(expected);
            _ = await Assert.That(Covers(pattern, tokens)).IsTrue();
            _ = await Assert.That(IsAcceptedByRegex(pattern, options)).IsTrue();
        }
    }

    [Test]
    [Arguments(
        "\\",
        RegexOptions.None,
        0,
        "The pattern ends with a single backslash, so its escape sequence is incomplete."
    )]
    [Arguments("\\Q", RegexOptions.None, 0, "The escape sequence '\\Q' is not recognized by .NET.")]
    [Arguments("\\E", RegexOptions.None, 0, "The escape sequence '\\E' is not recognized by .NET.")]
    [Arguments("\\m", RegexOptions.None, 0, "The escape sequence '\\m' is not recognized by .NET.")]
    [Arguments("\\_", RegexOptions.None, 0, "The escape sequence '\\_' is not recognized by .NET.")]
    [Arguments("\\R", RegexOptions.None, 0, "The escape sequence '\\R' is not recognized by .NET.")]
    [Arguments("\\pL", RegexOptions.None, 0, "The '\\p' escape requires a property name in braces, e.g. '\\p{Lu}'.")]
    [Arguments("\\p{", RegexOptions.None, 0, "The '\\p{' escape is never closed by '}'.")]
    [Arguments("\\x4", RegexOptions.None, 0, "The '\\x' escape requires exactly 2 hexadecimal digits.")]
    [Arguments("\\u041", RegexOptions.None, 0, "The '\\u' escape requires exactly 4 hexadecimal digits.")]
    [Arguments("\\c1", RegexOptions.None, 0, "The '\\c' escape requires a control character, e.g. '\\cA'.")]
    [Arguments("\\c`", RegexOptions.None, 0, "The '\\c' escape requires a control character, e.g. '\\cA'.")]
    [Arguments("[]", RegexOptions.None, 0, "The character class opened here is never closed by ']'.")]
    [Arguments("[a", RegexOptions.None, 0, "The character class opened here is never closed by ']'.")]
    [Arguments("[^]", RegexOptions.None, 0, "The character class opened here is never closed by ']'.")]
    [Arguments("[a-[b]", RegexOptions.None, 0, "The character class opened here is never closed by ']'.")]
    [Arguments("(", RegexOptions.None, 0, "The group opened here is never closed by ')'.")]
    [Arguments(")", RegexOptions.None, 0, "The ')' closes a group that was never opened.")]
    [Arguments("a)", RegexOptions.None, 1, "The ')' closes a group that was never opened.")]
    [Arguments("(?", RegexOptions.None, 0, "The '(?' is followed by an unrecognized grouping construct.")]
    [Arguments("(?i", RegexOptions.None, 0, "The '(?' is followed by an unrecognized grouping construct.")]
    [Arguments("(?y)a", RegexOptions.None, 0, "The '(?' is followed by an unrecognized grouping construct.")]
    [Arguments("(?P<n>a)", RegexOptions.None, 0, "The '(?' is followed by an unrecognized grouping construct.")]
    [Arguments("(?~a)", RegexOptions.None, 0, "The '(?' is followed by an unrecognized grouping construct.")]
    [Arguments("(?)", RegexOptions.None, 0, "The '(?' is followed by an unrecognized grouping construct.")]
    [Arguments("(?<>a)", RegexOptions.None, 3, "A named group requires a name.")]
    [Arguments("(?<1a>a)", RegexOptions.None, 3, "'1a' is not a valid group name.")]
    [Arguments("(?<a b>c)", RegexOptions.None, 3, "'a b' is not a valid group name.")]
    [Arguments("(?'n>a)", RegexOptions.None, 0, "The named group opened here is never closed by a single quote.")]
    [Arguments("(?<n'a)", RegexOptions.None, 0, "The named group opened here is never closed by '>'.")]
    [Arguments(
        "a**",
        RegexOptions.None,
        2,
        "The quantifier '*' follows another quantifier, which is a nested quantifier."
    )]
    [Arguments(
        "a*+",
        RegexOptions.None,
        2,
        "The quantifier '+' follows another quantifier, which is a nested quantifier."
    )]
    [Arguments(
        "a*??",
        RegexOptions.None,
        3,
        "The quantifier '?' follows another quantifier, which is a nested quantifier."
    )]
    [Arguments(
        "a{2}{3}",
        RegexOptions.None,
        4,
        "The quantifier '{' follows another quantifier, which is a nested quantifier."
    )]
    [Arguments("a{3,2}", RegexOptions.None, 1, "The quantifier specifies a maximum that is smaller than its minimum.")]
    [Arguments("a{2147483648}", RegexOptions.None, 1, "A quantifier bound must not exceed Int32.MaxValue.")]
    [Arguments("*a", RegexOptions.None, 0, "The quantifier '*' follows nothing that could be repeated.")]
    [Arguments("{2}", RegexOptions.None, 0, "The quantifier '{' follows nothing that could be repeated.")]
    [Arguments("(*)", RegexOptions.None, 1, "The quantifier '*' follows nothing that could be repeated.")]
    [Arguments("a|*", RegexOptions.None, 2, "The quantifier '*' follows nothing that could be repeated.")]
    [Arguments("(?i)*", RegexOptions.None, 4, "The quantifier '*' follows nothing that could be repeated.")]
    [Arguments("(?#c)*", RegexOptions.None, 5, "The quantifier '*' follows nothing that could be repeated.")]
    [Arguments("(?# unterminated", RegexOptions.None, 0, "The '(?#' comment is never closed by ')'.")]
    [Arguments("[\\A]", RegexOptions.None, 1, "The escape sequence '\\A' is not allowed inside a character class.")]
    [Arguments(
        "[\\8]",
        RegexOptions.None,
        1,
        "The digit escape '\\8' is not octal and therefore not allowed inside a character class."
    )]
    [Arguments("[\\k<n>]", RegexOptions.None, 1, "The escape sequence '\\k' is not allowed inside a character class.")]
    [Arguments(
        "\\k<n",
        RegexOptions.None,
        0,
        "The '\\k' backreference requires a group name in '<...>' or in \"'...'\"."
    )]
    [Arguments(
        "(?<n>a)\\kn",
        RegexOptions.None,
        7,
        "The '\\k' backreference requires a group name in '<...>' or in \"'...'\"."
    )]
    [Arguments(
        "(?x)a* *",
        RegexOptions.None,
        7,
        "The quantifier '*' follows another quantifier, which is a nested quantifier."
    )]
    [Arguments("(?x) *", RegexOptions.None, 5, "The quantifier '*' follows nothing that could be repeated.")]
    [Arguments("(?x)(a#c)b", RegexOptions.None, 4, "The group opened here is never closed by ')'.")]
    [Arguments("[\\pL]", RegexOptions.None, 1, "The '\\p' escape requires a property name in braces, e.g. '\\p{Lu}'.")]
    [Arguments("(?<n>a", RegexOptions.None, 0, "The group opened here is never closed by ')'.")]
    [Arguments(
        "a* *",
        RegexOptions.IgnorePatternWhitespace,
        3,
        "The quantifier '*' follows another quantifier, which is a nested quantifier."
    )]
    public async Task TryTokenize_MalformedPattern_ReportsTheOffendingIndexAndTheReason(
        string pattern,
        RegexOptions options,
        int expectedIndex,
        string expectedError
    )
    {
        var tokenized = RegexPatternTokenizer.TryTokenize(
            pattern,
            options,
            out var tokens,
            out var index,
            out var error
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(tokenized).IsFalse();
            _ = await Assert.That(tokens.IsEmpty).IsTrue();
            _ = await Assert.That(index).IsEqualTo(expectedIndex);
            _ = await Assert.That(error).IsEqualTo(expectedError);
            _ = await Assert.That(IsAcceptedByRegex(pattern, options)).IsFalse();
        }
    }

    /// <summary>
    /// The tokenizer is a lexer, so it accepts a pattern whose only problem is semantic: an undefined
    /// backreference, an unknown Unicode property, a reversed range or a class inside a range. Deciding
    /// those is the job of a validity check, and this test pins the boundary rather than leaving it to
    /// chance.
    /// </summary>
    /// <param name="pattern">The pattern that tokenizes although .NET rejects it.</param>
    /// <param name="expected">The expected token sequence.</param>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    [Arguments("\\1", "Backreference:0:\\1")]
    [Arguments("\\8", "Backreference:0:\\8")]
    [Arguments("\\k<n>", "Backreference:0:\\k<n>")]
    [Arguments("(?<-name>)", "GroupOpen:0:(?<-name>|GroupClose:9:)")]
    [Arguments("\\p{Foo}", "Escape:0:\\p{Foo}")]
    [Arguments(
        "[z-a]",
        "CharacterClassOpen:0:[|CharacterClassContent:1:z|CharacterClassRange:2:-|CharacterClassContent:3:a|CharacterClassClose:4:]"
    )]
    [Arguments(
        "(?(1)a|b)",
        "GroupOpen:0:(?|GroupOpen:2:(|Literal:3:1|GroupClose:4:)|Literal:5:a|Alternation:6:||Literal:7:b|GroupClose:8:)"
    )]
    [Arguments("(?<a-b>x)", "GroupOpen:0:(?<a-b>|Literal:7:x|GroupClose:8:)")]
    [Arguments(
        "[a-\\d]",
        "CharacterClassOpen:0:[|CharacterClassContent:1:a|CharacterClassRange:2:-|Escape:3:\\d|CharacterClassClose:5:]"
    )]
    public async Task Tokenize_SemanticallyInvalidPattern_TokenizesAlthoughRegexRejectsIt(
        string pattern,
        string expected
    )
    {
        var tokens = RegexPatternTokenizer.Tokenize(pattern, RegexOptions.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Describe(tokens)).IsEqualTo(expected);
            _ = await Assert.That(Covers(pattern, tokens)).IsTrue();
            _ = await Assert.That(IsAcceptedByRegex(pattern, RegexOptions.None)).IsFalse();
        }
    }

    [Test]
    public async Task Tokenize_EmptyPattern_ProducesNoTokens()
    {
        var tokens = RegexPatternTokenizer.Tokenize(string.Empty, RegexOptions.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(tokens.IsEmpty).IsTrue();
            _ = await Assert.That(Describe(tokens)).IsEqualTo(string.Empty);
        }
    }

    [Test]
    public async Task Tokenize_CommentOnlyPattern_ProducesASingleCommentToken()
    {
        const string pattern = "# nothing but a comment";

        var tokens = RegexPatternTokenizer.Tokenize(pattern, RegexOptions.IgnorePatternWhitespace);

        using (Assert.Multiple())
        {
            _ = await Assert.That(tokens.Length).IsEqualTo(1);
            _ = await Assert.That(tokens[0].Kind).IsEqualTo(RegexTokenKind.Comment);
            _ = await Assert.That(tokens[0].Text).IsEqualTo(pattern);
            _ = await Assert.That(tokens[0].Start).IsEqualTo(0);
            _ = await Assert.That(tokens[0].End).IsEqualTo(pattern.Length);
        }
    }

    /// <summary>
    /// The same pattern under the two grammars: without
    /// <see cref="RegexOptions.IgnorePatternWhitespace" /> the space and the <c>#</c> are literals, with it
    /// the space disappears and the <c>#</c> starts a comment that runs to the end of the line.
    /// </summary>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    public async Task Tokenize_IgnorePatternWhitespace_ChangesTheGrammarOfTheSamePattern()
    {
        const string pattern = "a b#c";

        var strict = RegexPatternTokenizer.Tokenize(pattern, RegexOptions.None);
        var relaxed = RegexPatternTokenizer.Tokenize(pattern, RegexOptions.IgnorePatternWhitespace);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Describe(strict))
                .IsEqualTo("Literal:0:a|Literal:1: |Literal:2:b|Literal:3:#|Literal:4:c");
            _ = await Assert
                .That(Describe(relaxed))
                .IsEqualTo("Literal:0:a|WhitespaceIgnored:1: |Literal:2:b|Comment:3:#c");
            _ = await Assert.That(Describe(strict)).IsNotEqualTo(Describe(relaxed));
            _ = await Assert.That(IsAcceptedByRegex(pattern, RegexOptions.None)).IsTrue();
            _ = await Assert.That(IsAcceptedByRegex(pattern, RegexOptions.IgnorePatternWhitespace)).IsTrue();
        }
    }

    /// <summary>
    /// An inline <c>(?x)</c> only reaches the end of the group it sits in, so the space behind the group is
    /// a literal again.
    /// </summary>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    public async Task Tokenize_ScopedIgnorePatternWhitespace_EndsWithItsGroup()
    {
        const string pattern = "(?x:a b)c d";

        var tokens = RegexPatternTokenizer.Tokenize(pattern, RegexOptions.None);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Describe(tokens))
                .IsEqualTo(
                    "GroupOpen:0:(?x:|Literal:4:a|WhitespaceIgnored:5: |Literal:6:b|GroupClose:7:)"
                        + "|Literal:8:c|Literal:9: |Literal:10:d"
                );
            _ = await Assert.That(IsAcceptedByRegex(pattern, RegexOptions.None)).IsTrue();
        }
    }

    /// <summary>
    /// A digit run that names no capture group is not a shortened backreference: .NET re-reads it as an
    /// octal escape, so with a single group <c>\10</c> is the single character U+0008 rather than group one
    /// followed by the literal <c>0</c>.
    /// </summary>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    public async Task Tokenize_DigitRunBeyondTheGroupCount_IsAnOctalEscape()
    {
        const string pattern = @"(a)\10";

        var tokens = RegexPatternTokenizer.Tokenize(pattern, RegexOptions.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Describe(tokens)).IsEqualTo("GroupOpen:0:(|Literal:1:a|GroupClose:2:)|Escape:3:\\10");
            _ = await Assert.That(IsAcceptedByRegex(pattern, RegexOptions.None)).IsTrue();
        }
    }

    [Test]
    public async Task Tokenize_BackreferenceWithinTheGroupCount_KeepsEveryDigit()
    {
        const string pattern = @"(a)(b)(c)(d)(e)(f)(g)(h)(i)(j)\10";

        var tokens = RegexPatternTokenizer.Tokenize(pattern, RegexOptions.None);
        var last = tokens[tokens.Length - 1];

        using (Assert.Multiple())
        {
            _ = await Assert.That(last.Kind).IsEqualTo(RegexTokenKind.Backreference);
            _ = await Assert.That(last.Text).IsEqualTo(@"\10");
            _ = await Assert.That(last.Start).IsEqualTo(30);
            _ = await Assert.That(Covers(pattern, tokens)).IsTrue();
            _ = await Assert.That(IsAcceptedByRegex(pattern, RegexOptions.None)).IsTrue();
        }
    }

    /// <summary>
    /// A named group is a capture as well, so it lengthens the backreference that follows it. Without the
    /// named group the same pattern would read <c>\1</c> plus a literal.
    /// </summary>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    public async Task Tokenize_NamedGroupCapture_CountsTowardsTheBackreferenceNumber()
    {
        const string pattern = @"(a)(?<n>b)\2";

        var tokens = RegexPatternTokenizer.Tokenize(pattern, RegexOptions.None);
        var last = tokens[tokens.Length - 1];

        using (Assert.Multiple())
        {
            _ = await Assert.That(last.Kind).IsEqualTo(RegexTokenKind.Backreference);
            _ = await Assert.That(last.Text).IsEqualTo(@"\2");
            _ = await Assert.That(IsAcceptedByRegex(pattern, RegexOptions.None)).IsTrue();
        }
    }

    [Test]
    public async Task TryTokenize_WellFormedPattern_ReportsNoError()
    {
        var tokenized = RegexPatternTokenizer.TryTokenize(
            @"^\d+$",
            RegexOptions.None,
            out var tokens,
            out var index,
            out var error
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(tokenized).IsTrue();
            _ = await Assert.That(Describe(tokens)).IsEqualTo(@"Anchor:0:^|Escape:1:\d|Quantifier:3:+|Anchor:4:$");
            _ = await Assert.That(index).IsEqualTo(-1);
            _ = await Assert.That(error).IsNull();
        }
    }

    [Test]
    public async Task Tokenize_MalformedPattern_ThrowsArgumentExceptionNamingTheIndexAndTheReason()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            RegexPatternTokenizer.Tokenize("a(b", RegexOptions.None)
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(exception.ParamName).IsEqualTo("pattern");
            _ = await Assert.That(exception.Message).Contains("malformed at index 1");
            _ = await Assert.That(exception.Message).Contains("never closed by ')'");
        }
    }

    [Test]
    public async Task Tokenize_PatternIsNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            RegexPatternTokenizer.Tokenize(null!, RegexOptions.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("pattern");
    }

    [Test]
    public async Task TryTokenize_PatternIsNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            RegexPatternTokenizer.TryTokenize(null!, RegexOptions.None, out _, out _, out _)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("pattern");
    }

    /// <summary>
    /// Two patterns are enough to produce every token kind, which keeps the enum and the tokenizer in step:
    /// a kind nothing can produce would fail here.
    /// </summary>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    public async Task Tokenize_TwoPatterns_ProduceEveryTokenKind()
    {
        var produced = RegexPatternTokenizer
            .Tokenize(CoverageWithOptions, RegexOptions.None)
            .Concat(RegexPatternTokenizer.Tokenize(CoverageWithClass, RegexOptions.None))
            .Select(token => token.Kind)
            .Distinct()
            .OrderBy(kind => kind)
            .ToArray();
        var all = AllKinds().OrderBy(kind => kind).ToArray();

        using (Assert.Multiple())
        {
            _ = await Assert.That(produced).IsEquivalentTo(all);
            _ = await Assert.That(IsAcceptedByRegex(CoverageWithOptions, RegexOptions.None)).IsTrue();
            _ = await Assert.That(IsAcceptedByRegex(CoverageWithClass, RegexOptions.None)).IsTrue();
        }
    }

    /// <summary>
    /// The point of the exact spans: a rewriter splices a replacement into the span of a single token and
    /// gets a pattern .NET still accepts, without ever looking at the surrounding characters.
    /// </summary>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    public async Task Tokenize_QuantifierSpan_IsEnoughToRewriteThePattern()
    {
        const string pattern = @"^\d{2,4}$";

        var tokens = RegexPatternTokenizer.Tokenize(pattern, RegexOptions.None);
        var quantifier = tokens.Single(token => token.Kind == RegexTokenKind.Quantifier);
        var mutated = pattern.Remove(quantifier.Start, quantifier.Length).Insert(quantifier.Start, "{2,}");

        using (Assert.Multiple())
        {
            _ = await Assert.That(quantifier.Text).IsEqualTo("{2,4}");
            _ = await Assert.That(quantifier.Start).IsEqualTo(3);
            _ = await Assert.That(quantifier.Length).IsEqualTo(5);
            _ = await Assert.That(mutated).IsEqualTo(@"^\d{2,}$");
            _ = await Assert.That(IsAcceptedByRegex(mutated, RegexOptions.None)).IsTrue();
        }
    }

    [Test]
    public async Task RegexToken_TextIsNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = new RegexToken(RegexTokenKind.Literal, 0, null!)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("text");
    }

    [Test]
    public async Task RegexToken_TextIsEmpty_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            _ = new RegexToken(RegexTokenKind.Literal, 0, string.Empty)
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(exception.ParamName).IsEqualTo("text");
            _ = await Assert.That(exception.Message).Contains("at least one character");
        }
    }

    [Test]
    public async Task RegexToken_StartIsNegative_ThrowsArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            _ = new RegexToken(RegexTokenKind.Literal, -1, "a")
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("start");
    }

    [Test]
    public async Task RegexToken_SameKindStartAndText_AreEqual()
    {
        var token = new RegexToken(RegexTokenKind.Quantifier, 3, "{2,4}");
        var same = new RegexToken(RegexTokenKind.Quantifier, 3, "{2,4}");

        using (Assert.Multiple())
        {
            _ = await Assert.That(token.Equals(same)).IsTrue();
            _ = await Assert.That(token.Equals((object)same)).IsTrue();
            _ = await Assert.That(token.GetHashCode()).IsEqualTo(same.GetHashCode());
            _ = await Assert.That(token.Length).IsEqualTo(5);
            _ = await Assert.That(token.End).IsEqualTo(8);
            _ = await Assert.That(token.ToString()).IsEqualTo("Quantifier[3..8)='{2,4}'");
        }
    }

    /// <summary>
    /// The inequality half of the equality contract, including both null forms. The analyzer is told that the
    /// typed null comparison is intentionally a constant: proving it answers <see langword="false" /> rather
    /// than throwing is the whole point of the assertion.
    /// </summary>
    /// <returns>A task that represents the asynchronous assertions.</returns>
    [Test]
    [SuppressMessage(
        "Reliability",
        "CA1508:Avoid dead conditional code",
        Justification = "Comparing against a typed null is the behaviour under test."
    )]
    public async Task RegexToken_DifferentKindStartOrText_AreNotEqual()
    {
        var token = new RegexToken(RegexTokenKind.Quantifier, 3, "{2,4}");

        using (Assert.Multiple())
        {
            _ = await Assert.That(token.Equals(new RegexToken(RegexTokenKind.Literal, 3, "{2,4}"))).IsFalse();
            _ = await Assert.That(token.Equals(new RegexToken(RegexTokenKind.Quantifier, 4, "{2,4}"))).IsFalse();
            _ = await Assert.That(token.Equals(new RegexToken(RegexTokenKind.Quantifier, 3, "{2,3}"))).IsFalse();
            _ = await Assert.That(token.Equals((object)"{2,4}")).IsFalse();
            _ = await Assert.That(token.Equals(null)).IsFalse();
        }
    }

    /// <summary>
    /// Renders a token sequence as <c>Kind:start:text</c> entries, which is what the tables assert.
    /// </summary>
    /// <param name="tokens">The tokens to render.</param>
    /// <returns>The rendered sequence.</returns>
    private static string Describe(ImmutableArray<RegexToken> tokens) =>
        string.Join("|", tokens.Select(token => $"{token.Kind}:{token.Start}:{token.Text}"));

    /// <summary>
    /// Verifies the contract a rewriter depends on: the tokens tile the pattern in order, without a gap,
    /// without an overlap and without a missing tail.
    /// </summary>
    /// <param name="pattern">The tokenized pattern.</param>
    /// <param name="tokens">The tokens of the pattern.</param>
    /// <returns><see langword="true" /> if the tokens cover the pattern exactly.</returns>
    private static bool Covers(string pattern, ImmutableArray<RegexToken> tokens)
    {
        var position = 0;
        foreach (var token in tokens)
        {
            if (token.Start != position || token.Length < 1)
            {
                return false;
            }

            position = token.End;
        }

        return position == pattern.Length;
    }

    /// <summary>
    /// Asks the real parser whether a pattern is legal, which is the oracle every table entry is held
    /// against.
    /// </summary>
    /// <param name="pattern">The pattern to compile.</param>
    /// <param name="options">The options to compile it with.</param>
    /// <returns><see langword="true" /> if <see cref="Regex" /> accepts the pattern.</returns>
    private static bool IsAcceptedByRegex(string pattern, RegexOptions options)
    {
        try
        {
            _ = new Regex(pattern, options, _parseTimeout);

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
