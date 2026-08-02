namespace NetEvolve.FrameShift.Tests.Integration;

using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Analyzers;
using NetEvolve.FrameShift.Diagnostics;
using NetEvolve.FrameShift.Tests.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Performs the complete two-pass cycle of FrameShift without touching the file system: the test-side
/// pass collects the surface of a real TUnit test assembly, the manifest is serialised exactly as it
/// would be checked in, and the production-side analyzer consumes that text as an additional file of
/// the production compilation.
/// </summary>
/// <remarks>
/// <para>
/// This is the proof that the design holds together. The production fixture is deliberately tiny and
/// free of literals and boolean logic, so that the mutation points are exactly the three binary
/// arithmetic expressions on <see cref="AddLine" />, <see cref="SubtractLine" /> and
/// <see cref="TwiceLine" />; every assertion can therefore pin the outcome to an exact set of
/// identifier and line pairs instead of a count.
/// </para>
/// <para>
/// <c>Doubler.Twice</c> exists to cover the transitive part of the closure: no test names it,
/// it is reachable only through the covered <c>Add</c>, and a gap reported inside it would mean the
/// production-side closure never happened.
/// </para>
/// <para>
/// <b>Snapshots.</b> Every cycle is additionally verified as a single document that carries the
/// serialised manifest and the diagnostics that manifest produced, see <see cref="DescribeCycle" />.
/// Cause and effect are therefore reviewed in one file: a manifest entry that disappears and the
/// warning that appears because of it show up in the same diff. The snapshots are evidence, never the
/// assertion — the claims this repository lives by are asserted explicitly in the tests below, so that
/// a regression can never degrade into "the snapshot changed".
/// </para>
/// <para>
/// Nothing in a snapshot of this class depends on the executing target framework. The manifest is
/// derived from the fixture sources alone, the diagnostics are located in the fixture file and carry
/// only the fixed message of their descriptor, the manifest path is the constant default of
/// <see cref="InMemoryAdditionalText" />, and the lines are joined with a literal line feed instead of
/// <see cref="Environment.NewLine" />, so that the eight runs of the matrix agree byte for byte.
/// </para>
/// </remarks>
public class MutationAnalysisRoundTripTests
{
    private const string ProductionAssemblyName = "ProductionAssembly";
    private const string TestAssemblyName = "TestAssembly";
    private const string ProductionPath = "Production.cs";
    private const string TestPath = "CalculatorTests.cs";

    private const string LineFeed = "\n";
    private const char LineFeedCharacter = '\n';

    /// <summary>
    /// The part of the <c>FSH0006</c> message the quoted test method name follows.
    /// </summary>
    private const string TestMethodMarker = "the one of test method '";

    /// <summary>
    /// The quote closing the test method name in an <c>FSH0006</c> message.
    /// </summary>
    private const char QuoteCharacter = '\'';

    private const string ManifestHeading = "--- manifest ---";
    private const string DiagnosticsHeading = "--- diagnostics ---";
    private const string BeforeHeading = "=== before: only Add is tested ===";
    private const string AfterHeading = "=== after: Subtract is tested as well ===";

    /// <summary>
    /// The line of <c>return Doubler.Twice(left) + right;</c>, the body of the covered method.
    /// </summary>
    private const int AddLine = 7;

    /// <summary>
    /// The line of <c>return left - right;</c>, the body of the method no test reaches.
    /// </summary>
    private const int SubtractLine = 12;

    /// <summary>
    /// The line of <c>return value + value;</c>, the body of the transitively reachable helper.
    /// </summary>
    private const int TwiceLine = 20;

    private const string ProductionSource = """
        namespace Fixture;

        public class Calculator
        {
            public int Add(int left, int right)
            {
                return Doubler.Twice(left) + right;
            }

            public int Subtract(int left, int right)
            {
                return left - right;
            }
        }

        public static class Doubler
        {
            public static int Twice(int value)
            {
                return value + value;
            }
        }
        """;

    private const string AddCoveredSource = """
        namespace Tests;

        using TUnit.Assertions;
        using TUnit.Assertions.Extensions;
        using TUnit.Core;

        public class CalculatorTests
        {
            [Test]
            public void Add_ReturnsTheSum()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                int result = calculator.Add(2, 3);

                Assert.That(result).IsEqualTo(7).GetAwaiter().GetResult();
            }
        }
        """;

