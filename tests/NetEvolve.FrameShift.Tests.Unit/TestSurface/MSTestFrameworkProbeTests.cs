namespace NetEvolve.FrameShift.Tests.Unit.TestSurface;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the MSTest end of the framework seam. The probe decides whether the test-side analysis runs at
/// all, and refusing a genuine MSTest project would silently switch FrameShift off for it, so each of the
/// two traces it accepts is exercised on its own.
/// </summary>
/// <remarks>
/// Exactly as in the TUnit, xUnit and NUnit probes, either trace is enough: the well-known attribute type
/// resolves, <em>or</em> a framework assembly is referenced. Whichever trace is missing, the recogniser
/// that comes back is still safe, because judging a method needs positive evidence of its own — that is
/// the fail-open / fail-closed split documented on <c>ITestFrameworkProbe</c>, and these tests pin both
/// halves of it.
/// </remarks>
public class MSTestFrameworkProbeTests
{
    private const string FrameworkAssemblyName = "Microsoft.VisualStudio.TestPlatform.TestFramework.Satellite";
    private const string DifferentlyCasedAssemblyName = "microsoft.visualstudio.testplatform.testframework.ext";
    private const string ForeignAssemblyName = "Foreign.Satellite";
    private const string LegacyQualityToolsAssemblyName = "Microsoft.VisualStudio.QualityTools.UnitTestFramework";

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

    /// <summary>
    /// A second file of the satellite, deriving from the attribute the satellite itself declares. The
    /// satellite does not reference MSTest, so nothing is ambiguous while it is compiled — the ambiguity
    /// only arises in a consumer that references the satellite <em>and</em> the real package.
    /// </summary>
    private const string AmbiguousSatelliteDerivedSource = """
        namespace Satellite;

        using Microsoft.VisualStudio.TestTools.UnitTesting;

        public class SatelliteTestMethodAttribute : TestMethodAttribute
        {
        }
        """;

