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
/// Covers how a TUnit test method is recognised: by the real test attribute, by an attribute that
/// derives from one, and never by an attribute that only happens to carry the same simple name.
/// </summary>
/// <remarks>
/// The "derived attribute" fixtures do not derive from <c>TUnit.Core.TestAttribute</c>, because that
/// type is sealed. They derive from a <c>TestAttribute</c> declared in a satellite assembly whose name
/// starts with <c>TUnit</c>, which is the second recognition rule the recogniser implements and the only
/// one a derived attribute can ever satisfy.
/// </remarks>
public class TUnitTestMethodRecognizerTests
{
    private const string FrameworkAssemblyName = "TUnit.Satellite";
    private const string ForeignAssemblyName = "Foreign.Satellite";

    private const string CasesTypeName = "Fixture.Cases";

    private const string SatelliteSource = """
        namespace Satellite;

        using System;

        [AttributeUsage(AttributeTargets.Method)]
        public class TestAttribute : Attribute
        {
        }
        """;

    private const string FrameworkFixtureSource = """
        namespace Fixture;

        public sealed class ScenarioTestAttribute : Satellite.TestAttribute
        {
        }

        public class SatelliteCases
        {
            [Satellite.Test]
            public void UsesFrameworkAttributeDirectly()
            {
            }

            [ScenarioTest]
            public void UsesDerivedAttribute()
            {
            }

            public void PlainMethod()
            {
            }
        }
        """;

    private const string TUnitFixtureSource = """
        namespace Fixture;

        using TUnit.Core;

        public class Cases
        {
            [Test]
            public void DecoratedTest()
            {
            }

            public void PlainMethod()
            {
            }

            [Test]
            public static void StaticTest()
            {
            }

            [Test]
            public void GenericTest<TValue>()
            {
            }
        }

        public class InheritanceBase
        {
            [Test]
            public void InheritedTest()
            {
            }
        }

        public sealed class InheritanceDerived : InheritanceBase
        {
        }
        """;

    private const string UnrelatedFixtureSource = """
        namespace Fixture;

        using System;

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class TestAttribute : Attribute
        {
        }

        public class UnrelatedCases
        {
            [Test]
            public void LooksLikeATest()
            {
            }
        }
        """;

