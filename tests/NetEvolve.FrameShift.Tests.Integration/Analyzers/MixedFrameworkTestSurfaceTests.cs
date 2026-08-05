namespace NetEvolve.FrameShift.Tests.Integration.Analyzers;

using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NetEvolve.FrameShift.Analyzers;
using NetEvolve.FrameShift.Diagnostics;
using NetEvolve.FrameShift.Tests.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers a test project that uses more than one test framework at the same time, which is the one
/// situation in which several framework analyzers are legitimately awake on the very same compilation.
/// </summary>
/// <remarks>
/// <para>
/// Two rules keep that harmless, and both are asserted here by running <em>every</em> framework analyzer
/// over one compilation and counting what the developer would actually see in the error list.
/// </para>
/// <para>
/// <c>FSH0004</c> names an individual test method, and every offending test is named exactly once. Where
/// the frameworks see disjoint sets of methods that follows from the sets alone; where two awake
/// frameworks describe the very same method, the first of them in
/// <see cref="TestFrameworkProbeRegistry.All" /> order reports it and every later one leaves it alone.
/// </para>
/// <para>
/// There is only ever one test-surface manifest, so <c>FSH0003</c> is reported exactly once, by the
/// first awake framework in <see cref="TestFrameworkProbeRegistry.All" /> order. Awake means the probe
/// recognises the framework <em>and</em> at least one of its tests is discovered — a merely referenced
/// framework must not take the lead, or a project referencing it without using it would leave the
/// manifest unchecked. The manifest is judged against the union of all awake frameworks' surfaces,
/// because a mixed project records all of its tests in that single file.
/// </para>
/// <para>
/// Since the two major versions of xUnit.net are two registry entries, the overlapping case is no longer
/// exotic. Both versions declare <c>Xunit.FactAttribute</c> under the identical metadata name, in
/// <c>xunit.core</c> and in <c>xunit.v3.core</c>, and a project may reference both at once. Then two probes
/// match one compilation, they may describe the same <c>[Fact]</c> method, and the developer must still
/// read one <c>FSH0004</c> per offending test and one <c>FSH0003</c> for the manifest. Those expectations
/// are guarded by <c>FRAMESHIFT_XUNIT_V3</c>, because <c>xunit.v3.core</c> ships no assets for net6.0 and
/// net7.0; everything else in this class runs on all eight target frameworks.
/// </para>
/// <para>
/// The counting is done over the five test-surface analyzers, not over every analyzer FrameShift ships.
/// <see cref="MutationCoverageAnalyzer" /> reads the very same manifest from the production side and
/// reports <c>FSH0003</c> for a broken one by design, so including it would change what "once" means for
/// a reason that has nothing to do with the frameworks. It has no notion of a test method at all, which is
/// why the <c>FSH0004</c> count is additionally taken with that analyzer running as well.
/// </para>
/// </remarks>
public class MixedFrameworkTestSurfaceTests
{
    private const string ProductionAssemblyName = "ProductionAssembly";
    private const string TestAssemblyName = "TestAssembly";
    private const string ProductionPath = "Production.cs";
    private const string TestPath = "MixedTests.cs";

    private const string TUnitFramework = TUnitTestFrameworkProbe.Name;
    private const string XunitV2Framework = XunitV2TestFrameworkProbe.Name;
    private const string XunitV3Framework = XunitV3TestFrameworkProbe.Name;
    private const string NUnitFramework = NUnitTestFrameworkProbe.Name;
    private const string MSTestFramework = MSTestTestFrameworkProbe.Name;

    private const string TUnitLocalOnlyTestName = "TUnitLocalStateOnly_TouchesNoProduction";
    private const string NUnitLocalOnlyTestName = "NUnitLocalStateOnly_TouchesNoProduction";
    private const string TUnitCoveringTestName = "TUnitAdd_ExercisesProduction";
    private const string NUnitCoveringTestName = "NUnitSubtract_ExercisesProduction";

#if FRAMESHIFT_XUNIT_V3
    private const string XunitV2LocalOnlyTestName = "XunitV2LocalStateOnly_TouchesNoProduction";
    private const string XunitV3LocalOnlyTestName = "XunitV3LocalStateOnly_TouchesNoProduction";
    private const string BothVersionsLocalOnlyTestName = "BothVersionsLocalStateOnly_TouchesNoProduction";
    private const string XunitV2CoveringTestName = "XunitV2Add_ExercisesProduction";
    private const string XunitV3CoveringTestName = "XunitV3Subtract_ExercisesProduction";

