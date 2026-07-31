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
/// Covers how a TUnit test method is recognised: by the marker base type <c>TUnit.Core.BaseTestAttribute</c>
/// every marker of the framework derives from, and never by an attribute that only happens to carry the
/// same simple name.
/// </summary>
/// <remarks>
/// <para>
/// <c>TUnit.Core.TestAttribute</c> is sealed and is not the only marker: <c>DynamicTestBuilderAttribute</c>
/// derives from the very same base type and marks a test as well. Recognising the base type is therefore
/// the only rule that covers both, and the only one a user-defined marker can satisfy.
/// </para>
/// <para>
/// The "derived attribute" fixture extends <c>DynamicTestBuilderAttribute</c> rather than
/// <c>BaseTestAttribute</c> directly, because the only constructor of the base type is internal to the
/// framework - nothing outside TUnit can extend it. Extending the public marker still puts
/// <c>BaseTestAttribute</c> into the base chain, which is what the recogniser walks.
/// </para>
/// <para>
/// The satellite fixtures exist for the fallback state, in which the marker base type cannot be resolved at
/// all. They declare a <c>BaseTestAttribute</c> of their own, once in an assembly whose name starts with
/// <c>TUnit</c> and once in a foreign one, which is exactly the difference the fallback is allowed to make.
/// </para>
/// </remarks>
public class TUnitTestMethodRecognizerTests
{
    private const string FrameworkAssemblyName = "TUnit.Satellite";
    private const string ForeignAssemblyName = "Foreign.Satellite";

    private const string CasesTypeName = "Fixture.Cases";
    private const string MarkersTypeName = "Fixture.Markers";

    private const string SatelliteSource = """
        namespace Satellite;

        using System;

        [AttributeUsage(AttributeTargets.Method)]
        public class BaseTestAttribute : Attribute
        {
        }
        """;

    private const string FrameworkFixtureSource = """
        namespace Fixture;

        public sealed class ScenarioTestAttribute : Satellite.BaseTestAttribute
        {
        }

        public class SatelliteCases
        {
            [Satellite.BaseTest]
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

    /// <summary>
    /// Every marker and every non-marker attribute of the framework on one type: the two markers TUnit
    /// declares, a user-defined marker, and the data-source and configuration attributes that never make a
    /// method a test on their own.
    /// </summary>
    private const string MarkerFixtureSource = """
        namespace Fixture;

        using System.Collections.Generic;
        using TUnit.Core;

        public sealed class ScenarioTestAttribute : DynamicTestBuilderAttribute
        {
        }

        public class Markers
        {
            [DynamicTestBuilder]
            public void DynamicBuilderTest()
            {
            }

            [ScenarioTest]
            public void CustomMarkerTest()
            {
            }

            [Test]
            [Arguments(1)]
            public void TestWithArguments(int value)
            {
            }

            [Arguments(1)]
            public void ArgumentsOnly(int value)
            {
            }

            [MethodDataSource(nameof(Values))]
            public void MethodDataSourceOnly(int value)
            {
            }

            [MatrixDataSource]
            public void MatrixDataSourceOnly()
            {
            }

            [ClassDataSource]
            public void ClassDataSourceOnly()
            {
            }

            [Repeat(2)]
            public void RepeatOnly()
            {
            }

            [Category("Fast")]
            public void CategoryOnly()
            {
            }

            [Skip("not now")]
            public void SkipOnly()
            {
            }

            public void MatrixOnly([Matrix(1, 2)] int value)
            {
            }

            public static IEnumerable<int> Values()
            {
                yield return 1;
            }
        }
        """;

    /// <summary>
    /// Look-alikes declared by the compilation itself: one named like the concrete marker, one named like
    /// the marker base type, and an attribute deriving from the latter.
    /// </summary>
    private const string UnrelatedFixtureSource = """
        namespace Fixture;

        using System;

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class TestAttribute : Attribute
        {
        }

        [AttributeUsage(AttributeTargets.Method)]
        public class BaseTestAttribute : Attribute
        {
        }

