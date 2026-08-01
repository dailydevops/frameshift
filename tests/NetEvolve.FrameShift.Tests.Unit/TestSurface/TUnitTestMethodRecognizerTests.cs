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
    private const string CountsTypeName = "Fixture.Counts";
    private const string ClassDataTypeName = "Fixture.CountsWithClassData";
    private const string UnrelatedTypeName = "Fixture.UnrelatedCases";

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
    /// Every shape a test case count is read off, one method per shape, plus the members the data sources
    /// name. The counts these methods are expected to produce are pinned by
    /// <see cref="GetTestCaseCount_EveryShape_IsCountedByItsDataAttributes(string, string)" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three data sources of the framework - <c>StaticDataSourceAttribute</c>,
    /// <c>DelegateDataSourceAttribute</c> and <c>EmptyDataSourceAttribute</c> - cannot appear in source at
    /// all: the first takes a nested array, the second a delegate, neither of which is a legal attribute
    /// argument, and the third is not public on every target framework. They are counted by the same rule
    /// that answers for <c>[ClassDataSource]</c> and <c>[CombinedDataSources]</c>, which are applied here,
    /// because the rule keys on the interface the framework implements on every data source rather than on a
    /// list of names.
    /// </para>
    /// <para>
    /// <c>MatrixRangeAttribute</c> is missing from the netstandard2.0 assets of the framework, which are the
    /// ones a .NET Framework target binds, so it cannot appear in a fixture that has to compile on all eight
    /// target frameworks. <c>[MatrixMethod]</c> covers the same shape: a matrix whose values are not written
    /// out in the source.
    /// </para>
    /// </remarks>
    private const string CountFixtureSource = """
        namespace Fixture;

        using System.Collections.Generic;
        using TUnit.Core;

        /// <summary>
        /// A matrix attribute of a shape the framework itself does not ship: its constructor takes no
        /// argument at all, rather than exactly the one array argument every real variant does. It exists
        /// only to exercise <c>GetMatrixSet</c>'s branch for that other shape.
        /// </summary>
        public sealed class ConstantMatrixAttribute : MatrixAttribute
        {
        }

        public class Counts
        {
            [Test]
            public void Parameterless()
            {
            }

            [Test]
            [Repeat(3)]
            public void RepeatedParameterless()
            {
            }

            [Test]
            [Arguments(1)]
            [Arguments(2)]
            [Arguments(3)]
            public void ThreeInlineRows(int value)
            {
            }

            [Test]
            [Arguments(1)]
            [MethodDataSource(nameof(LoopValues))]
            public void InlineAndLowerBoundSource(int value)
            {
            }

            [Test]
            [Arguments(1)]
            [Arguments(2)]
            [MethodDataSource(nameof(CollectionExpressionValues))]
            public void InlineAndExactSource(int value)
            {
            }

            [Test]
            public void MatrixOfTwoAndThree([Matrix(1, 2)] int left, [Matrix("a", "b", "c")] string right)
            {
            }

            [Test]
            [MatrixDataSource]
            public void MatrixWithExplicitDataSource([Matrix(1, 2)] int left, [Matrix(3, 4)] int right)
            {
            }

            [Test]
            public void MatrixWithNonLiteralValues([Matrix(typeof(int), typeof(long))] object value)
            {
            }

            [Test]
            public void MatrixFromAMethod([MatrixMethod<Counts>(nameof(MatrixValues))] int value)
            {
            }

            [Test]
            public void MatrixWithExclusion([Matrix(1, 2, 3, Excluding = new object[] { 2 })] int value)
            {
            }

            [Test]
            [MatrixExclusion(1, "a")]
            public void MatrixWithMethodLevelExclusion([Matrix(1, 2)] int left, [Matrix("a", "b")] string right)
            {
            }

            [Test]
            public void MatrixWithNonArrayConstructor([ConstantMatrix] int left, [Matrix(1, 2)] int right)
            {
            }

            [Test]
            public void MatrixAndUncoveredParameter([Matrix(1, 2)] int left, bool right)
            {
            }

            [Test]
            [MatrixDataSource]
            public void MatrixOverGeneratedValues(bool flag)
            {
            }

            [Test]
            [MethodDataSource(nameof(CollectionExpressionValues))]
            public void FromACollectionExpression(int value)
            {
            }

            [Test]
            [MethodDataSource(nameof(ArrayCreationValues))]
            public void FromAnArrayCreation(int value)
            {
            }

            [Test]
            [MethodDataSource(nameof(ReturnedValues))]
            public void FromASingleReturn(int value)
            {
            }

            [Test]
            [MethodDataSource(nameof(PropertyValues))]
            public void FromAProperty(int value)
            {
            }

            [Test]
            [MethodDataSource(nameof(FieldValues))]
            public void FromAField(int value)
            {
            }

            [Test]
            [MethodDataSource(nameof(LoopValues))]
            public void FromALoop(int value)
            {
            }

            [Test]
            [MethodDataSource(nameof(ConditionalValues))]
            public void FromACondition(int value)
            {
            }

            [Test]
            [MethodDataSource(nameof(CalledValues))]
            public void FromACall(int value)
            {
            }

            [Test]
            [MethodDataSource(typeof(ExternalValues), nameof(ExternalValues.Values))]
            public void FromAnotherType(int value)
            {
            }

            [Test]
            [MethodDataSource(nameof(OverloadedValues))]
            public void FromAnAmbiguousName(int value)
            {
            }

            [Test]
            [MethodDataSource("NoSuchMember")]
            public void FromAnUnknownMember(int value)
            {
            }

            [Test]
            [InstanceMethodDataSource(nameof(InstanceValues))]
            public void FromAnInstanceMethod(int value)
            {
            }

            [Test]
            [ClassDataSource]
            public void FromAClassDataSource()
            {
            }

            [Test]
            [CombinedDataSources]
            public void FromCombinedDataSources([Arguments(1)] int value)
            {
            }

            [Test]
            public void MissingSource(int value)
            {
            }

            public static int[] CollectionExpressionValues() => [1, 2, 3];

            public static int[] ArrayCreationValues() => new[] { 4, 5, 6 };

            public static int[] ReturnedValues()
            {
                return [7, 8];
            }

            public static int[] PropertyValues => [9, 10];

            public static readonly int[] FieldValues = [11, 12];

            public int[] InstanceValues() => [13, 14];

            public static int[] ConditionalValues() => FieldValues.Length > 0 ? [15] : [16, 17];

            public static int[] CalledValues() => CollectionExpressionValues();

            public static int[] OverloadedValues() => [18];

            public static int[] OverloadedValues(int seed) => [seed];

            public static IEnumerable<int> LoopValues()
            {
                for (var index = 0; index < 3; index++)
                {
                    yield return index;
                }
            }

            public static IEnumerable<int> MatrixValues()
            {
                yield return 1;
            }
        }

        public static class ExternalValues
        {
            public static int[] Values() => [20, 21];
        }

        [Arguments(1)]
        [Arguments(2)]
        public class CountsWithClassData
        {
            public CountsWithClassData(int value)
            {
            }

            [Test]
            public void Parameterless()
            {
            }

            [Test]
            [Arguments(1)]
            [Arguments(2)]
            public void TwoInlineRows(int value)
            {
            }
        }
        """;

    /// <summary>
    /// Look-alikes declared by the compilation itself: one named like the concrete marker, one named like
    /// the marker base type, an attribute deriving from the latter, and one named like the inline data
    /// attribute of the framework.
    /// </summary>
    private const string UnrelatedFixtureSource = """
        namespace Fixture;

        using System;

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class TestAttribute : Attribute
        {
        }

        [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
        public sealed class ArgumentsAttribute : Attribute
        {
            public ArgumentsAttribute(params object[] values)
            {
            }
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

            [Test]
            [Arguments(1)]
            [Arguments(2)]
            public void LookAlikeArguments(int value)
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
            Describe(CreateCountFixture()),
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(markerBase?.ToDisplayString()).IsEqualTo("TUnit.Core.BaseTestAttribute");
            _ = await Assert.That(attribute?.ToDisplayString()).IsEqualTo(metadataName);
            _ = await Assert.That(DerivesFrom(attribute, markerBase)).IsEqualTo(expected);
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(TUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation)).IsNull();
            _ = await Assert.That(found.Length).IsEqualTo(0);
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(recognizer.IsTestMethod(direct)).IsFalse();
            _ = await Assert.That(recognizer.IsTestMethod(derived)).IsFalse();
        }
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
        var method = FindMethod(compilation, UnrelatedTypeName, "LooksLikeATest");

        using (Assert.Multiple())
        {
            _ = await Assert.That(TUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation)).IsNull();
            _ = await Assert.That(CreateRecognizer(compilation).IsTestMethod(method)).IsFalse();
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(markerBase?.ToDisplayString()).IsEqualTo("TUnit.Core.BaseTestAttribute");
            _ = await Assert.That(recognizer.IsTestMethod(method)).IsEqualTo(expected);
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(attributeType?.ToDisplayString()).IsEqualTo("TUnit.Core.TestAttribute");
            _ = await Assert
                .That(new TUnitTestMethodRecognizer(attributeType).IsTestMethod(method))
                .IsEqualTo(expected);
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(recognizer.IsTestMethod(frameworkAttribute)).IsTrue();
            _ = await Assert.That(recognizer.IsTestMethod(dynamicBuilder)).IsTrue();
            _ = await Assert.That(recognizer.IsTestMethod(satelliteAttribute)).IsTrue();
            _ = await Assert.That(recognizer.IsTestMethod(satelliteDerived)).IsTrue();
        }
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
        var method = FindMethod(compilation, UnrelatedTypeName, methodName);

        using (Assert.Multiple())
        {
            _ = await Assert.That(CreateRecognizer(compilation).IsTestMethod(method)).IsFalse();
            _ = await Assert
                .That(new TUnitTestMethodRecognizer(testAttributeType: null).IsTestMethod(method))
                .IsFalse();
        }
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
    /// The counting rules, one row per method of the counting fixture and therefore exhaustive: an exact
    /// integer where the number of input combinations is written out in the source, and a lower bound
    /// wherever the framework would have to execute something to know it.
    /// </summary>
    /// <param name="methodName">The method to count.</param>
    /// <param name="expected">The expected count, spelled the way a manifest spells it.</param>
    [Test]
    [Arguments("Parameterless", "1")]
    [Arguments("RepeatedParameterless", "1")]
    [Arguments("ThreeInlineRows", "3")]
    [Arguments("InlineAndLowerBoundSource", "2+")]
    [Arguments("InlineAndExactSource", "5")]
    [Arguments("MatrixOfTwoAndThree", "6")]
    [Arguments("MatrixWithExplicitDataSource", "4")]
    [Arguments("MatrixWithNonLiteralValues", "2+")]
    [Arguments("MatrixFromAMethod", "1+")]
    [Arguments("MatrixWithExclusion", "1+")]
    [Arguments("MatrixWithMethodLevelExclusion", "1+")]
    [Arguments("MatrixWithNonArrayConstructor", "2+")]
    [Arguments("MatrixAndUncoveredParameter", "2+")]
    [Arguments("MatrixOverGeneratedValues", "1+")]
    [Arguments("FromACollectionExpression", "3")]
    [Arguments("FromAnArrayCreation", "3")]
    [Arguments("FromASingleReturn", "2")]
    [Arguments("FromAProperty", "2")]
    [Arguments("FromAField", "2")]
    [Arguments("FromALoop", "1+")]
    [Arguments("FromACondition", "1+")]
    [Arguments("FromACall", "1+")]
    [Arguments("FromAnotherType", "2")]
    [Arguments("FromAnAmbiguousName", "1+")]
    [Arguments("FromAnUnknownMember", "1+")]
    [Arguments("FromAnInstanceMethod", "2")]
    [Arguments("FromAClassDataSource", "1+")]
    [Arguments("FromCombinedDataSources", "1+")]
    [Arguments("MissingSource", "1+")]
    public async Task GetTestCaseCount_EveryShape_IsCountedByItsDataAttributes(string methodName, string expected)
    {
        var count = CountCases(CountsTypeName, methodName);

        _ = await Assert.That(count.ToString()).IsEqualTo(expected);
    }

    /// <summary>
    /// The guard that keeps the table above exhaustive: a shape added to the fixture without a row of its
    /// own would otherwise be counted by nothing at all. Thirty-one methods carry the shapes, and the two
    /// of the type with a class-level data source are counted separately.
    /// </summary>
    [Test]
    public async Task GetTestCaseCount_TheCountingFixture_DeclaresTheCountedMethodsOnly()
    {
        var found = FindTestMethods(CreateCountFixture());

        _ = await Assert.That(found.Length).IsEqualTo(31);
    }

    /// <summary>
    /// The counter-intuitive part of the contract: a test method without parameters is exactly one case,
    /// because its input values are hardcoded in its body, which makes it as narrow as a single inline row.
    /// </summary>
    [Test]
    public async Task GetTestCaseCount_ParameterlessTestMethod_IsExactlyOneCase()
    {
        var count = CountCases(CountsTypeName, "Parameterless");

        using (Assert.Multiple())
        {
            _ = await Assert.That(count.Value).IsEqualTo(1);
            _ = await Assert.That(count.IsExact).IsTrue();
        }
    }

    /// <summary>
    /// The pinned decision on <c>[Repeat]</c>: it does not multiply the count. A repeated test runs the very
    /// same inputs again, so it widens no input space, and a mutation surviving the single case survives
    /// every repetition of it. Multiplying would make the narrowest tests of all look wide.
    /// </summary>
    [Test]
    public async Task GetTestCaseCount_RepeatedTestMethod_IsStillExactlyOneCase()
    {
        var count = CountCases(CountsTypeName, "RepeatedParameterless");

        using (Assert.Multiple())
        {
            _ = await Assert.That(count.Value).IsEqualTo(1);
            _ = await Assert.That(count.IsExact).IsTrue();
        }
    }

    /// <summary>
    /// Three inline rows are three cases, exactly - the shape the whole heuristic is calibrated against.
    /// </summary>
    [Test]
    public async Task GetTestCaseCount_ThreeInlineRows_AreExactlyThreeCases()
    {
        var count = CountCases(CountsTypeName, "ThreeInlineRows");

        using (Assert.Multiple())
        {
            _ = await Assert.That(count.Value).IsEqualTo(3);
            _ = await Assert.That(count.IsExact).IsTrue();
        }
    }

    /// <summary>
    /// A matrix over two literal value sets of two and three values is exactly their cross product, whether
    /// the method states the matrix explicitly or leaves it to the parameters.
    /// </summary>
    /// <param name="methodName">The method to count.</param>
    /// <param name="expected">The expected size of the cross product.</param>
    [Test]
    [Arguments("MatrixOfTwoAndThree", 6)]
    [Arguments("MatrixWithExplicitDataSource", 4)]
    public async Task GetTestCaseCount_MatrixOverLiteralValueSets_IsExactlyTheCrossProduct(
        string methodName,
        int expected
    )
    {
        var count = CountCases(CountsTypeName, methodName);

        using (Assert.Multiple())
        {
            _ = await Assert.That(count.Value).IsEqualTo(expected);
            _ = await Assert.That(count.IsExact).IsTrue();
        }
    }

    /// <summary>
    /// Every matrix whose true size is not written out in the source is a lower bound: values that are no
    /// literals, a value set taken from a method, an exclusion taking combinations away again, and a
    /// parameter left to the values its type generates.
    /// </summary>
    /// <param name="methodName">The method to count.</param>
    /// <param name="expected">The expected lower bound.</param>
    [Test]
    [Arguments("MatrixWithNonLiteralValues", 2)]
    [Arguments("MatrixFromAMethod", 1)]
    [Arguments("MatrixWithExclusion", 1)]
    [Arguments("MatrixWithMethodLevelExclusion", 1)]
    [Arguments("MatrixWithNonArrayConstructor", 2)]
    [Arguments("MatrixAndUncoveredParameter", 2)]
    [Arguments("MatrixOverGeneratedValues", 1)]
    public async Task GetTestCaseCount_MatrixThatIsNotWrittenOut_IsALowerBound(string methodName, int expected)
    {
        var count = CountCases(CountsTypeName, methodName);

        using (Assert.Multiple())
        {
            _ = await Assert.That(count.Value).IsEqualTo(expected);
            _ = await Assert.That(count.IsExact).IsFalse();
        }
    }

    /// <summary>
    /// Inline rows and a data source on one method add up. The sum stays exact only while every contributing
    /// part is: one row plus a data source resolved by executing a member is at least two cases, while one
    /// row plus a literal sequence of three is exactly five.
    /// </summary>
    [Test]
    public async Task GetTestCaseCount_InlineDataAndADataSource_AddUp()
    {
        var lowerBound = CountCases(CountsTypeName, "InlineAndLowerBoundSource");
        var exact = CountCases(CountsTypeName, "InlineAndExactSource");

        using (Assert.Multiple())
        {
            _ = await Assert.That(lowerBound.Value).IsEqualTo(2);
            _ = await Assert.That(lowerBound.IsExact).IsFalse();
            _ = await Assert.That(exact.Value).IsEqualTo(5);
            _ = await Assert.That(exact.IsExact).IsTrue();
        }
    }

    /// <summary>
    /// A data source naming a member that hands over a literal sequence is exactly as long as that sequence,
    /// no matter which of the shapes carries it.
    /// </summary>
    /// <param name="methodName">The method to count.</param>
    /// <param name="expected">The expected length of the sequence.</param>
    [Test]
    [Arguments("FromACollectionExpression", 3)]
    [Arguments("FromAnArrayCreation", 3)]
    [Arguments("FromASingleReturn", 2)]
    [Arguments("FromAProperty", 2)]
    [Arguments("FromAField", 2)]
    [Arguments("FromAnotherType", 2)]
    [Arguments("FromAnInstanceMethod", 2)]
    public async Task GetTestCaseCount_DataSourceOnALiteralSequence_IsExactlyItsLength(string methodName, int expected)
    {
        var count = CountCases(CountsTypeName, methodName);

        using (Assert.Multiple())
        {
            _ = await Assert.That(count.Value).IsEqualTo(expected);
            _ = await Assert.That(count.IsExact).IsTrue();
        }
    }

    /// <summary>
    /// Everything the framework would have to execute to enumerate is a lower bound of one case: a loop, a
    /// condition, a call, an ambiguous member name, a member that does not exist, and every data source that
    /// names no member at all.
    /// </summary>
    /// <param name="methodName">The method to count.</param>
    [Test]
    [Arguments("FromALoop")]
    [Arguments("FromACondition")]
    [Arguments("FromACall")]
    [Arguments("FromAnAmbiguousName")]
    [Arguments("FromAnUnknownMember")]
    [Arguments("FromAClassDataSource")]
    [Arguments("FromCombinedDataSources")]
    public async Task GetTestCaseCount_DataSourceThatIsNotStaticallyEnumerable_IsALowerBoundOfOne(string methodName)
    {
        var count = CountCases(CountsTypeName, methodName);

        using (Assert.Multiple())
        {
            _ = await Assert.That(count.Value).IsEqualTo(1);
            _ = await Assert.That(count.IsExact).IsFalse();
        }
    }

    /// <summary>
    /// A method with parameters and no data source at all is nothing the framework can run, so no exact
    /// statement is made about it - counting it as one case would claim a narrowness that is not there.
    /// </summary>
    [Test]
    public async Task GetTestCaseCount_MethodWithParametersAndNoDataSource_IsALowerBoundOfOne()
    {
        var count = CountCases(CountsTypeName, "MissingSource");

        using (Assert.Multiple())
        {
            _ = await Assert.That(count.Value).IsEqualTo(1);
            _ = await Assert.That(count.IsExact).IsFalse();
        }
    }

    /// <summary>
    /// A data source on the declaring type multiplies every case of every test method of it by the number of
    /// instances it produces, which is not written out in the method itself. Both the parameterless method
    /// and the one with two inline rows therefore keep their value as a lower bound only.
    /// </summary>
    /// <param name="methodName">The method to count.</param>
    /// <param name="expected">The expected lower bound.</param>
    [Test]
    [Arguments("Parameterless", 1)]
    [Arguments("TwoInlineRows", 2)]
    public async Task GetTestCaseCount_DataSourceOnTheDeclaringType_MakesTheCountALowerBound(
        string methodName,
        int expected
    )
    {
        var count = CountCases(ClassDataTypeName, methodName);

        using (Assert.Multiple())
        {
            _ = await Assert.That(count.Value).IsEqualTo(expected);
            _ = await Assert.That(count.IsExact).IsFalse();
        }
    }

    /// <summary>
    /// An inline data attribute of the compilation itself contributes no case, however exactly it matches the
    /// name of the framework's own: the method is left with parameters it gets nothing for, which is a lower
    /// bound of one. That holds whether or not the well-known types could be resolved.
    /// </summary>
    [Test]
    public async Task GetTestCaseCount_LookAlikeInlineDataAttribute_ContributesNoCase()
    {
        var compilation = CompilationFactory.Create(UnrelatedFixtureSource, includeTUnit: true);
        var method = FindMethod(compilation, UnrelatedTypeName, "LookAlikeArguments");

        var resolved = CreateRecognizer(compilation).GetTestCaseCount(method);
        var unresolved = new TUnitTestMethodRecognizer(testAttributeType: null).GetTestCaseCount(method);

        using (Assert.Multiple())
        {
            _ = await Assert.That(resolved.ToString()).IsEqualTo("1+");
            _ = await Assert.That(unresolved.ToString()).IsEqualTo("1+");
        }
    }

    /// <summary>
    /// Counting is independent of the well-known types the recogniser was built with: the data attributes it
    /// reads are the framework's own by namespace and declaring assembly, so a recogniser that resolved
    /// nothing still counts the very same cases.
    /// </summary>
    [Test]
    public async Task GetTestCaseCount_WithoutAnyResolvedType_CountsTheSameCases()
    {
        var compilation = CreateCountFixture();
        var recognizer = new TUnitTestMethodRecognizer(testAttributeType: null);

        var inline = recognizer.GetTestCaseCount(FindMethod(compilation, CountsTypeName, "ThreeInlineRows"));
        var matrix = recognizer.GetTestCaseCount(FindMethod(compilation, CountsTypeName, "MatrixOfTwoAndThree"));

        using (Assert.Multiple())
        {
            _ = await Assert.That(inline.ToString()).IsEqualTo("3");
            _ = await Assert.That(matrix.ToString()).IsEqualTo("6");
        }
    }

    [Test]
    public async Task GetTestCaseCount_MethodIsNull_ThrowsArgumentNullException()
    {
        var recognizer = new TUnitTestMethodRecognizer(testAttributeType: null);
        var threw = false;

        try
        {
            _ = recognizer.GetTestCaseCount(null!);
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

    private static CSharpCompilation CreateCountFixture() =>
        CompilationFactory.Create(CountFixtureSource, includeTUnit: true, filePath: "Counts.cs");

    /// <summary>
    /// Counts the cases of one method of the counting fixture.
    /// </summary>
    /// <param name="typeName">The metadata name of the declaring type.</param>
    /// <param name="methodName">The name of the method to count.</param>
    /// <returns>The counted number of cases.</returns>
    private static TestCaseCount CountCases(string typeName, string methodName)
    {
        var compilation = CreateCountFixture();

        return CreateRecognizer(compilation).GetTestCaseCount(FindMethod(compilation, typeName, methodName));
    }

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
