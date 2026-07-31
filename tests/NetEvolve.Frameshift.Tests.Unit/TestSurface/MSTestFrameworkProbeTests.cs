namespace NetEvolve.Frameshift.Tests.Unit;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.Frameshift.Tests.Infrastructure;
using NetEvolve.Frameshift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the MSTest end of the framework seam. The probe decides whether the test-side analysis runs at
/// all, so both halves of its guard matter: claiming a compilation that merely looks like MSTest would
/// make the analysis judge invisible tests, while refusing a genuine MSTest project would silently
/// switch Frameshift off for it.
/// </summary>
/// <remarks>
/// Unlike the other probes, MSTest requires the well-known attribute type <em>and</em> a referenced
/// framework assembly, because <c>Microsoft.VisualStudio.TestTools.UnitTesting</c> is an ordinary
/// namespace any project may declare a look-alike attribute in. Both halves are therefore exercised on
/// their own.
/// </remarks>
public class MSTestFrameworkProbeTests
{
    private const string FrameworkAssemblyName = "Microsoft.VisualStudio.TestPlatform.TestFramework.Satellite";
    private const string DifferentlyCasedAssemblyName = "microsoft.visualstudio.testplatform.testframework.ext";
    private const string ForeignAssemblyName = "Foreign.Satellite";

    private const string DecoratedTestName = "DecoratedTest";
    private const string PlainMethodName = "PlainMethod";

    /// <summary>
    /// A satellite that declares the well-known MSTest attribute by its exact metadata name without being
    /// MSTest, which is what a hand-written look-alike does.
    /// </summary>
    private const string LookAlikeSatelliteSource = """
        namespace Microsoft.VisualStudio.TestTools.UnitTesting;

        using System;

        [AttributeUsage(AttributeTargets.Method)]
        public class TestMethodAttribute : Attribute
        {
        }
        """;

    /// <summary>
    /// A satellite that carries a framework-like assembly name while declaring nothing a test could be
    /// recognised by.
    /// </summary>
    private const string MarkerSatelliteSource = """
        namespace Satellite;

        public static class Marker
        {
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

    private const string LookAlikeConsumerSource = """
        namespace Fixture;

        using Microsoft.VisualStudio.TestTools.UnitTesting;

        public class Cases
        {
            [TestMethod]
            public void DecoratedTest()
            {
            }

            public void PlainMethod()
            {
            }
        }
        """;

    private const string MSTestSource = """
        namespace Fixture;

        using Microsoft.VisualStudio.TestTools.UnitTesting;

        [TestClass]
        public class Cases
        {
            [TestMethod]
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
        _ = await Assert.That(MSTestTestFrameworkProbe.Instance.FrameworkName).IsEqualTo("MSTest");

    [Test]
    public async Task TryCreateRecognizer_WithFrameworkReference_ReturnsARecognizer()
    {
        var compilation = CreateMSTest();

        var recognizer = MSTestTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer?.FrameworkName).IsEqualTo("MSTest");
    }

    [Test]
    public async Task TryCreateRecognizer_WithFrameworkReference_RecognisesOnlyTheDecoratedMethod()
    {
        var compilation = CreateMSTest();
        var recognizer = MSTestTestFrameworkProbe.Instance.TryCreateRecognizer(compilation)!;

        _ = await Assert.That(recognizer.IsTestMethod(FindMethod(compilation, DecoratedTestName))).IsTrue();
        _ = await Assert.That(recognizer.IsTestMethod(FindMethod(compilation, PlainMethodName))).IsFalse();
    }

