namespace NetEvolve.Frameshift.Tests.Unit;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.Frameshift.Tests.Infrastructure;
using NetEvolve.Frameshift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the seam xUnit is plugged in through. The probe decides whether the test-side analysis runs at
/// all, so reporting absence correctly matters more than recognising presence: a probe that claims a
/// compilation it does not understand would make the analysis judge invisible tests.
/// </summary>
/// <remarks>
/// Both major versions of the framework are exercised on their own, because they ship the very same type
/// names from differently named assemblies and only the assembly rule tells them apart.
/// </remarks>
public class XunitTestFrameworkProbeTests
{
    private const string FrameworkName = "xUnit";

    private const string FrameworkLikeAssemblyName = "xunit.satellite";
    private const string ForeignAssemblyName = "Foreign.Satellite";

    private const string XunitV2Scenario = "xUnit v2";
    private const string XunitV3Scenario = "xUnit v3";

    private const string TUnitScenario = "TUnit";
    private const string NUnitScenario = "NUnit";

    /// <summary>
    /// A satellite that carries the simple name of the xUnit test attribute in a namespace of its own, so
    /// that the well-known metadata name stays unresolvable and only the name rule can recognise it.
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
        _ = await Assert.That(XunitTestFrameworkProbe.Instance.FrameworkName).IsEqualTo(FrameworkName);

