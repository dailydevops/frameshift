namespace NetEvolve.FrameShift.Tests.Integration.Analyzers;

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using NetEvolve.FrameShift.Analyzers;
using NetEvolve.FrameShift.Diagnostics;
using NetEvolve.FrameShift.Mutations.Operators;
using NetEvolve.FrameShift.Mutations.RegularExpressions;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Drives the regular expression infrastructure - the tokenizer and its nested scanner and class frame,
/// the token, the pattern locator, the pattern validity check and the pattern cache - together with the
/// backreference, character-class, escape and quantifier operators through
/// <see cref="MutationCoverageAnalyzer" /> end to end.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RegexPatternMutationAnalysisTests" /> already proves the anchor, quantifier, group and
/// alternation operators, plus the character-class shorthand swap and the lookaround negation, reach the
/// build log as <c>FSH0001</c>. This class extends the same family with constructs that file does not
/// drive: a numbered backreference next to the plain capturing groups it renumbers, a character-class
/// range and its negation, a standalone class member and the dot/class equivalence in both directions, the
/// escaped-dot-to-any-character rewrite, and every shape of quantifier bound - the exact <c>{n}</c> form,
/// the open ended <c>{n,}</c> form and the full <c>{n,m}</c> form - together with the laziness toggle each
/// of the last two carries and the exact form does not.
/// </para>
/// <para>
/// Every fixture is built to keep a single operator family answerable for its reported gaps: the
/// backreference fixture pairs a backreference with the plain groups it needs to be a legal pattern, so
/// its expectation is the union of exactly those two operators, and every other fixture is a pattern that
/// carries no anchor, quantifier, group or shorthand escape at all, so only the operator under test can
/// report anything. Where a pattern cannot be kept that narrow - a class holding the very
/// <c>\s</c>/<c>\S</c> pair the dot-equivalence collapse recognises is unavoidably also a pair of shorthand
/// escapes the swap rewrites - the assertion falls back to naming the one message it needs to see, exactly
/// as <see cref="RegexPatternMutationAnalysisTests" /> does for its own character-class and lookaround
/// cases, rather than pinning a combinatorial set that would make the test fragile for reasons unrelated to
/// the construct it exists to prove.
/// </para>
/// <para>
/// Each fixture pairs the member under inspection with <c>Fixture.Reached.Identity</c>, whose body carries
/// no mutation point at all. Naming that member in the manifest is what gives the analyzer a non-empty
/// reachable set - without one it reports an unusable manifest and stays silent about the code - while
/// contributing not a single diagnostic of its own, exactly as in <see cref="CultureMutationTests" />.
/// Every reported gap is filtered down to the ones whose message names a pattern mutation, the same
/// <c>PatternMarker</c> filter <see cref="RegexPatternMutationAnalysisTests" /> uses, so a mutation of an
/// unrelated family sitting on the same literal - for instance the <c>RegexOptions</c> flags of an explicit
/// options argument - can never leak into an expectation this class does not state anything about.
/// </para>
/// </remarks>
public class RegexInfrastructureMutationTests
{
    private const string ProductionAssemblyName = "ProductionAssembly";

    /// <summary>
    /// The member every fixture declares to make the manifest resolvable, and whose <c>return value;</c>
    /// carries no mutation point.
    /// </summary>
    private const string AnchorMemberId = "M:Fixture.Reached.Identity(System.Int32)~System.Int32";

    /// <summary>
    /// The member of <see cref="BackreferenceAndGroupSource" /> that carries the pattern, used to prove the
    /// gaps of that fixture disappear once it is itself recorded as covered.
    /// </summary>
    private const string BackreferenceMemberId = "M:Fixture.Patterns.IsMatch(System.String)~System.Boolean";

    /// <summary>
    /// The test method id every manifest of this fixture attributes its references to. No test asserts on
    /// it, because these tests state what the operators report, not which test reached what.
    /// </summary>
    private const string AnonymousTestId = "M:Fixture.Tests.AnonymousTests.Reaches";

    /// <summary>
    /// The case count recorded for <see cref="AnonymousTestId" />: a lower bound, because nothing here
    /// establishes how many input combinations the reaching test carries. It also keeps <c>FSH0006</c>
    /// silent, so that every expectation below stays a statement about the operators alone.
    /// </summary>
    private const string LowerBoundCount = "1+";

    /// <summary>
    /// The text every display name of the family starts its description with, and therefore the filter
    /// that separates a pattern mutation from every other mutation of the same compilation.
    /// </summary>
    private const string PatternMarker = "pattern '";

    /// <summary>
    /// The text the assertions use for "not a single gap was reported".
    /// </summary>
    private const string NoGaps = "<no gaps>";

    /// <summary>
    /// The line feed the expectations are joined with, instead of <see cref="Environment.NewLine" />, so
    /// that the very same text is produced on Windows and on Linux.
    /// </summary>
    private const string LineFeed = "\n";