    /// <summary>
    /// The extern alias the xUnit.net v3 assemblies are referenced under, so that a single fixture can
    /// spell out the names of both major versions. Without it the two <c>Xunit.FactAttribute</c>
    /// declarations make every mention of the name a CS0433, and the fixture could not name either version.
    /// </summary>
    private const string XunitV3Alias = "xunitv3";
#endif

    private const string StaleDetail = "stale";
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
    /// <remarks>
    /// The third framework is xUnit.net in version 2, whose reference set is buildable on every target
    /// framework of this suite while version 3's is not. Version 2 alone is referenced here, so the
    /// unqualified name <c>Xunit.Fact</c> stays unambiguous.
    /// </remarks>
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
    /// A project of two frameworks that does not use the first one of the registry at all, so that the
    /// lead falls to a framework that is neither the first nor the last registered one.
    /// </summary>
    private const string XunitAndNUnitSource = """
        namespace Tests;

        public class MixedTests
        {
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
    /// The two frameworks at the end of the registry, so that the lead is asserted where a comparison
    /// against the first entry rather than against the first awake one would still look right.
    /// </summary>
    private const string NUnitAndMSTestSource = """
        namespace Tests;

        public class MixedTests
        {
            [NUnit.Framework.Test]
            public void NUnitSubtract_ExercisesProduction()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Subtract(5, 3);
            }

