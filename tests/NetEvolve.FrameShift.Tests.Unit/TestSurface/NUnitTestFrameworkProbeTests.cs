namespace NetEvolve.FrameShift.Tests.Unit;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Tests.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the seam NUnit is plugged in through. The probe decides whether the test-side analysis runs at
/// all, so reporting absence correctly matters more than recognising presence: a probe that claims a
/// compilation it does not understand would make the analysis judge invisible tests.
/// </summary>
public class NUnitTestFrameworkProbeTests
{
    private const string FrameworkName = "NUnit";

    private const string FrameworkLikeAssemblyName = "nunit.satellite";
    private const string ForeignAssemblyName = "Foreign.Satellite";

    private const string TUnitScenario = "TUnit";
    private const string XunitScenario = "xUnit v2";

    /// <summary>
    /// A satellite that carries the simple names of the two test builder interfaces in a namespace of its
    /// own, so that the well-known metadata names stay unresolvable and only the name rule can recognise an
    /// attribute implementing them.
    /// </summary>
    private const string SatelliteSource = """
        namespace NUnit.Framework.Satellite.Interfaces;

        public interface ISimpleTestBuilder
        {
        }

        public interface ITestBuilder
        {
        }
        """;

    /// <summary>
    /// A consumer whose attribute is a test marker only because it implements a builder interface the
    /// satellite declares. Nothing about the attribute's own name says "test", which is what makes this
    /// fixture exercise the interface name rule rather than an attribute name rule.
    /// </summary>
    private const string CasesSource = """
        namespace Fixture;

        using System;
        using NUnit.Framework.Satellite.Interfaces;

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class ExerciseAttribute : Attribute, ITestBuilder
        {
        }

        public class Cases
        {
            [Exercise]
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

    private const string NUnitSource = """
        namespace Fixture;

        using NUnit.Framework;

        public class Cases
        {
            [Test]
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
        _ = await Assert.That(NUnitTestFrameworkProbe.Instance.FrameworkName).IsEqualTo(FrameworkName);

    [Test]
    public async Task TryCreateRecognizer_WithFrameworkReference_ReturnsARecognizer()
    {
        var compilation = CreateNUnitFixture();

        var recognizer = NUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer?.FrameworkName).IsEqualTo(FrameworkName);
    }

