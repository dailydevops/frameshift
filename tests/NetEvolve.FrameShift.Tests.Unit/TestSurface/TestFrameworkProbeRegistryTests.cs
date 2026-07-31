namespace NetEvolve.FrameShift.Tests.Unit;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Tests.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the registry that lists the supported test frameworks. Its content and its order are contract,
/// not convenience: a test project may reference several frameworks at once, and the single shared
/// test-surface manifest is then handled by the first matching framework in exactly this order. Losing a
/// probe would switch FrameShift off for a whole framework, and reordering them would move the
/// responsibility for <c>FSH0003</c> somewhere else, so both are asserted explicitly.
/// </summary>
/// <remarks>
/// The class is <see langword="internal" /> because <see cref="TestFramework" /> appears in its test
/// signatures; TUnit discovers internal test classes just as well as public ones.
/// </remarks>
internal sealed class TestFrameworkProbeRegistryTests
{
    private const string PlainSource = """
        namespace Fixture;

        public class Cases
        {
            public void PlainMethod()
            {
            }
        }
        """;

    /// <summary>
    /// The documented registration order, spelled out so that adding or losing a framework is a
    /// deliberate change to this expectation rather than a silent one.
    /// </summary>
    [Test]
    public async Task All_Registry_ListsTheSupportedFrameworksInTheDocumentedOrder() =>
        _ = await Assert.That(Describe(TestFrameworkProbeRegistry.All)).IsEqualTo("TUnit, xUnit, NUnit, MSTest");

    [Test]
    public async Task All_Registry_ContainsEveryProbeExactlyOnce()
    {
        var types = TestFrameworkProbeRegistry.All.Select(probe => probe.GetType()).ToImmutableArray();

        _ = await Assert.That(types.Distinct().Count()).IsEqualTo(types.Length);
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
            XunitTestFrameworkProbe.Instance,
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
    /// Every supported framework has to be recognised on its own, and by nothing but its own probe. Both
    /// xUnit.net versions report the same framework name, because everything after detection is
    /// identical for them.
    /// </summary>
    /// <param name="framework">The framework the compilation references.</param>
    /// <param name="expected">The framework names the registry is expected to report.</param>
    [Test]
    [Arguments(TestFramework.TUnit, "TUnit")]
    [Arguments(TestFramework.XunitV3, "xUnit")]
    [Arguments(TestFramework.XunitV2, "xUnit")]
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
    [Arguments(TestFramework.MSTest, TestFramework.XunitV3, "xUnit, MSTest")]
    [Arguments(TestFramework.MSTest, TestFramework.NUnit, "NUnit, MSTest")]
    [Arguments(TestFramework.XunitV2, TestFramework.TUnit, "TUnit, xUnit")]
    public async Task Matching_CompilationOfSeveralFrameworks_ReturnsThemInRegistryOrder(
        TestFramework first,
        TestFramework second,
        string expected
    )
    {
        var compilation = Create(PlainSource, first, second);

        _ = await Assert.That(Describe(TestFrameworkProbeRegistry.Matching(compilation))).IsEqualTo(expected);
    }

    /// <summary>
    /// With every supported framework referenced at once — including both xUnit.net versions, which makes
    /// <c>Xunit.FactAttribute</c> ambiguous and unresolvable by name — the registry still reports all four
    /// probes in order.
    /// </summary>
    [Test]
    public async Task Matching_CompilationOfEveryFramework_ReturnsAllProbesInRegistryOrder()
    {
        var compilation = CompilationFactory.Create(PlainSource, TestFramework.All);

        _ = await Assert
            .That(Describe(TestFrameworkProbeRegistry.Matching(compilation)))
            .IsEqualTo("TUnit, xUnit, NUnit, MSTest");
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
            Describe(Create(PlainSource, TestFramework.MSTest, TestFramework.XunitV3)),
            Describe(Create(PlainSource, TestFramework.MSTest, TestFramework.NUnit)),
            Describe(Create(PlainSource, TestFramework.XunitV2, TestFramework.TUnit)),
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