    private const string SubtractCoveredSource = """
        namespace Tests;

        using TUnit.Assertions;
        using TUnit.Assertions.Extensions;
        using TUnit.Core;

        public class CalculatorTests
        {
            [Test]
            public void Subtract_ReturnsTheDifference()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                int result = calculator.Subtract(5, 2);

                Assert.That(result).IsEqualTo(3).GetAwaiter().GetResult();
            }
        }
        """;

    private const string BothCoveredSource = """
        namespace Tests;

        using TUnit.Assertions;
        using TUnit.Assertions.Extensions;
        using TUnit.Core;

        public class CalculatorTests
        {
            [Test]
            public void Add_ReturnsTheSum()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                int result = calculator.Add(2, 3);

                Assert.That(result).IsEqualTo(7).GetAwaiter().GetResult();
            }

            [Test]
            public void Subtract_ReturnsTheDifference()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                int result = calculator.Subtract(5, 2);

                Assert.That(result).IsEqualTo(3).GetAwaiter().GetResult();
            }
        }
        """;

    /// <summary>
    /// A test that touches the production assembly, but not a single member that carries a mutation
    /// point: it only creates the type. The manifest is therefore usable and the closure is not empty,
    /// which is exactly the situation in which every mutable member has to be reported.
    /// </summary>
    private const string ConstructionOnlySource = """
        namespace Tests;

        using TUnit.Core;

        public class CalculatorTests
        {
            [Test]
            public void Construction_DoesNotExerciseAnyBehaviour()
            {
                _ = new Fixture.Calculator();
            }
        }
        """;

    private const string WithoutAnyTestSource = """
        namespace Tests;

        public class NothingIsTestedHere
        {
        }
        """;

    /// <summary>
    /// Two tests, each exercising one input combination, which together reach <c>Doubler.Twice</c> —
    /// one transitively through <c>Calculator.Add</c> and one directly. Aggregated over both, the helper
    /// carries two input combinations, while <c>Calculator.Add</c> keeps exactly one and
    /// <c>Calculator.Subtract</c> stays out of the reachable set altogether.
    /// </summary>
    private const string SharedHelperSource = """
        namespace Tests;

        using TUnit.Assertions;
        using TUnit.Assertions.Extensions;
        using TUnit.Core;

        public class CalculatorTests
        {
            [Test]
            public void Add_ReturnsTheSum()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                int result = calculator.Add(2, 3);

                Assert.That(result).IsEqualTo(7).GetAwaiter().GetResult();
            }

            [Test]
            public void Twice_DoublesTheValue()
            {
                int result = Fixture.Doubler.Twice(4);

                Assert.That(result).IsEqualTo(8).GetAwaiter().GetResult();
            }
        }
        """;

    /// <summary>
    /// The very same coverage as <see cref="AddCoveredSource" />, but declared with three
    /// <c>[Arguments]</c> rows, so the recogniser answers an exact three instead of an exact one.
    /// </summary>
    private const string ThreeCaseSource = """
        namespace Tests;

        using TUnit.Assertions;
        using TUnit.Assertions.Extensions;
        using TUnit.Core;

        public class CalculatorTests
        {
            [Test]
            [Arguments(1, 2)]
            [Arguments(3, 4)]
            [Arguments(5, 6)]
            public void Add_ReturnsTheSum(int left, int right)
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                int result = calculator.Add(left, right);

                Assert.That(result).IsEqualTo((left * 2) + right).GetAwaiter().GetResult();
            }
        }
        """;

    /// <summary>
    /// The same single test, but taking its cases from a method the framework only resolves by executing
    /// it during discovery, which an analyzer must not do. The count is therefore a lower bound.
    /// </summary>
    private const string DataSourceSource = """
        namespace Tests;

        using System.Collections.Generic;
        using TUnit.Assertions;
        using TUnit.Assertions.Extensions;
        using TUnit.Core;

        public class CalculatorTests
        {
            public static IEnumerable<(int, int)> Rows()
            {
                yield return (1, 2);
            }

            [Test]
            [MethodDataSource(nameof(Rows))]
            public void Add_ReturnsTheSum(int left, int right)
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                int result = calculator.Add(left, right);

                Assert.That(result).IsEqualTo((left * 2) + right).GetAwaiter().GetResult();
            }
        }
        """;

