namespace NetEvolve.FrameShift.Tests.Unit.TestSurface;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the registry that lists the supported test framework versions. Its content and its order are
/// contract, not convenience: a test project may reference several frameworks — or both major versions of
/// one framework — at once, and the single shared test-surface manifest is then handled by the first
/// matching entry in exactly this order. Losing a probe would switch FrameShift off for a whole framework
/// version, and reordering them would move the responsibility for <c>FSH0003</c> somewhere else, so both
/// are asserted explicitly.
/// </summary>
/// <remarks>
/// <para>
/// The registered probes exist on every target framework, so <see cref="TestFrameworkProbeRegistry.All" />
/// and everything derived from it is asserted unconditionally. Only the fixtures are conditional: a
/// compilation that actually references xUnit.net v3 needs <c>xunit.v3.core</c>, which ships no assets for
/// net6.0 and net7.0, so those cases are guarded while every other one runs everywhere.
/// </para>
/// <para>
/// The class is <see langword="internal" /> because <see cref="TestFramework" /> appears in its test
/// signatures; TUnit discovers internal test classes just as well as public ones.
/// </para>
/// </remarks>
internal sealed class TestFrameworkProbeRegistryTests
{
    /// <summary>
    /// The documented registration order, spelled out so that adding or losing a framework version is a
    /// deliberate change to this expectation rather than a silent one. The two xUnit.net versions are two
    /// entries with two names of their own.
    /// </summary>
    private const string DocumentedOrder = "TUnit, xUnit v2, xUnit v3, NUnit, MSTest";

    private const int RegisteredProbeCount = 5;

    /// <summary>
    /// What a compilation referencing every framework at once is matched by. It is the full documented order
    /// wherever xUnit.net v3 ships assets, and the same order without that one entry on net6.0 and net7.0,
    /// where <see cref="TestFramework.All" /> cannot include it.
    /// </summary>
#if FRAMESHIFT_XUNIT_V3
    private const string EveryFrameworkOrder = DocumentedOrder;
#else
    private const string EveryFrameworkOrder = "TUnit, xUnit v2, NUnit, MSTest";
#endif

    private const string PlainSource = """
        namespace Fixture;

        public class Cases
        {
            public void PlainMethod()
            {
            }
        }
        """;

    [Test]
    public async Task All_Registry_ListsTheSupportedFrameworkVersionsInTheDocumentedOrder() =>
        _ = await Assert.That(Describe(TestFrameworkProbeRegistry.All)).IsEqualTo(DocumentedOrder);

    /// <summary>
    /// The count is asserted next to the order, because a probe added without a name of its own would
    /// otherwise slip through the order expectation unnoticed.
    /// </summary>
    [Test]
    public async Task All_Registry_ContainsExactlyTheRegisteredProbes() =>
        _ = await Assert.That(TestFrameworkProbeRegistry.All.Length).IsEqualTo(RegisteredProbeCount);

    [Test]
    public async Task All_Registry_ContainsEveryProbeExactlyOnce()
    {
        var types = TestFrameworkProbeRegistry.All.Select(probe => probe.GetType()).ToImmutableArray();

        _ = await Assert.That(types.Distinct().Count()).IsEqualTo(types.Length);
    }

