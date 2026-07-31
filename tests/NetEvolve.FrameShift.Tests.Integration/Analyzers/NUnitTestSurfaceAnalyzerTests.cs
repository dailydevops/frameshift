namespace NetEvolve.FrameShift.Tests.Integration;

using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Analyzers;
using NetEvolve.FrameShift.Diagnostics;
using NetEvolve.FrameShift.Tests.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Drives <see cref="NUnitTestSurfaceAnalyzer" /> end to end against a real two-assembly setup: a
/// production assembly that is visible only as a metadata reference, and a test assembly compiled against
/// it that carries genuine <c>[Test]</c> and <c>[TestCase]</c> methods.
/// </summary>
/// <remarks>
/// <para>
/// Every expectation here is about the two things the analyzer promises to the developer: a test that
/// cannot possibly contribute to the tested surface is named (<c>FSH0004</c>), and a checked-in manifest
/// that no longer describes the tests is reported before anybody trusts it (<c>FSH0003</c>). The manifest
/// the tests feed back in is always the one <see cref="TestSurfaceManifestWriter" /> produces for the very
/// compilation under analysis, so that no expectation depends on a hand-written documentation comment id.
/// </para>
/// <para>
/// <see cref="AnalyzerRunner" /> turns an analyzer exception into a failing run instead of returning
/// <c>AD0001</c>, therefore every test in this class also asserts that the analyzer did not crash.
/// </para>
/// </remarks>
public class NUnitTestSurfaceAnalyzerTests
{
    private const string ProductionAssemblyName = "ProductionAssembly";
    private const string TestAssemblyName = "TestAssembly";
    private const string ProductionPath = "Production.cs";
    private const string TestPath = "CalculatorTests.cs";

    private const string CoveringTestName = "Add_ExercisesProduction";
    private const string LocalOnlyTestName = "LocalStateOnly_TouchesNoProduction";

    private const string NoTestsScenario = "framework referenced, no test method";
    private const string TestFixtureOnlyScenario = "a test fixture without a single test method";
    private const string ForeignAttributeScenario = "test attribute of an unrelated framework";
    private const string ForeignFrameworkScenario = "a different test framework with real tests";

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
    /// The test assembly under analysis. The local-only test deliberately touches neither the production
    /// assembly nor the framework: a predefined type keyword, a <c>var</c> or an operator would all bind
    /// to a member outside this assembly and would therefore count as a production reference, which is
    /// exactly what the <c>FSH0004</c> expectation is about.
    /// </summary>
    private const string TestSource = """
        namespace Tests;

        using NUnit.Framework;

        [TestFixture]
        public class CalculatorTests
        {
            [Test]
            public void Add_ExercisesProduction()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Add(2, 3);
            }

            [TestCase(4)]
            public void Subtract_ExercisesProduction(int value)
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Subtract(value, 1);
            }

            [Test]
            public void LocalStateOnly_TouchesNoProduction() => Verify(Compute());

            private static int Compute() => 41;

            private static void Verify(int value)
            {
            }
        }
        """;

    /// <summary>
    /// A compilation that carries the framework reference but declares no test method at all.
    /// </summary>
    private const string WithoutTestsSource = """
        namespace Tests;

        public class NotATestClass
        {
            public int Compute() => 41;
        }
        """;

    /// <summary>
    /// A fixture class of the framework whose methods carry no test attribute. <c>[TestFixture]</c> says
    /// where tests live, never that a method is one, so nothing here may be judged.
    /// </summary>
    private const string TestFixtureOnlySource = """
        namespace Tests;

        using NUnit.Framework;

        [TestFixture]
        public class FixtureOnlyCases
        {
            public int Compute() => 41;
        }
        """;

    /// <summary>
    /// A compilation whose <c>[Test]</c> attribute belongs to an unrelated framework, so that the probe
    /// has to reject it on the declaring assembly rather than on the attribute name.
    /// </summary>
    private const string ForeignAttributeSource = """
        namespace Tests;

        using System;

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class TestAttribute : Attribute
        {
        }

        public class ForeignCases
        {
            [Test]
            public void LooksLikeATest()
            {
            }
        }
        """;

    /// <summary>
    /// A compilation carrying real tests of a different framework, which this analyzer must not judge.
    /// </summary>
    private const string ForeignFrameworkSource = """
        namespace Tests;

        using TUnit.Core;

        public class ForeignCases
        {
            [Test]
            public void TouchesNoProduction()
            {
            }
        }
        """;