    /// <summary>
    /// The framework prefix alone is enough to accept a compilation, because a specialised test attribute
    /// may live in a framework assembly that does not carry the well-known attribute types itself.
    /// </summary>
    [Test]
    public async Task TryCreateRecognizer_ReferencingAFrameworkLikeAssembly_ReturnsARecognizer()
    {
        var compilation = CreateSatelliteConsumer(PlainSource, FrameworkLikeAssemblyName);

        var recognizer = NUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer).IsNotNull();
    }

    [Test]
    public async Task TryCreateRecognizer_WithoutAnyFrameworkTrace_ReturnsNull()
    {
        var compilation = CompilationFactory.Create(PlainSource);

        var recognizer = NUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer).IsNull();
    }

    /// <summary>
    /// A compilation of a different test framework must shut the analysis down completely, which is the
    /// only thing that keeps the framework analyzers harmless next to each other.
    /// </summary>
    /// <param name="framework">The foreign framework the compilation references.</param>
    [Test]
    [Arguments(TUnitScenario)]
    [Arguments(XunitScenario)]
    public async Task TryCreateRecognizer_ReferencingADifferentFramework_ReturnsNull(string framework)
    {
        var compilation = CompilationFactory.Create(PlainSource, ToFramework(framework), filePath: "Cases.cs");

        var recognizer = NUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer).IsNull();
    }

    [Test]
    public async Task TryCreateRecognizer_ReferencingOnlyAForeignAssembly_ReturnsNull()
    {
        var compilation = CreateSatelliteConsumer(CasesSource, ForeignAssemblyName);

        var recognizer = NUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer).IsNull();
    }

    [Test]
    public async Task TryCreateRecognizer_CompilationIsNull_ThrowsArgumentNullException()
    {
        var threw = ThrowsArgumentNull(() => _ = NUnitTestFrameworkProbe.Instance.TryCreateRecognizer(null!));

        _ = await Assert.That(threw).IsTrue();
    }

    /// <summary>
    /// A recogniser that was created without the well-known types still has to recognise a test attribute
    /// declared against a framework assembly, which is the only rule available in that state. Both
    /// resolutions are asserted empty first, so the recognition below can only come from the name rule.
    /// </summary>
    [Test]
    public async Task IsTestMethod_RecognizerWithoutTheWellKnownTypes_UsesTheNameRule()
    {
        var compilation = CreateSatelliteConsumer(CasesSource, FrameworkLikeAssemblyName);
        var recognizer = NUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation)!;

        var decorated = FindMethod(compilation, "DecoratedTest");
        var plain = FindMethod(compilation, "PlainMethod");

        _ = await Assert.That(NUnitTestFrameworkProbe.GetTestAttributeTypes(compilation)).IsEmpty();
        _ = await Assert.That(NUnitTestFrameworkProbe.GetTestBuilderInterfaceTypes(compilation)).IsEmpty();
        _ = await Assert.That(recognizer.IsTestMethod(decorated)).IsTrue();
        _ = await Assert.That(recognizer.IsTestMethod(plain)).IsFalse();
    }

    [Test]
    public async Task IsTestMethod_MethodIsNull_ThrowsArgumentNullException()
    {
        var compilation = CreateNUnitFixture();
        var recognizer = NUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation)!;

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

        _ = await Assert.That(NUnitTestFrameworkProbe.IsFrameworkAssembly(frameworkAssembly)).IsTrue();
        _ = await Assert.That(NUnitTestFrameworkProbe.IsFrameworkAssembly(foreignAssembly)).IsFalse();
    }

    [Test]
    public async Task IsFrameworkAssembly_AssemblyIsNull_ReturnsFalse() =>
        _ = await Assert.That(NUnitTestFrameworkProbe.IsFrameworkAssembly(null)).IsFalse();

    /// <summary>
    /// The three attributes are siblings, so all of them have to be resolved: a missing one would drop
    /// every test that is written with it.
    /// </summary>
    [Test]
    public async Task GetTestAttributeTypes_WithFrameworkReference_ResolvesEverySiblingAttribute()
    {
        var resolved = NUnitTestFrameworkProbe.GetTestAttributeTypes(CreateNUnitFixture());

        var described = string.Join("|", resolved.Select(type => type.ToDisplayString()));

        _ = await Assert
            .That(described)
            .IsEqualTo(
                "NUnit.Framework.TestAttribute|NUnit.Framework.TestCaseAttribute|"
                    + "NUnit.Framework.TestCaseSourceAttribute"
            );
    }

    [Test]
    public async Task GetTestAttributeTypes_WithoutTheFramework_ReturnsAnEmptyArray()
    {
        var compilation = CompilationFactory.Create(PlainSource);

        _ = await Assert.That(NUnitTestFrameworkProbe.GetTestAttributeTypes(compilation)).IsEmpty();
    }

    [Test]
    public async Task Fixtures_EveryCompilation_CompilesWithoutErrors()
    {
        var errors = new[]
        {
            Describe(CreateNUnitFixture()),
            Describe(CompilationFactory.Create(PlainSource)),
            Describe(CreateSatelliteConsumer(CasesSource, FrameworkLikeAssemblyName)),
            Describe(CreateSatelliteConsumer(CasesSource, ForeignAssemblyName)),
        };

        _ = await Assert
            .That(string.Join(" / ", errors.Distinct(StringComparer.Ordinal)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    private static CSharpCompilation CreateNUnitFixture() =>
        CompilationFactory.Create(NUnitSource, TestFramework.NUnit, filePath: "Cases.cs");

    private static TestFramework ToFramework(string scenario) =>
        scenario switch
        {
            TUnitScenario => TestFramework.TUnit,
            XunitScenario => TestFramework.XunitV2,
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
