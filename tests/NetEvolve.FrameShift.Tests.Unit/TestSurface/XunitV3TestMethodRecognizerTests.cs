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
/// Covers how an xUnit.net v3 test method is recognised: by <c>[Fact]</c>, by <c>[Theory]</c>, by
/// <c>[CulturedFact]</c> and <c>[CulturedTheory]</c>, by an attribute that derives from
/// <c>FactAttribute</c>, by an attribute that merely implements <c>Xunit.v3.IFactAttribute</c> — and never by
/// a data source alone, nor by an attribute or interface that only happens to carry the same simple name, not
/// even when that attribute derives from the <c>FactAttribute</c> of version 2.
/// </summary>
/// <remarks>
/// <para>
/// The marker of version 3 is the interface, not the class: discovery collects a method's attributes as
/// <c>Xunit.v3.IFactAttribute</c>, so an attribute implementing it directly is a test although it shares no
/// base type with <c>FactAttribute</c>. That case is asserted on its own, and against each rule in isolation,
/// because a recogniser hooking only the base attribute passes every other test in this file.
/// </para>
/// <para>
/// The whole file is conditional, because every fixture needs the real <c>xunit.v3.core</c> assembly and
/// that package ships no assets for net6.0 and net7.0. The version 2 suite needs no such guard, its
/// assembly reaches every target framework.
/// </para>
/// <para>
/// The shapes a test method can take — static, generic, inherited, abstract and private — are covered
/// deliberately: whichever of them the recogniser dropped would silently shrink the recorded test surface
/// and make the production side claim mutations are unreachable when they are not.
/// </para>
/// <para>
/// The class is <see langword="internal" /> because <see cref="TestFramework" /> appears in the fixtures it
/// builds; TUnit discovers internal test classes just as well as public ones.
/// </para>
/// </remarks>
internal sealed class XunitV3TestMethodRecognizerTests
{
    private const string CasesTypeName = "Fixture.Cases";
    private const string MixedCasesTypeName = "Fixture.MixedCases";
    private const string CountCasesTypeName = "Fixture.CountCases";

    private const string VersionTwoAssemblyName = "Helper.VersionTwo";
    private const string VersionThreeAssemblyName = "Helper.VersionThree";

    private const string ExpectedTestMethods =
        "Cases.FactTest|Cases.TheoryTest|Cases.DerivedAttributeTest|Cases.CulturedFactTest|"
        + "Cases.CulturedTheoryTest|Cases.MarkerInterfaceTest|Cases.StaticTest|Cases.GenericTest|"
        + "Cases.PrivateTest|AbstractCases.AbstractTest|AbstractCases.InheritedTest";

    /// <summary>
    /// The fixture spells the attributes exactly as a version 2 fixture would, because
    /// <c>Xunit.FactAttribute</c>, <c>Xunit.TheoryAttribute</c> and <c>Xunit.InlineDataAttribute</c> exist in
    /// both major versions. Which version the names bind to is decided by the referenced assembly alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It carries one method per marker version 3 knows: <c>[Fact]</c>, <c>[Theory]</c>, an attribute derived
    /// from <c>FactAttribute</c>, the two shipped <c>[CulturedFact]</c> and <c>[CulturedTheory]</c>, and an
    /// attribute that implements <c>Xunit.v3.IFactAttribute</c> without deriving from <c>FactAttribute</c> at
    /// all. The last one is the case the base type alone cannot see, and version 3 runs it: discovery
    /// collects a method's attributes as <c>IFactAttribute</c>.
    /// </para>
    /// <para>
    /// It also carries one method per data-source attribute without any marker. Those derive from
    /// <c>Xunit.v3.DataAttribute</c> and implement <c>Xunit.v3.IDataAttribute</c>, never the marker
    /// interface, so version 3 would not run them and they must not be recognised.
    /// </para>
    /// </remarks>
    private const string XunitFixtureSource = """
        namespace Fixture;

        using System;
        using System.Collections.Generic;
        using Xunit;
        using Xunit.v3;

        public sealed class ScenarioFactAttribute : FactAttribute
        {
        }

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class MarkerInterfaceFactAttribute : Attribute, IFactAttribute
        {
            public string? DisplayName => null;

            public bool Explicit => false;

            public string? Skip => null;

            public Type[]? SkipExceptions => null;

            public Type? SkipType => null;

            public string? SkipUnless => null;

            public string? SkipWhen => null;

            public string? SourceFilePath => null;

            public int? SourceLineNumber => null;

            public int Timeout => 0;
        }

        public class Cases
        {
            [Fact]
            public void FactTest()
            {
            }

            [Theory]
            [InlineData(1)]
            public void TheoryTest(int value)
            {
            }

            [ScenarioFact]
            public void DerivedAttributeTest()
            {
            }

            [CulturedFact(new string[] { "en-US" })]
            public void CulturedFactTest()
            {
            }

            [CulturedTheory(new string[] { "en-US" })]
            [InlineData(1)]
            public void CulturedTheoryTest(int value)
            {
            }

            [MarkerInterfaceFact]
            public void MarkerInterfaceTest()
            {
            }

            [Fact]
            public static void StaticTest()
            {
            }

            [Fact]
            public void GenericTest<TValue>()
            {
            }

            [Fact]
            private void PrivateTest()
            {
            }

            [InlineData(1)]
            public void InlineDataOnlyMethod(int value)
            {
            }

            [MemberData(nameof(Rows))]
            public void MemberDataOnlyMethod(int value)
            {
            }

            [ClassData(typeof(RowSource))]
            public void ClassDataOnlyMethod(int value)
            {
            }

            public static IEnumerable<object[]> Rows => new[] { new object[] { 1 } };

            public void PlainMethod()
            {
            }
        }

        public sealed class RowSource
        {
        }

        public abstract class AbstractCases
        {
            [Fact]
            public abstract void AbstractTest();

            [Fact]
            public void InheritedTest()
            {
            }
        }

        public sealed class InheritanceDerived : AbstractCases
        {
            public override void AbstractTest()
            {
            }
        }
        """;

