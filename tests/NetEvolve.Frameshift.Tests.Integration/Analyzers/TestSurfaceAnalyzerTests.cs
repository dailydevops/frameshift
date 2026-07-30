namespace NetEvolve.Frameshift.Tests.Integration;

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.Frameshift.Analyzers;
using NetEvolve.Frameshift.Diagnostics;
using NetEvolve.Frameshift.Tests.Infrastructure;
using NetEvolve.Frameshift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Drives <see cref="TestSurfaceAnalyzer" /> end to end against a real two-assembly setup: a
/// production assembly that is visible only as a metadata reference, and a test assembly compiled
/// against it that carries genuine <c>[Test]</c> methods.
/// </summary>
/// <remarks>
/// <para>
/// Every expectation here is about the two things the analyzer promises to the developer: a test that
/// cannot possibly contribute to the tested surface is named (<c>FSH0004</c>), and a checked-in
/// manifest that no longer describes the tests is reported before anybody trusts it (<c>FSH0003</c>).
/// The manifest the tests feed back in is always the one <see cref="TestSurfaceManifestWriter" />
/// produces for the very compilation under analysis, so that no expectation depends on a hand-written
/// documentation comment id.
/// </para>
/// <para>
/// <see cref="AnalyzerRunner" /> turns an analyzer exception into a failing run instead of returning
/// <c>AD0001</c>, therefore every test in this class also asserts that the analyzer did not crash.
/// </para>
/// </remarks>
public class TestSurfaceAnalyzerTests
{
    private const string ProductionAssemblyName = "ProductionAssembly";
    private const string TestAssemblyName = "TestAssembly";
    private const string ProductionPath = "Production.cs";
    private const string TestPath = "CalculatorTests.cs";

    private const string CoveringTestName = "Add_ExercisesProduction";
    private const string LocalOnlyTestName = "LocalStateOnly_TouchesNoProduction";

    private const string MalformedManifest = "not-a-test-surface-manifest\n";
    private const string GhostReferenceId = "M:Fixture.Ghost.Vanished";
    private const string ReferencePrefix = "R ";

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

    /// <summary>
    /// The test assembly under analysis. The local-only test deliberately touches neither the
    /// production assembly nor the framework: a predefined type keyword, a <c>var</c> or an operator
    /// would all bind to a member outside this assembly and would therefore count as a production
    /// reference, which is exactly what the <c>FSH0004</c> expectation is about.
    /// </summary>
    private const string TestSource = """
        namespace Tests;

        using TUnit.Core;

        public class CalculatorTests
        {
            [Test]
            public void Add_ExercisesProduction()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Add(2, 3);
            }

            [Test]
            public void LocalStateOnly_TouchesNoProduction() => Verify(Compute());

            private static int Compute() => 41;

            private static void Verify(int value)
            {
            }
        }
        """;

