namespace NetEvolve.FrameShift.Tests.Unit.TestSurface;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the seam every test framework is plugged in through. The probe decides whether the test-side
/// analysis runs at all, so reporting absence correctly matters more than recognising presence: a probe
/// that claims a compilation it does not understand would make the analysis judge invisible tests.
/// </summary>
public class TUnitTestFrameworkProbeTests
{
    private const string FrameworkLikeAssemblyName = "TUnit.Satellite";
    private const string ForeignAssemblyName = "Foreign.Satellite";

    private const string SatelliteSource = """
        namespace Satellite;

        using System;

        [AttributeUsage(AttributeTargets.Method)]
        public class BaseTestAttribute : Attribute
        {
        }
        """;

    private const string CasesSource = """
        namespace Fixture;

        public class Cases
        {
            [Satellite.BaseTest]
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

    private const string TUnitSource = """
        namespace Fixture;

        using TUnit.Core;

        public class Cases
        {
            [Test]
            public void DecoratedTest()
            {
            }

            [DynamicTestBuilder]
            public void DynamicBuilderTest()
            {
            }

            [Category("Fast")]
            public void CategoryOnly()
            {
            }

            public void PlainMethod()
            {
            }
        }
        """;

    [Test]
    public async Task FrameworkName_Probe_NamesTheFramework() =>
        _ = await Assert.That(TUnitTestFrameworkProbe.Instance.FrameworkName).IsEqualTo("TUnit");

    [Test]
    public async Task TryCreateRecognizer_WithFrameworkReference_ReturnsARecognizer()
    {
        var compilation = CompilationFactory.Create(TUnitSource, includeTUnit: true);

        var recognizer = TUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer?.FrameworkName).IsEqualTo("TUnit");
    }

    /// <summary>
    /// The framework prefix alone is enough to accept a compilation, because a specialised test attribute
    /// may live in a framework assembly that does not carry the well-known attribute type itself.
    /// </summary>
    [Test]
    public async Task TryCreateRecognizer_ReferencingAFrameworkLikeAssembly_ReturnsARecognizer()
    {
        var compilation = CreateSatelliteConsumer(PlainSource, FrameworkLikeAssemblyName);

        var recognizer = TUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer).IsNotNull();
    }

    [Test]
    public async Task TryCreateRecognizer_WithoutAnyFrameworkTrace_ReturnsNull()
    {
        var compilation = CompilationFactory.Create(PlainSource);

        var recognizer = TUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer).IsNull();
    }

    [Test]
    public async Task TryCreateRecognizer_ReferencingOnlyAForeignAssembly_ReturnsNull()
    {
        var compilation = CreateSatelliteConsumer(CasesSource, ForeignAssemblyName);

        var recognizer = TUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer).IsNull();
    }

    [Test]
    public async Task TryCreateRecognizer_CompilationIsNull_ThrowsArgumentNullException()
    {
        var threw = false;

        try
        {
            _ = TUnitTestFrameworkProbe.Instance.TryCreateRecognizer(null!);
        }
        catch (ArgumentNullException)
        {
            threw = true;
        }

        _ = await Assert.That(threw).IsTrue();
    }

    /// <summary>
    /// The probe threads the marker base type into the recogniser, which is what makes the second marker of
    /// the framework a test. A method carrying only a configuration attribute stays no test.
    /// </summary>
    /// <param name="methodName">The method to classify.</param>
    /// <param name="expected">The expected classification.</param>
    [Test]
    [Arguments("DecoratedTest", true)]
    [Arguments("DynamicBuilderTest", true)]
    [Arguments("CategoryOnly", false)]
    [Arguments("PlainMethod", false)]
    public async Task IsTestMethod_RecognizerOfTheProbe_ClassifiesEveryMarkerOfTheFramework(
        string methodName,
        bool expected
    )
    {
        var compilation = CompilationFactory.Create(TUnitSource, includeTUnit: true);
        var recognizer = TUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation)!;

        var method = FindMethod(compilation, methodName);

        _ = await Assert.That(recognizer.IsTestMethod(method)).IsEqualTo(expected);
    }

    /// <summary>
    /// A recogniser that was created without any well-known type still has to recognise an attribute whose
    /// base chain reaches a <c>BaseTestAttribute</c> declared in a framework assembly, which is the only rule
    /// available in that state.
    /// </summary>
    [Test]
    public async Task IsTestMethod_RecognizerWithoutTheWellKnownType_UsesTheNameRule()
    {
        var compilation = CreateSatelliteConsumer(CasesSource, FrameworkLikeAssemblyName);
        var recognizer = TUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation)!;

        var decorated = FindMethod(compilation, "DecoratedTest");
        var plain = FindMethod(compilation, "PlainMethod");

        using (Assert.Multiple())
        {
            _ = await Assert.That(recognizer.IsTestMethod(decorated)).IsTrue();
            _ = await Assert.That(recognizer.IsTestMethod(plain)).IsFalse();
        }
    }

    [Test]
    public async Task IsTestMethod_MethodIsNull_ThrowsArgumentNullException()
    {
        var compilation = CompilationFactory.Create(TUnitSource, includeTUnit: true);
        var recognizer = TUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation)!;
        var threw = false;

        try
        {
            _ = recognizer.IsTestMethod(null!);
        }
        catch (ArgumentNullException)
        {
            threw = true;
        }

        _ = await Assert.That(threw).IsTrue();
    }

    /// <summary>
    /// The name fallback is what recognises a marker base type the probe could not resolve, so it has to say
    /// yes to the framework assemblies and no to everything else, including to no assembly at all.
    /// </summary>
    [Test]
    public async Task IsFrameworkAssembly_AssemblyName_IsClassifiedByItsPrefix()
    {
        var framework = CreateSatelliteConsumer(PlainSource, FrameworkLikeAssemblyName);
        var foreign = CreateSatelliteConsumer(PlainSource, ForeignAssemblyName);

        var frameworkAssembly = SatelliteAssembly(framework, FrameworkLikeAssemblyName);
        var foreignAssembly = SatelliteAssembly(foreign, ForeignAssemblyName);

        using (Assert.Multiple())
        {
            _ = await Assert.That(TUnitTestFrameworkProbe.IsFrameworkAssembly(frameworkAssembly)).IsTrue();
            _ = await Assert.That(TUnitTestFrameworkProbe.IsFrameworkAssembly(foreignAssembly)).IsFalse();
        }
    }

    [Test]
    public async Task IsFrameworkAssembly_AssemblyIsNull_ReturnsFalse() =>
        _ = await Assert.That(TUnitTestFrameworkProbe.IsFrameworkAssembly(null)).IsFalse();

    [Test]
    public async Task GetTestAttributeType_WithoutTheFramework_ReturnsNull()
    {
        var compilation = CompilationFactory.Create(PlainSource);

        _ = await Assert.That(TUnitTestFrameworkProbe.GetTestAttributeType(compilation)).IsNull();
    }

    /// <summary>
    /// The marker base type is the hook the recogniser is built on, so the probe has to resolve exactly it -
    /// the abstract base of every marker, not one of the concrete markers.
    /// </summary>
    [Test]
    public async Task GetBaseTestAttributeType_WithFrameworkReference_ResolvesTheMarkerBaseType()
    {
        var compilation = CompilationFactory.Create(TUnitSource, includeTUnit: true);

        var resolved = TUnitTestFrameworkProbe.GetBaseTestAttributeType(compilation);

        using (Assert.Multiple())
        {
            _ = await Assert.That(resolved?.ToDisplayString()).IsEqualTo("TUnit.Core.BaseTestAttribute");
            _ = await Assert.That(resolved!.IsAbstract).IsTrue();
        }
    }

    [Test]
    public async Task GetBaseTestAttributeType_WithoutTheFramework_ReturnsNull()
    {
        var compilation = CompilationFactory.Create(PlainSource);

        _ = await Assert.That(TUnitTestFrameworkProbe.GetBaseTestAttributeType(compilation)).IsNull();
    }

    [Test]
    public async Task Fixtures_EveryCompilation_CompilesWithoutErrors()
    {
        var errors = new[]
        {
            Describe(CompilationFactory.Create(TUnitSource, includeTUnit: true)),
            Describe(CompilationFactory.Create(PlainSource)),
            Describe(CreateSatelliteConsumer(CasesSource, FrameworkLikeAssemblyName)),
            Describe(CreateSatelliteConsumer(CasesSource, ForeignAssemblyName)),
        };

        _ = await Assert
            .That(string.Join(" / ", errors.Distinct(StringComparer.Ordinal)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

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
}
