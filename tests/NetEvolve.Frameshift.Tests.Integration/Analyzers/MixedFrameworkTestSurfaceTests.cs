namespace NetEvolve.Frameshift.Tests.Integration;

using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NetEvolve.Frameshift.Analyzers;
using NetEvolve.Frameshift.Diagnostics;
using NetEvolve.Frameshift.Tests.Infrastructure;
using NetEvolve.Frameshift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers a test project that uses more than one test framework at the same time, which is the one
/// situation in which several framework analyzers are legitimately awake on the very same compilation.
/// </summary>
/// <remarks>
/// <para>
/// Two rules keep that harmless, and both are asserted here by running <em>all four</em> analyzers over
/// one compilation and counting what the developer would actually see in the error list.
/// </para>
/// <para>
/// <c>FSH0004</c> names an individual test method and stays per framework: each analyzer sees a
/// different set of methods, so every offending test is named exactly once — never once per referenced
/// framework.
/// </para>
/// <para>
/// There is only ever one test-surface manifest, so <c>FSH0003</c> is reported exactly once, by the
/// first awake framework in <see cref="TestFrameworkProbeRegistry.All" /> order. Awake means the probe
/// recognises the framework <em>and</em> at least one of its tests is discovered — a merely referenced
/// framework must not take the lead, or a project referencing it without using it would leave the
/// manifest unchecked. The manifest is judged against the union of all awake frameworks' surfaces,
/// because a mixed project records all of its tests in that single file.
/// </para>
/// </remarks>
public class MixedFrameworkTestSurfaceTests
{
    private const string ProductionAssemblyName = "ProductionAssembly";
    private const string TestAssemblyName = "TestAssembly";
    private const string ProductionPath = "Production.cs";
    private const string TestPath = "MixedTests.cs";

    private const string TUnitLocalOnlyTestName = "TUnitLocalStateOnly_TouchesNoProduction";
    private const string NUnitLocalOnlyTestName = "NUnitLocalStateOnly_TouchesNoProduction";
    private const string TUnitCoveringTestName = "TUnitAdd_ExercisesProduction";
    private const string NUnitCoveringTestName = "NUnitSubtract_ExercisesProduction";

    private const string MalformedManifest = "not-a-test-surface-manifest\n";

    private const string ProductionSource = """
        namespace Fixture;

        public class Calculator
        {
            public int Add(int left, int right)
            {
                return left + right;
            }

            public int Subtract(int left, int right)
            {
                return left - right;
            }

            public int Negate(int value)
            {
                return -value;
            }
        }
        """;

    /// <summary>
    /// A test class holding TUnit and NUnit tests side by side. The attributes are spelled out in full,
    /// because both frameworks declare a <c>TestAttribute</c> and importing both namespaces would make
    /// the short name ambiguous. Each framework contributes one test that exercises production code and
    /// one that touches nothing outside the test assembly.
    /// </summary>
    private const string TwoFrameworkSource = """
        namespace Tests;

        public class MixedTests
        {
            [TUnit.Core.Test]
            public void TUnitAdd_ExercisesProduction()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Add(2, 3);
            }

            [TUnit.Core.Test]
            public void TUnitLocalStateOnly_TouchesNoProduction() => Verify(Compute());

            [NUnit.Framework.Test]
            public void NUnitSubtract_ExercisesProduction()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Subtract(5, 3);
            }

            [NUnit.Framework.Test]
            public void NUnitLocalStateOnly_TouchesNoProduction() => Verify(Compute());

            private static int Compute() => 41;

            private static void Verify(int value)
            {
            }
        }
        """;

    /// <summary>
    /// The same shape with a third framework added, so that the single-reporter rule is asserted beyond
    /// the two-framework case it is easiest to get right in.
    /// </summary>
    private const string ThreeFrameworkSource = """
        namespace Tests;

        public class MixedTests
        {
            [TUnit.Core.Test]
            public void TUnitAdd_ExercisesProduction()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Add(2, 3);
            }

            [Xunit.Fact]
            public void XunitNegate_ExercisesProduction()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Negate(4);
            }

            [NUnit.Framework.Test]
            public void NUnitSubtract_ExercisesProduction()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Subtract(5, 3);
            }
        }
        """;