    /// <summary>
    /// The fixture of the case-counting rules. It is a compilation of its own, so that the shapes it adds
    /// cannot shift the discovery assertions above, and it carries one method per rule: a parameterless
    /// <c>[Fact]</c>, one and three <c>[InlineData]</c> rows, a <c>[Theory]</c> without any data source, a
    /// member data source per literal shape — implicit and explicit array creation, an expression-bodied
    /// getter, a collection initializer, a <c>TheoryData</c> initializer, a collection expression, a
    /// collection expression with a spread element, an empty sequence, a method, a field, an inherited
    /// member and a body consisting of nothing but <c>yield return</c> statements — and the shapes no static
    /// reading can size: a genuine iterator built from a loop, an array created by length alone, a member
    /// that does not exist and a <c>[ClassData]</c> source.
    /// </summary>
    /// <remarks>
    /// On top of the version 2 shapes it carries the three version 3 knows on its own: <c>[CulturedFact]</c>
    /// and <c>[CulturedTheory]</c>, whose cultures multiply the cases, and a data source that implements
    /// <c>Xunit.v3.IDataAttribute</c> without deriving from <c>Xunit.v3.DataAttribute</c> — the shape a rule
    /// keyed on the base type alone would overlook, and the counterpart of the marker interface of a test.
    /// </remarks>
    private const string CaseCountFixtureSource = """
        namespace Fixture;

        using System;
        using System.Collections.Generic;
        using System.Reflection;
        using System.Threading.Tasks;
        using Xunit;
        using Xunit.Sdk;
        using Xunit.v3;

        public sealed class ScenarioFactAttribute : FactAttribute
        {
        }

        public sealed class ScenarioTheoryAttribute : TheoryAttribute
        {
        }

        [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
        public sealed class MarkerInterfaceDataAttribute : Attribute, IDataAttribute
        {
            public bool? Explicit => null;

            public string? Label => null;

            public string? Skip => null;

            public Type? SkipType => null;

            public string? SkipUnless => null;

            public string? SkipWhen => null;

            public string? TestDisplayName => null;

            public int? Timeout => null;

            public string[]? Traits => null;

            public ValueTask<IReadOnlyCollection<ITheoryDataRow>> GetData(
                MethodInfo testMethod,
                DisposalTracker disposalTracker
            ) => new ValueTask<IReadOnlyCollection<ITheoryDataRow>>(new List<ITheoryDataRow>());

            public bool SupportsDiscoveryEnumeration() => true;
        }

        public sealed class RowSource : List<object[]>
        {
        }

        public class Rows
        {
            public static readonly IEnumerable<object[]> Field = new[]
            {
                new object[] { 1 },
                new object[] { 2 },
                new object[] { 3 },
                new object[] { 4 },
                new object[] { 5 },
            };

            public static IEnumerable<object[]> ImplicitArrayProperty =>
                new[] { new object[] { 1 }, new object[] { 2 } };

            public static IEnumerable<object[]> ExplicitArrayProperty => new object[][] { new object[] { 1 } };

            public static IEnumerable<object[]> GetterProperty
            {
                get => new[] { new object[] { 1 }, new object[] { 2 }, new object[] { 3 } };
            }

            public static IEnumerable<object[]> ListProperty =>
                new List<object[]> { new object[] { 1 }, new object[] { 2 } };

            public static TheoryData<int> TheoryDataProperty => new TheoryData<int> { 1, 2, 3 };

            public static IEnumerable<object[]> CollectionExpressionProperty => [[1], [2]];

            public static IEnumerable<object[]> CollectionExpressionSpreadProperty =>
                [.. Method(), new object[] { 1 }, new object[] { 2 }];

            public static IEnumerable<object[]> EmptyProperty => new object[][] { };

            public static IEnumerable<object[]> SizedArrayProperty => new object[4][];

            public static IEnumerable<object[]> Method() =>
                new[] { new object[] { 1 }, new object[] { 2 }, new object[] { 3 }, new object[] { 4 } };

            public static IEnumerable<object[]> YieldedRows()
            {
                yield return new object[] { 1 };
                yield return new object[] { 2 };
            }

            public static IEnumerable<object[]> Iterator()
            {
                foreach (var value in new[] { 1, 2 })
                {
                    yield return new object[] { value };
                }
            }
        }

        public sealed class DerivedRows : Rows
        {
        }

        public class CountCases
        {
            public static IEnumerable<object[]> LocalRows => new[] { new object[] { 1 }, new object[] { 2 } };

            [Fact]
            public void ParameterlessFact()
            {
            }

            [Theory]
            [InlineData(1)]
            public void OneInlineData(int value)
            {
            }

            [Theory]
            [InlineData(1)]
            [InlineData(2)]
            [InlineData(3)]
            public void ThreeInlineData(int value)
            {
            }

            [Theory]
            public void TheoryWithoutData()
            {
            }

            [Theory]
            [MemberData(nameof(LocalRows))]
            public void LocalMemberData(int value)
            {
            }

            [Theory]
            [MemberData(nameof(Rows.ImplicitArrayProperty), MemberType = typeof(Rows))]
            public void ImplicitArrayMemberData(int value)
            {
            }

            [Theory]
            [MemberData(nameof(Rows.ExplicitArrayProperty), MemberType = typeof(Rows))]
            public void ExplicitArrayMemberData(int value)
            {
            }

            [Theory]
            [MemberData(nameof(Rows.GetterProperty), MemberType = typeof(Rows))]
            public void GetterPropertyMemberData(int value)
            {
            }

            [Theory]
            [MemberData(nameof(Rows.ListProperty), MemberType = typeof(Rows))]
            public void ListMemberData(int value)
            {
            }

            [Theory]
            [MemberData(nameof(Rows.TheoryDataProperty), MemberType = typeof(Rows))]
            public void TheoryDataMemberData(int value)
            {
            }

            [Theory]
            [MemberData(nameof(Rows.CollectionExpressionProperty), MemberType = typeof(Rows))]
            public void CollectionExpressionMemberData(int value)
            {
            }

            [Theory]
            [MemberData(nameof(Rows.CollectionExpressionSpreadProperty), MemberType = typeof(Rows))]
            public void CollectionExpressionSpreadMemberData(int value)
            {
            }

            [Theory]
            [MemberData(nameof(Rows.EmptyProperty), MemberType = typeof(Rows))]
            public void EmptyMemberData(int value)
            {
            }

            [Theory]
            [MemberData(nameof(Rows.SizedArrayProperty), MemberType = typeof(Rows))]
            public void SizedArrayMemberData(int value)
            {
            }

            [Theory]
            [MemberData(nameof(Rows.Method), MemberType = typeof(Rows))]
            public void MethodMemberData(int value)
            {
            }

            [Theory]
            [MemberData(nameof(Rows.Field), MemberType = typeof(Rows))]
            public void FieldMemberData(int value)
            {
            }

            [Theory]
            [MemberData(nameof(Rows.ImplicitArrayProperty), MemberType = typeof(DerivedRows))]
            public void InheritedMemberData(int value)
            {
            }

            [Theory]
            [MemberData(nameof(Rows.YieldedRows), MemberType = typeof(Rows))]
            public void YieldedRowsMemberData(int value)
            {
            }

            [Theory]
            [MemberData(nameof(Rows.Iterator), MemberType = typeof(Rows))]
            public void IteratorMemberData(int value)
            {
            }

            [Theory]
            [MemberData("DoesNotExist")]
            public void MissingMemberData(int value)
            {
            }

            [Theory]
            [ClassData(typeof(RowSource))]
            public void ClassDataTheory(int value)
            {
            }

            [Theory]
            [InlineData(1)]
            [MemberData(nameof(LocalRows))]
            public void InlineDataAndLiteralMemberData(int value)
            {
            }

            [Theory]
            [InlineData(1)]
            [InlineData(2)]
            [MemberData(nameof(Rows.Iterator), MemberType = typeof(Rows))]
            public void InlineDataAndIteratorMemberData(int value)
            {
            }

            [ScenarioFact]
            public void CustomMarkerFact()
            {
            }

            [ScenarioTheory]
            [InlineData(1)]
            [InlineData(2)]
            public void CustomMarkerTheory(int value)
            {
            }

            [CulturedFact(new string[] { "en-US", "fr-FR" })]
            public void CulturedFact()
            {
            }

            [CulturedTheory(new string[] { "en-US" })]
            [InlineData(1)]
            [InlineData(2)]
            public void CulturedTheory(int value)
            {
            }

            [Theory]
            [MarkerInterfaceData]
            public void MarkerInterfaceDataTheory(int value)
            {
            }

            [Theory]
            [InlineData(1)]
            [MarkerInterfaceData]
            public void InlineDataAndMarkerInterfaceData(int value)
            {
            }

            [ScenarioTheory]
            public void CustomMarkerTheoryWithoutData()
            {
            }
        }
        """;