    [Test]
    public async Task Fixtures_BothAssemblies_CompileWithoutErrors()
    {
        var production = CreateProduction();
        var test = CreateTest(production);

        _ = await Assert.That(Describe(production)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        _ = await Assert.That(Describe(test)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
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
    /// Neither the plain test nor the data-driven one may be named, because both of them do exercise
    /// production code.
    /// </summary>
    /// <param name="testName">The test method that must not be reported.</param>
    [Test]
    [Arguments(CoveringTestName)]
    [Arguments("Subtract_ExercisesProduction")]
    public async Task Analyzer_TestExercisingProduction_IsNotReportedAsWithoutProductionReference(string testName)
    {
        var diagnostics = await RunAsync(CreateTest(), DiagnosticIds.TestWithoutProductionReference)
            .ConfigureAwait(false);

        var namesTheCoveringTest = diagnostics.Any(diagnostic =>
            GetMessage(diagnostic).Contains(testName, StringComparison.Ordinal)
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

        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DescribeParseProblem());
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
    /// The analyzer must be down whenever it recognises no test of its own framework, and being down has
    /// to mean absolute silence: not a single diagnostic, not even the manifest complaint that the very
    /// same additional file provokes on a compilation whose tests it does recognise. Judging a compilation
    /// whose tests are invisible could only ever produce false findings.
    /// </summary>
    /// <param name="scenario">The name of the compilation shape under test.</param>
    [Test]
    [Arguments(NoTestsScenario)]
    [Arguments(TestFixtureOnlyScenario)]
    [Arguments(ForeignAttributeScenario)]
    [Arguments(ForeignFrameworkScenario)]
    public async Task Analyzer_NoTestOfItsFrameworkIsRecognised_ReportsNothing(string scenario)
    {
        var compilation = CreateCompilationWithoutRecognisableTests(scenario);

        var diagnostics = await RunAllAsync(compilation, MalformedManifest).ConfigureAwait(false);

        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    /// <summary>
    /// Guards the fixtures of <see cref="Analyzer_NoTestOfItsFrameworkIsRecognised_ReportsNothing(string)" />:
    /// each of them must compile, so that the silence of the analyzer is caused by the absence of
    /// recognisable tests rather than by a broken compilation.
    /// </summary>
    /// <param name="scenario">The name of the compilation shape under test.</param>
    [Test]
    [Arguments(NoTestsScenario)]
    [Arguments(TestFixtureOnlyScenario)]
    [Arguments(ForeignAttributeScenario)]
    [Arguments(ForeignFrameworkScenario)]
    public async Task Fixtures_WithoutRecognisableTests_CompileWithoutErrors(string scenario)
    {
        var compilation = CreateCompilationWithoutRecognisableTests(scenario);

        _ = await Assert.That(Describe(compilation)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
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

    [Test]
    public async Task Initialize_ContextIsNull_ThrowsArgumentNullException()
    {
        var analyzer = new NUnitTestSurfaceAnalyzer();

        var exception = Assert.Throws<ArgumentNullException>(() => analyzer.Initialize(null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("context");
    }

    private static IEnumerable<string?> GetManifestShapes(string manifest) =>
        [null, manifest, MalformedManifest, WithGhostReference(manifest), WithoutFirstReference(manifest)];

    private static CSharpCompilation CreateCompilationWithoutRecognisableTests(string scenario) =>
        scenario switch
        {
            NoTestsScenario => CompilationFactory.Create(WithoutTestsSource, TestFramework.NUnit, TestAssemblyName),
            TestFixtureOnlyScenario => CompilationFactory.Create(
                TestFixtureOnlySource,
                TestFramework.NUnit,
                TestAssemblyName
            ),
            ForeignAttributeScenario => CompilationFactory.Create(
                ForeignAttributeSource,
                TestFramework.None,
                TestAssemblyName
            ),
            ForeignFrameworkScenario => CompilationFactory.Create(
                ForeignFrameworkSource,
                TestFramework.TUnit,
                TestAssemblyName
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario."),
        };

    private static CSharpCompilation CreateProduction() =>
        CompilationFactory.Create(ProductionSource, ProductionAssemblyName, filePath: ProductionPath);

    private static CSharpCompilation CreateTest() => CreateTest(CreateProduction());

    private static CSharpCompilation CreateTest(Compilation production) =>
        CompilationFactory.Create(
            TestSource,
            TestFramework.NUnit,
            TestAssemblyName,
            additionalReferences: [production.ToMetadataReference()],
            filePath: TestPath
        );

    /// <summary>
    /// Collects the test surface of <paramref name="test" /> exactly the way the analyzer does, so that a
    /// manifest expectation never depends on a hand-written documentation comment id.
    /// </summary>
    /// <param name="test">The test compilation under analysis.</param>
    /// <returns>The manifest text describing the collected surface.</returns>
    private static string CreateManifest(Compilation test)
    {
        var recognizer = NUnitTestFrameworkProbe.Instance.TryCreateRecognizer(test)!;

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
    ) => AnalyzerRunner.RunAsync(new NUnitTestSurfaceAnalyzer(), compilation, diagnosticId, AdditionalFiles(manifest));

    private static Task<ImmutableArray<Diagnostic>> RunAllAsync(
        Compilation compilation,
        string? manifest = null,
        IReadOnlyDictionary<string, string>? globalOptions = null
    ) => AnalyzerRunner.RunAsync(new NUnitTestSurfaceAnalyzer(), compilation, AdditionalFiles(manifest), globalOptions);

    private static ImmutableArray<AdditionalText> AdditionalFiles(string? manifest) =>
        manifest is null ? [] : [new InMemoryAdditionalText(manifest)];

    private static string GetMessage(Diagnostic diagnostic) => diagnostic.GetMessage(CultureInfo.InvariantCulture);

    private static string Describe(Compilation compilation) =>
        DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation));

    private static string WithGhostReference(string manifest) => manifest + ReferencePrefix + GhostReferenceId + "\n";

    private static string WithoutFirstReference(string manifest)
    {
        var lines = SplitLines(manifest);
        var dropped = lines.First(line => line.StartsWith(ReferencePrefix, StringComparison.Ordinal));

        return string.Join("\n", lines.Where(line => !string.Equals(line, dropped, StringComparison.Ordinal))) + "\n";
    }

    private static ImmutableArray<string> SplitLines(string manifest) =>
        [.. manifest.Split('\n').Where(line => line.Length > 0)];

    private static string DescribeParseProblem() =>
        DescribeManifestProblem(
            "Line 1: expected the test-surface manifest header 'frameshift-test-surface/1', "
                + "but found 'not-a-test-surface-manifest'."
        );

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