    /// <summary>
    /// A compilation that references TUnit as well as NUnit but contains NUnit tests only. TUnit is
    /// matched by its probe and would lead by registry order, yet it is not awake, so NUnit has to take
    /// the manifest over instead of nobody checking it.
    /// </summary>
    private const string ReferencedButUnusedFrameworkSource = """
        namespace Tests;

        public class MixedTests
        {
            [NUnit.Framework.Test]
            public void NUnitSubtract_ExercisesProduction()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Subtract(5, 3);
            }
        }
        """;

    [Test]
    public async Task Fixtures_EveryCompilation_CompilesWithoutErrors()
    {
        var errors = new[]
        {
            Describe(CreateProduction()),
            Describe(CreateTwoFrameworkTest()),
            Describe(CreateThreeFrameworkTest()),
            Describe(CreateReferencedButUnusedFrameworkTest()),
        };

        _ = await Assert
            .That(string.Join(" / ", errors.Distinct(StringComparer.Ordinal)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    /// <summary>
    /// <c>FSH0004</c> is per framework, and per framework each test belongs to exactly one analyzer, so
    /// the developer sees each offending test once. Reporting it twice is what would happen if the
    /// analyzers judged every test of the compilation instead of only their own.
    /// </summary>
    [Test]
    public async Task EveryAnalyzer_TestsOfTwoFrameworksWithoutProductionReference_ReportsEachTestExactlyOnce()
    {
        var test = CreateTwoFrameworkTest();

        var diagnostics = await RunEveryAnalyzerOfIdAsync(test, DiagnosticIds.TestWithoutProductionReference)
            .ConfigureAwait(false);

        var messages = diagnostics.Select(diagnostic => GetMessage(diagnostic)).ToImmutableArray();

        _ = await Assert.That(diagnostics.Length).IsEqualTo(2);
        _ = await Assert.That(Count(messages, TUnitLocalOnlyTestName)).IsEqualTo(1);
        _ = await Assert.That(Count(messages, NUnitLocalOnlyTestName)).IsEqualTo(1);
        _ = await Assert.That(Count(messages, TUnitCoveringTestName)).IsEqualTo(0);
        _ = await Assert.That(Count(messages, NUnitCoveringTestName)).IsEqualTo(0);
    }

    /// <summary>
    /// The regression this whole coordination rule exists to prevent: one manifest, several matching
    /// analyzers, and therefore the very same complaint once per referenced framework.
    /// </summary>
    [Test]
    public async Task EveryAnalyzer_MalformedManifestOnATwoFrameworkProject_ReportsItExactlyOnce()
    {
        var test = CreateTwoFrameworkTest();

        var diagnostics = await RunEveryAnalyzerOfIdAsync(
                test,
                DiagnosticIds.InvalidTestSurfaceManifest,
                MalformedManifest
            )
            .ConfigureAwait(false);

        _ = await Assert.That(diagnostics.Length).IsEqualTo(1);
    }

    [Test]
    public async Task EveryAnalyzer_StaleManifestOnATwoFrameworkProject_ReportsItExactlyOnce()
    {
        var test = CreateTwoFrameworkTest();
        var manifest = CreateManifest(test, TUnitTestFrameworkProbe.Instance);

        var diagnostics = await RunEveryAnalyzerOfIdAsync(test, DiagnosticIds.InvalidTestSurfaceManifest, manifest)
            .ConfigureAwait(false);

        _ = await Assert.That(diagnostics.Length).IsEqualTo(1);
    }

    /// <summary>
    /// Which analyzer does the reporting is the point of the registry order: the first awake framework
    /// takes the manifest, every other one skips it entirely.
    /// </summary>
    /// <param name="framework">The framework whose analyzer is run on its own.</param>
    /// <param name="expected">The number of <c>FSH0003</c> diagnostics that analyzer must report.</param>
    [Test]
    [Arguments("TUnit", 1)]
    [Arguments("xUnit", 0)]
    [Arguments("NUnit", 0)]
    [Arguments("MSTest", 0)]
    public async Task SingleAnalyzer_MalformedManifestOnATwoFrameworkProject_OnlyTheLeadingFrameworkReports(
        string framework,
        int expected
    )
    {
        var diagnostics = await AnalyzerRunner
            .RunAsync(
                CreateAnalyzer(framework),
                CreateTwoFrameworkTest(),
                DiagnosticIds.InvalidTestSurfaceManifest,
                AdditionalFiles(MalformedManifest)
            )
            .ConfigureAwait(false);

        _ = await Assert.That(diagnostics.Length).IsEqualTo(expected);
    }

    /// <summary>
    /// A manifest holding the tests of both frameworks is exactly what the build writes for a mixed
    /// project, so it must not be reported as stale even though the leading analyzer can only discover
    /// half of those tests by itself.
    /// </summary>
    [Test]
    public async Task EveryAnalyzer_ManifestCoveringBothFrameworks_ReportsNoManifestProblem()
    {
        var test = CreateTwoFrameworkTest();
        var manifest = CreateManifest(test, TUnitTestFrameworkProbe.Instance, NUnitTestFrameworkProbe.Instance);

        var diagnostics = await RunEveryAnalyzerOfIdAsync(test, DiagnosticIds.InvalidTestSurfaceManifest, manifest)
            .ConfigureAwait(false);

        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    /// <summary>
    /// The counterpart: a manifest recording only the leading framework's tests really is stale, so the
    /// union must not be confused with "whatever the leader happens to see".
    /// </summary>
    /// <param name="framework">The framework whose surface alone the manifest records.</param>
    [Test]
    [Arguments("TUnit")]
    [Arguments("NUnit")]
    public async Task EveryAnalyzer_ManifestCoveringOnlyOneFramework_IsReportedAsStale(string framework)
    {
        var test = CreateTwoFrameworkTest();
        var manifest = CreateManifest(test, ProbeOf(framework));

        var diagnostics = await RunEveryAnalyzerOfIdAsync(test, DiagnosticIds.InvalidTestSurfaceManifest, manifest)
            .ConfigureAwait(false);

        _ = await Assert.That(diagnostics.Length).IsEqualTo(1);
        _ = await Assert.That(GetMessage(diagnostics[0]).Contains("stale", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task EveryAnalyzer_MalformedManifestOnAThreeFrameworkProject_ReportsItExactlyOnce()
    {
        var test = CreateThreeFrameworkTest();

        var diagnostics = await RunEveryAnalyzerOfIdAsync(
                test,
                DiagnosticIds.InvalidTestSurfaceManifest,
                MalformedManifest
            )
            .ConfigureAwait(false);

        _ = await Assert.That(diagnostics.Length).IsEqualTo(1);
    }

    [Test]
    public async Task EveryAnalyzer_ManifestCoveringAllThreeFrameworks_ReportsNoManifestProblem()
    {
        var test = CreateThreeFrameworkTest();
        var manifest = CreateManifest(
            test,
            TUnitTestFrameworkProbe.Instance,
            XunitTestFrameworkProbe.Instance,
            NUnitTestFrameworkProbe.Instance
        );

        var diagnostics = await RunEveryAnalyzerOfIdAsync(test, DiagnosticIds.InvalidTestSurfaceManifest, manifest)
            .ConfigureAwait(false);

        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    /// <summary>
    /// A framework that is referenced but has no test of its own is not awake and therefore does not
    /// lead. If mere probe matching decided, TUnit would take the lead here, skip the manifest as
    /// somebody else's business and nobody would ever look at it.
    /// </summary>
    [Test]
    public async Task EveryAnalyzer_LeadingFrameworkReferencedButUnused_StillReportsTheManifestOnce()
    {
        var test = CreateReferencedButUnusedFrameworkTest();

        var diagnostics = await RunEveryAnalyzerOfIdAsync(
                test,
                DiagnosticIds.InvalidTestSurfaceManifest,
                MalformedManifest
            )
            .ConfigureAwait(false);

        _ = await Assert.That(diagnostics.Length).IsEqualTo(1);
    }

    /// <summary>
    /// None of the mixed shapes may make an analyzer throw, which Roslyn would otherwise hide behind an
    /// <c>AD0001</c> diagnostic and which would turn every count above into a false pass.
    /// </summary>
    [Test]
    public async Task EveryAnalyzer_EveryMixedShape_NeverCrashes()
    {
        var reported = new List<string>();

        foreach (var test in new[] { CreateTwoFrameworkTest(), CreateThreeFrameworkTest() })
        {
            var manifest = CreateManifest(test, TUnitTestFrameworkProbe.Instance);

            reported.AddRange(DiagnosticAssertions.Ids(await RunEveryAnalyzerAsync(test, null).ConfigureAwait(false)));
            reported.AddRange(
                DiagnosticAssertions.Ids(await RunEveryAnalyzerAsync(test, manifest).ConfigureAwait(false))
            );
        }

        _ = await Assert.That(reported.Contains(AnalyzerRunner.AnalyzerFailureId, StringComparer.Ordinal)).IsFalse();
        _ = await Assert
            .That(reported.Contains(DiagnosticIds.InvalidTestSurfaceManifest, StringComparer.Ordinal))
            .IsTrue();
    }

    private static CSharpCompilation CreateProduction() =>
        CompilationFactory.Create(
            ProductionSource,
            TestFramework.None,
            ProductionAssemblyName,
            filePath: ProductionPath
        );

    private static CSharpCompilation CreateTwoFrameworkTest() =>
        CreateTest(TwoFrameworkSource, TestFramework.TUnit, TestFramework.NUnit);

    private static CSharpCompilation CreateThreeFrameworkTest() =>
        CreateTest(ThreeFrameworkSource, TestFramework.TUnit, TestFramework.XunitV3, TestFramework.NUnit);

    private static CSharpCompilation CreateReferencedButUnusedFrameworkTest() =>
        CreateTest(ReferencedButUnusedFrameworkSource, TestFramework.TUnit, TestFramework.NUnit);

    /// <summary>
    /// Builds the test assembly against the production assembly and the assemblies of several test
    /// frameworks at once. The framework reference sets overlap in the runtime assemblies and each one
    /// carries its own <see cref="MetadataReference" /> object per file, so they are merged by path;
    /// handing Roslyn two references to the same assembly identity would be a compile error rather than
    /// a mixed-framework project.
    /// </summary>
    /// <param name="source">The C# source of the test assembly.</param>
    /// <param name="frameworks">The frameworks whose assemblies are referenced.</param>
    /// <returns>The created compilation.</returns>
    private static CSharpCompilation CreateTest(string source, params TestFramework[] frameworks)
    {
        var references = Merge(frameworks).Add(CreateProduction().ToMetadataReference());

        return CSharpCompilation.Create(
            TestAssemblyName,
            [CompilationFactory.ParseTree(source, TestPath)],
            references,
            CompilationFactory.CompilationOptions
        );
    }

    private static ImmutableArray<MetadataReference> Merge(TestFramework[] frameworks) =>
        [
            .. frameworks
                .SelectMany(framework => ReferenceAssemblies.For(framework))
                .GroupBy(reference => reference.Display ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()),
        ];

    /// <summary>
    /// Renders the manifest of the union of the test surfaces <paramref name="probes" /> can see, which
    /// is how the build records a project that uses several frameworks.
    /// </summary>
    /// <param name="test">The test compilation under analysis.</param>
    /// <param name="probes">The probes whose surfaces are unioned.</param>
    /// <returns>The rendered manifest.</returns>
    private static string CreateManifest(Compilation test, params ITestFrameworkProbe[] probes)
    {
        var testMethodIds = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var referencedMemberIds = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);

        foreach (var probe in probes)
        {
            var recognizer = CreateRecognizer(test, probe);
            var collected = TestSurfaceCollector.Collect(test, recognizer, CancellationToken.None);

            testMethodIds.UnionWith(collected.TestMethodIds);
            referencedMemberIds.UnionWith(collected.ReferencedMemberIds);
        }

        var manifest = new TestSurfaceManifest(testMethodIds.ToImmutable(), referencedMemberIds.ToImmutable());

        return TestSurfaceManifestWriter.Write(manifest);
    }

    /// <summary>
    /// Creates the recogniser of <paramref name="probe" /> and fails loudly when the probe does not
    /// recognise the fixture, because a silently skipped framework would make the union assertions pass
    /// for the wrong reason.
    /// </summary>
    /// <param name="test">The test compilation under analysis.</param>
    /// <param name="probe">The probe to create the recogniser with.</param>
    /// <returns>The created recogniser.</returns>
    /// <exception cref="InvalidOperationException">The probe did not recognise the compilation.</exception>
    private static ITestMethodRecognizer CreateRecognizer(Compilation test, ITestFrameworkProbe probe) =>
        probe.TryCreateRecognizer(test)
        ?? throw new InvalidOperationException(
            $"The probe of '{probe.FrameworkName}' did not recognise the mixed-framework fixture."
        );

    private static ITestFrameworkProbe ProbeOf(string framework) =>
        TestFrameworkProbeRegistry.All.First(probe =>
            string.Equals(probe.FrameworkName, framework, StringComparison.Ordinal)
        );

    private static DiagnosticAnalyzer CreateAnalyzer(string framework) =>
        framework switch
        {
            "TUnit" => new TUnitTestSurfaceAnalyzer(),
            "xUnit" => new XunitTestSurfaceAnalyzer(),
            "NUnit" => new NUnitTestSurfaceAnalyzer(),
            "MSTest" => new MSTestTestSurfaceAnalyzer(),
            _ => throw new ArgumentOutOfRangeException(nameof(framework), framework, "Unknown framework."),
        };

    /// <summary>
    /// Runs every framework analyzer over the same compilation and returns everything they reported,
    /// which is what a developer building a mixed-framework project actually sees.
    /// </summary>
    /// <param name="compilation">The compilation to analyse.</param>
    /// <param name="manifest">The manifest handed to the analyzers, or <see langword="null" /> for none.</param>
    /// <returns>The reported diagnostics of all four analyzers.</returns>
    private static async Task<ImmutableArray<Diagnostic>> RunEveryAnalyzerAsync(
        Compilation compilation,
        string? manifest
    )
    {
        var builder = ImmutableArray.CreateBuilder<Diagnostic>();

        foreach (var framework in TestFrameworkProbeRegistry.All.Select(probe => probe.FrameworkName))
        {
            var analyzer = CreateAnalyzer(framework);
            var files = AdditionalFiles(manifest);

            builder.AddRange(await AnalyzerRunner.RunAsync(analyzer, compilation, files).ConfigureAwait(false));
        }

        return builder.ToImmutable();
    }

    private static async Task<ImmutableArray<Diagnostic>> RunEveryAnalyzerOfIdAsync(
        Compilation compilation,
        string diagnosticId,
        string? manifest = null
    )
    {
        var diagnostics = await RunEveryAnalyzerAsync(compilation, manifest).ConfigureAwait(false);

        return AnalyzerRunner.OfId(diagnostics, diagnosticId);
    }

    private static ImmutableArray<AdditionalText> AdditionalFiles(string? manifest) =>
        manifest is null ? [] : [new InMemoryAdditionalText(manifest)];

    private static int Count(ImmutableArray<string> messages, string name) =>
        messages.Count(message => message.Contains(name, StringComparison.Ordinal));

    private static string GetMessage(Diagnostic diagnostic) => diagnostic.GetMessage(CultureInfo.InvariantCulture);

    private static string Describe(Compilation compilation) =>
        DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation));
}