    /// <summary>
    /// A <c>FactAttribute</c> and an <c>IFactAttribute</c> of the project itself, which share nothing with the
    /// framework but their simple names and must therefore never mark a test. The look-alike interface is the
    /// counterpart of the look-alike attribute: now that the marker is an interface, a project declaring one
    /// under that name must not be able to smuggle a method onto the test surface either.
    /// </summary>
    private const string UnrelatedFixtureSource = """
        namespace Fixture;

        using System;

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class FactAttribute : Attribute
        {
        }

        public interface IFactAttribute
        {
        }

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class LookAlikeMarkerAttribute : Attribute, IFactAttribute
        {
        }

        public class UnrelatedCases
        {
            [Fact]
            public void LooksLikeATest()
            {
            }

            [LookAlikeMarker]
            public void AlsoLooksLikeATest()
            {
            }
        }
        """;

    /// <summary>
    /// A test attribute derived from the version 2 <c>FactAttribute</c>, compiled against version 2 alone so
    /// that its base type can only ever be the version 2 one.
    /// </summary>
    private const string VersionTwoHelperSource = """
        namespace Helper;

        public sealed class VersionTwoFactAttribute : Xunit.FactAttribute
        {
        }
        """;

    /// <summary>
    /// The same attribute derived from the version 3 <c>FactAttribute</c>, compiled against version 3 alone.
    /// </summary>
    private const string VersionThreeHelperSource = """
        namespace Helper;

        public sealed class VersionThreeFactAttribute : Xunit.FactAttribute
        {
        }
        """;