    /// <summary>
    /// A consumer of the ambiguous pair. It names only the satellite's derived attribute, because spelling
    /// out the doubly declared <c>TestMethodAttribute</c> would not compile.
    /// </summary>
    private const string AmbiguousConsumerSource = """
        namespace Fixture;

        public class Cases
        {
            [Satellite.SatelliteTestMethod]
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(recognizer.IsTestMethod(FindMethod(compilation, DecoratedTestName))).IsTrue();
            _ = await Assert.That(recognizer.IsTestMethod(FindMethod(compilation, PlainMethodName))).IsFalse();
        }
    }

    [Test]
    public async Task TryCreateRecognizer_WithoutAnyFrameworkTrace_ReturnsNull()
    {
        var compilation = CompilationFactory.Create(PlainSource);

        var recognizer = MSTestTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer).IsNull();
    }

    /// <summary>
    /// The resolved attribute type on its own is a trace of the framework, even without a framework
    /// assembly: a type carrying the framework's exact full name in the framework's own namespace is the
    /// framework's identity, and taking it at face value is what the other three probes do too. The tests
    /// of such a project are therefore recognised rather than the analysis being shut down.
    /// </summary>
    [Test]
    public async Task TryCreateRecognizer_AttributeTypeWithoutTheFrameworkAssembly_RecognisesItsTests()
    {
        var compilation = CreateSatelliteConsumer(
            LookAlikeConsumerSource,
            LookAlikeSatelliteSource,
            ForeignAssemblyName
        );

        var recognizer = MSTestTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        using (Assert.Multiple())
        {
            _ = await Assert.That(recognizer).IsNotNull();
            _ = await Assert.That(recognizer!.IsTestMethod(FindMethod(compilation, DecoratedTestName))).IsTrue();
            _ = await Assert.That(recognizer.IsTestMethod(FindMethod(compilation, PlainMethodName))).IsFalse();
        }
    }

    /// <summary>
    /// A framework assembly without a resolvable attribute type is a trace as well, and the probe must not
    /// read the missing type as absence — the type is also missing when its name is ambiguous. What comes
    /// back is a recogniser that has only the name-based rule left and consequently finds no tests, which
    /// is indistinguishable from a test project without tests and therefore harmless.
    /// </summary>
    [Test]
    public async Task TryCreateRecognizer_FrameworkAssemblyWithoutTheAttributeType_FindsNoTests()
    {
        var compilation = CreateSatelliteConsumer(PlainSource, MarkerSatelliteSource, FrameworkAssemblyName);

        var recognizer = MSTestTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        using (Assert.Multiple())
        {
            _ = await Assert.That(MSTestTestFrameworkProbe.GetTestAttributeType(compilation)).IsNull();
            _ = await Assert.That(recognizer?.FrameworkName).IsEqualTo("MSTest");
            _ = await Assert.That(recognizer!.IsTestMethod(FindMethod(compilation, PlainMethodName))).IsFalse();
        }
    }

    /// <summary>
    /// Declaring the well-known attribute twice — here the real package alongside a framework-named
    /// assembly of its own — makes
    /// <see cref="Compilation.GetTypeByMetadataName(string)" /> answer <see langword="null" /> although
    /// MSTest is plainly there. That must not be mistaken for absence: the probe stays awake on the
    /// assembly rule, and the recogniser it hands out still finds the tests through the name-based rule.
    /// </summary>
    [Test]
    public async Task TryCreateRecognizer_WithAnAmbiguousAttributeType_StillRecognisesTests()
    {
        var compilation = CreateAmbiguousConsumer();

        var recognizer = MSTestTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        using (Assert.Multiple())
        {
            _ = await Assert.That(MSTestTestFrameworkProbe.GetTestAttributeType(compilation)).IsNull();
            _ = await Assert.That(recognizer).IsNotNull();
            _ = await Assert.That(recognizer!.IsTestMethod(FindMethod(compilation, DecoratedTestName))).IsTrue();
            _ = await Assert.That(recognizer.IsTestMethod(FindMethod(compilation, PlainMethodName))).IsFalse();
        }
    }

    /// <summary>
    /// The legacy, pre-MSTestV2, in-box Visual Studio unit test framework assembly is a framework trace as
    /// well: it declares the identical well-known attribute type, so a compilation that references only it
    /// (and neither <c>MSTest.TestFramework</c> nor <c>Microsoft.VisualStudio.TestPlatform.TestFramework</c>)
    /// must still be recognised through the assembly-name fallback.
    /// </summary>
    [Test]
    public async Task TryCreateRecognizer_LegacyQualityToolsAssemblyWithoutTheAttributeType_FindsNoTests()
    {
        var compilation = CreateSatelliteConsumer(PlainSource, MarkerSatelliteSource, LegacyQualityToolsAssemblyName);

        var recognizer = MSTestTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        using (Assert.Multiple())
        {
            _ = await Assert.That(MSTestTestFrameworkProbe.GetTestAttributeType(compilation)).IsNull();
            _ = await Assert.That(recognizer?.FrameworkName).IsEqualTo("MSTest");
            _ = await Assert.That(recognizer!.IsTestMethod(FindMethod(compilation, PlainMethodName))).IsFalse();
        }
    }

    /// <summary>
    /// The legacy assembly name is also classified positively by <see cref="MSTestTestFrameworkProbe.IsFrameworkAssembly" />.
    /// </summary>
    [Test]
    public async Task IsFrameworkAssembly_LegacyQualityToolsAssemblyName_IsClassifiedAsTheFramework()
    {
        var consumer = CreateSatelliteConsumer(PlainSource, MarkerSatelliteSource, LegacyQualityToolsAssemblyName);

        var assembly = SatelliteAssembly(consumer, LegacyQualityToolsAssemblyName);

        _ = await Assert.That(MSTestTestFrameworkProbe.IsFrameworkAssembly(assembly)).IsTrue();
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(MSTestTestFrameworkProbe.IsFrameworkAssembly(framework)).IsTrue();
            _ = await Assert.That(MSTestTestFrameworkProbe.IsFrameworkAssembly(foreign)).IsFalse();
        }
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
            Describe(CreateSatelliteConsumer(PlainSource, MarkerSatelliteSource, LegacyQualityToolsAssemblyName)),
            Describe(CreateAmbiguousConsumer()),
        };

        _ = await Assert
            .That(string.Join(" / ", errors.Distinct(StringComparer.Ordinal)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    private static CSharpCompilation CreateMSTest() =>
        CompilationFactory.Create(MSTestSource, TestFramework.MSTest, filePath: "Cases.cs");

    /// <summary>
    /// Builds a compilation in which
    /// <c>Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute</c> is declared twice: once by
    /// the real MSTest package and once by a satellite carrying a framework-like assembly name, which is
    /// what makes the well-known name unresolvable while MSTest is plainly referenced.
    /// </summary>
    /// <returns>The consuming compilation.</returns>
    private static CSharpCompilation CreateAmbiguousConsumer()
    {
        var satellite = CompilationFactory.Create(
            [("Satellite.cs", LookAlikeSatelliteSource), ("SatelliteDerived.cs", AmbiguousSatelliteDerivedSource)],
            FrameworkAssemblyName
        );

        return CompilationFactory.Create(
            AmbiguousConsumerSource,
            TestFramework.MSTest,
            additionalReferences: [satellite.ToMetadataReference()],
            filePath: "Cases.cs"
        );
    }

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