    /// <summary>
    /// A numbered backreference next to the three plain capturing groups it needs to be a legal pattern.
    /// The group mutator offers to turn each of the three plain groups into a non-capturing one, and every
    /// one of the three results is still a legal pattern because the backreference always finds a second
    /// group to resolve against; the backreference mutator offers to shift <c>\2</c> to <c>\3</c> and to
    /// <c>\1</c>, both legal because the pattern defines three groups. No anchor, quantifier, character
    /// class or shorthand escape sits anywhere in the pattern, so these five mutations are the whole of
    /// what either operator can report here.
    /// </summary>
    private const string BackreferenceAndGroupSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Patterns
        {
            public static bool IsMatch(string value)
            {
                return Regex.IsMatch(value, @"(a)(b)(c)\2");
            }
        }
        """;

    /// <summary>
    /// A plain range with no other construct in the pattern: the negation toggle of the class open, and
    /// the widening of both the range's start and its end by one code unit. Neither <c>a</c> nor <c>z</c>
    /// is offered as a standalone removal, because both are the endpoint of the range rather than a
    /// standalone member.
    /// </summary>
    private const string RangeSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Patterns
        {
            public static bool IsMatch(string value)
            {
                return Regex.IsMatch(value, "[a-z]");
            }
        }
        """;

    /// <summary>
    /// Three standalone class members next to a literal dot: the negation toggle of the class, the
    /// removal of each of the three members, and the expansion of the dot into <c>[\s\S]</c>. None of the
    /// three members is the endpoint of a range, so every one of them is offered as a removal.
    /// </summary>
    private const string MemberRemovalAndDotSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Patterns
        {
            public static bool IsMatch(string value)
            {
                return Regex.IsMatch(value, "[xyz].");
            }
        }
        """;

    /// <summary>
    /// The exact four token run <c>[\s\S]</c>, which the class-open dispatch recognises as the operand of
    /// the dot-equivalence collapse in addition to offering its own negation toggle. The very same two
    /// tokens are also shorthand escapes in their own right, so the shorthand swap fires for each of them
    /// as well; that combinatorial part of the output is not pinned here; see the class remarks.
    /// </summary>
    private const string AnyClassCollapseSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Patterns
        {
            public static bool IsMatch(string value)
            {
                return Regex.IsMatch(value, @"[\s\S]");
            }
        }
        """;

    /// <summary>
    /// A lone escaped dot, the one construct <c>RegexEscapeMutator</c> answers for. It is not one of the
    /// six shorthand classes the character-class operator swaps, so this fixture is answerable by exactly
    /// one operator.
    /// </summary>
    private const string EscapedDotSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Patterns
        {
            public static bool IsMatch(string value)
            {
                return Regex.IsMatch(value, @"\.");
            }
        }
        """;

    /// <summary>
    /// The exact <c>{n}</c> form of a counted quantifier, whose two rewrites decrease and increase the
    /// count. Its repetition count leaves the engine no choice about how many times to repeat, so the
    /// laziness toggle is never offered for it.
    /// </summary>
    private const string ExactCountSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Patterns
        {
            public static bool IsMatch(string value)
            {
                return Regex.IsMatch(value, "a{3}");
            }
        }
        """;

    /// <summary>
    /// The open ended <c>{n,}</c> form of a counted quantifier, whose lower bound is shifted in both
    /// directions and whose laziness is toggled, because unlike the exact form it does leave the engine a
    /// choice.
    /// </summary>
    private const string OpenEndedCountSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Patterns
        {
            public static bool IsMatch(string value)
            {
                return Regex.IsMatch(value, "a{2,}");
            }
        }
        """;

    /// <summary>
    /// The full <c>{n,m}</c> form of a counted quantifier, whose lower and upper bounds are each shifted
    /// in both directions and whose laziness is toggled.
    /// </summary>
    private const string BoundedRangeSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Patterns
        {
            public static bool IsMatch(string value)
            {
                return Regex.IsMatch(value, "a{2,3}");
            }
        }
        """;

    /// <summary>
    /// A lazy <c>+?</c> quantifier: the shape swap to <c>*?</c>, which keeps the laziness, and the
    /// laziness toggle back to the greedy <c>+</c>.
    /// </summary>
    private const string LazyPlusSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Patterns
        {
            public static bool IsMatch(string value)
            {
                return Regex.IsMatch(value, "a+?");
            }
        }
        """;

    /// <summary>
    /// An optional <c>?</c> quantifier: its removal, which makes the atom mandatory, and its laziness
    /// toggle to <c>??</c>.
    /// </summary>
    private const string OptionalSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Patterns
        {
            public static bool IsMatch(string value)
            {
                return Regex.IsMatch(value, "a?");
            }
        }
        """;

    /// <summary>
    /// The pattern reached through a named, reordered constructor argument rather than the positional
    /// leading argument every other fixture of this class uses, which is what exercises the locator's
    /// argument binding by name instead of by position. The explicit <c>RegexOptions.IgnoreCase</c>
    /// argument is itself a mutation point of the unrelated <c>RegexOptions</c> family, which is exactly
    /// why the assertion below filters by <see cref="PatternMarker" /> instead of taking every gap of the
    /// member.
    /// </summary>
    private const string NamedArgumentSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Patterns
        {
            public static Regex Create()
            {
                return new Regex(options: RegexOptions.IgnoreCase, pattern: "a+");
            }
        }
        """;

    /// <summary>
    /// A plain named group with the angle-bracket delimiter and no other construct in the pattern. The
    /// group mutator recognises it as a capturing open exactly like a plain <c>(</c>, so it offers the very
    /// same demotion to <c>(?:</c>; this is what proves the tokenizer's angle-bracket name scan
    /// (<c>ScanAngleGroup</c>/<c>ScanNamedGroup</c>) and the mutator's name-aware dispatch agree with each
    /// other.
    /// </summary>
    private const string NamedAngleGroupSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Patterns
        {
            public static bool IsMatch(string value)
            {
                return Regex.IsMatch(value, "(?<year>x)");
            }
        }
        """;