    [Test]
    public async Task Fixtures_ProductionAndEveryTestAssembly_CompileWithoutErrors()
    {
        var production = CreateProduction();
        var described = new List<string> { Describe(production) };

        foreach (var source in GetTestSources())
        {
            described.Add(Describe(CreateTest(production, source)));
        }

        _ = await Assert
            .That(string.Join("|", described.Distinct(StringComparer.Ordinal)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    /// <summary>
    /// The full cycle for a production assembly whose <c>Add</c> is tested and whose <c>Subtract</c> is
    /// not: the gap is reported inside <c>Subtract</c> only, neither in the tested method nor in the
    /// helper that is reachable only through it.
    /// </summary>
    /// <remarks>
    /// The assertion on <see cref="TwiceLine" /> is the one that pins the transitive closure. No test
    /// names <c>Doubler.Twice</c>; it enters the reachable set only because the covered <c>Add</c> calls
    /// it, and the moment the production-side closure stops expanding, a gap appears there.
    /// </remarks>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    public async Task RoundTrip_TestCoveringOneMethod_ReportsTheGapOnlyInTheUncoveredMethod()
    {
        var cycle = await RunCycleAsync(AddCoveredSource).ConfigureAwait(false);
        var lines = GapLines(cycle.Diagnostics);

        using (Assert.Multiple())
        {
            _ = await Assert.That(DescribeGaps(cycle.Diagnostics)).IsEqualTo(Gap(SubtractLine));
            _ = await Assert.That(lines.Contains(SubtractLine)).IsTrue();
            _ = await Assert.That(lines.Contains(AddLine)).IsFalse();
            _ = await Assert.That(lines.Contains(TwiceLine)).IsFalse();
        }

        _ = await Verify(DescribeCycle(cycle)).ConfigureAwait(false);
    }

    /// <summary>
    /// The single most important expectation of this repository: writing a test for a reported gap and
    /// running the whole cycle again makes that gap disappear completely.
    /// </summary>
    /// <remarks>
    /// The claim is asserted as a set difference in both directions — the gap of <c>Subtract</c> is
    /// removed, nothing is added, and not a single <c>FSH0001</c> survives anywhere — so that it can
    /// never weaken into "the snapshot changed". The snapshot is the additional evidence beside it: it
    /// shows the manifest entry the new test contributed next to the warning that entry silenced.
    /// </remarks>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    public async Task RoundTrip_TestAddedForTheReportedGap_MakesTheGapDisappear()
    {
        var before = await RunCycleAsync(AddCoveredSource).ConfigureAwait(false);
        var after = await RunCycleAsync(BothCoveredSource).ConfigureAwait(false);

        var removed = GapEntries(before.Diagnostics).Except(GapEntries(after.Diagnostics), StringComparer.Ordinal);
        var added = GapEntries(after.Diagnostics).Except(GapEntries(before.Diagnostics), StringComparer.Ordinal);

        using (Assert.Multiple())
        {
            _ = await Assert.That(string.Join("|", removed)).IsEqualTo(Gap(SubtractLine));
            _ = await Assert.That(string.Join("|", added)).IsEqualTo(string.Empty);
            _ = await Assert.That(OnlyGaps(after.Diagnostics).IsEmpty).IsTrue();
            _ = await Assert.That(DescribeGaps(after.Diagnostics)).IsEqualTo(string.Empty);
        }

        _ = await Verify(DescribeTransition(before, after)).ConfigureAwait(false);
    }

    /// <summary>
    /// The counterpart of the previous test: deleting the test for a covered method makes new gaps
    /// appear, both in that method and in the helper it was the only route to.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    public async Task RoundTrip_TestDeletedForACoveredMethod_ReportsNewGaps()
    {
        var before = await RunCycleAsync(BothCoveredSource).ConfigureAwait(false);
        var after = await RunCycleAsync(SubtractCoveredSource).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(DescribeGaps(before.Diagnostics)).IsEqualTo(string.Empty);
            _ = await Assert.That(DescribeGaps(after.Diagnostics)).IsEqualTo(Gap(AddLine) + "|" + Gap(TwiceLine));
        }

        _ = await Verify(DescribeCycle(after)).ConfigureAwait(false);
    }

    /// <summary>
    /// A production assembly whose tests reach none of its behaviour reports a gap at every mutable
    /// member, not just at the one a reviewer happens to look at.
    /// </summary>
    /// <remarks>
    /// The single test creates the type and nothing else, so the manifest is perfectly usable and the
    /// closure is not empty — the analyzer therefore has no reason to blame the manifest and has to name
    /// all three mutation points, including the helper it can only reach through an untested caller.
    /// </remarks>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    public async Task RoundTrip_TestTouchingNoMutableMember_ReportsAGapAtEveryMutationPoint()
    {
        var cycle = await RunCycleAsync(ConstructionOnlySource).ConfigureAwait(false);
        var expected = Gap(AddLine) + "|" + Gap(SubtractLine) + "|" + Gap(TwiceLine);

        using (Assert.Multiple())
        {
            _ = await Assert.That(DescribeGaps(cycle.Diagnostics)).IsEqualTo(expected);
            _ = await Assert
                .That(string.Join("|", DiagnosticAssertions.Ids(cycle.Diagnostics).Distinct(StringComparer.Ordinal)))
                .IsEqualTo(DiagnosticIds.UnreachableMutationPoint);
        }

        _ = await Verify(DescribeCycle(cycle)).ConfigureAwait(false);
    }

    /// <summary>
    /// A test assembly without a single test produces a manifest without a single recorded member, and
    /// the production side then names the unusable manifest instead of blaming every mutation point of
    /// the compilation.
    /// </summary>
    /// <remarks>
    /// Reporting a gap for every member here would drown the build in warnings and would point at the
    /// code although the manifest is the problem, so the analyzer deliberately reports the cause once.
    /// The complementary case, a usable manifest that reaches nothing mutable, is
    /// <see cref="RoundTrip_TestTouchingNoMutableMember_ReportsAGapAtEveryMutationPoint" />, and there
    /// every mutable member is reported.
    /// </remarks>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    public async Task RoundTrip_ProductionWithoutAnyTest_NamesTheUnusableManifestInsteadOfEveryMember()
    {
        var cycle = await RunCycleAsync(WithoutAnyTestSource).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(string.Join("|", DiagnosticAssertions.Ids(cycle.Diagnostics)))
                .IsEqualTo(DiagnosticIds.InvalidTestSurfaceManifest);
            _ = await Assert.That(DescribeGaps(cycle.Diagnostics)).IsEqualTo(string.Empty);

            var message = Message(cycle.Diagnostics[0]);
            var namesTheEmptyManifest = message.Contains(
                "does not record a single referenced production member",
                StringComparison.Ordinal
            );

            _ = await Assert.That(namesTheEmptyManifest).IsTrue();
        }

        _ = await Verify(DescribeCycle(cycle)).ConfigureAwait(false);
    }

    /// <summary>
    /// Both passes have to agree on the very same canonical text, therefore the manifest the production
    /// side just consumed is handed back to the test-side analyzer, which must not call it stale.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    public async Task RoundTrip_GeneratedManifest_IsAcceptedByTheTestSideAnalyzerAgain()
    {
        var production = CreateProduction();
        var test = CreateTest(production, BothCoveredSource);
        var manifest = CreateManifest(test);

        var diagnostics = await AnalyzerRunner
            .RunAsync(
                new TUnitTestSurfaceAnalyzer(),
                test,
                DiagnosticIds.InvalidTestSurfaceManifest,
                [new InMemoryAdditionalText(manifest)]
            )
            .ConfigureAwait(false);

        var hasCanonicalHeader = manifest.StartsWith("frameshift-test-surface/1\n", StringComparison.Ordinal);

        using (Assert.Multiple())
        {
            _ = await Assert.That(hasCanonicalHeader).IsTrue();
            _ = await Assert
                .That(DiagnosticAssertions.Describe(diagnostics))
                .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    /// <summary>
    /// The whole point of <c>FSH0006</c>, driven through the real collector rather than a hand-written
    /// manifest: a member reached by exactly one exact test case is reported, and the message names that
    /// very test.
    /// </summary>
    /// <remarks>
    /// The transitive part matters as much as the direct one. No test names <c>Doubler.Twice</c>; it is
    /// reached only through the covered <c>Add</c>, so the single case has to travel along the closure and
    /// arrive there with its attribution intact. Both reported lines are pinned, and <c>Subtract</c> is
    /// pinned as an <c>FSH0001</c> gap instead, because the hint refines "covered" and must never fire
    /// where nothing is covered at all.
    /// </remarks>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    public async Task RoundTrip_MemberReachedByOneExactTestCase_ReportsTheSingleTestCaseHint()
    {
        var cycle = await RunCycleAsync(AddCoveredSource).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(DescribeSingleCases(cycle.Diagnostics))
                .IsEqualTo(SingleCase(AddLine) + "|" + SingleCase(TwiceLine));
            _ = await Assert.That(DescribeGaps(cycle.Diagnostics)).IsEqualTo(Gap(SubtractLine));
            _ = await Assert
                .That(SingleCaseTestNames(cycle.Diagnostics))
                .IsEqualTo("Tests.CalculatorTests.Add_ReturnsTheSum");
        }
    }

    /// <summary>
    /// The aggregation, and the failure mode that would make <c>FSH0006</c> lie: a member reached by two
    /// tests through two different callers carries two input combinations and must not be reported, while
    /// a member still reached by one of them keeps its hint.
    /// </summary>
    /// <remarks>
    /// This is the understated-attribution guard. If the closure attributed <c>Doubler.Twice</c> to only
    /// one of the two tests reaching it, the sum would be one and the helper would be reported falsely —
    /// which is worse than reporting nothing, because it names a gap that does not exist. Asserting the
    /// exact reported set rather than the absence of one line is what makes that visible.
    /// </remarks>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    public async Task RoundTrip_MemberReachedByTwoTests_ReportsTheHintOnlyWhereOneTestReaches()
    {
        var cycle = await RunCycleAsync(SharedHelperSource).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(DescribeSingleCases(cycle.Diagnostics)).IsEqualTo(SingleCase(AddLine));
            _ = await Assert.That(DescribeGaps(cycle.Diagnostics)).IsEqualTo(Gap(SubtractLine));
            _ = await Assert
                .That(SingleCaseTestNames(cycle.Diagnostics))
                .IsEqualTo("Tests.CalculatorTests.Add_ReturnsTheSum");
        }
    }

    /// <summary>
    /// Three inline rows are three input combinations, so the same coverage that produced the hint with a
    /// parameterless test produces none — the sum is exact, but it is not one.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    public async Task RoundTrip_MemberReachedByThreeExactTestCases_ReportsNoSingleTestCaseHint()
    {
        var cycle = await RunCycleAsync(ThreeCaseSource).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(cycle.Manifest.Contains(" 3\n", StringComparison.Ordinal)).IsTrue();
            _ = await Assert.That(DescribeSingleCases(cycle.Diagnostics)).IsEqualTo(string.Empty);
            _ = await Assert.That(DescribeGaps(cycle.Diagnostics)).IsEqualTo(Gap(SubtractLine));
        }
    }

