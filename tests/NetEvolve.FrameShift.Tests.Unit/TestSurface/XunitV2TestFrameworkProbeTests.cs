namespace NetEvolve.FrameShift.Tests.Unit;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Tests.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the seam xUnit v2 is plugged in through. The probe decides whether the test-side analysis runs
/// at all, so reporting absence correctly matters more than recognising presence: a probe that claims a
/// compilation it does not understand would make the analysis judge invisible tests.
/// </summary>
/// <remarks>
/// <para>
/// Every expectation here holds on every target framework, because <c>xunit.core</c> ships assets for all
/// of them. Only the one case that needs the xUnit v3 package next to the v2 one is compiled
/// conditionally.
/// </para>
/// <para>
/// What separates this probe from its v3 counterpart is the assembly name alone: both versions declare
/// <c>Xunit.FactAttribute</c> under the identical metadata name, so the version a compilation references
/// can only be told from the assembly that declares it. The guard is therefore exactly
/// <c>xunit.core</c> - the prefix <c>xunit</c> would also match every <c>xunit.v3.*</c> assembly and make
/// this probe claim a pure v3 project, which is what the tests using a v3-named satellite pin down.
/// </para>
/// </remarks>
public class XunitV2TestFrameworkProbeTests
{
    private const string FrameworkName = "xUnit v2";

    private const string FrameworkAssemblyName = "xunit.core";
    private const string XunitV3AssemblyName = "xunit.v3.core";
    private const string ForeignAssemblyName = "Foreign.Satellite";

    private const string TUnitScenario = "TUnit";
    private const string NUnitScenario = "NUnit";
    private const string MSTestScenario = "MSTest";

    /// <summary>
    /// A satellite carrying the simple name of the xUnit test attribute in a namespace of its own. Compiled
    /// under the name of a framework assembly it stands for a reference the probe recognises but whose
    /// well-known type it cannot resolve; compiled under any other name it stands for an unrelated
    /// dependency.
    /// </summary>
    private const string SatelliteSource = """
        namespace Satellite;

        using System;

        [AttributeUsage(AttributeTargets.Method)]
        public class FactAttribute : Attribute
        {
        }
        """;

    private const string CasesSource = """
        namespace Fixture;

        public class Cases
        {
            [Satellite.Fact]
            public void DecoratedTest()
            {
            }

            public void PlainMethod()
            {
            }
        }
        """;

    private const string PlainSource = """
        namespace Fixture;

        public class Cases
        {
            public void PlainMethod()
            {
            }
        }
        """;

    private const string XunitSource = """
        namespace Fixture;

        using Xunit;

        public class Cases
        {
            [Fact]
            public void DecoratedTest()
            {
            }

            public void PlainMethod()
            {
            }
        }
        """;

    [Test]
    public async Task FrameworkName_Probe_NamesTheFramework() =>
        _ = await Assert.That(XunitV2TestFrameworkProbe.Instance.FrameworkName).IsEqualTo(FrameworkName);

