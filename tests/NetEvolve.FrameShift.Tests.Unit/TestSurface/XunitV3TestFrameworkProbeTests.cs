#if FRAMESHIFT_XUNIT_V3
namespace NetEvolve.FrameShift.Tests.Unit.TestSurface;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the seam xUnit.net v3 is plugged in through. The probe decides whether the test-side analysis
/// runs at all, so reporting absence correctly matters more than recognising presence: a probe that claims
/// a compilation it does not understand would make the analysis judge invisible tests.
/// </summary>
/// <remarks>
/// <para>
/// The whole file is conditional, because every expectation in it needs the real <c>xunit.v3.core</c>
/// assembly and that package ships no assets for net6.0 and net7.0. On those two target frameworks the
/// file contributes nothing at all instead of contributing something that cannot compile; the v2 side has
/// assets everywhere and is therefore covered unconditionally.
/// </para>
/// <para>
/// The crux of the version split is asserted explicitly here: a compilation that references only version 2
/// must not be claimed by this probe, and a compilation that references both must still resolve exactly the
/// version 3 attribute, even though both versions declare it under the identical metadata name.
/// </para>
/// <para>
/// The class is <see langword="internal" /> because <see cref="TestFramework" /> appears in its test
/// signatures; TUnit discovers internal test classes just as well as public ones.
/// </para>
/// </remarks>
internal sealed class XunitV3TestFrameworkProbeTests
{
    private const string FrameworkName = "xUnit v3";

    private const string TestAttributeDisplayName = "Xunit.FactAttribute";

    private const string FrameworkAssemblyName = "xunit.v3.core";
    private const string VersionTwoAssemblyName = "xunit.core";

    private const string FrameworkLikeAssemblyName = "xunit.v3.satellite";
    private const string ForeignAssemblyName = "Foreign.Satellite";

    /// <summary>
    /// A satellite that carries the simple name of the xUnit.net test attribute in a namespace of its own,
    /// so that its assembly name is the only version 3 trace the compilation has and the well-known type
    /// stays unresolvable.
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
    public async Task FrameworkName_Probe_NamesTheVersionItDetects() =>
        _ = await Assert.That(XunitV3TestFrameworkProbe.Instance.FrameworkName).IsEqualTo(FrameworkName);