    /// <summary>
    /// Every registered framework name is distinct, which is what lets a diagnostic message and the shared
    /// analysis tell the two major versions of one framework apart.
    /// </summary>
    [Test]
    public async Task All_Registry_NamesEveryFrameworkVersionDistinctly()
    {
        var names = TestFrameworkProbeRegistry.All.Select(probe => probe.FrameworkName).ToImmutableArray();

        _ = await Assert.That(names.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(names.Length);
    }

    /// <summary>
    /// The registry hands out shared, stateless instances, which is what makes it safe to walk from
    /// concurrent analyzer callbacks.
    /// </summary>
    [Test]
    public async Task All_Registry_HandsOutTheSharedProbeInstances()
    {
        var registered = TestFrameworkProbeRegistry.All;

        var shared = new[]
        {
            TUnitTestFrameworkProbe.Instance,
            XunitV2TestFrameworkProbe.Instance,
            XunitV3TestFrameworkProbe.Instance,
            NUnitTestFrameworkProbe.Instance,
            MSTestTestFrameworkProbe.Instance,
        };

        _ = await Assert.That(registered.SequenceEqual(shared)).IsTrue();
    }

    [Test]
    public async Task Matching_CompilationWithoutAnyFramework_ReturnsNothing()
    {
        var compilation = CompilationFactory.Create(PlainSource);

        _ = await Assert.That(Describe(TestFrameworkProbeRegistry.Matching(compilation))).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// Every supported framework version has to be recognised on its own, and by nothing but its own probe.
    /// The two xUnit.net versions are the interesting rows: they declare the identical test attribute name,
    /// so a compilation on one of them must never wake the probe of the other.
    /// </summary>
    /// <param name="framework">The framework the compilation references.</param>
    /// <param name="expected">The framework names the registry is expected to report.</param>
    [Test]
    [Arguments(TestFramework.TUnit, "TUnit")]
    [Arguments(TestFramework.XunitV2, "xUnit v2")]
#if FRAMESHIFT_XUNIT_V3
    [Arguments(TestFramework.XunitV3, "xUnit v3")]
#endif
    [Arguments(TestFramework.NUnit, "NUnit")]
    [Arguments(TestFramework.MSTest, "MSTest")]
    public async Task Matching_CompilationOfASingleFramework_ReturnsOnlyThatProbe(
        TestFramework framework,
        string expected
    )
    {
        var compilation = CompilationFactory.Create(PlainSource, framework);

        _ = await Assert.That(Describe(TestFrameworkProbeRegistry.Matching(compilation))).IsEqualTo(expected);
    }

    /// <summary>
    /// A compilation referencing several frameworks is reported in registry order, never in the order the
    /// references happen to be listed in, because that order decides who owns the manifest.
    /// </summary>
    /// <param name="first">One framework the compilation references.</param>
    /// <param name="second">Another framework the compilation references.</param>
    /// <param name="expected">The framework names the registry is expected to report, in order.</param>
    [Test]
    [Arguments(TestFramework.NUnit, TestFramework.TUnit, "TUnit, NUnit")]
    [Arguments(TestFramework.MSTest, TestFramework.XunitV2, "xUnit v2, MSTest")]
    [Arguments(TestFramework.MSTest, TestFramework.NUnit, "NUnit, MSTest")]
    [Arguments(TestFramework.XunitV2, TestFramework.TUnit, "TUnit, xUnit v2")]
#if FRAMESHIFT_XUNIT_V3
    [Arguments(TestFramework.MSTest, TestFramework.XunitV3, "xUnit v3, MSTest")]
    [Arguments(TestFramework.XunitV3, TestFramework.TUnit, "TUnit, xUnit v3")]
#endif
    public async Task Matching_CompilationOfSeveralFrameworks_ReturnsThemInRegistryOrder(
        TestFramework first,
        TestFramework second,
        string expected
    )
    {
        var compilation = Create(PlainSource, first, second);

        _ = await Assert.That(Describe(TestFrameworkProbeRegistry.Matching(compilation))).IsEqualTo(expected);
    }

#if FRAMESHIFT_XUNIT_V3
    /// <summary>
    /// The case the version split exists for: one project referencing both major versions of xUnit.net is
    /// matched by both xUnit entries and by nothing else, in registry order — so version 2 leads the
    /// manifest comparison and each version judges only its own tests. A probe that matched the other
    /// version's assembly would show up here as a duplicate name or a wrong order.
    /// </summary>
    [Test]
    public async Task Matching_CompilationOfBothXunitVersions_ReturnsBothXunitProbesInRegistryOrder()
    {
        var compilation = Create(PlainSource, TestFramework.XunitV3, TestFramework.XunitV2);

        _ = await Assert
            .That(Describe(TestFrameworkProbeRegistry.Matching(compilation)))
            .IsEqualTo("xUnit v2, xUnit v3");
    }
#endif

    /// <summary>
    /// Three frameworks at once, referenced in an order that is the reverse of the registry's.
    /// </summary>
    [Test]
    public async Task Matching_CompilationOfThreeFrameworks_ReturnsThemInRegistryOrder()
    {
        var compilation = Create(PlainSource, TestFramework.MSTest, TestFramework.NUnit, TestFramework.TUnit);

        _ = await Assert
            .That(Describe(TestFrameworkProbeRegistry.Matching(compilation)))
            .IsEqualTo("TUnit, NUnit, MSTest");
    }

    /// <summary>
    /// Four frameworks at once, with xUnit.net v2 among them and its version 3 sibling deliberately absent,
    /// which is the shape every target framework can build and the one that proves the v3 probe stays
    /// asleep next to a v2 reference.
    /// </summary>
    [Test]
    public async Task Matching_CompilationOfFourFrameworks_ReturnsThemInRegistryOrder()
    {
        var compilation = Create(
            PlainSource,
            TestFramework.MSTest,
            TestFramework.NUnit,
            TestFramework.XunitV2,
            TestFramework.TUnit
        );

        _ = await Assert
            .That(Describe(TestFrameworkProbeRegistry.Matching(compilation)))
            .IsEqualTo("TUnit, xUnit v2, NUnit, MSTest");
    }

    /// <summary>
    /// With every supported framework referenced at once — including both xUnit.net versions, which makes
    /// <c>Xunit.FactAttribute</c> ambiguous for the compilation as a whole — the registry still reports
    /// every probe in order, because each one resolves its own assembly instead of asking the compilation.
    /// Where xUnit.net v3 has no assets, the same fixture covers every framework except that one.
    /// </summary>
    [Test]
    public async Task Matching_CompilationOfEveryFramework_ReturnsAllProbesInRegistryOrder()
    {
        var compilation = CompilationFactory.Create(PlainSource, TestFramework.All);

        _ = await Assert
            .That(Describe(TestFrameworkProbeRegistry.Matching(compilation)))
            .IsEqualTo(EveryFrameworkOrder);
    }

    [Test]
    public async Task Matching_CompilationIsNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => TestFrameworkProbeRegistry.Matching(null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("compilation");
    }

    /// <summary>
    /// Guards the fixtures: a compilation that does not compile would make every expectation above say
    /// something about a broken fixture instead of about the registry.
    /// </summary>
    [Test]
    public async Task Fixtures_EveryCompilation_CompilesWithoutErrors()
    {
        var errors = new[]
        {
            Describe(CompilationFactory.Create(PlainSource)),
            Describe(CompilationFactory.Create(PlainSource, TestFramework.All)),
            Describe(Create(PlainSource, TestFramework.NUnit, TestFramework.TUnit)),
            Describe(Create(PlainSource, TestFramework.MSTest, TestFramework.XunitV2)),
            Describe(Create(PlainSource, TestFramework.MSTest, TestFramework.NUnit)),
            Describe(Create(PlainSource, TestFramework.XunitV2, TestFramework.TUnit)),
#if FRAMESHIFT_XUNIT_V3
            Describe(Create(PlainSource, TestFramework.MSTest, TestFramework.XunitV3)),
            Describe(Create(PlainSource, TestFramework.XunitV3, TestFramework.TUnit)),
            Describe(Create(PlainSource, TestFramework.XunitV3, TestFramework.XunitV2)),
#endif
            Describe(Create(PlainSource, TestFramework.MSTest, TestFramework.NUnit, TestFramework.TUnit)),
            Describe(
                Create(
                    PlainSource,
                    TestFramework.MSTest,
                    TestFramework.NUnit,
                    TestFramework.XunitV2,
                    TestFramework.TUnit
                )
            ),
        };

        _ = await Assert
            .That(string.Join(" / ", errors.Distinct(StringComparer.Ordinal)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    /// <summary>
    /// Builds a compilation referencing the assemblies of several frameworks at once. The reference sets
    /// overlap in the runtime assemblies and each one carries its own <see cref="MetadataReference" />
    /// object per file, so they are merged by path; handing Roslyn two references to the same assembly
    /// identity would be a compile error rather than a mixed-framework project.
    /// </summary>
    /// <param name="source">The C# source of the compilation.</param>
    /// <param name="frameworks">The frameworks whose assemblies are referenced.</param>
    /// <returns>The created compilation.</returns>
    private static CSharpCompilation Create(string source, params TestFramework[] frameworks) =>
        CSharpCompilation.Create(
            CompilationFactory.DefaultAssemblyName,
            [CompilationFactory.ParseTree(source)],
            Merge(frameworks),
            CompilationFactory.CompilationOptions
        );

    private static ImmutableArray<MetadataReference> Merge(TestFramework[] frameworks) =>
        [
            .. frameworks
                .SelectMany(framework => ReferenceAssemblies.For(framework))
                .GroupBy(reference => reference.Display ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()),
        ];

    private static string Describe(ImmutableArray<ITestFrameworkProbe> probes) =>
        string.Join(", ", probes.Select(probe => probe.FrameworkName));

    private static string Describe(Compilation compilation) =>
        DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation));
}