    [Test]
    public async Task Fixtures_EveryCompilation_CompilesWithoutErrors()
    {
        var errors = new[]
        {
            Describe(CreateTUnitFixture()),
            Describe(CreateSatelliteFixture(FrameworkAssemblyName)),
            Describe(CompilationFactory.Create(UnrelatedFixtureSource, includeTUnit: true)),
            Describe(CompilationFactory.Create(UnrelatedFixtureSource)),
        };

        _ = await Assert
            .That(string.Join(" / ", errors.Distinct(StringComparer.Ordinal)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    public async Task FindTestMethods_DecoratedMethods_AreDiscoveredInDeclarationOrder()
    {
        var found = FindTestMethods(CreateTUnitFixture());

        _ = await Assert
            .That(Describe(found))
            .IsEqualTo("Cases.DecoratedTest|Cases.StaticTest|Cases.GenericTest|InheritanceBase.InheritedTest");
    }

    [Test]
    public async Task FindTestMethods_UndecoratedMethod_IsNotDiscovered()
    {
        var found = FindTestMethods(CreateTUnitFixture());

        var plain = found.Where(method => string.Equals(method.Name, "PlainMethod", StringComparison.Ordinal));

        _ = await Assert.That(Describe(plain)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task FindTestMethods_InheritedTestMethod_IsDiscoveredOnceOnItsDeclaringType()
    {
        var found = FindTestMethods(CreateTUnitFixture());

        var inherited = found.Where(method => string.Equals(method.Name, "InheritedTest", StringComparison.Ordinal));

        _ = await Assert.That(Describe(inherited)).IsEqualTo("InheritanceBase.InheritedTest");
    }

    [Test]
    public async Task FindTestMethods_AttributeDerivedFromAFrameworkTestAttribute_IsDiscovered()
    {
        var found = FindTestMethods(CreateSatelliteFixture(FrameworkAssemblyName));

        _ = await Assert
            .That(Describe(found))
            .IsEqualTo("SatelliteCases.UsesFrameworkAttributeDirectly|SatelliteCases.UsesDerivedAttribute");
    }

    /// <summary>
    /// The name rule requires an assembly of the framework, so an attribute of the very same simple name
    /// coming from a foreign satellite marks nothing — and the probe does not even offer a recogniser for
    /// such a compilation.
    /// </summary>
    [Test]
    public async Task FindTestMethods_AttributeFromANonFrameworkAssembly_IsNotDiscovered()
    {
        var compilation = CreateSatelliteFixture(ForeignAssemblyName);

        var found = FindTestMethods(compilation);

        _ = await Assert.That(TUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation)).IsNull();
        _ = await Assert.That(found.Length).IsEqualTo(0);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task FindTestMethods_TestAttributeFromAnUnrelatedNamespace_IsNotDiscovered(bool includeTUnit)
    {
        var compilation = CompilationFactory.Create(UnrelatedFixtureSource, includeTUnit: includeTUnit);

        var found = FindTestMethods(compilation);

        _ = await Assert.That(found.Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("DecoratedTest", true)]
    [Arguments("StaticTest", true)]
    [Arguments("GenericTest", true)]
    [Arguments("PlainMethod", false)]
    public async Task IsTestMethod_Method_IsClassifiedByItsAttributes(string methodName, bool expected)
    {
        var compilation = CreateTUnitFixture();
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, CasesTypeName, methodName);

        _ = await Assert.That(recognizer.IsTestMethod(method)).IsEqualTo(expected);
    }

    /// <summary>
    /// Without the framework there is no recogniser, and a recogniser built without the well-known type
    /// answers a plain "no" rather than throwing, because the production side asks the question about
    /// every compilation it sees.
    /// </summary>
    [Test]
    public async Task IsTestMethod_CompilationWithoutTheFramework_ReturnsFalse()
    {
        var compilation = CompilationFactory.Create(UnrelatedFixtureSource);
        var method = FindMethod(compilation, "Fixture.UnrelatedCases", "LooksLikeATest");

        _ = await Assert.That(TUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation)).IsNull();
        _ = await Assert.That(CreateRecognizer(compilation).IsTestMethod(method)).IsFalse();
    }

    /// <summary>
    /// The attribute type is resolved once for the whole compilation and handed to the recogniser, so that
    /// the well-known type is looked up once instead of once per method.
    /// </summary>
    /// <param name="methodName">The method to classify.</param>
    /// <param name="expected">The expected classification.</param>
    [Test]
    [Arguments("DecoratedTest", true)]
    [Arguments("StaticTest", true)]
    [Arguments("PlainMethod", false)]
    public async Task IsTestMethod_WithAPreResolvedAttributeType_ClassifiesByThatType(string methodName, bool expected)
    {
        var compilation = CreateTUnitFixture();
        var method = FindMethod(compilation, CasesTypeName, methodName);
        var attributeType = TUnitTestFrameworkProbe.GetTestAttributeType(compilation);

        _ = await Assert.That(attributeType?.ToDisplayString()).IsEqualTo("TUnit.Core.TestAttribute");
        _ = await Assert.That(new TUnitTestMethodRecognizer(attributeType).IsTestMethod(method)).IsEqualTo(expected);
    }

    /// <summary>
    /// When the well-known attribute type could not be resolved, only the name rule is left. It has to
    /// carry the recognition on its own, both for the attribute of the framework itself and for one of a
    /// framework satellite.
    /// </summary>
    [Test]
    public async Task IsTestMethod_WithoutAnAttributeType_FallsBackToTheNameRule()
    {
        var recognizer = new TUnitTestMethodRecognizer(testAttributeType: null);

        var frameworkAttribute = FindMethod(CreateTUnitFixture(), CasesTypeName, "DecoratedTest");
        var satelliteAttribute = FindMethod(
            CreateSatelliteFixture(FrameworkAssemblyName),
            "Fixture.SatelliteCases",
            "UsesFrameworkAttributeDirectly"
        );

        _ = await Assert.That(recognizer.IsTestMethod(frameworkAttribute)).IsTrue();
        _ = await Assert.That(recognizer.IsTestMethod(satelliteAttribute)).IsTrue();
    }

    /// <summary>
    /// An attribute that only shares the simple name is not a test attribute, no matter whether the
    /// well-known type could be resolved for the compilation.
    /// </summary>
    [Test]
    public async Task IsTestMethod_AttributeFromAnUnrelatedNamespace_IsNotClassifiedAsATest()
    {
        var compilation = CompilationFactory.Create(UnrelatedFixtureSource, includeTUnit: true);
        var method = FindMethod(compilation, "Fixture.UnrelatedCases", "LooksLikeATest");

        _ = await Assert.That(CreateRecognizer(compilation).IsTestMethod(method)).IsFalse();
        _ = await Assert.That(new TUnitTestMethodRecognizer(testAttributeType: null).IsTestMethod(method)).IsFalse();
    }

    [Test]
    public async Task IsTestMethod_MethodIsNull_ThrowsArgumentNullException()
    {
        var recognizer = new TUnitTestMethodRecognizer(testAttributeType: null);
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

    private static ImmutableArray<IMethodSymbol> FindTestMethods(Compilation compilation) =>
        TestMethodDiscovery.FindTestMethods(compilation, CreateRecognizer(compilation), CancellationToken.None);

    private static TUnitTestMethodRecognizer CreateRecognizer(Compilation compilation) =>
        new TUnitTestMethodRecognizer(TUnitTestFrameworkProbe.GetTestAttributeType(compilation));

    private static CSharpCompilation CreateTUnitFixture() =>
        CompilationFactory.Create(TUnitFixtureSource, includeTUnit: true, filePath: "Cases.cs");

    private static CSharpCompilation CreateSatelliteFixture(string satelliteAssemblyName)
    {
        var satellite = CompilationFactory.Create(SatelliteSource, satelliteAssemblyName, filePath: "Satellite.cs");

        return CompilationFactory.Create(
            FrameworkFixtureSource,
            additionalReferences: [satellite.ToMetadataReference()],
            filePath: "SatelliteCases.cs"
        );
    }

    private static IMethodSymbol FindMethod(Compilation compilation, string typeName, string methodName) =>
        compilation.GetTypeByMetadataName(typeName)!.GetMembers(methodName).OfType<IMethodSymbol>().First();

    private static string Describe(IEnumerable<IMethodSymbol> methods) =>
        string.Join("|", methods.Select(method => method.ContainingType.Name + "." + method.Name));

    private static string Describe(Compilation compilation) =>
        DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation));
}