    /// <summary>
    /// The quote-delimited spelling of a named group, <c>(?'name'</c>, which the tokenizer scans through the
    /// very same <c>ScanNamedGroup</c> path as the angle-bracket form but with the other closing character.
    /// The mutator's own quote-prefix branch is what has to recognise it as a capturing open.
    /// </summary>
    private const string QuotedNameGroupSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Patterns
        {
            public static bool IsMatch(string value)
            {
                return Regex.IsMatch(value, "(?'year'x)");
            }
        }
        """;

    /// <summary>
    /// An atomic group <c>(?&gt;...)</c> next to the string-start anchor it does not interact with. The
    /// group mutator offers nothing for the atomic opening - it changes backtracking, not capturing - so the
    /// anchor's own removal is the only pattern mutation this fixture can report; that absence is what
    /// proves the mutator's capturing-open dispatch actually excludes this token text rather than merely
    /// never having been asked about it.
    /// </summary>
    private const string AtomicGroupWithAnchorSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Patterns
        {
            public static bool IsMatch(string value)
            {
                return Regex.IsMatch(value, @"\A(?>x)");
            }
        }
        """;

    /// <summary>
    /// A balancing group pair, <c>(?&lt;open&gt;x)</c> defining the capture and <c>(?&lt;-open&gt;)</c>
    /// popping it. The mutator's own dash check keeps it from ever touching the pop, and while it does offer
    /// to demote the defining group to <c>(?:</c>, that rewrite leaves the pop referring to a capture that
    /// no longer exists, so <see cref="RegexPatternMutatorBase" /> discards it as an invalid mutant. This is
    /// therefore a fixture with a real candidate mutation and zero surviving gaps, which is what separates
    /// "the operator declined to look" from "the base class rejected what it produced".
    /// </summary>
    private const string BalancingGroupSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Patterns
        {
            public static bool IsMatch(string value)
            {
                return Regex.IsMatch(value, "(?<open>x)(?<-open>)");
            }
        }
        """;

    /// <summary>
    /// Two named backreferences, one of each delimiter, next to the named groups they resolve against. The
    /// backreference operator recognises the <c>\k</c> prefix and offers nothing for either of them, and the
    /// group mutator's candidate demotion of either defining group is discarded because it would leave the
    /// matching backreference undefined - exactly the same "candidate produced, mutant invalid" shape as
    /// <see cref="BalancingGroupSource" />, but exercised through <c>ScanNamedBackreference</c> and both
    /// forms of <c>TryMeasureBracketedName</c> instead of the balancing branch of <c>ScanNamedGroup</c>.
    /// </summary>
    private const string NamedBackreferenceSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Patterns
        {
            public static bool IsMatch(string value)
            {
                return Regex.IsMatch(value, @"(?<a>x)\k<a>(?'b'y)\k'b'");
            }
        }
        """;

    /// <summary>
    /// A <c>(?#...)</c> comment, a scoped inline-options group <c>(?i:...)</c> and a standalone inline
    /// options construct <c>(?i)</c>, one after another. None of the three is a capturing open the group
    /// mutator answers for - the comment carries no group token at all, the scoped form is a group whose
    /// text is neither <c>(</c> nor <c>(?:</c> nor a plain named prefix, and the standalone form tokenizes
    /// as <see cref="RegexTokenKind.InlineOptions" /> rather than as
    /// <see cref="RegexTokenKind.GroupOpen" /> - so the whole pattern reports nothing at all, which is what
    /// proves every one of the three tokenizer paths (<c>ScanInlineComment</c>, the scoped and the
    /// standalone branch of <c>ScanOptions</c>) was actually exercised rather than short-circuited by an
    /// earlier failure.
    /// </summary>
    private const string InlineConstructsSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Patterns
        {
            public static bool IsMatch(string value)
            {
                return Regex.IsMatch(value, "(?#note)(?i:a)(?i)b");
            }
        }
        """;

    /// <summary>
    /// A Unicode category escape and its negation, <c>\p{L}</c> and <c>\P{Lu}</c>, which the tokenizer scans
    /// through <c>ScanUnicodeCategoryEscape</c> and no operator of the family recognises: neither is one of
    /// the six shorthand classes the character-class operator swaps, and neither is the escaped dot the
    /// escape operator rewrites. The pattern therefore reports nothing at all, which is the fixture's whole
    /// point - it exists to reach the property-escape scan, not to report a mutation.
    /// </summary>
    private const string UnicodeCategoryEscapeSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Patterns
        {
            public static bool IsMatch(string value)
            {
                return Regex.IsMatch(value, @"\p{L}\P{Lu}");
            }
        }
        """;

    /// <summary>
    /// A hexadecimal escape, a Unicode escape and a control-character escape back to back, which reach
    /// <c>ScanHexadecimalEscape</c> under both of its digit counts and <c>ScanControlEscape</c>. None of the
    /// three is a shorthand class or the escaped dot, so - exactly like
    /// <see cref="UnicodeCategoryEscapeSource" /> - the pattern reports nothing at all.
    /// </summary>
    private const string HexAndControlEscapeSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Patterns
        {
            public static bool IsMatch(string value)
            {
                return Regex.IsMatch(value, @"\x41\u0041\cA");
            }
        }
        """;

    /// <summary>
    /// A single plain group followed by <c>\10</c>. With exactly one group defined, <c>\10</c> names no
    /// capture the pattern has, so the tokenizer's <c>ResolveNumberedBackreferences</c> step re-reads it as
    /// the octal escape <c>\10</c> in full - both digits are octal, so nothing is left over as a literal -
    /// instead of leaving it a backreference. Neither the escape nor the backreference operator has anything
    /// to say about the result, so the only mutation left standing is the plain group's own demotion, and
    /// demoting it away is harmless here: with the group gone, the very same digits are still read as the
    /// very same octal escape by a fresh construction of the mutant, so the rewrite validates.
    /// </summary>
    private const string OctalReinterpretationWithGroupSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Patterns
        {
            public static bool IsMatch(string value)
            {
                return Regex.IsMatch(value, @"(a)\10");
            }
        }
        """;