    [Test]
    public async Task Fixtures_BothAssemblies_CompileWithoutErrors()
    {
        var production = CreateProduction();
        var test = CreateTest(production);

        _ = await Assert
            .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(production)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        _ = await Assert
            .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(test)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    /// <summary>
    /// A production project must never see a single diagnostic of the test-side analyzer, not even the
    /// manifest complaint that the very same additional file provokes on a test project.
    /// </summary>
    [Test]
    public async Task Analyzer_CompilationWithoutTestFramework_ReportsNothing()
    {
        var diagnostics = await RunAllAsync(CreateProduction(), MalformedManifest).ConfigureAwait(false);

        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    public async Task Analyzer_TestExercisingProduction_IsNotReportedAsWithoutProductionReference()
    {
        var diagnostics = await RunAsync(CreateTest(), DiagnosticIds.TestWithoutProductionReference)
            .ConfigureAwait(false);

        var namesTheCoveringTest = diagnostics.Any(diagnostic =>
            GetMessage(diagnostic).Contains(CoveringTestName, StringComparison.Ordinal)
        );

        _ = await Assert.That(namesTheCoveringTest).IsFalse();
    }

    [Test]
    public async Task Analyzer_TestWithoutProductionReference_IsReportedOnceAtItsIdentifier()
    {
        var test = CreateTest();
        var identifier = FindMethod(test, LocalOnlyTestName).Identifier;

        var diagnostics = await RunAsync(test, DiagnosticIds.TestWithoutProductionReference).ConfigureAwait(false);

        _ = await Assert.That(diagnostics).Count().IsEqualTo(1);
        _ = await Assert.That(diagnostics[0].Location.SourceSpan).IsEqualTo(identifier.Span);
        _ = await Assert
            .That(GetMessage(diagnostics[0]).Contains(LocalOnlyTestName, StringComparison.Ordinal))
            .IsTrue();
    }

    [Test]
    public async Task Analyzer_WithoutAnyManifest_ReportsNoManifestProblem()
    {
        var diagnostics = await RunAsync(CreateTest(), DiagnosticIds.InvalidTestSurfaceManifest).ConfigureAwait(false);

        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    public async Task Analyzer_MalformedManifest_ReportsTheParseProblem()
    {
        var diagnostics = await RunAsync(CreateTest(), DiagnosticIds.InvalidTestSurfaceManifest, MalformedManifest)
            .ConfigureAwait(false);

        _ = await Assert
            .That(DiagnosticAssertions.Describe(diagnostics))
            .IsEqualTo(
                DescribeManifestProblem(
                    "Line 1: expected the test-surface manifest header 'frameshift-test-surface/1', "
                        + "but found 'not-a-test-surface-manifest'."
                )
            );
    }

    [Test]
    public async Task Analyzer_ManifestMatchingTheCollectedSurface_ReportsNoManifestProblem()
    {
        var test = CreateTest();

        var diagnostics = await RunAsync(test, DiagnosticIds.InvalidTestSurfaceManifest, CreateManifest(test))
            .ConfigureAwait(false);

        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    public async Task Analyzer_ManifestWithAnIdTooMany_ReportsOneRemovedId()
    {
        var test = CreateTest();
        var manifest = WithGhostReference(CreateManifest(test));

        var diagnostics = await RunAsync(test, DiagnosticIds.InvalidTestSurfaceManifest, manifest)
            .ConfigureAwait(false);

        _ = await Assert
            .That(DiagnosticAssertions.Describe(diagnostics))
            .IsEqualTo(DescribeStale(added: 0, removed: 1));
    }

    [Test]
    public async Task Analyzer_ManifestWithAnIdMissing_ReportsOneAddedId()
    {
        var test = CreateTest();
        var manifest = WithoutFirstReference(CreateManifest(test));

        var diagnostics = await RunAsync(test, DiagnosticIds.InvalidTestSurfaceManifest, manifest)
            .ConfigureAwait(false);

        _ = await Assert
            .That(DiagnosticAssertions.Describe(diagnostics))
            .IsEqualTo(DescribeStale(added: 1, removed: 0));
    }

    [Test]
    public async Task Analyzer_ManifestWithAnIdMissingAndOneTooMany_ReportsBothCounts()
    {
        var test = CreateTest();
        var manifest = WithGhostReference(WithoutFirstReference(CreateManifest(test)));

        var diagnostics = await RunAsync(test, DiagnosticIds.InvalidTestSurfaceManifest, manifest)
            .ConfigureAwait(false);

        _ = await Assert
            .That(DiagnosticAssertions.Describe(diagnostics))
            .IsEqualTo(DescribeStale(added: 1, removed: 1));
    }

    /// <summary>
    /// The comparison is on id sets, never on text, so a manifest that a merge or a developer reordered,
    /// commented or padded still describes the same recorded surface.
    /// </summary>
    [Test]
    public async Task Analyzer_ManifestDifferingOnlyInFormatting_ReportsNoManifestProblem()
    {
        var test = CreateTest();
        var manifest = CreateManifest(test);
        var reformatted = Reformat(manifest);

        var diagnostics = await RunAsync(test, DiagnosticIds.InvalidTestSurfaceManifest, reformatted)
            .ConfigureAwait(false);

        _ = await Assert.That(reformatted).IsNotEqualTo(manifest);
        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    public async Task Analyzer_Disabled_ReportsNothing()
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["build_property.FrameshiftEnabled"] = "false",
        };

        var diagnostics = await RunAllAsync(CreateTest(), MalformedManifest, options).ConfigureAwait(false);

        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    /// <summary>
    /// Runs every manifest shape the other tests use through the analyzer once more and proves that none
    /// of them makes it throw, which Roslyn would otherwise hide behind an <c>AD0001</c> diagnostic.
    /// </summary>
    [Test]
    public async Task Analyzer_EveryManifestShape_NeverCrashes()
    {
        var test = CreateTest();
        var reported = new List<string>();

        foreach (var shape in GetManifestShapes(CreateManifest(test)))
        {
            reported.AddRange(DiagnosticAssertions.Ids(await RunAllAsync(test, shape).ConfigureAwait(false)));
        }

        _ = await Assert.That(reported.Contains(AnalyzerRunner.AnalyzerFailureId, StringComparer.Ordinal)).IsFalse();
        _ = await Assert
            .That(reported.Contains(DiagnosticIds.TestWithoutProductionReference, StringComparer.Ordinal))
            .IsTrue();
    }

    private static IEnumerable<string?> GetManifestShapes(string manifest) =>
        [
            null,
            manifest,
            MalformedManifest,
            Reformat(manifest),
            WithGhostReference(manifest),
            WithoutFirstReference(manifest),
        ];

    private static CSharpCompilation CreateProduction() =>
        CompilationFactory.Create(ProductionSource, ProductionAssemblyName, filePath: ProductionPath);

    private static CSharpCompilation CreateTest() => CreateTest(CreateProduction());

    private static CSharpCompilation CreateTest(Compilation production) =>
        CompilationFactory.Create(
            TestSource,
            TestAssemblyName,
            includeTUnit: true,
            additionalReferences: [production.ToMetadataReference()],
            filePath: TestPath
        );

    private static string CreateManifest(Compilation test) =>
        TestSurfaceManifestWriter.Write(TestSurfaceCollector.Collect(test, CancellationToken.None));

    private static MethodDeclarationSyntax FindMethod(Compilation compilation, string name) =>
        SyntaxNodeLocator.FindFirst<MethodDeclarationSyntax>(
            compilation.SyntaxTrees.First(),
            method => string.Equals(method.Identifier.ValueText, name, StringComparison.Ordinal)
        );

    private static Task<ImmutableArray<Diagnostic>> RunAsync(
        Compilation compilation,
        string diagnosticId,
        string? manifest = null
    ) => AnalyzerRunner.RunAsync(new TestSurfaceAnalyzer(), compilation, diagnosticId, AdditionalFiles(manifest));

    private static Task<ImmutableArray<Diagnostic>> RunAllAsync(
        Compilation compilation,
        string? manifest = null,
        IReadOnlyDictionary<string, string>? globalOptions = null
    ) => AnalyzerRunner.RunAsync(new TestSurfaceAnalyzer(), compilation, AdditionalFiles(manifest), globalOptions);

    private static ImmutableArray<AdditionalText> AdditionalFiles(string? manifest) =>
        manifest is null ? [] : [new InMemoryAdditionalText(manifest)];

    private static string GetMessage(Diagnostic diagnostic) => diagnostic.GetMessage(CultureInfo.InvariantCulture);

    private static string WithGhostReference(string manifest) => manifest + ReferencePrefix + GhostReferenceId + "\n";

    private static string WithoutFirstReference(string manifest)
    {
        var lines = SplitLines(manifest);
        var dropped = lines.First(line => line.StartsWith(ReferencePrefix, StringComparison.Ordinal));

        return Join(lines.Where(line => !string.Equals(line, dropped, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Rewrites a manifest so that its text differs as much as the format allows while its id sets stay
    /// exactly the same: the entries are reversed and comment and blank lines are interleaved.
    /// </summary>
    /// <param name="manifest">The canonical manifest text.</param>
    /// <returns>The reformatted manifest.</returns>
    private static string Reformat(string manifest)
    {
        var lines = SplitLines(manifest);
        var builder = new StringBuilder();

        _ = builder.Append("# a comment in front of the header\n\n").Append(lines[0]).Append("\n\n");

        foreach (var entry in lines.Skip(1).Reverse())
        {
            _ = builder.Append("# an entry follows\n").Append(entry).Append("\n\n");
        }

        return builder.ToString();
    }

    private static ImmutableArray<string> SplitLines(string manifest) =>
        [.. manifest.Split('\n').Where(line => line.Length > 0)];

    private static string Join(IEnumerable<string> lines) => string.Join("\n", lines) + "\n";

    private static string DescribeStale(int added, int removed) =>
        DescribeManifestProblem(
            "the recorded test surface no longer matches the tests of this project, so the manifest is "
                + "stale and must be regenerated ("
                + added.ToString(CultureInfo.InvariantCulture)
                + " id(s) added, "
                + removed.ToString(CultureInfo.InvariantCulture)
                + " id(s) removed)."
        );

    private static string DescribeManifestProblem(string detail) =>
        DiagnosticIds.InvalidTestSurfaceManifest
        + " "
        + InMemoryAdditionalText.DefaultPath
        + "(1,1): Test-surface manifest '"
        + InMemoryAdditionalText.DefaultPath
        + "' could not be read: "
        + detail;
}