    /// <summary>
    /// Both shipped versions have to be detected by the single probe, each one on its own.
    /// </summary>
    /// <param name="version">The version of the framework the compilation references.</param>
    [Test]
    [Arguments(XunitV2Scenario)]
    [Arguments(XunitV3Scenario)]
    public async Task TryCreateRecognizer_WithFrameworkReference_ReturnsARecognizer(string version)
    {
        var compilation = CreateXunitFixture(version);

        var recognizer = XunitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer?.FrameworkName).IsEqualTo(FrameworkName);
    }

    /// <summary>
    /// The framework prefix alone is enough to accept a compilation, because a specialised test attribute
    /// may live in a framework assembly that does not carry the well-known attribute type itself.
    /// </summary>
    [Test]
    public async Task TryCreateRecognizer_ReferencingAFrameworkLikeAssembly_ReturnsARecognizer()
    {
        var compilation = CreateSatelliteConsumer(PlainSource, FrameworkLikeAssemblyName);

        var recognizer = XunitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer).IsNotNull();
    }

    [Test]
    public async Task TryCreateRecognizer_WithoutAnyFrameworkTrace_ReturnsNull()
    {
        var compilation = CompilationFactory.Create(PlainSource);

        var recognizer = XunitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer).IsNull();
    }

    /// <summary>
    /// A compilation of a different test framework must shut the analysis down completely, which is the
    /// only thing that keeps the framework analyzers harmless next to each other.
    /// </summary>
    /// <param name="framework">The foreign framework the compilation references.</param>
    [Test]
    [Arguments(TUnitScenario)]
    [Arguments(NUnitScenario)]
    public async Task TryCreateRecognizer_ReferencingADifferentFramework_ReturnsNull(string framework)
    {
        var compilation = CreateForeignFrameworkFixture(framework);

        var recognizer = XunitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer).IsNull();
    }

    [Test]
    public async Task TryCreateRecognizer_ReferencingOnlyAForeignAssembly_ReturnsNull()
    {
        var compilation = CreateSatelliteConsumer(CasesSource, ForeignAssemblyName);

        var recognizer = XunitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer).IsNull();
    }

    [Test]
    public async Task TryCreateRecognizer_CompilationIsNull_ThrowsArgumentNullException()
    {
        var threw = ThrowsArgumentNull(() => _ = XunitTestFrameworkProbe.Instance.TryCreateRecognizer(null!));

        _ = await Assert.That(threw).IsTrue();
    }

    /// <summary>
    /// A recogniser that was created without the well-known attribute type still has to recognise a test
    /// attribute declared in a framework assembly, which is the only rule available in that state.
    /// </summary>
    [Test]
    public async Task IsTestMethod_RecognizerWithoutTheWellKnownType_UsesTheNameRule()
    {
        var compilation = CreateSatelliteConsumer(CasesSource, FrameworkLikeAssemblyName);
        var recognizer = XunitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation)!;

        var decorated = FindMethod(compilation, "DecoratedTest");
        var plain = FindMethod(compilation, "PlainMethod");

        _ = await Assert.That(XunitTestFrameworkProbe.GetTestAttributeType(compilation)).IsNull();
        _ = await Assert.That(recognizer.IsTestMethod(decorated)).IsTrue();
        _ = await Assert.That(recognizer.IsTestMethod(plain)).IsFalse();
    }

    [Test]
    public async Task IsTestMethod_MethodIsNull_ThrowsArgumentNullException()
    {
        var compilation = CreateXunitFixture(XunitV3Scenario);
        var recognizer = XunitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation)!;

        var threw = ThrowsArgumentNull(() => _ = recognizer.IsTestMethod(null!));

        _ = await Assert.That(threw).IsTrue();
    }

    /// <summary>
    /// The name rule is what recognises a specialised test attribute of the framework, so it has to say
    /// yes to the framework assemblies and no to everything else, including to no assembly at all.
    /// </summary>
    [Test]
    public async Task IsFrameworkAssembly_AssemblyName_IsClassifiedByItsPrefix()
    {
        var framework = CreateSatelliteConsumer(PlainSource, FrameworkLikeAssemblyName);
        var foreign = CreateSatelliteConsumer(PlainSource, ForeignAssemblyName);

        var frameworkAssembly = SatelliteAssembly(framework, FrameworkLikeAssemblyName);
        var foreignAssembly = SatelliteAssembly(foreign, ForeignAssemblyName);

        _ = await Assert.That(XunitTestFrameworkProbe.IsFrameworkAssembly(frameworkAssembly)).IsTrue();
        _ = await Assert.That(XunitTestFrameworkProbe.IsFrameworkAssembly(foreignAssembly)).IsFalse();
    }

    [Test]
    public async Task IsFrameworkAssembly_AssemblyIsNull_ReturnsFalse() =>
        _ = await Assert.That(XunitTestFrameworkProbe.IsFrameworkAssembly(null)).IsFalse();

    [Test]
    [Arguments(XunitV2Scenario)]
    [Arguments(XunitV3Scenario)]
    public async Task GetTestAttributeType_WithFrameworkReference_ResolvesTheAttribute(string version)
    {
        var resolved = XunitTestFrameworkProbe.GetTestAttributeType(CreateXunitFixture(version));

        _ = await Assert.That(resolved?.ToDisplayString()).IsEqualTo("Xunit.FactAttribute");
    }

    [Test]
    public async Task GetTestAttributeType_WithoutTheFramework_ReturnsNull()
    {
        var compilation = CompilationFactory.Create(PlainSource);

        _ = await Assert.That(XunitTestFrameworkProbe.GetTestAttributeType(compilation)).IsNull();
    }

    /// <summary>
    /// Referencing both major versions at once declares <c>Xunit.FactAttribute</c> twice, so the well-known
    /// type cannot be resolved by its metadata name any more. That must not be mistaken for the absence of
    /// the framework: the probe stays awake on the assembly rule and hands out the name-based recogniser.
    /// </summary>
    [Test]
    public async Task TryCreateRecognizer_WithAnAmbiguousAttributeName_StillReturnsARecognizer()
    {
        var compilation = CompilationFactory.Create(PlainSource, TestFramework.All);

        _ = await Assert.That(XunitTestFrameworkProbe.GetTestAttributeType(compilation)).IsNull();
        _ = await Assert.That(XunitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation)).IsNotNull();
    }

    [Test]
    public async Task Fixtures_EveryCompilation_CompilesWithoutErrors()
    {
        var errors = new[]
        {
            Describe(CreateXunitFixture(XunitV2Scenario)),
            Describe(CreateXunitFixture(XunitV3Scenario)),
            Describe(CompilationFactory.Create(PlainSource)),
            Describe(CreateSatelliteConsumer(CasesSource, FrameworkLikeAssemblyName)),
            Describe(CreateSatelliteConsumer(CasesSource, ForeignAssemblyName)),
        };

        _ = await Assert
            .That(string.Join(" / ", errors.Distinct(StringComparer.Ordinal)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    private static CSharpCompilation CreateXunitFixture(string version) =>
        CompilationFactory.Create(XunitSource, ToFramework(version), filePath: "Cases.cs");

    private static CSharpCompilation CreateForeignFrameworkFixture(string framework) =>
        CompilationFactory.Create(PlainSource, ToFramework(framework), filePath: "Cases.cs");

    private static TestFramework ToFramework(string scenario) =>
        scenario switch
        {
            XunitV2Scenario => TestFramework.XunitV2,
            XunitV3Scenario => TestFramework.XunitV3,
            TUnitScenario => TestFramework.TUnit,
            NUnitScenario => TestFramework.NUnit,
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