    /// <summary>
    /// <c>\18</c> with no group defined anywhere in the pattern. Only the leading <c>1</c> is an octal digit,
    /// so <c>ResolveNumberedBackreferences</c> consumes it alone as the octal escape <c>\1</c> and reinserts
    /// the trailing <c>8</c> as a separate literal token - the one branch of that method none of the other
    /// fixtures in this class reach, because they either define enough groups to stay a genuine
    /// backreference or consume every digit as octal with nothing left over. Neither the reinterpreted escape
    /// nor the reinserted literal digit is a construct any operator of the family rewrites, so the pattern
    /// reports nothing at all.
    /// </summary>
    private const string LeftoverOctalDigitSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Patterns
        {
            public static bool IsMatch(string value)
            {
                return Regex.IsMatch(value, @"\18");
            }
        }
        """;

    /// <summary>
    /// A range next to a class subtraction, <c>[a-z-[aeiou]]</c>: the outer class negation, the widening of
    /// both ends of the range, the nested class's own negation, and the removal of each of its five
    /// standalone members. This is the one fixture of the class that reaches the subtraction dispatch of
    /// <c>ScanCharacterClassDash</c> - the <c>-[</c> that opens a nested class rather than a range - and the
    /// <c>SubtractionApplied</c> enforcement that nothing but the outer <c>]</c> may follow it.
    /// </summary>
    private const string CharacterClassSubtractionSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Patterns
        {
            public static bool IsMatch(string value)
            {
                return Regex.IsMatch(value, "[a-z-[aeiou]]");
            }
        }
        """;

    /// <summary>
    /// The line every pattern of this class sits on. Every fixture shares the same layout - the
    /// <c>Reached</c> anchor, a blank line, the class declaring the pattern - so the pattern always lands
    /// on the very same line.
    /// </summary>
    private const int PatternLine = 17;

    /// <summary>
    /// Every mutant of <see cref="BackreferenceAndGroupSource" />'s pattern: the three group demotions in
    /// pattern order, followed by the backreference's increase and decrease.
    /// </summary>
    private static readonly string[] _backreferenceAndGroupMutants =
    [
        Mutant(@"(a)(b)(c)\2", @"(?:a)(b)(c)\2"),
        Mutant(@"(a)(b)(c)\2", @"(a)(?:b)(c)\2"),
        Mutant(@"(a)(b)(c)\2", @"(a)(b)(?:c)\2"),
        Mutant(@"(a)(b)(c)\2", @"(a)(b)(c)\3"),
        Mutant(@"(a)(b)(c)\2", @"(a)(b)(c)\1"),
    ];

    /// <summary>
    /// The two mutants of <see cref="RangeSource" />'s pattern: the negation toggle and the widening of
    /// both ends of the range.
    /// </summary>
    private static readonly string[] _rangeMutants =
    [
        Mutant("[a-z]", "[^a-z]"),
        Mutant("[a-z]", "[`-z]"),
        Mutant("[a-z]", "[a-{]"),
    ];

    /// <summary>
    /// Every mutant of <see cref="MemberRemovalAndDotSource" />'s pattern: the negation toggle, the
    /// removal of each standalone member in pattern order, and the dot expansion.
    /// </summary>
    private static readonly string[] _memberRemovalAndDotMutants =
    [
        Mutant("[xyz].", "[^xyz]."),
        Mutant("[xyz].", "[yz]."),
        Mutant("[xyz].", "[xz]."),
        Mutant("[xyz].", "[xy]."),
        Mutant("[xyz].", @"[xyz][\s\S]"),
    ];

    /// <summary>
    /// The two mutants of <see cref="ExactCountSource" />'s pattern: the decrease and the increase of the
    /// exact count.
    /// </summary>
    private static readonly string[] _exactCountMutants = [Mutant("a{3}", "a{2}"), Mutant("a{3}", "a{4}")];

    /// <summary>
    /// The three mutants of <see cref="OpenEndedCountSource" />'s pattern: both shifts of the lower bound,
    /// then the laziness toggle.
    /// </summary>
    private static readonly string[] _openEndedCountMutants =
    [
        Mutant("a{2,}", "a{1,}"),
        Mutant("a{2,}", "a{3,}"),
        Mutant("a{2,}", "a{2,}?"),
    ];

    /// <summary>
    /// The five mutants of <see cref="BoundedRangeSource" />'s pattern: both shifts of the lower bound,
    /// both shifts of the upper bound, then the laziness toggle.
    /// </summary>
    private static readonly string[] _boundedRangeMutants =
    [
        Mutant("a{2,3}", "a{1,3}"),
        Mutant("a{2,3}", "a{3,3}"),
        Mutant("a{2,3}", "a{2,2}"),
        Mutant("a{2,3}", "a{2,4}"),
        Mutant("a{2,3}", "a{2,3}?"),
    ];