    /// <summary>
    /// A single test whose cases come from a data source the analyzer must not execute contributes a lower
    /// bound, and a lower bound anywhere in the contributing set suppresses the hint — the true total
    /// could be far higher.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    public async Task RoundTrip_MemberReachedByALowerBoundTestCase_ReportsNoSingleTestCaseHint()
    {
        var cycle = await RunCycleAsync(DataSourceSource).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(cycle.Manifest.Contains(" 1+\n", StringComparison.Ordinal)).IsTrue();
            _ = await Assert.That(DescribeSingleCases(cycle.Diagnostics)).IsEqualTo(string.Empty);
            _ = await Assert.That(DescribeGaps(cycle.Diagnostics)).IsEqualTo(Gap(SubtractLine));
        }
    }

    private static IEnumerable<string> GetTestSources() =>
        [
            AddCoveredSource,
            SubtractCoveredSource,
            BothCoveredSource,
            ConstructionOnlySource,
            WithoutAnyTestSource,
            SharedHelperSource,
            ThreeCaseSource,
            DataSourceSource,
        ];

    /// <summary>
    /// Runs the complete cycle: collect the surface of the test assembly, serialise it, and analyse the
    /// production compilation with that text as its only additional file.
    /// </summary>
    /// <param name="testSource">The source of the test assembly compiled against the production one.</param>
    /// <returns>The manifest that was fed in and every diagnostic the production-side analyzer reported.</returns>
    private static async Task<(string Manifest, ImmutableArray<Diagnostic> Diagnostics)> RunCycleAsync(
        string testSource
    )
    {
        var production = CreateProduction();
        var manifest = CreateManifest(CreateTest(production, testSource));

        var diagnostics = await AnalyzerRunner
            .RunAsync(
                new MutationCoverageAnalyzer(),
                production,
                additionalFiles: [new InMemoryAdditionalText(manifest)]
            )
            .ConfigureAwait(false);

        return (manifest, diagnostics);
    }

    private static CSharpCompilation CreateProduction() =>
        CompilationFactory.Create(ProductionSource, ProductionAssemblyName, filePath: ProductionPath);

    private static CSharpCompilation CreateTest(Compilation production, string source) =>
        CompilationFactory.Create(
            source,
            TestAssemblyName,
            includeTUnit: true,
            additionalReferences:
            [
                production.ToMetadataReference(),
                MetadataReference.CreateFromFile(typeof(TUnit.Assertions.Assert).Assembly.Location),
            ],
            filePath: TestPath
        );

    /// <summary>
    /// Collects the surface of <paramref name="test" /> and serialises it, which sorts both groups of
    /// entries ordinally, exactly as the checked-in manifest of a real project is sorted.
    /// </summary>
    /// <param name="test">The test compilation to collect.</param>
    /// <returns>The canonical manifest text, ending with a line feed.</returns>
    private static string CreateManifest(Compilation test)
    {
        var recognizer = TUnitTestFrameworkProbe.Instance.TryCreateRecognizer(test)!;

        return TestSurfaceManifestWriter.Write(TestSurfaceCollector.Collect(test, recognizer, CancellationToken.None));
    }

    /// <summary>
    /// Describes one cycle as the manifest that went in and the diagnostics that came out.
    /// </summary>
    /// <param name="cycle">The manifest and the diagnostics of one cycle.</param>
    /// <returns>The snapshot text, without a trailing line feed.</returns>
    private static string DescribeCycle((string Manifest, ImmutableArray<Diagnostic> Diagnostics) cycle)
    {
        string[] parts =
        [
            ManifestHeading,
            cycle.Manifest.TrimEnd(LineFeedCharacter),
            string.Empty,
            DiagnosticsHeading,
            DescribeDiagnostics(cycle.Diagnostics),
        ];

        return string.Join(LineFeed, parts);
    }

    /// <summary>
    /// Describes two cycles as one document, so that the manifest entry a new test contributes and the
    /// warning that entry silences are read next to each other.
    /// </summary>
    /// <param name="before">The cycle before the test was added.</param>
    /// <param name="after">The cycle after the test was added.</param>
    /// <returns>The snapshot text, without a trailing line feed.</returns>
    private static string DescribeTransition(
        (string Manifest, ImmutableArray<Diagnostic> Diagnostics) before,
        (string Manifest, ImmutableArray<Diagnostic> Diagnostics) after
    )
    {
        string[] parts = [BeforeHeading, DescribeCycle(before), string.Empty, AfterHeading, DescribeCycle(after)];

        return string.Join(LineFeed, parts);
    }

    /// <summary>
    /// Describes every diagnostic on its own line, in a fully deterministic order.
    /// </summary>
    /// <param name="diagnostics">The diagnostics of one cycle.</param>
    /// <returns>The description, or the text for an empty set.</returns>
    /// <remarks>
    /// The order is file, then position, then identifier, then message. The message is part of the key
    /// on purpose: the four arithmetic mutants of one expression share their file, their position and
    /// their identifier, and sorting an immutable array is not stable, so without that last component
    /// their order would be whatever the sort happened to produce. The lines are joined with a literal
    /// line feed rather than <see cref="Environment.NewLine" />, so that the snapshot does not depend on
    /// the host operating system.
    /// </remarks>
    private static string DescribeDiagnostics(ImmutableArray<Diagnostic> diagnostics)
    {
        if (diagnostics.IsDefaultOrEmpty)
        {
            return DiagnosticAssertions.NoDiagnostics;
        }

        var ordered = diagnostics.Sort(CompareDiagnostics);
        var lines = ordered.Select(diagnostic => DiagnosticAssertions.Describe(diagnostic));

        return string.Join(LineFeed, lines);
    }

    private static int CompareDiagnostics(Diagnostic left, Diagnostic right)
    {
        var leftSpan = left.Location.GetLineSpan();
        var rightSpan = right.Location.GetLineSpan();
        var result = StringComparer.Ordinal.Compare(leftSpan.Path, rightSpan.Path);

        if (result != 0)
        {
            return result;
        }

        result = leftSpan.StartLinePosition.CompareTo(rightSpan.StartLinePosition);

        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(left.Id, right.Id);

        return result != 0 ? result : StringComparer.Ordinal.Compare(Message(left), Message(right));
    }

    /// <summary>
    /// Reduces the reported gaps to the distinct identifier and line pairs they sit on, ordered by
    /// location, so that an expectation does not depend on how many operators mutate the same line.
    /// </summary>
    /// <param name="diagnostics">The diagnostics of one cycle.</param>
    /// <returns>The pinned description of the gaps, empty when there is none.</returns>
    private static string DescribeGaps(ImmutableArray<Diagnostic> diagnostics) =>
        string.Join("|", GapEntries(diagnostics));

    /// <summary>
    /// Collects the distinct identifier and line pairs the gaps sit on, which is the set the central
    /// before-and-after claim is compared on.
    /// </summary>
    /// <param name="diagnostics">The diagnostics of one cycle.</param>
    /// <returns>The entries, possibly empty.</returns>
    private static ImmutableArray<string> GapEntries(ImmutableArray<Diagnostic> diagnostics) =>
        [
            .. DiagnosticAssertions
                .Summarise(OnlyGaps(diagnostics))
                .Select(entry => entry.Id + ":" + ToText(entry.Line))
                .Distinct(StringComparer.Ordinal),
        ];

    private static ImmutableArray<int> GapLines(ImmutableArray<Diagnostic> diagnostics) =>
        [.. DiagnosticAssertions.Summarise(OnlyGaps(diagnostics)).Select(entry => entry.Line).Distinct()];

    private static ImmutableArray<Diagnostic> OnlyGaps(ImmutableArray<Diagnostic> diagnostics) =>
        AnalyzerRunner.OfId(diagnostics, DiagnosticIds.UnreachableMutationPoint);

    private static string Gap(int line) => DiagnosticIds.UnreachableMutationPoint + ":" + ToText(line);

    /// <summary>
    /// Reduces the reported single-test-case hints to the distinct identifier and line pairs they sit on,
    /// exactly as <see cref="DescribeGaps" /> does for the gaps.
    /// </summary>
    /// <param name="diagnostics">The diagnostics of one cycle.</param>
    /// <returns>The pinned description of the hints, empty when there is none.</returns>
    private static string DescribeSingleCases(ImmutableArray<Diagnostic> diagnostics) =>
        string.Join(
            "|",
            DiagnosticAssertions
                .Summarise(OnlySingleCases(diagnostics))
                .Select(entry => entry.Id + ":" + ToText(entry.Line))
                .Distinct(StringComparer.Ordinal)
        );

    /// <summary>
    /// Collects the test method names the single-test-case hints name, which is what proves the attribution
    /// arrived rather than merely that some hint was reported.
    /// </summary>
    /// <param name="diagnostics">The diagnostics of one cycle.</param>
    /// <returns>The distinct names, ordered ordinally and joined, empty when there is no hint.</returns>
    private static string SingleCaseTestNames(ImmutableArray<Diagnostic> diagnostics) =>
        string.Join(
            "|",
            OnlySingleCases(diagnostics)
                .Select(diagnostic => ExtractTestName(Message(diagnostic)))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
        );

    /// <summary>
    /// Reads the test method name out of an <c>FSH0006</c> message, which quotes it behind
    /// <see cref="TestMethodMarker" />.
    /// </summary>
    /// <param name="message">The formatted message of the diagnostic.</param>
    /// <returns>The quoted test method name, or the whole message when the marker is absent.</returns>
    private static string ExtractTestName(string message)
    {
        var start = message.IndexOf(TestMethodMarker, StringComparison.Ordinal);

        if (start < 0)
        {
            return message;
        }

        // Splitting keeps the closing quote out of the name without an IndexOf overload that the classic
        // target frameworks do not offer.
        return message.Substring(start + TestMethodMarker.Length).Split(QuoteCharacter)[0];
    }

    private static ImmutableArray<Diagnostic> OnlySingleCases(ImmutableArray<Diagnostic> diagnostics) =>
        AnalyzerRunner.OfId(diagnostics, DiagnosticIds.SingleTestCaseMutationPoint);

    private static string SingleCase(int line) => DiagnosticIds.SingleTestCaseMutationPoint + ":" + ToText(line);

    private static string Describe(Compilation compilation) =>
        DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation));

    private static string Message(Diagnostic diagnostic) => diagnostic.GetMessage(CultureInfo.InvariantCulture);

    private static string ToText(int value) => value.ToString(CultureInfo.InvariantCulture);
}