    [Test]
    public async Task TryCreateRecognizer_WithFrameworkReference_ReturnsARecognizer()
    {
        var compilation = CreateXunitFixture(TestFramework.XunitV3);

        var recognizer = XunitV3TestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer?.FrameworkName).IsEqualTo(FrameworkName);
    }

    [Test]
    public async Task TryCreateRecognizer_WithoutAnyFrameworkTrace_ReturnsNull()
    {
        var compilation = CompilationFactory.Create(PlainSource);

        var recognizer = XunitV3TestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer).IsNull();
    }

    /// <summary>
    /// The point of separating the two major versions: a project on version 2 alone is none of this probe's
    /// business. Every version 3 assembly carries the prefix <c>xunit.v3</c> and no version 2 assembly does,
    /// so the shared attribute name must not be able to wake this probe up on a pure version 2 compilation —
    /// it would otherwise report version 3 diagnostics for a version the project does not even reference.
    /// </summary>
    [Test]
    public async Task TryCreateRecognizer_ReferencingOnlyVersionTwo_ReturnsNull()
    {
        var compilation = CreateXunitFixture(TestFramework.XunitV2);

        using (Assert.Multiple())
        {
            _ = await Assert.That(XunitV3TestFrameworkProbe.GetTestAttributeType(compilation)).IsNull();
            _ = await Assert.That(XunitV3TestFrameworkProbe.Instance.TryCreateRecognizer(compilation)).IsNull();
        }
    }

    /// <summary>
    /// A compilation of a different test framework must shut the analysis down completely, which is the
    /// only thing that keeps the framework analyzers harmless next to each other.
    /// </summary>
    /// <param name="framework">The foreign framework the compilation references.</param>
    [Test]
    [Arguments(TestFramework.TUnit)]
    [Arguments(TestFramework.NUnit)]
    [Arguments(TestFramework.MSTest)]
    public async Task TryCreateRecognizer_ReferencingADifferentFramework_ReturnsNull(TestFramework framework)
    {
        var compilation = CompilationFactory.Create(PlainSource, framework, filePath: "Cases.cs");

        var recognizer = XunitV3TestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer).IsNull();
    }

    /// <summary>
    /// Detection fails open: an assembly of version 3 that does not declare the well-known attribute type
    /// itself is still enough to accept the compilation, because a silent shutdown would be indistinguishable
    /// from a project without tests.
    /// </summary>
    [Test]
    public async Task TryCreateRecognizer_ReferencingAFrameworkLikeAssembly_ReturnsARecognizer()
    {
        var compilation = CreateSatelliteConsumer(PlainSource, FrameworkLikeAssemblyName);

        var recognizer = XunitV3TestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer).IsNotNull();
    }

    [Test]
    public async Task TryCreateRecognizer_ReferencingOnlyAForeignAssembly_ReturnsNull()
    {
        var compilation = CreateSatelliteConsumer(CasesSource, ForeignAssemblyName);

        var recognizer = XunitV3TestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        _ = await Assert.That(recognizer).IsNull();
    }

    [Test]
    public async Task TryCreateRecognizer_CompilationIsNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = XunitV3TestFrameworkProbe.Instance.TryCreateRecognizer(null!)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("compilation");
    }

    /// <summary>
    /// Judging a method fails closed, the counterpart to the fail-open detection above: without the resolved
    /// attribute type there is no positive evidence left, and an attribute that merely carries the same
    /// simple name is deliberately not treated as any.
    /// </summary>
    [Test]
    public async Task IsTestMethod_RecognizerWithoutTheWellKnownType_RecognizesNothing()
    {
        var compilation = CreateSatelliteConsumer(CasesSource, FrameworkLikeAssemblyName);
        var recognizer = XunitV3TestFrameworkProbe.Instance.TryCreateRecognizer(compilation)!;

        var decorated = FindMethod(compilation, "DecoratedTest");
        var plain = FindMethod(compilation, "PlainMethod");

        using (Assert.Multiple())
        {
            _ = await Assert.That(XunitV3TestFrameworkProbe.GetTestAttributeType(compilation)).IsNull();
            _ = await Assert.That(recognizer.IsTestMethod(decorated)).IsFalse();
            _ = await Assert.That(recognizer.IsTestMethod(plain)).IsFalse();
        }
    }

    [Test]
    public async Task IsTestMethod_MethodIsNull_ThrowsArgumentNullException()
    {
        var compilation = CreateXunitFixture(TestFramework.XunitV3);
        var recognizer = XunitV3TestFrameworkProbe.Instance.TryCreateRecognizer(compilation)!;

        var exception = Assert.Throws<ArgumentNullException>(() => _ = recognizer.IsTestMethod(null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("method");
    }

    [Test]
    public async Task GetTestAttributeType_WithFrameworkReference_ResolvesTheAttributeInItsOwnAssembly()
    {
        var resolved = XunitV3TestFrameworkProbe.GetTestAttributeType(CreateXunitFixture(TestFramework.XunitV3));

        using (Assert.Multiple())
        {
            _ = await Assert.That(resolved?.ToDisplayString()).IsEqualTo(TestAttributeDisplayName);
            _ = await Assert.That(resolved?.ContainingAssembly.Name).IsEqualTo(FrameworkAssemblyName);
        }
    }

    [Test]
    public async Task GetTestAttributeType_WithoutTheFramework_ReturnsNull()
    {
        var compilation = CompilationFactory.Create(PlainSource);

        _ = await Assert.That(XunitV3TestFrameworkProbe.GetTestAttributeType(compilation)).IsNull();
    }

    /// <summary>
    /// The reason the versions are probed apart. A compilation referencing both declares
    /// <c>Xunit.FactAttribute</c> twice, so asking the compilation for that metadata name answers
    /// <see langword="null" /> — ambiguity, not absence. Resolving inside <c>xunit.v3.core</c> instead is
    /// exact, and it yields a different symbol than the version 2 probe resolves.
    /// </summary>
    [Test]
    public async Task GetTestAttributeType_ReferencingBothMajorVersions_ResolvesExactlyTheVersionThreeType()
    {
        var compilation = CompilationFactory.Create(PlainSource, TestFramework.All);

        var resolved = XunitV3TestFrameworkProbe.GetTestAttributeType(compilation);
        var versionTwo = XunitV2TestFrameworkProbe.GetTestAttributeType(compilation);

        using (Assert.Multiple())
        {
            _ = await Assert.That(compilation.GetTypeByMetadataName(TestAttributeDisplayName)).IsNull();
            _ = await Assert.That(resolved?.ContainingAssembly.Name).IsEqualTo(FrameworkAssemblyName);
            _ = await Assert.That(versionTwo?.ContainingAssembly.Name).IsEqualTo(VersionTwoAssemblyName);
            _ = await Assert.That(SymbolEqualityComparer.Default.Equals(resolved, versionTwo)).IsFalse();
        }
    }

    /// <summary>
    /// Both probes stay awake on a compilation that references both versions, because each one finds its own
    /// assembly and each recogniser then judges only the tests of its own version.
    /// </summary>
    [Test]
    public async Task TryCreateRecognizer_ReferencingBothMajorVersions_ReturnsARecognizerForEachVersion()
    {
        var compilation = CompilationFactory.Create(PlainSource, TestFramework.All);

        var versionThree = XunitV3TestFrameworkProbe.Instance.TryCreateRecognizer(compilation);
        var versionTwo = XunitV2TestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        using (Assert.Multiple())
        {
            _ = await Assert.That(versionThree?.FrameworkName).IsEqualTo(FrameworkName);
            _ = await Assert.That(versionTwo?.FrameworkName).IsEqualTo(XunitV2TestFrameworkProbe.Name);
        }
    }

    [Test]
    public async Task FindFrameworkAssembly_WithFrameworkReference_FindsTheVersionThreeAssembly()
    {
        var compilation = CreateXunitFixture(TestFramework.XunitV3);

        var assembly = XunitV3TestFrameworkProbe.FindFrameworkAssembly(compilation);

        _ = await Assert.That(assembly?.Name).IsEqualTo(FrameworkAssemblyName);
    }

    [Test]
    public async Task FindFrameworkAssembly_ReferencingOnlyVersionTwo_ReturnsNull()
    {
        var compilation = CreateXunitFixture(TestFramework.XunitV2);

        _ = await Assert.That(XunitV3TestFrameworkProbe.FindFrameworkAssembly(compilation)).IsNull();
    }

    /// <summary>
    /// The assembly rule is what tells the two major versions apart, so it has to accept every assembly of
    /// version 3 — including one that declares no attribute at all — and reject the version 2 assembly that
    /// declares the very same attribute name.
    /// </summary>
    [Test]
    public async Task IsFrameworkAssembly_AssemblyName_IsClassifiedByTheVersionPrefix()
    {
        var mixed = CompilationFactory.Create(PlainSource, TestFramework.All);
        var satellite = CreateSatelliteConsumer(PlainSource, FrameworkLikeAssemblyName);
        var foreign = CreateSatelliteConsumer(PlainSource, ForeignAssemblyName);

        var versionThree = ReferencedAssembly(mixed, FrameworkAssemblyName);
        var versionTwo = ReferencedAssembly(mixed, VersionTwoAssemblyName);
        var frameworkLike = ReferencedAssembly(satellite, FrameworkLikeAssemblyName);
        var unrelated = ReferencedAssembly(foreign, ForeignAssemblyName);

        using (Assert.Multiple())
        {
            _ = await Assert.That(XunitV3TestFrameworkProbe.IsFrameworkAssembly(versionThree)).IsTrue();
            _ = await Assert.That(XunitV3TestFrameworkProbe.IsFrameworkAssembly(versionTwo)).IsFalse();
            _ = await Assert.That(XunitV3TestFrameworkProbe.IsFrameworkAssembly(frameworkLike)).IsTrue();
            _ = await Assert.That(XunitV3TestFrameworkProbe.IsFrameworkAssembly(unrelated)).IsFalse();
        }
    }

    [Test]
    public async Task IsFrameworkAssembly_AssemblyIsNull_ReturnsFalse() =>
        _ = await Assert.That(XunitV3TestFrameworkProbe.IsFrameworkAssembly(null)).IsFalse();

    /// <summary>
    /// Guards the fixtures: a compilation that does not compile would make every expectation above say
    /// something about a broken fixture instead of about the probe.
    /// </summary>
    [Test]
    public async Task Fixtures_EveryCompilation_CompilesWithoutErrors()
    {
        var errors = new[]
        {
            Describe(CreateXunitFixture(TestFramework.XunitV3)),
            Describe(CreateXunitFixture(TestFramework.XunitV2)),
            Describe(CompilationFactory.Create(PlainSource)),
            Describe(CompilationFactory.Create(PlainSource, TestFramework.All)),
            Describe(CreateSatelliteConsumer(CasesSource, FrameworkLikeAssemblyName)),
            Describe(CreateSatelliteConsumer(CasesSource, ForeignAssemblyName)),
        };

        _ = await Assert
            .That(string.Join(" / ", errors.Distinct(StringComparer.Ordinal)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    /// <summary>
    /// Builds the <c>[Fact]</c> fixture for one major version. The source is spelled the same for both,
    /// because <c>Xunit.FactAttribute</c> exists in either of them — which is exactly why the assembly, and
    /// not the name, has to decide who owns the compilation.
    /// </summary>
    /// <param name="framework">The version the compilation references.</param>
    /// <returns>The created compilation.</returns>
    private static CSharpCompilation CreateXunitFixture(TestFramework framework) =>
        CompilationFactory.Create(XunitSource, framework, filePath: "Cases.cs");

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
    /// Resolves a referenced assembly of <paramref name="compilation" /> by its name, which is how the
    /// fixtures hand the real framework assemblies and the satellites to the assembly rule.
    /// </summary>
    /// <param name="compilation">The compilation referencing the assembly.</param>
    /// <param name="assemblyName">The name of the referenced assembly.</param>
    /// <returns>The resolved assembly symbol.</returns>
    private static IAssemblySymbol ReferencedAssembly(Compilation compilation, string assemblyName) =>
        compilation.SourceModule.ReferencedAssemblySymbols.First(assembly =>
            string.Equals(assembly.Name, assemblyName, StringComparison.OrdinalIgnoreCase)
        );

    private static IMethodSymbol FindMethod(Compilation compilation, string methodName) =>
        compilation.GetTypeByMetadataName("Fixture.Cases")!.GetMembers(methodName).OfType<IMethodSymbol>().First();

    private static string Describe(Compilation compilation) =>
        DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation));
}
#endif
