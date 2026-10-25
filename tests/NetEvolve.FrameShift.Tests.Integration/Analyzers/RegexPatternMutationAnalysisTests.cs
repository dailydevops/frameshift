namespace NetEvolve.FrameShift.Tests.Integration.Analyzers;

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using NetEvolve.FrameShift.Analyzers;
using NetEvolve.FrameShift.Diagnostics;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Drives the operators of the regular expression pattern family - anchors, quantifiers, groups and
/// alternation, plus character classes, escapes, lookaround and backreferences - through
/// <see cref="MutationCoverageAnalyzer" /> end to end, so that a pattern mutation is proven to reach the
/// build log of a consumer as an <c>FSH0001</c> instead of merely to be constructed. The boundary between
/// the classifier's quantifier shorthand equivalence and a merely similar-looking bound shift is proven
/// end to end here too - the former is an <c>FSH0002</c>, the latter stays the <c>FSH0001</c> it is.
/// </summary>
/// <remarks>
/// <para>
/// The fixture holds the very same shape of pattern twice: once in a member the manifest records and once
/// in a member it does not. Both patterns therefore carry an identical set of mutation points, and the only
/// difference between them is coverage — which is what turns "a gap is reported here and not there" into a
/// statement about the analysis rather than about the pattern.
/// </para>
/// <para>
/// The gaps are split into the ones whose message names a pattern mutation and the ones that do not, see
/// <see cref="PatternMarker" />. The message of an <c>FSH0001</c> carries the display name of the mutation,
/// and every mutation of this family describes itself as <c>pattern '...' =&gt; '...'</c>, so the marker is
/// exactly the family filter a consumer could apply to its own build log. The pattern gaps are then pinned
/// as one text block holding the full message of each diagnostic, because the four operators report several
/// mutants at one and the same location and a set of line numbers could not tell them apart.
/// </para>
/// <para>
/// The remaining gaps of the same compilation are deliberately not pinned to an exact text. They are the
/// evidence that switching the family off leaves the other operators alone, and comparing the two runs with
/// each other states that more strongly than repeating what the arithmetic and string literal operators
/// happen to produce.
/// </para>
/// <para>
/// Nothing here depends on the executing target framework: the fixture is analysed as source, the
/// diagnostics are located in it, the expectations are joined with a literal line feed rather than
/// <see cref="Environment.NewLine" />, and no snapshot is taken — the neighbouring family test,
/// <see cref="CultureMutationTests" />, states its claims explicitly in the same way.
/// </para>
/// </remarks>
public class RegexPatternMutationAnalysisTests
{
    private const string ProductionAssemblyName = "ProductionAssembly";

    /// <summary>
    /// The member the manifest records, whose pattern is the covered counterpart of the reported one.
    /// </summary>
    private const string CoveredMemberId = "M:Fixture.Patterns.IsCovered(System.String)~System.Boolean";

    /// <summary>
    /// The member holding the pattern no test reaches.
    /// </summary>
    private const string UncoveredMemberId = "M:Fixture.Patterns.IsUncovered(System.String)~System.Boolean";

    /// <summary>
    /// The test method id every manifest of this fixture attributes its references to. No test asserts on
    /// it, because these tests state what the pattern operators report, not which test reached what.
    /// </summary>
    private const string AnonymousTestId = "M:Fixture.Tests.AnonymousTests.Reaches";

    /// <summary>
    /// The case count recorded for <see cref="AnonymousTestId" />: a lower bound, because nothing here
    /// establishes how many input combinations the reaching test carries. It also keeps <c>FSH0006</c>
    /// silent, so that every expectation below stays a statement about the pattern operators alone.
    /// </summary>
    private const string LowerBoundCount = "1+";

    /// <summary>
    /// The text every display name of the family starts its description with, and therefore the filter that
    /// separates a pattern mutation from every other mutation of the same compilation.
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
    /// The line of the pattern inside the member the manifest records.
    /// </summary>
    private const int CoveredPatternLine = 9;