    /// <summary>
    /// The fixture that separates the versions. It references both of them at once, which makes
    /// <c>Xunit.FactAttribute</c> an ambiguous name the source may not spell out at all — so each test
    /// carries an attribute from a helper assembly instead, one derived from the version 2 type and one from
    /// the version 3 type. The two methods are indistinguishable by name and separable only by symbol.
    /// </summary>
    private const string MixedFixtureSource = """
        namespace Fixture;

        using Helper;

        public class MixedCases
        {
            [VersionTwoFact]
            public void VersionTwoTest()
            {
            }

            [VersionThreeFact]
            public void VersionThreeTest()
            {
            }
        }
        """;

    [Test]
    public async Task Fixtures_EveryCompilation_CompilesWithoutErrors()
    {
        var errors = new[]
        {
            Describe(CreateXunitFixture()),
            Describe(CreateCaseCountFixture()),
            Describe(CompilationFactory.Create(UnrelatedFixtureSource, TestFramework.XunitV3)),
            Describe(CompilationFactory.Create(UnrelatedFixtureSource)),
            Describe(CreateVersionTwoHelper()),
            Describe(CreateVersionThreeHelper()),
            Describe(CreateMixedFixture()),
        };

        _ = await Assert
            .That(string.Join(" / ", errors.Distinct(StringComparer.Ordinal)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    public async Task FrameworkName_Recognizer_NamesTheVersionItRecognizes()
    {
        var recognizer = CreateRecognizer(CreateXunitFixture());

        _ = await Assert.That(recognizer.FrameworkName).IsEqualTo(XunitV3TestFrameworkProbe.Name);
    }

    /// <summary>
    /// Every shape a test method can take is discovered, in declaration order and without duplicates. The
    /// override of the abstract test is not listed again, because the attribute sits on the declaration
    /// the override replaces.
    /// </summary>
    [Test]
    public async Task FindTestMethods_EveryTestShape_IsDiscoveredInDeclarationOrder()
    {
        var compilation = CreateXunitFixture();

        var found = TestMethodDiscovery.FindTestMethods(
            compilation,
            CreateRecognizer(compilation),
            CancellationToken.None
        );

        _ = await Assert.That(Describe(found)).IsEqualTo(ExpectedTestMethods);
    }

    [Test]
    [Arguments("FactTest", true)]
    [Arguments("TheoryTest", true)]
    [Arguments("DerivedAttributeTest", true)]
    [Arguments("CulturedFactTest", true)]
    [Arguments("CulturedTheoryTest", true)]
    [Arguments("MarkerInterfaceTest", true)]
    [Arguments("StaticTest", true)]
    [Arguments("GenericTest", true)]
    [Arguments("PrivateTest", true)]
    [Arguments("PlainMethod", false)]
    [Arguments("InlineDataOnlyMethod", false)]
    [Arguments("MemberDataOnlyMethod", false)]
    [Arguments("ClassDataOnlyMethod", false)]
    public async Task IsTestMethod_Method_IsClassifiedByItsAttributes(string methodName, bool expected)
    {
        var compilation = CreateXunitFixture();
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, CasesTypeName, methodName);

        _ = await Assert.That(recognizer.IsTestMethod(method)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("AbstractTest")]
    [Arguments("InheritedTest")]
    public async Task IsTestMethod_AbstractAndInheritedMethod_IsClassifiedAsATest(string methodName)
    {
        var compilation = CreateXunitFixture();
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, "Fixture.AbstractCases", methodName);

        _ = await Assert.That(recognizer.IsTestMethod(method)).IsTrue();
    }

    /// <summary>
    /// The marker interface is what version 3 itself keys on, so an attribute implementing
    /// <c>Xunit.v3.IFactAttribute</c> without deriving from <c>FactAttribute</c> is a test — and it is the one
    /// case the base type alone cannot see. Hooking the base attribute only would leave the method off the
    /// test surface and make the production analyzer report its references as unreached.
    /// </summary>
    [Test]
    public async Task IsTestMethod_AttributeImplementingTheMarkerInterfaceOnly_IsClassifiedAsATest()
    {
        var compilation = CreateXunitFixture();
        var method = FindMethod(compilation, CasesTypeName, "MarkerInterfaceTest");
        var attributeType = XunitV3TestFrameworkProbe.GetTestAttributeType(compilation);
        var markerInterface = XunitV3TestFrameworkProbe.GetTestMarkerInterfaceType(compilation);

        using (Assert.Multiple())
        {
            _ = await Assert.That(CreateRecognizer(compilation).IsTestMethod(method)).IsTrue();
            _ = await Assert.That(new XunitV3TestMethodRecognizer(null, markerInterface).IsTestMethod(method)).IsTrue();
            _ = await Assert.That(new XunitV3TestMethodRecognizer(attributeType, null).IsTestMethod(method)).IsFalse();
        }
    }

    /// <summary>
    /// The shipped markers stay recognised through the base attribute alone, so the interface rule adds cases
    /// instead of replacing them. <c>[CulturedFact]</c> and <c>[CulturedTheory]</c> are the two markers
    /// version 3 ships beyond <c>[Fact]</c> and <c>[Theory]</c>, and version 2 has neither.
    /// </summary>
    [Test]
    [Arguments("FactTest")]
    [Arguments("TheoryTest")]
    [Arguments("DerivedAttributeTest")]
    [Arguments("CulturedFactTest")]
    [Arguments("CulturedTheoryTest")]
    public async Task IsTestMethod_ShippedMarker_IsClassifiedByEitherRuleAlone(string methodName)
    {
        var compilation = CreateXunitFixture();
        var method = FindMethod(compilation, CasesTypeName, methodName);
        var attributeType = XunitV3TestFrameworkProbe.GetTestAttributeType(compilation);
        var markerInterface = XunitV3TestFrameworkProbe.GetTestMarkerInterfaceType(compilation);

        using (Assert.Multiple())
        {
            _ = await Assert.That(new XunitV3TestMethodRecognizer(attributeType, null).IsTestMethod(method)).IsTrue();
            _ = await Assert.That(new XunitV3TestMethodRecognizer(null, markerInterface).IsTestMethod(method)).IsTrue();
        }
    }

    /// <summary>
    /// A data source marks no test on its own. <c>[InlineData]</c>, <c>[MemberData]</c> and
    /// <c>[ClassData]</c> implement <c>Xunit.v3.IDataAttribute</c>, never the marker interface, and version 3
    /// requires <c>[Theory]</c> next to them; a recogniser accepting them would put methods on the test
    /// surface that no test run ever executes.
    /// </summary>
    [Test]
    public async Task IsTestMethod_DataSourceAttributeWithoutAMarker_IsNotClassifiedAsATest()
    {
        var compilation = CreateXunitFixture();
        var recognizer = CreateRecognizer(compilation);
        var dataOnly = new[] { "InlineDataOnlyMethod", "MemberDataOnlyMethod", "ClassDataOnlyMethod" };

        var recognized = dataOnly
            .Where(methodName => recognizer.IsTestMethod(FindMethod(compilation, CasesTypeName, methodName)))
            .ToArray();

        _ = await Assert.That(string.Join("|", recognized)).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// An attribute that only shares the simple name is not a test attribute, no matter whether the
    /// framework is referenced at all. The judgement rests on symbol identity, never on a name — for the
    /// marker interface just as much as for the base attribute.
    /// </summary>
    [Test]
    public async Task IsTestMethod_FactAttributeFromAnUnrelatedNamespace_IsNotClassifiedAsATest()
    {
        var compilation = CompilationFactory.Create(UnrelatedFixtureSource, TestFramework.XunitV3);
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, "Fixture.UnrelatedCases", "LooksLikeATest");

        using (Assert.Multiple())
        {
            _ = await Assert.That(recognizer.IsTestMethod(method)).IsFalse();
            _ = await Assert.That(new XunitV3TestMethodRecognizer(null, null).IsTestMethod(method)).IsFalse();
        }
    }

    /// <summary>
    /// The same for the interface rule: an <c>IFactAttribute</c> the project declares itself is a different
    /// symbol than the one of <c>xunit.v3.core</c>, and an attribute implementing it marks no test.
    /// </summary>
    [Test]
    public async Task IsTestMethod_MarkerInterfaceFromAnUnrelatedNamespace_IsNotClassifiedAsATest()
    {
        var compilation = CompilationFactory.Create(UnrelatedFixtureSource, TestFramework.XunitV3);
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, "Fixture.UnrelatedCases", "AlsoLooksLikeATest");

        _ = await Assert.That(recognizer.IsTestMethod(method)).IsFalse();
    }

    /// <summary>
    /// Judging a method fails closed. Without the resolved types — the framework is referenced by name but
    /// its metadata is unavailable — there is no positive evidence for anything, and the recogniser
    /// recognises nothing instead of guessing from a name.
    /// </summary>
    [Test]
    public async Task IsTestMethod_RecognizerWithoutAnAttributeType_RecognizesNothing()
    {
        var compilation = CreateXunitFixture();
        var recognizer = new XunitV3TestMethodRecognizer(null, null);

        var fact = FindMethod(compilation, CasesTypeName, "FactTest");
        var theory = FindMethod(compilation, CasesTypeName, "TheoryTest");
        var derived = FindMethod(compilation, CasesTypeName, "DerivedAttributeTest");
        var markerInterface = FindMethod(compilation, CasesTypeName, "MarkerInterfaceTest");
        var plain = FindMethod(compilation, CasesTypeName, "PlainMethod");

        using (Assert.Multiple())
        {
            _ = await Assert.That(recognizer.IsTestMethod(fact)).IsFalse();
            _ = await Assert.That(recognizer.IsTestMethod(theory)).IsFalse();
            _ = await Assert.That(recognizer.IsTestMethod(derived)).IsFalse();
            _ = await Assert.That(recognizer.IsTestMethod(markerInterface)).IsFalse();
            _ = await Assert.That(recognizer.IsTestMethod(plain)).IsFalse();
        }
    }

    /// <summary>
    /// The crux of the split, on the only fixture that can show it: one compilation referencing both major
    /// versions, with one test per version. Each recogniser accepts the tests of its own version and rejects
    /// the other one's, although both attributes derive from a type named <c>Xunit.FactAttribute</c>.
    /// </summary>
    [Test]
    public async Task IsTestMethod_BothMajorVersionsReferenced_RecognizesOnlyTheOwnVersion()
    {
        var compilation = CreateMixedFixture();
        var versionThree = CreateRecognizer(compilation);
        var versionTwo = new XunitV2TestMethodRecognizer(XunitV2TestFrameworkProbe.GetTestAttributeType(compilation));

        var versionThreeTest = FindMethod(compilation, MixedCasesTypeName, "VersionThreeTest");
        var versionTwoTest = FindMethod(compilation, MixedCasesTypeName, "VersionTwoTest");

        using (Assert.Multiple())
        {
            _ = await Assert.That(versionThree.IsTestMethod(versionThreeTest)).IsTrue();
            _ = await Assert.That(versionThree.IsTestMethod(versionTwoTest)).IsFalse();
            _ = await Assert.That(versionTwo.IsTestMethod(versionTwoTest)).IsTrue();
            _ = await Assert.That(versionTwo.IsTestMethod(versionThreeTest)).IsFalse();
        }
    }

    /// <summary>
    /// Only the tests of version 3 end up on the version 3 test surface, which is what keeps the two
    /// analyzers of one framework from reporting each other's methods.
    /// </summary>
    [Test]
    public async Task FindTestMethods_BothMajorVersionsReferenced_FindsOnlyTheOwnVersion()
    {
        var compilation = CreateMixedFixture();

        var found = TestMethodDiscovery.FindTestMethods(
            compilation,
            CreateRecognizer(compilation),
            CancellationToken.None
        );

        _ = await Assert.That(Describe(found)).IsEqualTo("MixedCases.VersionThreeTest");
    }

    [Test]
    public async Task IsTestMethod_MethodIsNull_ThrowsArgumentNullException()
    {
        var recognizer = new XunitV3TestMethodRecognizer(null, null);

        var exception = Assert.Throws<ArgumentNullException>(() => _ = recognizer.IsTestMethod(null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("method");
    }

    /// <summary>
    /// Every counting rule, on the shape that states it. The expectation is the string form of the count, so
    /// that the value and its exactness are asserted in one place: <c>3</c> is exactly three cases, <c>1+</c>
    /// is a lower bound of one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rules are the ones of version 2, and that is the point of asserting them again here: version 3
    /// renamed the base type of a data source and added an interface next to it, but it did not change a
    /// single count. A parameterless <c>[Fact]</c> is one case and deliberately not exempt, its inputs being
    /// hardcoded in the body is exactly as narrow as a single <c>[InlineData]</c> row. A <c>[Theory]</c>
    /// without any data source is <em>no</em> case at all, because discovery finds no data and version 3
    /// fails the theory instead of running anything.
    /// </para>
    /// <para>
    /// The three shapes that are version 3's own are the reason the rules cannot simply be copied.
    /// <c>[CulturedFact(["en-US", "fr-FR"])]</c> is two test cases and <c>[CulturedTheory]</c> multiplies its
    /// cultures with the data rows, so no marker beyond the shipped <c>[Fact]</c> and <c>[Theory]</c> may
    /// ever be counted exactly. And a data source implementing <c>Xunit.v3.IDataAttribute</c> directly is a
    /// data source, so the theory carrying it is a lower bound of one rather than the zero a rule keyed on
    /// the base type alone would report.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("ParameterlessFact", "1")]
    [Arguments("OneInlineData", "1")]
    [Arguments("ThreeInlineData", "3")]
    [Arguments("TheoryWithoutData", "0")]
    [Arguments("LocalMemberData", "2")]
    [Arguments("ImplicitArrayMemberData", "2")]
    [Arguments("ExplicitArrayMemberData", "1")]
    [Arguments("GetterPropertyMemberData", "3")]
    [Arguments("ListMemberData", "2")]
    [Arguments("TheoryDataMemberData", "3")]
    [Arguments("CollectionExpressionMemberData", "2")]
    [Arguments("CollectionExpressionSpreadMemberData", "1+")]
    [Arguments("EmptyMemberData", "0")]
    [Arguments("MethodMemberData", "4")]
    [Arguments("FieldMemberData", "5")]
    [Arguments("InheritedMemberData", "2")]
    [Arguments("YieldedRowsMemberData", "2")]
    [Arguments("SizedArrayMemberData", "1+")]
    [Arguments("IteratorMemberData", "1+")]
    [Arguments("MissingMemberData", "1+")]
    [Arguments("ClassDataTheory", "1+")]
    [Arguments("InlineDataAndLiteralMemberData", "3")]
    [Arguments("InlineDataAndIteratorMemberData", "3+")]
    [Arguments("CustomMarkerFact", "1+")]
    [Arguments("CustomMarkerTheory", "2+")]
    [Arguments("CustomMarkerTheoryWithoutData", "0")]
    [Arguments("CulturedFact", "1+")]
    [Arguments("CulturedTheory", "2+")]
    [Arguments("MarkerInterfaceDataTheory", "1+")]
    [Arguments("InlineDataAndMarkerInterfaceData", "2+")]
    public async Task GetTestCaseCount_TestShape_IsCountedExactlyOrAsALowerBound(string methodName, string expected)
    {
        var compilation = CreateCaseCountFixture();
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, CountCasesTypeName, methodName);

        _ = await Assert.That(recognizer.GetTestCaseCount(method).ToString()).IsEqualTo(expected);
    }

    /// <summary>
    /// The value and the exactness are two separate answers, and the aggregation of the heuristic rests on
    /// the second one: a single lower bound anywhere suppresses a finding. Both are therefore asserted
    /// without going through the string form.
    /// </summary>
    [Test]
    public async Task GetTestCaseCount_ExactCountAndLowerBound_AreDistinguishedByIsExact()
    {
        var compilation = CreateCaseCountFixture();
        var recognizer = CreateRecognizer(compilation);

        var exact = recognizer.GetTestCaseCount(FindMethod(compilation, CountCasesTypeName, "ThreeInlineData"));
        var bound = recognizer.GetTestCaseCount(FindMethod(compilation, CountCasesTypeName, "CulturedTheory"));

        using (Assert.Multiple())
        {
            _ = await Assert.That(exact.Value).IsEqualTo(3);
            _ = await Assert.That(exact.IsExact).IsTrue();
            _ = await Assert.That(bound.Value).IsEqualTo(2);
            _ = await Assert.That(bound.IsExact).IsFalse();
        }
    }

    /// <summary>
    /// The data marker interface counts just as the base type does. A theory whose only data source
    /// implements <c>Xunit.v3.IDataAttribute</c> directly has at least one case, while the very same theory
    /// without any data source has none — so overlooking the interface would turn a test that runs into one
    /// that reportedly runs nothing.
    /// </summary>
    [Test]
    public async Task GetTestCaseCount_DataSourceImplementingTheMarkerInterfaceOnly_IsSeenAsADataSource()
    {
        var compilation = CreateCaseCountFixture();
        var recognizer = CreateRecognizer(compilation);

        var withSource = recognizer.GetTestCaseCount(
            FindMethod(compilation, CountCasesTypeName, "MarkerInterfaceDataTheory")
        );
        var withoutSource = recognizer.GetTestCaseCount(
            FindMethod(compilation, CountCasesTypeName, "TheoryWithoutData")
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(withSource.ToString()).IsEqualTo("1+");
            _ = await Assert.That(withoutSource.ToString()).IsEqualTo("0");
        }
    }

    /// <summary>
    /// Every method of the counting fixture is a test of version 3, which is what makes the counts above mean
    /// anything: a count is only ever asked for a method that is on the test surface.
    /// </summary>
    [Test]
    public async Task IsTestMethod_EveryMethodOfTheCountingFixture_IsClassifiedAsATest()
    {
        var compilation = CreateCaseCountFixture();
        var recognizer = CreateRecognizer(compilation);

        var notRecognized = compilation
            .GetTypeByMetadataName(CountCasesTypeName)!
            .GetMembers()
            .OfType<IMethodSymbol>()
            .Where(method => method.MethodKind == MethodKind.Ordinary && !recognizer.IsTestMethod(method))
            .Select(method => method.Name);

        _ = await Assert.That(string.Join("|", notRecognized)).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// A recogniser without any resolved type cannot see the data sources either, so it answers the lower
    /// bound that suppresses every finding built on it, instead of the exact three cases the inline data rows
    /// would otherwise be.
    /// </summary>
    [Test]
    public async Task GetTestCaseCount_RecognizerWithoutAnAttributeType_AnswersALowerBoundOfOne()
    {
        var compilation = CreateCaseCountFixture();
        var recognizer = new XunitV3TestMethodRecognizer(null, null);
        var method = FindMethod(compilation, CountCasesTypeName, "ThreeInlineData");

        _ = await Assert.That(recognizer.GetTestCaseCount(method).ToString()).IsEqualTo("1+");
    }

    /// <summary>
    /// The counter is built from whichever of the two types resolved, so a recogniser that only found the
    /// marker interface still counts the cases exactly — both types live in <c>xunit.v3.core</c>.
    /// </summary>
    [Test]
    public async Task GetTestCaseCount_RecognizerWithTheMarkerInterfaceOnly_CountsTheCases()
    {
        var compilation = CreateCaseCountFixture();
        var markerInterface = XunitV3TestFrameworkProbe.GetTestMarkerInterfaceType(compilation);
        var recognizer = new XunitV3TestMethodRecognizer(null, markerInterface);
        var method = FindMethod(compilation, CountCasesTypeName, "ThreeInlineData");

        _ = await Assert.That(recognizer.GetTestCaseCount(method).ToString()).IsEqualTo("3");
    }

    [Test]
    public async Task GetTestCaseCount_MethodIsNull_ThrowsArgumentNullException()
    {
        var recognizer = new XunitV3TestMethodRecognizer(null, null);

        var exception = Assert.Throws<ArgumentNullException>(() => _ = recognizer.GetTestCaseCount(null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("method");
    }

    private static CSharpCompilation CreateXunitFixture() =>
        CompilationFactory.Create(XunitFixtureSource, TestFramework.XunitV3, filePath: "Cases.cs");

    private static CSharpCompilation CreateCaseCountFixture() =>
        CompilationFactory.Create(CaseCountFixtureSource, TestFramework.XunitV3, filePath: "CountCases.cs");

    private static CSharpCompilation CreateVersionTwoHelper() =>
        CompilationFactory.Create(
            VersionTwoHelperSource,
            TestFramework.XunitV2,
            VersionTwoAssemblyName,
            filePath: "VersionTwoFactAttribute.cs"
        );

    private static CSharpCompilation CreateVersionThreeHelper() =>
        CompilationFactory.Create(
            VersionThreeHelperSource,
            TestFramework.XunitV3,
            VersionThreeAssemblyName,
            filePath: "VersionThreeFactAttribute.cs"
        );

    /// <summary>
    /// Builds the compilation that references both major versions, plus the two helper assemblies whose
    /// attributes derive from the one <c>FactAttribute</c> each. The helpers are referenced as compilations,
    /// so their base types bind to the very same assembly symbols the probes resolve their type in.
    /// </summary>
    /// <returns>The created compilation.</returns>
    private static CSharpCompilation CreateMixedFixture() =>
        CompilationFactory.Create(
            MixedFixtureSource,
            TestFramework.All,
            additionalReferences:
            [
                CreateVersionTwoHelper().ToMetadataReference(),
                CreateVersionThreeHelper().ToMetadataReference(),
            ],
            filePath: "MixedCases.cs"
        );

    private static XunitV3TestMethodRecognizer CreateRecognizer(Compilation compilation) =>
        new XunitV3TestMethodRecognizer(
            XunitV3TestFrameworkProbe.GetTestAttributeType(compilation),
            XunitV3TestFrameworkProbe.GetTestMarkerInterfaceType(compilation)
        );

    private static IMethodSymbol FindMethod(Compilation compilation, string typeName, string methodName) =>
        compilation.GetTypeByMetadataName(typeName)!.GetMembers(methodName).OfType<IMethodSymbol>().First();

    private static string Describe(IEnumerable<IMethodSymbol> methods) =>
        string.Join("|", methods.Select(method => method.ContainingType.Name + "." + method.Name));

    private static string Describe(Compilation compilation) =>
        DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation));
}
#endif
