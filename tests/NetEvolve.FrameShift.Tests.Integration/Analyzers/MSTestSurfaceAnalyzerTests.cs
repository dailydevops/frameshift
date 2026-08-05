namespace NetEvolve.FrameShift.Tests.Integration.Analyzers;

using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Analyzers;
using NetEvolve.FrameShift.Diagnostics;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Drives <see cref="MSTestTestSurfaceAnalyzer" /> end to end against a real two-assembly setup: a
/// production assembly that is visible only as a metadata reference, and an MSTest assembly compiled
/// against it that carries genuine <c>[TestMethod]</c> methods.
/// </summary>
/// <remarks>
/// The expectations mirror the TUnit suite one for one, because the promise the analyzer makes to the
/// developer is the same whichever framework the tests are written in: a test that cannot possibly
/// contribute to the tested surface is named (<c>FSH0004</c>), and a checked-in manifest that no longer
/// describes the tests is reported before anybody trusts it (<c>FSH0003</c>). Every manifest fed back in
/// is the one collected from the very compilation under analysis, so that no expectation depends on a
/// hand-written documentation comment id. <see cref="AnalyzerRunner" /> turns an analyzer exception into
/// a failing run instead of an <c>AD0001</c> diagnostic.
/// </remarks>
public class MSTestSurfaceAnalyzerTests
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
                return left + right;
            }
        }
        """;

    /// <summary>
    /// The MSTest assembly under analysis. The local-only test deliberately touches neither the
    /// production assembly nor the framework: a predefined type keyword, a <c>var</c> or an operator
    /// would all bind to a member outside this assembly and would therefore count as a production
    /// reference, which is exactly what the <c>FSH0004</c> expectation is about.
    /// </summary>
    private const string TestSource = """
        namespace Tests;

        using Microsoft.VisualStudio.TestTools.UnitTesting;

        [TestClass]
        public class CalculatorTests
        {
            [TestMethod]
            public void Add_ExercisesProduction()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Add(2, 3);
            }

            [TestMethod]
            public void LocalStateOnly_TouchesNoProduction() => Verify(Compute());

            private static int Compute() => 41;

            private static void Verify(int value)
            {
            }
        }
        """;

    /// <summary>
    /// An MSTest-referencing compilation that declares no test method at all, which has to leave the
    /// analyzer completely silent rather than merely without <c>FSH0004</c>.
    /// </summary>
    private const string WithoutTestsSource = """
        namespace Tests;

        using Microsoft.VisualStudio.TestTools.UnitTesting;

        [TestClass]
        public class NotATestClass
        {
            public int Compute() => 41;
        }
        """;

    [Test]
    public async Task Fixtures_BothAssemblies_CompileWithoutErrors()
    {
        var production = CreateProduction();
        var test = CreateTest(production);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(production)))
                .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
            _ = await Assert
                .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(test)))
                .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
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

    /// <summary>
    /// A compilation whose framework is referenced but that has no test of it at all is not the
    /// analyzer's business, and being down has to mean absolute silence — not even about the manifest.
    /// </summary>
    [Test]
    public async Task Analyzer_FrameworkWithoutAnyTest_ReportsNothing()
    {
        var compilation = CompilationFactory.Create(
            WithoutTestsSource,
            TestFramework.MSTest,
            TestAssemblyName,
            filePath: TestPath
        );

        var diagnostics = await RunAllAsync(compilation, MalformedManifest).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(DiagnosticAssertions.Describe(diagnostics))
                .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
            _ = await Assert
                .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation)))
                .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(diagnostics.Length).IsEqualTo(1);
            _ = await Assert.That(diagnostics[0].Location.SourceSpan).IsEqualTo(identifier.Span);
            _ = await Assert
                .That(GetMessage(diagnostics[0]).Contains(LocalOnlyTestName, StringComparison.Ordinal))
                .IsTrue();
        }
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
    public async Task Analyzer_ManifestWithoutReadableContent_ReportsTheUnreadableFile()
    {
        var diagnostics = await AnalyzerRunner
            .RunAsync(
                new MSTestTestSurfaceAnalyzer(),
                CreateTest(),
                DiagnosticIds.InvalidTestSurfaceManifest,
                [InMemoryAdditionalText.WithoutContent()]
            )
            .ConfigureAwait(false);

        _ = await Assert
            .That(DiagnosticAssertions.Describe(diagnostics))
            .IsEqualTo(DescribeManifestProblem("the content of the file is not available."));
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
    public async Task Analyzer_Disabled_ReportsNothing()
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["build_property.FrameShiftEnabled"] = "false",
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
        var manifest = CreateManifest(test);
        var reported = new List<string>();

        foreach (var shape in GetManifestShapes(manifest))
        {
            reported.AddRange(DiagnosticAssertions.Ids(await RunAllAsync(test, shape).ConfigureAwait(false)));
        }

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(reported.Contains(AnalyzerRunner.AnalyzerFailureId, StringComparer.Ordinal))
                .IsFalse();
            _ = await Assert
                .That(reported.Contains(DiagnosticIds.TestWithoutProductionReference, StringComparer.Ordinal))
                .IsTrue();
        }
    }

    [Test]
    public async Task Initialize_ContextIsNull_ThrowsArgumentNullException()
    {
        var analyzer = new MSTestTestSurfaceAnalyzer();

        var exception = Assert.Throws<ArgumentNullException>(() => analyzer.Initialize(null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("context");
    }

    private static IEnumerable<string?> GetManifestShapes(string manifest) =>
        [null, manifest, MalformedManifest, WithGhostReference(manifest), WithoutFirstReference(manifest)];

    private static CSharpCompilation CreateProduction() =>
        CompilationFactory.Create(
            ProductionSource,
            TestFramework.None,
            ProductionAssemblyName,
            filePath: ProductionPath
        );

    private static CSharpCompilation CreateTest() => CreateTest(CreateProduction());

    private static CSharpCompilation CreateTest(Compilation production) =>
        CompilationFactory.Create(
            TestSource,
            TestFramework.MSTest,
            TestAssemblyName,
            [production.ToMetadataReference()],
            TestPath
        );

    /// <summary>
    /// Collects the test surface of <paramref name="test" /> through the MSTest probe, so that the
    /// manifest a test feeds back in is exactly the one the analyzer collects for itself.
    /// </summary>
    /// <param name="test">The test compilation under analysis.</param>
    /// <returns>The rendered manifest.</returns>
    /// <exception cref="InvalidOperationException">The MSTest probe did not recognise the compilation.</exception>
    private static string CreateManifest(Compilation test)
    {
        var recognizer =
            MSTestTestFrameworkProbe.Instance.TryCreateRecognizer(test)
            ?? throw new InvalidOperationException(
                "The MSTest probe did not recognise a compilation built against the real MSTest assemblies."
            );

        return TestSurfaceManifestWriter.Write(TestSurfaceCollector.Collect(test, recognizer, CancellationToken.None));
    }

    private static MethodDeclarationSyntax FindMethod(Compilation compilation, string name) =>
        SyntaxNodeLocator.FindFirst<MethodDeclarationSyntax>(
            compilation.SyntaxTrees.First(),
            method => string.Equals(method.Identifier.ValueText, name, StringComparison.Ordinal)
        );

    private static Task<ImmutableArray<Diagnostic>> RunAsync(
        Compilation compilation,
        string diagnosticId,
        string? manifest = null
    ) => AnalyzerRunner.RunAsync(new MSTestTestSurfaceAnalyzer(), compilation, diagnosticId, AdditionalFiles(manifest));

    private static Task<ImmutableArray<Diagnostic>> RunAllAsync(
        Compilation compilation,
        string? manifest = null,
        IReadOnlyDictionary<string, string>? globalOptions = null
    ) =>
        AnalyzerRunner.RunAsync(new MSTestTestSurfaceAnalyzer(), compilation, AdditionalFiles(manifest), globalOptions);

    private static ImmutableArray<AdditionalText> AdditionalFiles(string? manifest) =>
        manifest is null ? [] : [new InMemoryAdditionalText(manifest)];

    private static string GetMessage(Diagnostic diagnostic) => diagnostic.GetMessage(CultureInfo.InvariantCulture);

    private static string WithGhostReference(string manifest) => manifest + ReferencePrefix + GhostReferenceId + "\n";

    private static string WithoutFirstReference(string manifest)
    {
        var lines = SplitLines(manifest);
        var dropped = lines.First(line => line.StartsWith(ReferencePrefix, StringComparison.Ordinal));

        return string.Join("\n", lines.Where(line => !string.Equals(line, dropped, StringComparison.Ordinal))) + "\n";
    }

    private static ImmutableArray<string> SplitLines(string manifest) =>
        [.. manifest.Split('\n').Where(line => line.Length > 0)];

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