    /// <summary>
    /// The line of the pattern inside the member no test reaches.
    /// </summary>
    private const int UncoveredPatternLine = 14;

    /// <summary>
    /// The pattern of the uncovered member, written as the regular expression engine sees it.
    /// </summary>
    /// <remarks>
    /// It is deliberately the smallest pattern that carries a mutation point of all four operators at once:
    /// two anchors, one quantifier, one capturing group and one alternation of two branches.
    /// </remarks>
    private const string UncoveredPattern = "^(a|b)+$";

    /// <summary>
    /// <c>Patterns.IsCovered</c> and <c>Patterns.IsUncovered</c> hold the same shape of pattern, and
    /// <c>Arithmetic.Add</c> carries a mutation point of an entirely different family, which is what the
    /// family switch must not touch. Every pattern sits directly in the constructor call, without options,
    /// so the options of both sites are statically known to be <c>RegexOptions.None</c>.
    /// </summary>
    private const string PatternSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Patterns
        {
            public static bool IsCovered(string value)
            {
                return new Regex("^(c|d)+$").IsMatch(value);
            }

            public static bool IsUncovered(string value)
            {
                return new Regex("^(a|b)+$").IsMatch(value);
            }
        }

        public static class Arithmetic
        {
            public static int Add(int left, int right)
            {
                return left + right;
            }
        }
        """;

    /// <summary>
    /// Every mutant of <see cref="UncoveredPattern" />, one per mutation point of the four operators: the
    /// removal of each anchor, the two mutants of the quantifier, the loss of the capture, the removal of
    /// each branch and the swap of the two branches.
    /// </summary>
    private static readonly string[] _patternMutants =
    [
        Mutant("(a|b)+$"),
        Mutant("^(a|b)+"),
        Mutant("^(a|b)*$"),
        Mutant("^(a|b)+?$"),
        Mutant("^(?:a|b)+$"),
        Mutant("^(b)+$"),
        Mutant("^(a)+$"),
        Mutant("^(b|a)+$"),
    ];

    /// <summary>
    /// The acceptance case of the family: the pattern of a member no test reaches is reported once per
    /// pattern mutation, the messages name those mutations as pattern mutations, and the identically shaped
    /// pattern of the recorded member is not reported at all.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    public async Task Analyze_UntestedPattern_ReportsAGapPerPatternMutation()
    {
        var compilation = CompilationFactory.Create(PatternSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(CoveredMemberId)]).ConfigureAwait(false);
        var namesAPatternMutation = PatternGapCount(diagnostics) > 0;

        _ = await Assert.That(Errors(compilation)).IsEmpty();
        _ = await Assert.That(PatternGaps(diagnostics)).IsEqualTo(ExpectAt(UncoveredPatternLine, _patternMutants));
        _ = await Assert.That(namesAPatternMutation).IsTrue();
        _ = await Assert.That(GapLines(diagnostics).Contains(CoveredPatternLine)).IsFalse();
        _ = await Assert.That(GapLines(diagnostics).Contains(UncoveredPatternLine)).IsTrue();
        _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
    }

    /// <summary>
    /// The other half of the same claim: recording the second member as well makes every pattern gap
    /// disappear, which is what proves the eight gaps above to be a statement about coverage rather than
    /// about the operators.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    public async Task Analyze_TestedPattern_ReportsNoPatternGapAtAll()
    {
        var compilation = CompilationFactory.Create(PatternSource, ProductionAssemblyName);
        var manifest = new[] { CreateManifest(CoveredMemberId, UncoveredMemberId) };

        var diagnostics = await RunAsync(compilation, manifest).ConfigureAwait(false);

        _ = await Assert.That(Errors(compilation)).IsEmpty();
        _ = await Assert.That(PatternGaps(diagnostics)).IsEqualTo(NoGaps);
        _ = await Assert.That(GapLines(diagnostics).Contains(CoveredPatternLine)).IsFalse();
        _ = await Assert.That(GapLines(diagnostics).Contains(UncoveredPatternLine)).IsFalse();
    }

    /// <summary>
    /// The family switch, driven through the analyzer configuration the way MSBuild sets it: with
    /// <c>FrameShiftEnableRegexPatternMutations</c> set to <see langword="false" />, not a single pattern
    /// mutation is reported any more, while every other gap of the very same compilation is reported exactly
    /// as before.
    /// </summary>
    /// <remarks>
    /// The second assertion is what makes the first one meaningful. A switch that silenced the whole
    /// analysis would satisfy "no pattern gap" just as well, so the remaining gaps are compared between the
    /// two runs and are additionally required to exist at all.
    /// </remarks>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    public async Task Analyze_PatternMutationsDisabled_DropsThemAndKeepsEveryOtherGap()
    {
        var compilation = CompilationFactory.Create(PatternSource, ProductionAssemblyName);
        var manifest = new[] { CreateManifest(CoveredMemberId) };

        var enabled = await RunAsync(compilation, manifest).ConfigureAwait(false);
        var disabled = await RunAsync(compilation, manifest, CreateFamilyOptions(enable: false)).ConfigureAwait(false);

        _ = await Assert.That(Errors(compilation)).IsEmpty();
        _ = await Assert.That(PatternGaps(enabled)).IsEqualTo(ExpectAt(UncoveredPatternLine, _patternMutants));
        _ = await Assert.That(PatternGaps(disabled)).IsEqualTo(NoGaps);
        _ = await Assert.That(OtherGaps(disabled)).IsEqualTo(OtherGaps(enabled));
        _ = await Assert.That(OtherGaps(disabled)).IsNotEqualTo(NoGaps);
    }

    /// <summary>
    /// The documented default of the switch is <see langword="true" />, so setting it explicitly must not
    /// change a single diagnostic of the compilation.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    public async Task Analyze_PatternMutationsEnabledExplicitly_ReportsWhatTheDefaultReports()
    {
        var compilation = CompilationFactory.Create(PatternSource, ProductionAssemblyName);
        var manifest = new[] { CreateManifest(CoveredMemberId) };

        var byDefault = await RunAsync(compilation, manifest).ConfigureAwait(false);
        var enabled = await RunAsync(compilation, manifest, CreateFamilyOptions(enable: true)).ConfigureAwait(false);

        _ = await Assert.That(PatternGaps(enabled)).IsEqualTo(PatternGaps(byDefault));
        _ = await Assert.That(OtherGaps(enabled)).IsEqualTo(OtherGaps(byDefault));
    }

    /// <summary>
    /// The character-class shorthand swap of <c>RegexCharacterClassMutator</c> reaches the build log as an
    /// <c>FSH0001</c> through the very same pipeline as the four structural operators above, proving the
    /// operator is wired into the analyzer rather than merely constructed in isolation.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    public async Task Analyze_UntestedCharacterClassPattern_ReportsFSH0001ForTheShorthandSwap()
    {
        const string source = """
            namespace Fixture;