        public sealed class LocalScenarioTestAttribute : BaseTestAttribute
        {
        }

        public class UnrelatedCases
        {
            [Test]
            public void LooksLikeATest()
            {
            }

            [BaseTest]
            public void LooksLikeAMarkerBase()
            {
            }

            [LocalScenarioTest]
            public void DerivesFromALookAlike()
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
            Describe(CreateMarkerFixture()),
            Describe(CreateSatelliteFixture(FrameworkAssemblyName)),
            Describe(CompilationFactory.Create(UnrelatedFixtureSource, includeTUnit: true)),
            Describe(CompilationFactory.Create(UnrelatedFixtureSource)),
        };

        _ = await Assert
            .That(string.Join(" / ", errors.Distinct(StringComparer.Ordinal)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    /// <summary>
    /// The facts the recogniser is built on, read off the framework's own symbols: both markers derive from
    /// <c>TUnit.Core.BaseTestAttribute</c>, and not a single data-source or configuration attribute does.
    /// </summary>
    /// <param name="metadataName">The framework attribute to inspect.</param>
    /// <param name="expected">Whether its base chain includes the marker base type.</param>
    [Test]
    [Arguments("TUnit.Core.TestAttribute", true)]
    [Arguments("TUnit.Core.DynamicTestBuilderAttribute", true)]
    [Arguments("TUnit.Core.ArgumentsAttribute", false)]
    [Arguments("TUnit.Core.MethodDataSourceAttribute", false)]
    [Arguments("TUnit.Core.MatrixDataSourceAttribute", false)]
    [Arguments("TUnit.Core.ClassDataSourceAttribute", false)]
    [Arguments("TUnit.Core.MatrixAttribute", false)]
    [Arguments("TUnit.Core.RepeatAttribute", false)]
    [Arguments("TUnit.Core.CategoryAttribute", false)]
    [Arguments("TUnit.Core.SkipAttribute", false)]
    public async Task MarkerBaseType_FrameworkAttribute_IsInTheBaseChainOfMarkersOnly(
        string metadataName,
        bool expected
    )
    {
        var compilation = CreateMarkerFixture();
        var markerBase = TUnitTestFrameworkProbe.GetBaseTestAttributeType(compilation);
        var attribute = compilation.GetTypeByMetadataName(metadataName);

        _ = await Assert.That(markerBase?.ToDisplayString()).IsEqualTo("TUnit.Core.BaseTestAttribute");
        _ = await Assert.That(attribute?.ToDisplayString()).IsEqualTo(metadataName);
        _ = await Assert.That(DerivesFrom(attribute, markerBase)).IsEqualTo(expected);
    }

    [Test]
    public async Task FindTestMethods_DecoratedMethods_AreDiscoveredInDeclarationOrder()
    {
        var found = FindTestMethods(CreateTUnitFixture());

        _ = await Assert
            .That(Describe(found))
            .IsEqualTo("Cases.DecoratedTest|Cases.StaticTest|Cases.GenericTest|InheritanceBase.InheritedTest");
    }

    /// <summary>
    /// The whole point of the marker base type: exactly the three marked methods are found, and none of the
    /// eight methods carrying only a data source or only a configuration attribute is.
    /// </summary>
    [Test]
    public async Task FindTestMethods_EveryMarkerAndNonMarkerAttribute_DiscoversTheMarkedMethodsOnly()
    {
        var found = FindTestMethods(CreateMarkerFixture());

        _ = await Assert
            .That(Describe(found))
            .IsEqualTo("Markers.DynamicBuilderTest|Markers.CustomMarkerTest|Markers.TestWithArguments");
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
    public async Task FindTestMethods_AttributeDerivedFromAFrameworkMarkerBase_IsDiscovered()
    {
        var found = FindTestMethods(CreateSatelliteFixture(FrameworkAssemblyName));

        _ = await Assert
            .That(Describe(found))
            .IsEqualTo("SatelliteCases.UsesFrameworkAttributeDirectly|SatelliteCases.UsesDerivedAttribute");
    }

    /// <summary>
    /// The fallback requires an assembly of the framework, so a <c>BaseTestAttribute</c> of the very same
    /// simple name coming from a foreign satellite marks nothing — and the probe does not even offer a
    /// recogniser for such a compilation.
    /// </summary>
    [Test]
    public async Task FindTestMethods_AttributeFromANonFrameworkAssembly_IsNotDiscovered()
    {
        var compilation = CreateSatelliteFixture(ForeignAssemblyName);

        var found = FindTestMethods(compilation);

        _ = await Assert.That(TUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation)).IsNull();
        _ = await Assert.That(found.Length).IsEqualTo(0);
    }

    /// <summary>
    /// A <c>BaseTestAttribute</c> of an unrelated assembly is no marker in either state of the recogniser:
    /// with the framework present the semantic rule rejects it, without the framework the fallback rejects
    /// its declaring assembly.
    /// </summary>
    [Test]
    public async Task IsTestMethod_MarkerBaseNameFromANonFrameworkAssembly_IsNotClassifiedAsATest()
    {
        var compilation = CreateSatelliteFixture(ForeignAssemblyName);
        var direct = FindMethod(compilation, "Fixture.SatelliteCases", "UsesFrameworkAttributeDirectly");
        var derived = FindMethod(compilation, "Fixture.SatelliteCases", "UsesDerivedAttribute");
        var recognizer = CreateRecognizer(compilation);

        _ = await Assert.That(recognizer.IsTestMethod(direct)).IsFalse();
        _ = await Assert.That(recognizer.IsTestMethod(derived)).IsFalse();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task FindTestMethods_LookAlikeAttributesOfTheCompilationItself_AreNotDiscovered(bool includeTUnit)
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
    /// The defect this recogniser was fixed for, method by method: the second marker of the framework and a
    /// user-defined one are tests, and no data-source or configuration attribute makes a method one.
    /// </summary>
    /// <param name="methodName">The method to classify.</param>
    /// <param name="expected">The expected classification.</param>
    [Test]
    [Arguments("DynamicBuilderTest", true)]
    [Arguments("CustomMarkerTest", true)]
    [Arguments("TestWithArguments", true)]
    [Arguments("ArgumentsOnly", false)]
    [Arguments("MethodDataSourceOnly", false)]
    [Arguments("MatrixDataSourceOnly", false)]
    [Arguments("ClassDataSourceOnly", false)]
    [Arguments("RepeatOnly", false)]
    [Arguments("CategoryOnly", false)]
    [Arguments("SkipOnly", false)]
    [Arguments("MatrixOnly", false)]
    [Arguments("Values", false)]
    public async Task IsTestMethod_MarkerAndNonMarkerAttributes_AreClassifiedByTheMarkerBaseType(
        string methodName,
        bool expected
    )
    {
        var compilation = CreateMarkerFixture();
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, MarkersTypeName, methodName);

        _ = await Assert.That(recognizer.IsTestMethod(method)).IsEqualTo(expected);
    }

    /// <summary>
    /// Without the framework there is no recogniser, and a recogniser built without any well-known type
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
    /// The marker base type is resolved once for the whole compilation and handed to the recogniser, so that
    /// the well-known type is looked up once instead of once per method. Handing over that one type is enough
    /// to classify both markers.
    /// </summary>
    /// <param name="methodName">The method to classify.</param>
    /// <param name="expected">The expected classification.</param>
    [Test]
    [Arguments("DynamicBuilderTest", true)]
    [Arguments("CustomMarkerTest", true)]
    [Arguments("TestWithArguments", true)]
    [Arguments("ArgumentsOnly", false)]
    [Arguments("Values", false)]
    public async Task IsTestMethod_WithOnlyTheMarkerBaseType_ClassifiesByThatType(string methodName, bool expected)
    {
        var compilation = CreateMarkerFixture();
        var method = FindMethod(compilation, MarkersTypeName, methodName);
        var markerBase = TUnitTestFrameworkProbe.GetBaseTestAttributeType(compilation);
        var recognizer = new TUnitTestMethodRecognizer(testAttributeType: null, baseTestAttributeType: markerBase);

        _ = await Assert.That(markerBase?.ToDisplayString()).IsEqualTo("TUnit.Core.BaseTestAttribute");
        _ = await Assert.That(recognizer.IsTestMethod(method)).IsEqualTo(expected);
    }

    /// <summary>
    /// A recogniser holding only the sealed <c>TestAttribute</c> still classifies a plain <c>[Test]</c>, which
    /// keeps every caller that resolves nothing but that type working.
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
    /// When the marker base type could not be resolved, only the fallback is left. It has to carry the
    /// recognition on its own, both for the base type of the framework itself and for an attribute deriving
    /// from the one a framework satellite declares.
    /// </summary>
    [Test]
    public async Task IsTestMethod_WithoutAnyResolvedType_FallsBackToTheNameRule()
    {
        var recognizer = new TUnitTestMethodRecognizer(testAttributeType: null);
        var satellite = CreateSatelliteFixture(FrameworkAssemblyName);

        var frameworkAttribute = FindMethod(CreateTUnitFixture(), CasesTypeName, "DecoratedTest");
        var dynamicBuilder = FindMethod(CreateMarkerFixture(), MarkersTypeName, "DynamicBuilderTest");
        var satelliteAttribute = FindMethod(satellite, "Fixture.SatelliteCases", "UsesFrameworkAttributeDirectly");
        var satelliteDerived = FindMethod(satellite, "Fixture.SatelliteCases", "UsesDerivedAttribute");

        _ = await Assert.That(recognizer.IsTestMethod(frameworkAttribute)).IsTrue();
        _ = await Assert.That(recognizer.IsTestMethod(dynamicBuilder)).IsTrue();
        _ = await Assert.That(recognizer.IsTestMethod(satelliteAttribute)).IsTrue();
        _ = await Assert.That(recognizer.IsTestMethod(satelliteDerived)).IsTrue();
    }

    /// <summary>
    /// An attribute that only shares a simple name with a framework type is no test attribute, no matter
    /// which of the well-known types could be resolved for the compilation.
    /// </summary>
    /// <param name="methodName">The method to classify.</param>
    [Test]
    [Arguments("LooksLikeATest")]
    [Arguments("LooksLikeAMarkerBase")]
    [Arguments("DerivesFromALookAlike")]
    public async Task IsTestMethod_LookAlikeAttributeOfTheCompilation_IsNotClassifiedAsATest(string methodName)
    {
        var compilation = CompilationFactory.Create(UnrelatedFixtureSource, includeTUnit: true);
        var method = FindMethod(compilation, "Fixture.UnrelatedCases", methodName);

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

    /// <summary>
    /// Walks the base chain of <paramref name="attribute" /> looking for <paramref name="candidate" />.
    /// </summary>
    /// <param name="attribute">The attribute type to walk.</param>
    /// <param name="candidate">The base type to look for.</param>
    /// <returns><see langword="true" /> when the base chain includes the candidate.</returns>
    private static bool DerivesFrom(INamedTypeSymbol? attribute, INamedTypeSymbol? candidate)
    {
        for (var current = attribute?.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static ImmutableArray<IMethodSymbol> FindTestMethods(Compilation compilation) =>
        TestMethodDiscovery.FindTestMethods(compilation, CreateRecognizer(compilation), CancellationToken.None);

    private static TUnitTestMethodRecognizer CreateRecognizer(Compilation compilation) =>
        new TUnitTestMethodRecognizer(
            TUnitTestFrameworkProbe.GetTestAttributeType(compilation),
            TUnitTestFrameworkProbe.GetBaseTestAttributeType(compilation)
        );

    private static CSharpCompilation CreateTUnitFixture() =>
        CompilationFactory.Create(TUnitFixtureSource, includeTUnit: true, filePath: "Cases.cs");

    private static CSharpCompilation CreateMarkerFixture() =>
        CompilationFactory.Create(MarkerFixtureSource, includeTUnit: true, filePath: "Markers.cs");

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