    [Test]
    public async Task TryCreateRecognizer_WithFrameworkReference_ReturnsARecognizer()
    {
        var compilation = CreateXunitV2Fixture();

        var recognizer = XunitV2TestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer?.FrameworkName).IsEqualTo(FrameworkName);
    }

    /// <summary>
    /// The assembly reference alone is enough to accept a compilation, because a reference that cannot be
    /// bound to a symbol must never switch the whole analysis off silently. Judging then fails closed: the
    /// recogniser exists but finds nothing.
    /// </summary>
    [Test]
    public async Task TryCreateRecognizer_ReferencingTheFrameworkAssemblyWithoutTheType_ReturnsARecognizer()
    {
        var compilation = CreateSatelliteConsumer(PlainSource, FrameworkAssemblyName);

        var recognizer = XunitV2TestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(XunitV2TestFrameworkProbe.GetTestAttributeType(compilation)).IsNull();
        _ = await Assert.That(recognizer).IsNotNull();
    }

    [Test]
    public async Task TryCreateRecognizer_WithoutAnyFrameworkTrace_ReturnsNull()
    {
        var compilation = CompilationFactory.Create(PlainSource);

        var recognizer = XunitV2TestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer).IsNull();
    }

    /// <summary>
    /// A compilation of a different test framework must shut this analysis down completely, which is the
    /// only thing that keeps the framework analyzers harmless next to each other.
    /// </summary>
    /// <param name="framework">The foreign framework the compilation references.</param>
    [Test]
    [Arguments(TUnitScenario)]
    [Arguments(NUnitScenario)]
    [Arguments(MSTestScenario)]
    public async Task TryCreateRecognizer_ReferencingADifferentFramework_ReturnsNull(string framework)
    {
        var compilation = CompilationFactory.Create(PlainSource, ToFramework(framework), filePath: "Cases.cs");

        var recognizer = XunitV2TestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer).IsNull();
    }

    /// <summary>
    /// A compilation carrying only the other major version of the same framework is a foreign compilation
    /// for this probe, and the assembly declaring the attribute is the only thing saying so. The v3
    /// assembly name is modelled by a satellite here, so that the expectation holds on every target
    /// framework rather than only on those the xUnit v3 package ships assets for.
    /// </summary>
    [Test]
    public async Task TryCreateRecognizer_ReferencingOnlyXunitV3_ReturnsNull()
    {
        var compilation = CreateSatelliteConsumer(CasesSource, XunitV3AssemblyName);

        var recognizer = XunitV2TestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer).IsNull();
    }

    [Test]
    public async Task TryCreateRecognizer_ReferencingOnlyAForeignAssembly_ReturnsNull()
    {
        var compilation = CreateSatelliteConsumer(CasesSource, ForeignAssemblyName);

        var recognizer = XunitV2TestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer).IsNull();
    }

    [Test]
    public async Task TryCreateRecognizer_CompilationIsNull_ThrowsArgumentNullException()
    {
        var threw = ThrowsArgumentNull(() => _ = XunitV2TestFrameworkProbe.Instance.TryCreateRecognizer(null!));

        _ = await Assert.That(threw).IsTrue();
    }

    /// <summary>
    /// A recogniser created without the well-known attribute type finds no test at all instead of guessing
    /// from a simple name, and it does so without throwing: the compilation whose tests cannot be seen is
    /// the one that must not be judged.
    /// </summary>
    [Test]
    public async Task IsTestMethod_RecognizerWithoutTheWellKnownType_FindsNoTest()
    {
        var compilation = CreateSatelliteConsumer(CasesSource, FrameworkAssemblyName);
        var recognizer = XunitV2TestFrameworkProbe.Instance.TryCreateRecognizer(compilation)!;

        var decorated = FindMethod(compilation, "DecoratedTest");
        var plain = FindMethod(compilation, "PlainMethod");

        _ = await Assert.That(XunitV2TestFrameworkProbe.GetTestAttributeType(compilation)).IsNull();
        _ = await Assert.That(recognizer.IsTestMethod(decorated)).IsFalse();
        _ = await Assert.That(recognizer.IsTestMethod(plain)).IsFalse();
    }

    [Test]
    public async Task IsTestMethod_MethodIsNull_ThrowsArgumentNullException()
    {
        var compilation = CreateXunitV2Fixture();
        var recognizer = XunitV2TestFrameworkProbe.Instance.TryCreateRecognizer(compilation)!;

        var threw = ThrowsArgumentNull(() => _ = recognizer.IsTestMethod(null!));

        _ = await Assert.That(threw).IsTrue();
    }

    /// <summary>
    /// The assembly rule is what separates the two major versions, so it has to say yes to
    /// <c>xunit.core</c> and no to everything else - to the v3 assembly whose name merely starts the same
    /// way, to an unrelated dependency, and to no assembly at all.
    /// </summary>
    [Test]
    public async Task IsFrameworkAssembly_AssemblyName_IsClassifiedByItsName()
    {
        var framework = SatelliteAssembly(
            CreateSatelliteConsumer(PlainSource, FrameworkAssemblyName),
            FrameworkAssemblyName
        );
        var xunitV3 = SatelliteAssembly(CreateSatelliteConsumer(PlainSource, XunitV3AssemblyName), XunitV3AssemblyName);
        var foreign = SatelliteAssembly(CreateSatelliteConsumer(PlainSource, ForeignAssemblyName), ForeignAssemblyName);

        _ = await Assert.That(XunitV2TestFrameworkProbe.IsFrameworkAssembly(framework)).IsTrue();
        _ = await Assert.That(XunitV2TestFrameworkProbe.IsFrameworkAssembly(xunitV3)).IsFalse();
        _ = await Assert.That(XunitV2TestFrameworkProbe.IsFrameworkAssembly(foreign)).IsFalse();
    }

    [Test]
    public async Task IsFrameworkAssembly_AssemblyIsNull_ReturnsFalse() =>
        _ = await Assert.That(XunitV2TestFrameworkProbe.IsFrameworkAssembly(null)).IsFalse();

    [Test]
    public async Task FindFrameworkAssembly_WithFrameworkReference_FindsTheDeclaringAssembly()
    {
        var assembly = XunitV2TestFrameworkProbe.FindFrameworkAssembly(CreateXunitV2Fixture());

        _ = await Assert.That(assembly?.Name).IsEqualTo(FrameworkAssemblyName);
    }

    [Test]
    public async Task FindFrameworkAssembly_WithoutTheFramework_ReturnsNull()
    {
        var compilation = CompilationFactory.Create(PlainSource);

        _ = await Assert.That(XunitV2TestFrameworkProbe.FindFrameworkAssembly(compilation)).IsNull();
    }

    /// <summary>
    /// The attribute type is resolved inside the assembly that declares it, so the result carries both the
    /// expected metadata name and the expected origin. The origin is the part that matters: the metadata
    /// name alone cannot tell the two major versions apart.
    /// </summary>
    [Test]
    public async Task GetTestAttributeType_WithFrameworkReference_ResolvesTheAttributeOfVersionTwo()
    {
        var resolved = XunitV2TestFrameworkProbe.GetTestAttributeType(CreateXunitV2Fixture());

        _ = await Assert.That(resolved?.ToDisplayString()).IsEqualTo("Xunit.FactAttribute");
        _ = await Assert.That(resolved?.ContainingAssembly.Name).IsEqualTo(FrameworkAssemblyName);
    }

    [Test]
    public async Task GetTestAttributeType_WithoutTheFramework_ReturnsNull()
    {
        var compilation = CompilationFactory.Create(PlainSource);

        _ = await Assert.That(XunitV2TestFrameworkProbe.GetTestAttributeType(compilation)).IsNull();
    }

    /// <summary>
    /// A satellite named like the framework assembly is accepted as a reference but declares no
    /// <c>Xunit.FactAttribute</c>, so resolving the type inside it yields nothing - proving the type is
    /// really taken from that one assembly and not from the compilation as a whole.
    /// </summary>
    [Test]
    public async Task GetTestAttributeType_FrameworkAssemblyWithoutTheType_ReturnsNull()
    {
        var compilation = CreateSatelliteConsumer(CasesSource, FrameworkAssemblyName);

        _ = await Assert.That(XunitV2TestFrameworkProbe.FindFrameworkAssembly(compilation)).IsNotNull();
        _ = await Assert.That(XunitV2TestFrameworkProbe.GetTestAttributeType(compilation)).IsNull();
    }

    [Test]
    public async Task Fixtures_EveryCompilation_CompilesWithoutErrors()
    {
        var errors = new[]
        {
            Describe(CreateXunitV2Fixture()),
            Describe(CompilationFactory.Create(PlainSource)),
            Describe(CreateSatelliteConsumer(CasesSource, FrameworkAssemblyName)),
            Describe(CreateSatelliteConsumer(CasesSource, XunitV3AssemblyName)),
            Describe(CreateSatelliteConsumer(CasesSource, ForeignAssemblyName)),
        };

        _ = await Assert
            .That(string.Join(" / ", errors.Distinct(StringComparer.Ordinal)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

#if FRAMESHIFT_XUNIT_V3
    /// <summary>
    /// Referencing both major versions at once declares <c>Xunit.FactAttribute</c> twice, so
    /// <see cref="Compilation.GetTypeByMetadataName(string)" /> answers <see langword="null" /> for that
    /// name - ambiguity, not absence. Resolving the type inside <c>xunit.core</c> is exact regardless, and
    /// that is the whole point of probing the two versions separately: the v2 probe stays awake and hands
    /// out a recogniser bound to the v2 attribute, never to the v3 one.
    /// </summary>
    /// <remarks>
    /// This is the only expectation of this class that needs the xUnit v3 package and is therefore the only
    /// one compiled conditionally; net6.0 and net7.0 have no v3 assets, where the name is unambiguous
    /// anyway. The fixture cannot spell <c>Xunit.FactAttribute</c> out, because the reference to it would
    /// not compile - which is exactly the condition being covered.
    /// </remarks>
    [Test]
    public async Task GetTestAttributeType_ReferencingBothVersions_ResolvesTheAttributeOfVersionTwo()
    {
        var compilation = CompilationFactory.Create(PlainSource, TestFramework.All, filePath: "Cases.cs");
        var resolved = XunitV2TestFrameworkProbe.GetTestAttributeType(compilation);

        _ = await Assert.That(Describe(compilation)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        _ = await Assert.That(compilation.GetTypeByMetadataName("Xunit.FactAttribute")).IsNull();
        _ = await Assert.That(compilation.GetTypesByMetadataName("Xunit.FactAttribute").Length).IsEqualTo(2);
        _ = await Assert.That(resolved?.ToDisplayString()).IsEqualTo("Xunit.FactAttribute");
        _ = await Assert.That(resolved?.ContainingAssembly.Name).IsEqualTo(FrameworkAssemblyName);
        _ = await Assert
            .That(XunitV2TestFrameworkProbe.Instance.TryCreateRecognizer(compilation)?.FrameworkName)
            .IsEqualTo(FrameworkName);
    }
#endif

    private static CSharpCompilation CreateXunitV2Fixture() =>
        CompilationFactory.Create(XunitSource, TestFramework.XunitV2, filePath: "Cases.cs");

    private static TestFramework ToFramework(string scenario) =>
        scenario switch
        {
            TUnitScenario => TestFramework.TUnit,
            NUnitScenario => TestFramework.NUnit,
            MSTestScenario => TestFramework.MSTest,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario."),
        };

    private static CSharpCompilation CreateSatelliteConsumer(string source, string satelliteAssemblyName)
    {
        var satellite = CompilationFactory.Create(SatelliteSource, satelliteAssemblyName, filePath: "Satellite.cs");

        return CompilationFactory.Create(
            source,
            additionalReferences: [satellite.ToMetadataReference()],
            filePath: "Cases.cs"
        );
    }

    /// <summary>
    /// Resolves the referenced satellite assembly of <paramref name="compilation" /> by its name.
    /// </summary>
    /// <param name="compilation">The compilation referencing the satellite.</param>
    /// <param name="assemblyName">The name of the satellite assembly.</param>
    /// <returns>The resolved assembly symbol.</returns>
    private static IAssemblySymbol SatelliteAssembly(Compilation compilation, string assemblyName) =>
        compilation
            .References.Select(reference => compilation.GetAssemblyOrModuleSymbol(reference))
            .OfType<IAssemblySymbol>()
            .First(assembly => string.Equals(assembly.Name, assemblyName, StringComparison.Ordinal));

    private static IMethodSymbol FindMethod(Compilation compilation, string methodName) =>
        compilation.GetTypeByMetadataName("Fixture.Cases")!.GetMembers(methodName).OfType<IMethodSymbol>().First();

    private static string Describe(Compilation compilation) =>
        DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation));

    private static bool ThrowsArgumentNull(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentNullException)
        {
            return true;
        }

        return false;
    }
}