            using System.Text.RegularExpressions;

            public static class CharacterClassPatterns
            {
                public static bool IsCovered(string value)
                {
                    return new Regex(@"\d").IsMatch(value);
                }

                public static bool IsUncovered(string value)
                {
                    return new Regex(@"\d").IsMatch(value);
                }
            }
            """;
        const string coveredId = "M:Fixture.CharacterClassPatterns.IsCovered(System.String)~System.Boolean";

        var compilation = CompilationFactory.Create(source, ProductionAssemblyName);
        var diagnostics = await RunAsync(compilation, [CreateManifest(coveredId)]).ConfigureAwait(false);
        var messages = Summaries(diagnostics).Select(summary => summary.Message).ToArray();

        _ = await Assert.That(Errors(compilation)).IsEmpty();
        _ = await Assert
            .That(messages.Any(message => message.Contains(@"pattern '\d' => '\D'", StringComparison.Ordinal)))
            .IsTrue();
    }

    /// <summary>
    /// The lookaround negation of <c>RegexLookaroundMutator</c> reaches the build log as an <c>FSH0001</c>
    /// through the same pipeline, exactly like the character-class swap above.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    public async Task Analyze_UntestedLookaroundPattern_ReportsFSH0001ForTheNegation()
    {
        const string source = """
            namespace Fixture;