    /// <summary>
    /// The two mutants of <see cref="LazyPlusSource" />'s pattern: the shape swap to a lazy star, then the
    /// laziness toggle back to greedy.
    /// </summary>
    private static readonly string[] _lazyPlusMutants = [Mutant("a+?", "a*?"), Mutant("a+?", "a+")];

    /// <summary>
    /// The two mutants of <see cref="OptionalSource" />'s pattern: the removal, then the laziness toggle.
    /// </summary>
    private static readonly string[] _optionalMutants = [Mutant("a?", "a"), Mutant("a?", "a??")];

    /// <summary>
    /// The two mutants of <see cref="NamedArgumentSource" />'s pattern, the same shape swap and laziness
    /// toggle a bare greedy <c>+</c> offers everywhere else in this class.
    /// </summary>
    private static readonly string[] _namedArgumentMutants = [Mutant("a+", "a*"), Mutant("a+", "a+?")];

    /// <summary>
    /// The single mutant of <see cref="NamedAngleGroupSource" />'s pattern: the demotion of the named group
    /// to non-capturing.
    /// </summary>
    private static readonly string[] _namedAngleGroupMutants = [Mutant("(?<year>x)", "(?:x)")];

    /// <summary>
    /// The single mutant of <see cref="QuotedNameGroupSource" />'s pattern: the demotion of the named group
    /// to non-capturing.
    /// </summary>
    private static readonly string[] _quotedNameGroupMutants = [Mutant("(?'year'x)", "(?:x)")];

    /// <summary>
    /// The single mutant of <see cref="AtomicGroupWithAnchorSource" />'s pattern: the removal of the
    /// string-start anchor. The atomic group offers nothing of its own.
    /// </summary>
    private static readonly string[] _atomicGroupWithAnchorMutants = [Mutant(@"\A(?>x)", "(?>x)")];

    /// <summary>
    /// The single mutant of <see cref="OctalReinterpretationWithGroupSource" />'s pattern: the demotion of
    /// the plain group to non-capturing, which validates because the octal escape behind it is read the same
    /// way whether or not the group exists.
    /// </summary>
    private static readonly string[] _octalReinterpretationWithGroupMutants = [Mutant(@"(a)\10", @"(?:a)\10")];

    /// <summary>
    /// Every mutant of <see cref="CharacterClassSubtractionSource" />'s pattern: the outer class negation,
    /// the widening of both ends of the range, the nested class's own negation, and the removal of each of
    /// its five standalone members.
    /// </summary>
    private static readonly string[] _characterClassSubtractionMutants =
    [
        Mutant("[a-z-[aeiou]]", "[^a-z-[aeiou]]"),
        Mutant("[a-z-[aeiou]]", "[`-z-[aeiou]]"),
        Mutant("[a-z-[aeiou]]", "[a-{-[aeiou]]"),
        Mutant("[a-z-[aeiou]]", "[a-z-[^aeiou]]"),
        Mutant("[a-z-[aeiou]]", "[a-z-[eiou]]"),
        Mutant("[a-z-[aeiou]]", "[a-z-[aiou]]"),
        Mutant("[a-z-[aeiou]]", "[a-z-[aeou]]"),
        Mutant("[a-z-[aeiou]]", "[a-z-[aeiu]]"),
        Mutant("[a-z-[aeiou]]", "[a-z-[aeio]]"),
    ];

    /// <summary>
    /// Every fixture of this class, so that one test can prove that all of them compile and that none of
    /// them makes the analyzer crash.
    /// </summary>
    /// <returns>One factory per fixture.</returns>
    public static IEnumerable<Func<string>> Fixtures() =>
        new[]
        {
            BackreferenceAndGroupSource,
            RangeSource,
            MemberRemovalAndDotSource,
            AnyClassCollapseSource,
            EscapedDotSource,
            ExactCountSource,
            OpenEndedCountSource,
            BoundedRangeSource,
            LazyPlusSource,
            OptionalSource,
            NamedArgumentSource,
            NamedAngleGroupSource,
            QuotedNameGroupSource,
            AtomicGroupWithAnchorSource,
            BalancingGroupSource,
            NamedBackreferenceSource,
            InlineConstructsSource,
            UnicodeCategoryEscapeSource,
            HexAndControlEscapeSource,
            OctalReinterpretationWithGroupSource,
            LeftoverOctalDigitSource,
            CharacterClassSubtractionSource,
        }.Select(source => (Func<string>)(() => source));

    /// <summary>
    /// The backreference next to the plain groups it renumbers reports a gap per group demotion and per
    /// backreference shift, and nothing else.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedBackreferenceAndGroups_ReportsAGapPerGroupAndBackreferenceMutation()
    {
        var compilation = CompilationFactory.Create(BackreferenceAndGroupSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(PatternGaps(diagnostics))
                .IsEqualTo(ExpectAt(PatternLine, _backreferenceAndGroupMutants));
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
        }
    }