    [Test]
    public async Task TryCreateRecognizer_WithoutAnyFrameworkTrace_ReturnsNull()
    {
        var compilation = CompilationFactory.Create(PlainSource);

        var recognizer = MSTestTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer).IsNull();
    }

    /// <summary>
    /// The attribute type on its own is not enough. A project that declares
    /// <c>Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute</c> itself is not an MSTest
    /// project, and treating it as one would let the analysis judge tests it does not understand.
    /// </summary>
    [Test]
    public async Task TryCreateRecognizer_AttributeLookAlikeWithoutTheFrameworkAssembly_ReturnsNull()
    {
        var compilation = CreateSatelliteConsumer(
            LookAlikeConsumerSource,
            LookAlikeSatelliteSource,
            ForeignAssemblyName
        );

        var recognizer = MSTestTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer).IsNull();
    }

    /// <summary>
    /// The framework assembly on its own is not enough either: without the attribute type nothing could
    /// ever be recognised as a test, so the probe reports absence instead of handing out a blind
    /// recogniser.
    /// </summary>
    [Test]
    public async Task TryCreateRecognizer_FrameworkAssemblyWithoutTheAttributeType_ReturnsNull()
    {
        var compilation = CreateSatelliteConsumer(PlainSource, MarkerSatelliteSource, FrameworkAssemblyName);

        var recognizer = MSTestTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer).IsNull();
    }

    [Test]
    public async Task TryCreateRecognizer_CompilationIsNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            MSTestTestFrameworkProbe.Instance.TryCreateRecognizer(null!)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("compilation");
    }

    [Test]
    public async Task IsFrameworkAssembly_AssemblyName_IsClassifiedByItsPrefix()
    {
        var frameworkConsumer = CreateSatelliteConsumer(PlainSource, MarkerSatelliteSource, FrameworkAssemblyName);
        var foreignConsumer = CreateSatelliteConsumer(PlainSource, MarkerSatelliteSource, ForeignAssemblyName);

        var framework = SatelliteAssembly(frameworkConsumer, FrameworkAssemblyName);
        var foreign = SatelliteAssembly(foreignConsumer, ForeignAssemblyName);

        _ = await Assert.That(MSTestTestFrameworkProbe.IsFrameworkAssembly(framework)).IsTrue();
        _ = await Assert.That(MSTestTestFrameworkProbe.IsFrameworkAssembly(foreign)).IsFalse();
    }

    /// <summary>
    /// Assembly identities are not case-sensitive, and a false negative here means silently analysing
    /// nothing at all, so the prefix is matched without regard to casing.
    /// </summary>
    [Test]
    public async Task IsFrameworkAssembly_DifferentlyCasedAssemblyName_IsStillTheFramework()
    {
        var consumer = CreateSatelliteConsumer(PlainSource, MarkerSatelliteSource, DifferentlyCasedAssemblyName);

        var assembly = SatelliteAssembly(consumer, DifferentlyCasedAssemblyName);

        _ = await Assert.That(MSTestTestFrameworkProbe.IsFrameworkAssembly(assembly)).IsTrue();
    }

    [Test]
    public async Task IsFrameworkAssembly_AssemblyIsNull_ReturnsFalse() =>
        _ = await Assert.That(MSTestTestFrameworkProbe.IsFrameworkAssembly(null)).IsFalse();

    [Test]
    public async Task GetTestAttributeType_WithTheFramework_ResolvesTheAttribute()
    {
        var type = MSTestTestFrameworkProbe.GetTestAttributeType(CreateMSTest());

        _ = await Assert.That(type?.Name).IsEqualTo("TestMethodAttribute");
    }

    [Test]
    public async Task GetTestAttributeType_WithoutTheFramework_ReturnsNull()
    {
        var compilation = CompilationFactory.Create(PlainSource);

        _ = await Assert.That(MSTestTestFrameworkProbe.GetTestAttributeType(compilation)).IsNull();
    }

    [Test]
    public async Task Fixtures_EveryCompilation_CompilesWithoutErrors()
    {
        var errors = new[]
        {
            Describe(CreateMSTest()),
            Describe(CompilationFactory.Create(PlainSource)),
            Describe(CreateSatelliteConsumer(LookAlikeConsumerSource, LookAlikeSatelliteSource, ForeignAssemblyName)),
            Describe(CreateSatelliteConsumer(PlainSource, MarkerSatelliteSource, FrameworkAssemblyName)),
            Describe(CreateSatelliteConsumer(PlainSource, MarkerSatelliteSource, DifferentlyCasedAssemblyName)),
        };

        _ = await Assert
            .That(string.Join(" / ", errors.Distinct(StringComparer.Ordinal)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    private static CSharpCompilation CreateMSTest() =>
        CompilationFactory.Create(MSTestSource, TestFramework.MSTest, filePath: "Cases.cs");

    /// <summary>
    /// Compiles <paramref name="satelliteSource" /> into an assembly called
    /// <paramref name="satelliteAssemblyName" /> and builds a compilation of <paramref name="source" />
    /// that references it, which is how a fixture controls the assembly name a type is declared in.
    /// </summary>
    /// <param name="source">The source of the consuming compilation.</param>
    /// <param name="satelliteSource">The source of the referenced satellite.</param>
    /// <param name="satelliteAssemblyName">The assembly name of the satellite.</param>
    /// <returns>The consuming compilation.</returns>
    private static CSharpCompilation CreateSatelliteConsumer(
        string source,
        string satelliteSource,
        string satelliteAssemblyName
    )
    {
        var satellite = CompilationFactory.Create(satelliteSource, satelliteAssemblyName, filePath: "Satellite.cs");

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
}