            using System.Text.RegularExpressions;

            public static class LookaroundPatterns
            {
                public static bool IsCovered(string value)
                {
                    return new Regex("a(?=b)").IsMatch(value);
                }

                public static bool IsUncovered(string value)
                {
                    return new Regex("a(?=b)").IsMatch(value);
                }
            }
            """;
        const string coveredId = "M:Fixture.LookaroundPatterns.IsCovered(System.String)~System.Boolean";

        var compilation = CompilationFactory.Create(source, ProductionAssemblyName);
        var diagnostics = await RunAsync(compilation, [CreateManifest(coveredId)]).ConfigureAwait(false);
        var messages = Summaries(diagnostics).Select(summary => summary.Message).ToArray();

        _ = await Assert.That(Errors(compilation)).IsEmpty();
        _ = await Assert
            .That(messages.Any(message => message.Contains("pattern 'a(?=b)' => 'a(?!b)'", StringComparison.Ordinal)))
            .IsTrue();
    }

    /// <summary>
    /// A bound shift is never the shorthand equivalence the classifier recognises: shifting <c>{0,1}</c>
    /// up to <c>{1,1}</c> narrows the accepted count from "zero or one" to "exactly one", which is an
    /// observable change under the real regular expression engine - the empty string matches the
    /// former and not the latter - so the classifier must decline to call it trivial and the mutant must
    /// surface as the <c>FSH0001</c> gap it actually is, even though its reason text superficially
    /// resembles the exact-one shorthand reason.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    public async Task Analyze_QuantifierBoundShiftToExactOne_ReportsFSH0001NotFSH0002()
    {
        const string source = """
            namespace Fixture;

            using System.Text.RegularExpressions;

            public static class QuantifierShorthandPatterns
            {
                public static bool IsCovered(string value)
                {
                    return value.Length > 0;
                }