    /// <summary>
    /// Recording the pattern's own member as covered makes every one of its gaps disappear, which is what
    /// proves the five gaps above to be a statement about coverage rather than about the operators.
    /// </summary>
    [Test]
    public async Task Analyze_CoveredBackreferenceAndGroups_ReportsNoPatternGapAtAll()
    {
        var compilation = CompilationFactory.Create(BackreferenceAndGroupSource, ProductionAssemblyName);
        var manifest = new[] { CreateManifest(AnchorMemberId, BackreferenceMemberId) };

        var diagnostics = await RunAsync(compilation, manifest).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(PatternGaps(diagnostics)).IsEqualTo(NoGaps);
        }
    }

    /// <summary>
    /// A range with no other construct in its pattern reports its negation toggle and the widening of both
    /// of its ends, and nothing else - neither one of its two endpoints is offered as a standalone removal.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedRange_ReportsTheNegationAndBothWidenings()
    {
        var compilation = CompilationFactory.Create(RangeSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(PatternGaps(diagnostics)).IsEqualTo(ExpectAt(PatternLine, _rangeMutants));
        }
    }

    /// <summary>
    /// Three standalone class members next to a literal dot report the class negation, the removal of
    /// each member and the dot expansion, and nothing else.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedStandaloneMembersAndDot_ReportsEveryRemovalAndTheDotExpansion()
    {
        var compilation = CompilationFactory.Create(MemberRemovalAndDotSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(PatternGaps(diagnostics))
                .IsEqualTo(ExpectAt(PatternLine, _memberRemovalAndDotMutants));
        }
    }

    /// <summary>
    /// The exact <c>[\s\S]</c> token run collapses to a dot, in addition to offering its own negation
    /// toggle, which is what proves the class-open dispatch recognises the run rather than merely toggling
    /// the class it opens.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedAnyCharacterClass_ReportsTheCollapseToDot()
    {
        var compilation = CompilationFactory.Create(AnyClassCollapseSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);
        var messages = Summaries(diagnostics).Select(summary => summary.Message).ToArray();

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(messages.Any(message => message.Contains(@"pattern '[\s\S]' => '.'", StringComparison.Ordinal)))
                .IsTrue();
            _ = await Assert
                .That(
                    messages.Any(message =>
                        message.Contains(@"pattern '[\s\S]' => '[^\s\S]'", StringComparison.Ordinal)
                    )
                )
                .IsTrue();
        }
    }

    /// <summary>
    /// A lone escaped dot reports its widening into any character, and nothing else - it is not one of
    /// the six shorthand classes the character-class operator swaps.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedEscapedDot_ReportsTheWideningToAnyCharacter()
    {
        var compilation = CompilationFactory.Create(EscapedDotSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(PatternGaps(diagnostics)).IsEqualTo(ExpectAt(PatternLine, [Mutant(@"\.", ".")]));
        }
    }

    /// <summary>
    /// The exact <c>{n}</c> form reports its decrease and its increase, and never a laziness toggle: its
    /// repetition count leaves the engine no choice to make lazy.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedExactCount_ReportsTheDecreaseAndIncreaseButNoLazinessToggle()
    {
        var compilation = CompilationFactory.Create(ExactCountSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(PatternGaps(diagnostics)).IsEqualTo(ExpectAt(PatternLine, _exactCountMutants));
        }
    }

    /// <summary>
    /// The open ended <c>{n,}</c> form reports both shifts of its lower bound and its laziness toggle.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedOpenEndedCount_ReportsBothBoundShiftsAndTheLazinessToggle()
    {
        var compilation = CompilationFactory.Create(OpenEndedCountSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(PatternGaps(diagnostics)).IsEqualTo(ExpectAt(PatternLine, _openEndedCountMutants));
        }
    }

    /// <summary>
    /// The full <c>{n,m}</c> form reports both shifts of its lower bound, both shifts of its upper bound
    /// and its laziness toggle.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedBoundedRange_ReportsEveryBoundShiftAndTheLazinessToggle()
    {
        var compilation = CompilationFactory.Create(BoundedRangeSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(PatternGaps(diagnostics)).IsEqualTo(ExpectAt(PatternLine, _boundedRangeMutants));
        }
    }

    /// <summary>
    /// A lazy plus reports the shape swap to a lazy star, which keeps the laziness, and the laziness
    /// toggle back to a greedy plus.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedLazyPlus_ReportsTheShapeSwapAndTheLazinessToggle()
    {
        var compilation = CompilationFactory.Create(LazyPlusSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(PatternGaps(diagnostics)).IsEqualTo(ExpectAt(PatternLine, _lazyPlusMutants));
        }
    }

    /// <summary>
    /// An optional quantifier reports its removal, which makes the atom mandatory, and its laziness
    /// toggle.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedOptional_ReportsTheRemovalAndTheLazinessToggle()
    {
        var compilation = CompilationFactory.Create(OptionalSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(PatternGaps(diagnostics)).IsEqualTo(ExpectAt(PatternLine, _optionalMutants));
        }
    }

    /// <summary>
    /// A pattern reached through a named, reordered constructor argument is located and mutated exactly
    /// like one reached positionally, which is what proves the locator's argument binding resolves a
    /// pattern by parameter rather than by its position in the argument list.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedPatternBehindNamedArgument_ReportsItsQuantifierMutations()
    {
        var compilation = CompilationFactory.Create(NamedArgumentSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(PatternGaps(diagnostics)).IsEqualTo(ExpectAt(PatternLine, _namedArgumentMutants));
        }
    }

    /// <summary>
    /// A plain named group with the angle-bracket delimiter is demoted to non-capturing exactly like a bare
    /// <c>(</c>, which is what proves the mutator's named-capture recognition - and the tokenizer's
    /// <c>ScanAngleGroup</c>/<c>ScanNamedGroup</c> path behind it - agree that the two spellings define the
    /// same kind of capture.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedNamedAngleGroup_ReportsItsDemotionToNonCapturing()
    {
        var compilation = CompilationFactory.Create(NamedAngleGroupSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(PatternGaps(diagnostics)).IsEqualTo(ExpectAt(PatternLine, _namedAngleGroupMutants));
        }
    }

    /// <summary>
    /// The quote-delimited spelling of a named group is demoted exactly like its angle-bracket counterpart,
    /// which is what proves the mutator's quote-prefix branch, and the corresponding closing-quote path of
    /// <c>ScanNamedGroup</c>, are exercised rather than only the angle-bracket one.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedQuotedNameGroup_ReportsItsDemotionToNonCapturing()
    {
        var compilation = CompilationFactory.Create(QuotedNameGroupSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(PatternGaps(diagnostics)).IsEqualTo(ExpectAt(PatternLine, _quotedNameGroupMutants));
        }
    }

    /// <summary>
    /// An atomic group next to the string-start anchor reports only the anchor's own removal - the group
    /// mutator offers nothing for the atomic opening at all, which is what separates "the operator was asked
    /// and declined" from "the operator was never asked".
    /// </summary>
    [Test]
    public async Task Analyze_UntestedAtomicGroupWithAnchor_ReportsOnlyTheAnchorRemoval()
    {
        var compilation = CompilationFactory.Create(AtomicGroupWithAnchorSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(PatternGaps(diagnostics))
                .IsEqualTo(ExpectAt(PatternLine, _atomicGroupWithAnchorMutants));
        }
    }

    /// <summary>
    /// A balancing group pair reports no pattern gap at all: the pop is never offered a rewrite because its
    /// name carries the dash the mutator's capturing-open check rejects, and the one candidate the defining
    /// group does offer is discarded because demoting it would leave the pop referring to a capture that no
    /// longer exists. A real candidate mutation therefore still produces zero surviving gaps.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedBalancingGroup_ReportsNoPatternGapAtAll()
    {
        var compilation = CompilationFactory.Create(BalancingGroupSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(PatternGaps(diagnostics)).IsEqualTo(NoGaps);
        }
    }

    /// <summary>
    /// Two named backreferences, one of each delimiter, report no pattern gap at all: the backreference
    /// operator recognises the <c>\k</c> prefix and offers nothing for either of them, and the group
    /// mutator's candidate demotion of either defining group is discarded because it would leave the
    /// matching backreference undefined.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedNamedBackreferences_ReportsNoPatternGapAtAll()
    {
        var compilation = CompilationFactory.Create(NamedBackreferenceSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(PatternGaps(diagnostics)).IsEqualTo(NoGaps);
        }
    }

    /// <summary>
    /// A comment, a scoped inline-options group and a standalone inline-options construct report no pattern
    /// gap at all: none of the three tokenizes as a capturing open the group mutator answers for, which is
    /// what proves every one of the three tokenizer paths behind them was reached rather than short-circuited
    /// by an earlier failure.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedInlineConstructs_ReportsNoPatternGapAtAll()
    {
        var compilation = CompilationFactory.Create(InlineConstructsSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(PatternGaps(diagnostics)).IsEqualTo(NoGaps);
        }
    }

    /// <summary>
    /// A Unicode category escape and its negation report no pattern gap at all: neither is one of the six
    /// shorthand classes the character-class operator swaps, and neither is the escaped dot the escape
    /// operator rewrites.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedUnicodeCategoryEscapes_ReportsNoPatternGapAtAll()
    {
        var compilation = CompilationFactory.Create(UnicodeCategoryEscapeSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(PatternGaps(diagnostics)).IsEqualTo(NoGaps);
        }
    }

    /// <summary>
    /// A hexadecimal escape, a Unicode escape and a control-character escape report no pattern gap at all,
    /// for the same reason as <see cref="UnicodeCategoryEscapeSource" />: none of the three is a shorthand
    /// class or the escaped dot.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedHexAndControlEscapes_ReportsNoPatternGapAtAll()
    {
        var compilation = CompilationFactory.Create(HexAndControlEscapeSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(PatternGaps(diagnostics)).IsEqualTo(NoGaps);
        }
    }

    /// <summary>
    /// A single plain group followed by <c>\10</c> reports only the group's own demotion: with exactly one
    /// group defined, <c>\10</c> names no capture the pattern has, so the tokenizer re-reads it as a full
    /// octal escape instead of a backreference, and neither the escape nor the backreference operator has
    /// anything to say about the result.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedOctalReinterpretationWithGroup_ReportsOnlyTheGroupDemotion()
    {
        var compilation = CompilationFactory.Create(OctalReinterpretationWithGroupSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(PatternGaps(diagnostics))
                .IsEqualTo(ExpectAt(PatternLine, _octalReinterpretationWithGroupMutants));
        }
    }

    /// <summary>
    /// <c>\18</c> with no group defined anywhere in the pattern reports no pattern gap at all: the tokenizer
    /// consumes only the leading digit as an octal escape and reinserts the trailing digit as a literal,
    /// and neither token is a construct any operator of the family rewrites.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedLeftoverOctalDigit_ReportsNoPatternGapAtAll()
    {
        var compilation = CompilationFactory.Create(LeftoverOctalDigitSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(PatternGaps(diagnostics)).IsEqualTo(NoGaps);
        }
    }

    /// <summary>
    /// A range next to a class subtraction reports the outer class negation, the widening of both ends of
    /// the range, the nested class's own negation, and the removal of each of its five standalone members -
    /// the one fixture of the class that reaches the subtraction dispatch of <c>ScanCharacterClassDash</c>
    /// and the <c>SubtractionApplied</c> enforcement behind it.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedCharacterClassSubtraction_ReportsEveryConstituentMutation()
    {
        var compilation = CompilationFactory.Create(CharacterClassSubtractionSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(PatternGaps(diagnostics))
                .IsEqualTo(ExpectAt(PatternLine, _characterClassSubtractionMutants));
        }
    }

    /// <summary>
    /// Every fixture of this class compiles and is analysed without the analyzer throwing. Roslyn turns an
    /// analyzer exception into <c>AD0001</c> and carries on, so a crash would otherwise look like a
    /// diagnostic the tests above simply did not expect.
    /// </summary>
    /// <param name="source">The fixture to analyse.</param>
    /// <returns>A task that completes when the fixture was analysed.</returns>
    [Test]
    [MethodDataSource(nameof(Fixtures))]
    public async Task Analyze_EveryFixture_CompilesAndReportsNoAnalyzerFailure(string source)
    {
        var compilation = CompilationFactory.Create(source, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(string.Join("; ", Errors(compilation))).IsEqualTo(string.Empty);
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, AnalyzerRunner.AnalyzerFailureId)).IsEmpty();
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
        }
    }

    private static Task<ImmutableArray<Diagnostic>> RunAsync(
        Compilation compilation,
        IEnumerable<AdditionalText>? additionalFiles = null,
        IReadOnlyDictionary<string, string>? globalOptions = null
    ) => AnalyzerRunner.RunAsync(new MutationCoverageAnalyzer(), compilation, additionalFiles, globalOptions);

    /// <summary>
    /// Builds a manifest recording <paramref name="referencedMemberIds" /> as the production members the
    /// tests of the first pass touched.
    /// </summary>
    /// <param name="referencedMemberIds">The declaration ids of the covered members.</param>
    /// <returns>The manifest as an additional file.</returns>
    private static InMemoryAdditionalText CreateManifest(params string[] referencedMemberIds)
    {
        var builder = new StringBuilder();
        _ = builder.Append(TestSurfaceManifestFormat.Header).Append('\n');
        _ = builder
            .Append(TestSurfaceManifestFormat.TestPrefix)
            .Append(TestSurfaceManifestFormat.FieldSeparator)
            .Append(AnonymousTestId)
            .Append(TestSurfaceManifestFormat.FieldSeparator)
            .Append(LowerBoundCount)
            .Append('\n');

        foreach (var referencedMemberId in referencedMemberIds)
        {
            _ = builder
                .Append(TestSurfaceManifestFormat.ReferencePrefix)
                .Append(TestSurfaceManifestFormat.FieldSeparator)
                .Append(referencedMemberId)
                .Append('\n');
        }

        return new InMemoryAdditionalText(builder.ToString());
    }

    /// <summary>
    /// Describes the reported gaps that name a pattern mutation, one line per diagnostic, ordered
    /// ordinally so that the result does not depend on the order the concurrently running analyzer
    /// callbacks reported them in.
    /// </summary>
    /// <param name="diagnostics">All diagnostics of a run.</param>
    /// <returns>The described gaps, or <see cref="NoGaps" /> when there is none.</returns>
    private static string PatternGaps(ImmutableArray<Diagnostic> diagnostics)
    {
        var entries = Summaries(diagnostics)
            .Where(summary => NamesAPattern(summary.Message))
            .Select(summary => Entry(summary.Id, summary.Line, summary.Message))
            .ToList();

        return entries.Count == 0 ? NoGaps : Join(entries);
    }

    private static bool NamesAPattern(string message) => message.Contains(PatternMarker, StringComparison.Ordinal);

    private static ImmutableArray<(string Id, int Line, string Message)> Summaries(
        ImmutableArray<Diagnostic> diagnostics
    ) => DiagnosticAssertions.Summarise(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.UnreachableMutationPoint));

    /// <summary>
    /// Builds the expectation of a set of gaps that all sit on <paramref name="line" />.
    /// </summary>
    /// <param name="line">The 1-based line every gap is reported on.</param>
    /// <param name="displayNames">The display names of the expected mutations.</param>
    /// <returns>The expected text block.</returns>
    private static string ExpectAt(int line, IEnumerable<string> displayNames) =>
        Join([.. displayNames.Select(displayName => GapEntry(line, displayName))]);

    /// <summary>
    /// Composes the display name a pattern mutation carries.
    /// </summary>
    /// <param name="original">The original pattern, as the regular expression engine sees it.</param>
    /// <param name="mutated">The rewritten pattern, as the regular expression engine sees it.</param>
    /// <returns>The display name.</returns>
    private static string Mutant(string original, string mutated) =>
        PatternMarker + original + "' => '" + mutated + "'";

    /// <summary>
    /// Builds the described gap of one mutation, spelling out the message
    /// <see cref="Descriptors.UnreachableMutationPoint" /> formats.
    /// </summary>
    /// <param name="line">The 1-based line the gap is reported on.</param>
    /// <param name="displayName">The display name of the mutation.</param>
    /// <returns>The described gap.</returns>
    private static string GapEntry(int line, string displayName) =>
        Entry(
            DiagnosticIds.UnreachableMutationPoint,
            line,
            "Mutation '"
                + displayName
                + "' at this location is not reachable from any test; a surviving mutant here would go unnoticed"
        );

    private static string Entry(string id, int line, string message) => id + " line " + ToText(line) + ": " + message;

    private static string Join(IEnumerable<string> entries) =>
        string.Join(LineFeed, entries.OrderBy(entry => entry, StringComparer.Ordinal));

    private static string ToText(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static ImmutableArray<Diagnostic> Errors(Compilation compilation) =>
        CompilationFactory.GetCompileErrors(compilation);
}