            [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
            public void MSTestAdd_ExercisesProduction()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Add(2, 3);
            }
        }
        """;

    /// <summary>
    /// Tests of the last registered framework only. Combined with a reference set holding every
    /// framework, this is the case in which several probes match and none of them but the last is awake.
    /// </summary>
    private const string MSTestOnlySource = """
        namespace Tests;

        public class MixedTests
        {
            [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
            public void MSTestAdd_ExercisesProduction()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Add(2, 3);
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

#if FRAMESHIFT_XUNIT_V3
    /// <summary>
    /// A project that references both major versions of xUnit.net at once, which is where the risk of the
    /// version split lives: two registry entries match one compilation, and both may describe the same
    /// method.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The v3 assemblies are referenced under the extern alias <c>xunitv3</c>, because both versions
    /// declare <c>Xunit.FactAttribute</c> and an unaliased reference would make every mention of that name
    /// a CS0433 - a fixture could then name neither version. With the alias, <c>Xunit.Fact</c> is the
    /// version 2 attribute and <c>xunitv3::Xunit.Fact</c> the version 3 one, and the aliasing changes
    /// nothing about detection: both assemblies stay part of
    /// <see cref="IModuleSymbol.ReferencedAssemblySymbols" /> and of
    /// <see cref="Compilation.ReferencedAssemblyNames" />, which is all the two probes look at.
    /// </para>
    /// <para>
    /// The last test carries the <c>[Fact]</c> attribute of both versions at the same time. That is the one
    /// shape in which the two probes really do describe the very same method - a method attributed with one
    /// version only is recognised by that version's recogniser alone, because each of them resolves its
    /// attribute type inside its own assembly and compares symbols. It exercises no production code, so it
    /// is exactly the method both xUnit analyzers would report if the shared analysis did not deduplicate.
    /// </para>
    /// </remarks>
    private const string BothXunitVersionsSource = """
        extern alias xunitv3;

        namespace Tests;

        public class MixedTests
        {
            [Xunit.Fact]
            public void XunitV2Add_ExercisesProduction()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Add(2, 3);
            }

            [Xunit.Fact]
            public void XunitV2LocalStateOnly_TouchesNoProduction() => Verify(Compute());

            [xunitv3::Xunit.Fact]
            public void XunitV3Subtract_ExercisesProduction()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Subtract(5, 3);
            }

            [xunitv3::Xunit.Fact]
            public void XunitV3LocalStateOnly_TouchesNoProduction() => Verify(Compute());

            [Xunit.Fact]
            [xunitv3::Xunit.Fact]
            public void BothVersionsLocalStateOnly_TouchesNoProduction() => Verify(Compute());

            private static int Compute() => 41;

            private static void Verify(int value)
            {
            }
        }
        """;
#endif

    [Test]
    public async Task Fixtures_EveryCompilation_CompilesWithoutErrors()
    {
        var errors = new[]
        {
            Describe(CreateProduction()),
            Describe(CreateTwoFrameworkTest()),
            Describe(CreateThreeFrameworkTest()),
            Describe(CreateReferencedButUnusedFrameworkTest()),
            Describe(CreateXunitAndNUnitTest()),
            Describe(CreateNUnitAndMSTestTest()),
            Describe(CreateEveryFrameworkReferencedTest()),
#if FRAMESHIFT_XUNIT_V3
            Describe(CreateBothXunitVersionsTest()),
#endif
        };

        _ = await Assert
            .That(string.Join(" / ", errors.Distinct(StringComparer.Ordinal)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    /// <summary>
    /// <c>FSH0004</c> is per framework, and here each test belongs to exactly one analyzer, so the
    /// developer sees each offending test once. Reporting it twice is what would happen if the analyzers
    /// judged every test of the compilation instead of only their own.
    /// </summary>
    [Test]
    public async Task EveryAnalyzer_TestsOfTwoFrameworksWithoutProductionReference_ReportsEachTestExactlyOnce()
    {
        var test = CreateTwoFrameworkTest();

        var diagnostics = await RunEveryAnalyzerOfIdAsync(test, DiagnosticIds.TestWithoutProductionReference)
            .ConfigureAwait(false);

        var messages = diagnostics.Select(diagnostic => GetMessage(diagnostic)).ToImmutableArray();

        using (Assert.Multiple())
        {
            _ = await Assert.That(diagnostics.Length).IsEqualTo(2);
            _ = await Assert.That(Count(messages, TUnitLocalOnlyTestName)).IsEqualTo(1);
            _ = await Assert.That(Count(messages, NUnitLocalOnlyTestName)).IsEqualTo(1);
            _ = await Assert.That(Count(messages, TUnitCoveringTestName)).IsEqualTo(0);
            _ = await Assert.That(Count(messages, NUnitCoveringTestName)).IsEqualTo(0);
        }
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
    [Arguments(TUnitFramework, 1)]
    [Arguments(XunitV2Framework, 0)]
    [Arguments(XunitV3Framework, 0)]
    [Arguments(NUnitFramework, 0)]
    [Arguments(MSTestFramework, 0)]
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
    /// The lead is not "TUnit unless it is missing": it is the first awake framework in registry order,
    /// whichever that turns out to be. Here the first registered framework is not referenced at all, and
    /// the second one takes the manifest over with the exact complaint the file deserves.
    /// </summary>
    /// <param name="framework">The framework whose analyzer is run on its own.</param>
    /// <param name="leads">Whether that analyzer is the one that reports the manifest.</param>
    [Test]
    [Arguments(TUnitFramework, false)]
    [Arguments(XunitV2Framework, true)]
    [Arguments(XunitV3Framework, false)]
    [Arguments(NUnitFramework, false)]
    [Arguments(MSTestFramework, false)]
    public async Task SingleAnalyzer_MalformedManifestOnAnXunitAndNUnitProject_OnlyXunitV2Reports(
        string framework,
        bool leads
    )
    {
        var diagnostics = await RunSingleAnalyzerAsync(framework, CreateXunitAndNUnitTest()).ConfigureAwait(false);

        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DescribeMalformedManifest(leads));
    }

    /// <summary>
    /// The same for the two frameworks at the very end of the registry, so that the tie-break is asserted
    /// where the leading one is not the first entry of the registry either.
    /// </summary>
    /// <param name="framework">The framework whose analyzer is run on its own.</param>
    /// <param name="leads">Whether that analyzer is the one that reports the manifest.</param>
    [Test]
    [Arguments(TUnitFramework, false)]
    [Arguments(XunitV2Framework, false)]
    [Arguments(XunitV3Framework, false)]
    [Arguments(NUnitFramework, true)]
    [Arguments(MSTestFramework, false)]
    public async Task SingleAnalyzer_MalformedManifestOnANUnitAndMSTestProject_OnlyNUnitReports(
        string framework,
        bool leads
    )
    {
        var diagnostics = await RunSingleAnalyzerAsync(framework, CreateNUnitAndMSTestTest()).ConfigureAwait(false);

        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DescribeMalformedManifest(leads));
    }

    /// <summary>
    /// Every registered framework is referenced, so every probe matches, yet only the last registered one
    /// has a test of its own. Being awake is what elects, so that framework leads even though every probe
    /// ahead of it recognises the compilation.
    /// </summary>
    /// <param name="framework">The framework whose analyzer is run on its own.</param>
    /// <param name="leads">Whether that analyzer is the one that reports the manifest.</param>
    [Test]
    [Arguments(TUnitFramework, false)]
    [Arguments(XunitV2Framework, false)]
    [Arguments(XunitV3Framework, false)]
    [Arguments(NUnitFramework, false)]
    [Arguments(MSTestFramework, true)]
    public async Task SingleAnalyzer_EveryFrameworkReferencedButOnlyMSTestAwake_OnlyMSTestReports(
        string framework,
        bool leads
    )
    {
        var diagnostics = await RunSingleAnalyzerAsync(framework, CreateEveryFrameworkReferencedTest())
            .ConfigureAwait(false);

        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DescribeMalformedManifest(leads));
    }

    /// <summary>
    /// The union the manifest is judged against is the union of the <em>awake</em> frameworks, not of the
    /// matching ones. A manifest recording the tests of the only awake framework is therefore complete,
    /// even though further probes recognise the compilation.
    /// </summary>
    [Test]
    public async Task EveryAnalyzer_ManifestOfTheOnlyAwakeFramework_ReportsNoManifestProblem()
    {
        var test = CreateEveryFrameworkReferencedTest();
        var manifest = CreateManifest(test, MSTestTestFrameworkProbe.Instance);

        var diagnostics = await RunEveryAnalyzerOfIdAsync(test, DiagnosticIds.InvalidTestSurfaceManifest, manifest)
            .ConfigureAwait(false);

        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
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
    [Arguments(TUnitFramework)]
    [Arguments(NUnitFramework)]
    public async Task EveryAnalyzer_ManifestCoveringOnlyOneFramework_IsReportedAsStale(string framework)
    {
        var test = CreateTwoFrameworkTest();
        var manifest = CreateManifest(test, ProbeOf(framework));

        var diagnostics = await RunEveryAnalyzerOfIdAsync(test, DiagnosticIds.InvalidTestSurfaceManifest, manifest)
            .ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(diagnostics.Length).IsEqualTo(1);
            _ = await Assert.That(GetMessage(diagnostics[0]).Contains(StaleDetail, StringComparison.Ordinal)).IsTrue();
        }
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
            XunitV2TestFrameworkProbe.Instance,
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

#if FRAMESHIFT_XUNIT_V3
    /// <summary>
    /// The expectation the version split has to earn. Two probes match this compilation and both of them
    /// describe <c>BothVersionsLocalStateOnly_TouchesNoProduction</c>, so a shared analysis that reported
    /// per matching probe would name that one test twice. Each of the three offending tests is named
    /// exactly once instead, and neither of the two tests that do exercise production code is named at all.
    /// </summary>
    [Test]
    public async Task EveryAnalyzer_TestsOfBothXunitVersionsWithoutProductionReference_ReportsEachTestExactlyOnce()
    {
        var test = CreateBothXunitVersionsTest();

        var diagnostics = await RunEveryAnalyzerOfIdAsync(test, DiagnosticIds.TestWithoutProductionReference)
            .ConfigureAwait(false);

        var messages = diagnostics.Select(diagnostic => GetMessage(diagnostic)).ToImmutableArray();

        using (Assert.Multiple())
        {
            _ = await Assert.That(diagnostics.Length).IsEqualTo(3);
            _ = await Assert.That(Count(messages, XunitV2LocalOnlyTestName)).IsEqualTo(1);
            _ = await Assert.That(Count(messages, XunitV3LocalOnlyTestName)).IsEqualTo(1);
            _ = await Assert.That(Count(messages, BothVersionsLocalOnlyTestName)).IsEqualTo(1);
            _ = await Assert.That(Count(messages, XunitV2CoveringTestName)).IsEqualTo(0);
            _ = await Assert.That(Count(messages, XunitV3CoveringTestName)).IsEqualTo(0);
        }
    }

    /// <summary>
    /// The same count taken with every analyzer FrameShift ships, including
    /// <see cref="MutationCoverageAnalyzer" />, so that the total really is what a developer building this
    /// project reads and not just what the five test-surface analyzers agree on.
    /// </summary>
    /// <remarks>
    /// No manifest is handed in, which is the one shape in which the production-side analyzer contributes
    /// nothing: it returns before doing any work when the compilation has no manifest at all. That keeps
    /// this a count of <c>FSH0004</c> reports rather than a count mixed with the production side's own
    /// manifest complaint.
    /// </remarks>
    [Test]
    public async Task EveryShippedAnalyzer_TestRecognisedByBothXunitVersions_IsReportedExactlyOnce()
    {
        var test = CreateBothXunitVersionsTest();

        var reported = await RunEveryShippedAnalyzerAsync(test).ConfigureAwait(false);
        var diagnostics = AnalyzerRunner.OfId(reported, DiagnosticIds.TestWithoutProductionReference);
        var messages = diagnostics.Select(diagnostic => GetMessage(diagnostic)).ToImmutableArray();

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(
                    DiagnosticAssertions
                        .Ids(reported)
                        .Contains(AnalyzerRunner.AnalyzerFailureId, StringComparer.Ordinal)
                )
                .IsFalse();
            _ = await Assert.That(diagnostics.Length).IsEqualTo(3);
            _ = await Assert.That(Count(messages, BothVersionsLocalOnlyTestName)).IsEqualTo(1);
        }
    }

    /// <summary>
    /// One manifest, two matching xUnit.net entries: the stale complaint is read once. The manifest records
    /// the version 2 surface only, which really is incomplete on this compilation, so the report is the one
    /// the file deserves rather than an artefact of the counting.
    /// </summary>
    [Test]
    public async Task EveryAnalyzer_StaleManifestOnABothXunitVersionProject_ReportsItExactlyOnce()
    {
        var test = CreateBothXunitVersionsTest();
        var manifest = CreateManifest(test, XunitV2TestFrameworkProbe.Instance);

        var diagnostics = await RunEveryAnalyzerOfIdAsync(test, DiagnosticIds.InvalidTestSurfaceManifest, manifest)
            .ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(diagnostics.Length).IsEqualTo(1);
            _ = await Assert.That(GetMessage(diagnostics[0]).Contains(StaleDetail, StringComparison.Ordinal)).IsTrue();
        }
    }

    /// <summary>
    /// Which of the two xUnit.net entries reports the manifest is decided by the registry order alone, and
    /// version 2 comes first. Every other analyzer, the v3 one included, stays completely silent about the
    /// file.
    /// </summary>
    /// <param name="framework">The framework whose analyzer is run on its own.</param>
    /// <param name="leads">Whether that analyzer is the one that reports the manifest.</param>
    [Test]
    [Arguments(TUnitFramework, false)]
    [Arguments(XunitV2Framework, true)]
    [Arguments(XunitV3Framework, false)]
    [Arguments(NUnitFramework, false)]
    [Arguments(MSTestFramework, false)]
    public async Task SingleAnalyzer_MalformedManifestOnABothXunitVersionProject_OnlyXunitV2Reports(
        string framework,
        bool leads
    )
    {
        var diagnostics = await RunSingleAnalyzerAsync(framework, CreateBothXunitVersionsTest()).ConfigureAwait(false);

        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DescribeMalformedManifest(leads));
    }

    /// <summary>
    /// The manifest of a project on both versions is compared against the union of the two surfaces, and
    /// the union does not double-count: a manifest that records every test once - which is exactly what the
    /// build writes - is complete, although one of its tests is described by both awake frameworks.
    /// </summary>
    [Test]
    public async Task EveryAnalyzer_ManifestCoveringBothXunitVersionsOnce_ReportsNoManifestProblem()
    {
        var test = CreateBothXunitVersionsTest();
        var manifest = CreateManifest(test, XunitV2TestFrameworkProbe.Instance, XunitV3TestFrameworkProbe.Instance);

        var diagnostics = await RunEveryAnalyzerOfIdAsync(test, DiagnosticIds.InvalidTestSurfaceManifest, manifest)
            .ConfigureAwait(false);

        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    /// <summary>
    /// The reason the manifest above is accepted, pinned directly at the artefact: the test both versions
    /// recognise is recorded by a single line. A surface that counted per matching framework would list it
    /// twice, and the comparison would then be against a manifest no build could ever produce.
    /// </summary>
    [Test]
    public async Task Manifest_TestRecognisedByBothXunitVersions_RecordsItsIdOnce()
    {
        var test = CreateBothXunitVersionsTest();
        var manifest = CreateManifest(test, XunitV2TestFrameworkProbe.Instance, XunitV3TestFrameworkProbe.Instance);

        var lines = manifest.Split('\n').Where(line => line.Length > 0);
        var occurrences = lines.Count(line => line.Contains(BothVersionsLocalOnlyTestName, StringComparison.Ordinal));

        _ = await Assert.That(occurrences).IsEqualTo(1);
    }
#endif

    /// <summary>
    /// None of the mixed shapes may make an analyzer throw, which Roslyn would otherwise hide behind an
    /// <c>AD0001</c> diagnostic and which would turn every count above into a false pass.
    /// </summary>
    [Test]
    public async Task EveryAnalyzer_EveryMixedShape_NeverCrashes()
    {
        var reported = new List<string>();

        foreach (var shape in GetMixedShapes())
        {
            reported.AddRange(
                DiagnosticAssertions.Ids(await RunEveryAnalyzerAsync(shape.Compilation, null).ConfigureAwait(false))
            );
            reported.AddRange(
                DiagnosticAssertions.Ids(
                    await RunEveryAnalyzerAsync(shape.Compilation, shape.Manifest).ConfigureAwait(false)
                )
            );
        }

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(reported.Contains(AnalyzerRunner.AnalyzerFailureId, StringComparer.Ordinal))
                .IsFalse();
            _ = await Assert
                .That(reported.Contains(DiagnosticIds.InvalidTestSurfaceManifest, StringComparer.Ordinal))
                .IsTrue();
        }
    }

    /// <summary>
    /// The mixed compilations of this suite, each one paired with a manifest that records the surface of a
    /// framework it really is awake on, so that the crash test drives the manifest comparison too.
    /// </summary>
    /// <returns>The compilations and their manifests.</returns>
    private static ImmutableArray<(Compilation Compilation, string Manifest)> GetMixedShapes()
    {
        var builder = ImmutableArray.CreateBuilder<(Compilation Compilation, string Manifest)>();
        var twoFrameworks = CreateTwoFrameworkTest();
        var threeFrameworks = CreateThreeFrameworkTest();

        builder.Add((twoFrameworks, CreateManifest(twoFrameworks, TUnitTestFrameworkProbe.Instance)));
        builder.Add((threeFrameworks, CreateManifest(threeFrameworks, TUnitTestFrameworkProbe.Instance)));

#if FRAMESHIFT_XUNIT_V3
        var bothVersions = CreateBothXunitVersionsTest();

        builder.Add((bothVersions, CreateManifest(bothVersions, XunitV2TestFrameworkProbe.Instance)));
#endif

        return builder.ToImmutable();
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
        CreateTest(ThreeFrameworkSource, TestFramework.TUnit, TestFramework.XunitV2, TestFramework.NUnit);

    private static CSharpCompilation CreateXunitAndNUnitTest() =>
        CreateTest(XunitAndNUnitSource, TestFramework.XunitV2, TestFramework.NUnit);

    private static CSharpCompilation CreateNUnitAndMSTestTest() =>
        CreateTest(NUnitAndMSTestSource, TestFramework.NUnit, TestFramework.MSTest);

    private static CSharpCompilation CreateEveryFrameworkReferencedTest() =>
        CreateTest(MSTestOnlySource, TestFramework.All);

    private static CSharpCompilation CreateReferencedButUnusedFrameworkTest() =>
        CreateTest(ReferencedButUnusedFrameworkSource, TestFramework.TUnit, TestFramework.NUnit);

#if FRAMESHIFT_XUNIT_V3
    /// <summary>
    /// Builds a test assembly that references xUnit.net v2 globally and xUnit.net v3 under an extern alias,
    /// which is the only way one fixture can name the <c>[Fact]</c> attribute of both major versions.
    /// </summary>
    /// <returns>The created compilation.</returns>
    private static CSharpCompilation CreateBothXunitVersionsTest() =>
        CreateTest(BothXunitVersionsSource, MergeWithAliasedXunitV3(TestFramework.XunitV2));

    /// <summary>
    /// Merges the reference sets of <paramref name="frameworks" /> and adds the xUnit.net v3 assemblies
    /// under <see cref="XunitV3Alias" />, leaving out everything the merged set already carries so that
    /// only the assemblies version 3 contributes of its own are aliased. Aliasing a runtime assembly would
    /// take <c>object</c> out of the global namespace and nothing would compile at all.
    /// </summary>
    /// <param name="frameworks">The frameworks whose assemblies are referenced without an alias.</param>
    /// <returns>The merged references.</returns>
    private static ImmutableArray<MetadataReference> MergeWithAliasedXunitV3(params TestFramework[] frameworks)
    {
        var global = Merge(frameworks);
        var known = global.Select(reference => Display(reference)).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var aliases = ImmutableArray.Create(XunitV3Alias);
        var aliased = ReferenceAssemblies
            .For(TestFramework.XunitV3)
            .Where(reference => !known.Contains(Display(reference)))
            .Select(reference => reference.WithAliases(aliases));

        return global.AddRange(aliased);
    }
#endif

    /// <summary>
    /// Builds the test assembly against the production assembly and the assemblies of several test
    /// frameworks at once.
    /// </summary>
    /// <param name="source">The C# source of the test assembly.</param>
    /// <param name="frameworks">The frameworks whose assemblies are referenced.</param>
    /// <returns>The created compilation.</returns>
    private static CSharpCompilation CreateTest(string source, params TestFramework[] frameworks) =>
        CreateTest(source, Merge(frameworks));

    /// <summary>
    /// Builds the test assembly against the production assembly and an explicit reference set, which is
    /// what a compilation needs whose references are not all handed out unaliased.
    /// </summary>
    /// <param name="source">The C# source of the test assembly.</param>
    /// <param name="references">The references of the compilation, without the production assembly.</param>
    /// <returns>The created compilation.</returns>
    private static CSharpCompilation CreateTest(string source, ImmutableArray<MetadataReference> references) =>
        CSharpCompilation.Create(
            TestAssemblyName,
            [CompilationFactory.ParseTree(source, TestPath)],
            references.Add(CreateProduction().ToMetadataReference()),
            CompilationFactory.CompilationOptions
        );

    /// <summary>
    /// Merges the reference sets of several frameworks. They overlap in the runtime assemblies and each one
    /// carries its own <see cref="MetadataReference" /> object per file, so they are merged by path;
    /// handing Roslyn two references to the same assembly identity would be a compile error rather than
    /// a mixed-framework project.
    /// </summary>
    /// <param name="frameworks">The frameworks whose assemblies are referenced.</param>
    /// <returns>The merged references.</returns>
    private static ImmutableArray<MetadataReference> Merge(TestFramework[] frameworks) =>
        [
            .. frameworks
                .SelectMany(framework => ReferenceAssemblies.For(framework))
                .GroupBy(reference => Display(reference), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()),
        ];

    private static string Display(MetadataReference reference) => reference.Display ?? string.Empty;

    /// <summary>
    /// Renders the manifest of the union of the test surfaces <paramref name="probes" /> can see, which
    /// is how the build records a project that uses several frameworks.
    /// </summary>
    /// <param name="test">The test compilation under analysis.</param>
    /// <param name="probes">The probes whose surfaces are unioned.</param>
    /// <returns>The rendered manifest.</returns>
    private static string CreateManifest(Compilation test, params ITestFrameworkProbe[] probes)
    {
        var collected = ImmutableArray.CreateBuilder<TestSurfaceManifest>(probes.Length);

        foreach (var probe in probes)
        {
            var recognizer = CreateRecognizer(test, probe);

            collected.Add(TestSurfaceCollector.Collect(test, recognizer, CancellationToken.None));
        }

        // Merged block by block, exactly as the generator and the test-surface analyzer do it, so the
        // fixture manifest carries the per-test attribution and the case counts the build really emits.
        // Uniting the flat id sets instead would attribute every reference to every test and degrade
        // every count to a lower bound, which is a manifest shape no build produces.
        return TestSurfaceManifestWriter.Write(TestSurfaceManifest.Merge(collected.ToImmutable()));
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
            TUnitFramework => new TUnitTestSurfaceAnalyzer(),
            XunitV2Framework => new XunitV2TestSurfaceAnalyzer(),
            XunitV3Framework => new XunitV3TestSurfaceAnalyzer(),
            NUnitFramework => new NUnitTestSurfaceAnalyzer(),
            MSTestFramework => new MSTestTestSurfaceAnalyzer(),
            _ => throw new ArgumentOutOfRangeException(nameof(framework), framework, "Unknown framework."),
        };

    /// <summary>
    /// Runs every framework analyzer over the same compilation and returns everything they reported,
    /// which is what a developer building a mixed-framework project actually sees.
    /// </summary>
    /// <param name="compilation">The compilation to analyse.</param>
    /// <param name="manifest">The manifest handed to the analyzers, or <see langword="null" /> for none.</param>
    /// <returns>The reported diagnostics of every framework analyzer.</returns>
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

#if FRAMESHIFT_XUNIT_V3
    /// <summary>
    /// Runs every analyzer FrameShift ships over the compilation, the production-side
    /// <see cref="MutationCoverageAnalyzer" /> included, and returns everything they reported. No manifest
    /// is handed in, which is the shape in which the production-side analyzer does nothing at all.
    /// </summary>
    /// <param name="compilation">The compilation to analyse.</param>
    /// <returns>The reported diagnostics of every shipped analyzer.</returns>
    private static async Task<ImmutableArray<Diagnostic>> RunEveryShippedAnalyzerAsync(Compilation compilation)
    {
        var builder = ImmutableArray.CreateBuilder<Diagnostic>();

        builder.AddRange(await RunEveryAnalyzerAsync(compilation, null).ConfigureAwait(false));
        builder.AddRange(
            await AnalyzerRunner.RunAsync(new MutationCoverageAnalyzer(), compilation).ConfigureAwait(false)
        );

        return builder.ToImmutable();
    }
#endif

    private static async Task<ImmutableArray<Diagnostic>> RunEveryAnalyzerOfIdAsync(
        Compilation compilation,
        string diagnosticId,
        string? manifest = null
    )
    {
        var diagnostics = await RunEveryAnalyzerAsync(compilation, manifest).ConfigureAwait(false);

        return AnalyzerRunner.OfId(diagnostics, diagnosticId);
    }

    /// <summary>
    /// Runs the analyzer of one framework over <paramref name="compilation" /> with a manifest that
    /// cannot be parsed, and keeps the manifest diagnostics.
    /// </summary>
    /// <param name="framework">The framework whose analyzer is run.</param>
    /// <param name="compilation">The compilation to analyse.</param>
    /// <returns>The reported <c>FSH0003</c> diagnostics.</returns>
    private static Task<ImmutableArray<Diagnostic>> RunSingleAnalyzerAsync(string framework, Compilation compilation) =>
        AnalyzerRunner.RunAsync(
            CreateAnalyzer(framework),
            compilation,
            DiagnosticIds.InvalidTestSurfaceManifest,
            AdditionalFiles(MalformedManifest)
        );

    /// <summary>
    /// Describes what the analyzer of a framework has to report about <see cref="MalformedManifest" />:
    /// the header complaint when it leads the manifest comparison, and nothing at all when it does not.
    /// </summary>
    /// <param name="leads">Whether the analyzer is the one that reports the manifest.</param>
    /// <returns>The expected description.</returns>
    private static string DescribeMalformedManifest(bool leads) =>
        leads
            ? DiagnosticIds.InvalidTestSurfaceManifest
                + " "
                + InMemoryAdditionalText.DefaultPath
                + "(1,1): Test-surface manifest '"
                + InMemoryAdditionalText.DefaultPath
                + "' could not be read: Line 1: expected the test-surface manifest header "
                + "'frameshift-test-surface/1', but found 'not-a-test-surface-manifest'."
            : DiagnosticAssertions.NoDiagnostics;

    private static ImmutableArray<AdditionalText> AdditionalFiles(string? manifest) =>
        manifest is null ? [] : [new InMemoryAdditionalText(manifest)];

    private static int Count(ImmutableArray<string> messages, string name) =>
        messages.Count(message => message.Contains(name, StringComparison.Ordinal));

    private static string GetMessage(Diagnostic diagnostic) => diagnostic.GetMessage(CultureInfo.InvariantCulture);

    private static string Describe(Compilation compilation) =>
        DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation));
}