                public static bool IsUncovered(string value)
                {
                    return new Regex("a{0,1}").IsMatch(value);
                }
            }
            """;
        const string coveredId = "M:Fixture.QuantifierShorthandPatterns.IsCovered(System.String)~System.Boolean";
        const string mutation = "pattern 'a{0,1}' => 'a{1,1}'";

        var compilation = CompilationFactory.Create(source, ProductionAssemblyName);
        var diagnostics = await RunAsync(compilation, [CreateManifest(coveredId)]).ConfigureAwait(false);

        var gapMessages = Summaries(diagnostics).Select(summary => summary.Message).ToArray();
        var trivialMessages = DiagnosticAssertions
            .Summarise(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.TrivialMutant))
            .Select(summary => summary.Message)
            .ToArray();

        _ = await Assert.That(Errors(compilation)).IsEmpty();
        _ = await Assert
            .That(gapMessages.Any(message => message.Contains(mutation, StringComparison.Ordinal)))
            .IsTrue();
        _ = await Assert
            .That(trivialMessages.Any(message => message.Contains(mutation, StringComparison.Ordinal)))
            .IsFalse();
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
    /// Builds the analyzer configuration the MSBuild property of the family produces.
    /// </summary>
    /// <param name="enable">Whether the family produces mutations.</param>
    /// <returns>The global options of a run.</returns>
    private static Dictionary<string, string> CreateFamilyOptions(bool enable) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["build_property.FrameShiftEnableRegexPatternMutations"] = enable ? "true" : "false",
        };

    /// <summary>
    /// Describes the reported gaps that name a pattern mutation, one line per diagnostic.
    /// </summary>
    /// <param name="diagnostics">All diagnostics of a run.</param>
    /// <returns>The described gaps, or <see cref="NoGaps" /> when there is none.</returns>
    private static string PatternGaps(ImmutableArray<Diagnostic> diagnostics) => Describe(diagnostics, pattern: true);

    /// <summary>
    /// Describes the reported gaps that do not name a pattern mutation, one line per diagnostic. They come
    /// from the other operators of the same compilation and must survive the family switch untouched.
    /// </summary>
    /// <param name="diagnostics">All diagnostics of a run.</param>
    /// <returns>The described gaps, or <see cref="NoGaps" /> when there is none.</returns>
    private static string OtherGaps(ImmutableArray<Diagnostic> diagnostics) => Describe(diagnostics, pattern: false);

    /// <summary>
    /// Counts the reported gaps that name a pattern mutation.
    /// </summary>
    /// <param name="diagnostics">All diagnostics of a run.</param>
    /// <returns>The number of matching gaps.</returns>
    private static int PatternGapCount(ImmutableArray<Diagnostic> diagnostics) =>
        Summaries(diagnostics).Count(summary => NamesAPattern(summary.Message));

    /// <summary>
    /// Describes one half of the reported gaps as one text block, ordered ordinally so that the result does
    /// not depend on the order the concurrently running analyzer callbacks reported them in. Several gaps
    /// share one location, which is exactly the case a positional order cannot separate.
    /// </summary>
    /// <param name="diagnostics">All diagnostics of a run.</param>
    /// <param name="pattern">
    /// <see langword="true" /> for the gaps naming a pattern mutation, <see langword="false" /> for the rest.
    /// </param>
    /// <returns>The described gaps, or <see cref="NoGaps" /> when there is none.</returns>
    private static string Describe(ImmutableArray<Diagnostic> diagnostics, bool pattern)
    {
        var entries = Summaries(diagnostics)
            .Where(summary => NamesAPattern(summary.Message) == pattern)
            .Select(summary => Entry(summary.Id, summary.Line, summary.Message))
            .ToList();

        return entries.Count == 0 ? NoGaps : Join(entries);
    }

    private static bool NamesAPattern(string message) => message.Contains(PatternMarker, StringComparison.Ordinal);

    private static ImmutableArray<(string Id, int Line, string Message)> Summaries(
        ImmutableArray<Diagnostic> diagnostics
    ) => DiagnosticAssertions.Summarise(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.UnreachableMutationPoint));

    /// <summary>
    /// Collects the distinct lines the reported gaps sit on, whichever operator produced them.
    /// </summary>
    /// <param name="diagnostics">All diagnostics of a run.</param>
    /// <returns>The lines, possibly empty.</returns>
    private static ImmutableArray<int> GapLines(ImmutableArray<Diagnostic> diagnostics) =>
        [.. Summaries(diagnostics).Select(summary => summary.Line).Distinct()];

    /// <summary>
    /// Builds the expectation of a set of gaps that all sit on <paramref name="line" />.
    /// </summary>
    /// <param name="line">The 1-based line every gap is reported on.</param>
    /// <param name="displayNames">The display names of the expected mutations.</param>
    /// <returns>The expected text block.</returns>
    private static string ExpectAt(int line, IEnumerable<string> displayNames) =>
        Join([.. displayNames.Select(displayName => GapEntry(line, displayName))]);

    /// <summary>
    /// Composes the display name a pattern mutation of <see cref="UncoveredPattern" /> carries.
    /// </summary>
    /// <param name="mutated">The rewritten pattern, as the regular expression engine sees it.</param>
    /// <returns>The display name.</returns>
    private static string Mutant(string mutated) => PatternMarker + UncoveredPattern + "' => '" + mutated + "'";

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
