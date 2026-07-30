namespace NetEvolve.Frameshift.Tests.Integration;

using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.Frameshift.Analyzers;
using NetEvolve.Frameshift.Diagnostics;
using NetEvolve.Frameshift.Tests.Infrastructure;
using NetEvolve.Frameshift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Performs the complete two-pass cycle of Frameshift without touching the file system: the test-side
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
/// </remarks>
public class MutationAnalysisRoundTripTests
{
    private const string ProductionAssemblyName = "ProductionAssembly";
    private const string TestAssemblyName = "TestAssembly";
    private const string ProductionPath = "Production.cs";
    private const string TestPath = "CalculatorTests.cs";

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

        using TUnit.Core;

        public class CalculatorTests
        {
            [Test]
            public void Add_ReturnsTheSum()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Add(2, 3);
            }
        }
        """;

    private const string SubtractCoveredSource = """
        namespace Tests;

        using TUnit.Core;

        public class CalculatorTests
        {
            [Test]
            public void Subtract_ReturnsTheDifference()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Subtract(5, 2);
            }
        }
        """;

    private const string BothCoveredSource = """
        namespace Tests;

        using TUnit.Core;

        public class CalculatorTests
        {
            [Test]
            public void Add_ReturnsTheSum()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Add(2, 3);
            }

            [Test]
            public void Subtract_ReturnsTheDifference()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Subtract(5, 2);
            }
        }
        """;

    private const string WithoutAnyTestSource = """
        namespace Tests;

        public class NothingIsTestedHere
        {
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
    [Test]
    public async Task RoundTrip_TestCoveringOneMethod_ReportsTheGapOnlyInTheUncoveredMethod()
    {
        var diagnostics = await RunCycleAsync(AddCoveredSource).ConfigureAwait(false);
        var lines = GapLines(diagnostics);

        _ = await Assert.That(DescribeGaps(diagnostics)).IsEqualTo(Gap(SubtractLine));
        _ = await Assert.That(lines.Contains(SubtractLine)).IsTrue();
        _ = await Assert.That(lines.Contains(AddLine)).IsFalse();
        _ = await Assert.That(lines.Contains(TwiceLine)).IsFalse();
    }

    /// <summary>
    /// The single most important expectation of this repository: writing a test for a reported gap and
    /// running the whole cycle again makes that gap disappear completely.
    /// </summary>
    [Test]
    public async Task RoundTrip_TestAddedForTheReportedGap_MakesTheGapDisappear()
    {
        var before = DescribeGaps(await RunCycleAsync(AddCoveredSource).ConfigureAwait(false));

        var after = DescribeGaps(await RunCycleAsync(BothCoveredSource).ConfigureAwait(false));

        _ = await Assert.That(before).IsEqualTo(Gap(SubtractLine));
        _ = await Assert.That(after).IsEqualTo(string.Empty);
        _ = await Assert.That(after).IsNotEqualTo(before);
    }

    /// <summary>
    /// The counterpart of the previous test: deleting the test for a covered method makes new gaps
    /// appear, both in that method and in the helper it was the only route to.
    /// </summary>
    [Test]
    public async Task RoundTrip_TestDeletedForACoveredMethod_ReportsNewGaps()
    {
        var before = DescribeGaps(await RunCycleAsync(BothCoveredSource).ConfigureAwait(false));

        var after = DescribeGaps(await RunCycleAsync(SubtractCoveredSource).ConfigureAwait(false));

        _ = await Assert.That(before).IsEqualTo(string.Empty);
        _ = await Assert.That(after).IsEqualTo(Gap(AddLine) + "|" + Gap(TwiceLine));
    }

    /// <summary>
    /// A test assembly without a single test produces a manifest without a single recorded member, and
    /// the production side then names the unusable manifest instead of blaming every mutation point of
    /// the compilation.
    /// </summary>
    /// <remarks>
    /// Reporting a gap for every member here would drown the build in warnings and would point at the
    /// code although the manifest is the problem, so the analyzer deliberately reports the cause once.
    /// </remarks>
    [Test]
    public async Task RoundTrip_ProductionWithoutAnyTest_NamesTheUnusableManifestInsteadOfEveryMember()
    {
        var diagnostics = await RunCycleAsync(WithoutAnyTestSource).ConfigureAwait(false);

        _ = await Assert
            .That(string.Join("|", DiagnosticAssertions.Ids(diagnostics)))
            .IsEqualTo(DiagnosticIds.InvalidTestSurfaceManifest);
        _ = await Assert.That(DescribeGaps(diagnostics)).IsEqualTo(string.Empty);

        var message = Message(diagnostics[0]);
        var namesTheEmptyManifest = message.Contains(
            "does not record a single referenced production member",
            StringComparison.Ordinal
        );

        _ = await Assert.That(namesTheEmptyManifest).IsTrue();
    }

    /// <summary>
    /// Both passes have to agree on the very same canonical text, therefore the manifest the production
    /// side just consumed is handed back to the test-side analyzer, which must not call it stale.
    /// </summary>
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

        _ = await Assert.That(hasCanonicalHeader).IsTrue();
        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    private static IEnumerable<string> GetTestSources() =>
        [AddCoveredSource, SubtractCoveredSource, BothCoveredSource, WithoutAnyTestSource];

    /// <summary>
    /// Runs the complete cycle: collect the surface of the test assembly, serialise it, and analyse the
    /// production compilation with that text as its only additional file.
    /// </summary>
    /// <param name="testSource">The source of the test assembly compiled against the production one.</param>
    /// <returns>Every diagnostic the production-side analyzer reported.</returns>
    private static async Task<ImmutableArray<Diagnostic>> RunCycleAsync(string testSource)
    {
        var production = CreateProduction();
        var manifest = CreateManifest(CreateTest(production, testSource));

        return await AnalyzerRunner
            .RunAsync(
                new MutationCoverageAnalyzer(),
                production,
                additionalFiles: [new InMemoryAdditionalText(manifest)]
            )
            .ConfigureAwait(false);
    }

    private static CSharpCompilation CreateProduction() =>
        CompilationFactory.Create(ProductionSource, ProductionAssemblyName, filePath: ProductionPath);

    private static CSharpCompilation CreateTest(Compilation production, string source) =>
        CompilationFactory.Create(
            source,
            TestAssemblyName,
            includeTUnit: true,
            additionalReferences: [production.ToMetadataReference()],
            filePath: TestPath
        );

    private static string CreateManifest(Compilation test) =>
        TestSurfaceManifestWriter.Write(TestSurfaceCollector.Collect(test, CancellationToken.None));

    /// <summary>
    /// Reduces the reported gaps to the distinct identifier and line pairs they sit on, ordered by
    /// location, so that an expectation does not depend on how many operators mutate the same line.
    /// </summary>
    /// <param name="diagnostics">The diagnostics of one cycle.</param>
    /// <returns>The pinned description of the gaps, empty when there is none.</returns>
    private static string DescribeGaps(ImmutableArray<Diagnostic> diagnostics) =>
        string.Join(
            "|",
            DiagnosticAssertions
                .Summarise(OnlyGaps(diagnostics))
                .Select(entry => entry.Id + ":" + ToText(entry.Line))
                .Distinct(StringComparer.Ordinal)
        );

    private static ImmutableArray<int> GapLines(ImmutableArray<Diagnostic> diagnostics) =>
        [.. DiagnosticAssertions.Summarise(OnlyGaps(diagnostics)).Select(entry => entry.Line).Distinct()];

    private static ImmutableArray<Diagnostic> OnlyGaps(ImmutableArray<Diagnostic> diagnostics) =>
        AnalyzerRunner.OfId(diagnostics, DiagnosticIds.UnreachableMutationPoint);

    private static string Gap(int line) => DiagnosticIds.UnreachableMutationPoint + ":" + ToText(line);

    private static string Describe(Compilation compilation) =>
        DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation));

    private static string Message(Diagnostic diagnostic) => diagnostic.GetMessage(CultureInfo.InvariantCulture);

    private static string ToText(int value) => value.ToString(CultureInfo.InvariantCulture);
}
