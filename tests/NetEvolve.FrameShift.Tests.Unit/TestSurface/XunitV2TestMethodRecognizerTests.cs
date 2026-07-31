namespace NetEvolve.FrameShift.Tests.Unit;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Tests.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers how an xUnit v2 test method is recognised: by <c>[Fact]</c>, by <c>[Theory]</c>, by an attribute
/// that derives from <c>FactAttribute</c>, and never by an attribute that only happens to carry the same
/// simple name.
/// </summary>
/// <remarks>
/// <para>
/// The recogniser matches the attribute type resolved inside <c>xunit.core</c> and its derivations, and
/// nothing else. There is deliberately no rule matching an attribute by its simple name, so a
/// <c>FactAttribute</c> of the project itself - or of xUnit v3 - is not a v2 test attribute, and a
/// recogniser without a resolved type recognises nothing at all.
/// </para>
/// <para>
/// The shapes a test method can take - static, generic, inherited, abstract and private - are covered
/// deliberately: whichever of them the recogniser dropped would silently shrink the recorded test surface
/// and make the production side claim mutations are unreachable when they are not.
/// </para>
/// <para>
/// Everything here runs on every target framework, because <c>xunit.core</c> ships assets for all of them.
/// </para>
/// </remarks>
public class XunitV2TestMethodRecognizerTests
{
    private const string FrameworkName = "xUnit v2";

    private const string CasesTypeName = "Fixture.Cases";
    private const string AbstractCasesTypeName = "Fixture.AbstractCases";
    private const string CountCasesTypeName = "Fixture.CountCases";

    private const string ExpectedTestMethods =
        "Cases.FactTest|Cases.TheoryTest|Cases.DerivedAttributeTest|Cases.StaticTest|"
        + "Cases.GenericTest|Cases.PrivateTest|AbstractCases.AbstractTest|AbstractCases.InheritedTest";

    /// <summary>
    /// The fixture carries one method per marker version 2 has - <c>[Fact]</c>, <c>[Theory]</c> and an
    /// attribute derived from <c>FactAttribute</c> - and one method per data-source attribute without any
    /// marker. A data source derives from <c>Xunit.Sdk.DataAttribute</c> and never from
    /// <c>FactAttribute</c>, so version 2 would not run those methods and they must not be recognised.
    /// </summary>
    private const string XunitFixtureSource = """
        namespace Fixture;

        using System.Collections.Generic;
        using Xunit;

        public sealed class ScenarioFactAttribute : FactAttribute
        {
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
    /// member data source per literal shape - implicit and explicit array creation, an expression-bodied
    /// getter, a collection initializer, a <c>TheoryData</c> initializer, a collection expression, an empty
    /// sequence, a method, a field and an inherited member - and the shapes no static reading can size: an
    /// iterator, an array created by length alone, a member that does not exist and a <c>[ClassData]</c>
    /// source. The last methods mix the rules: inline data next to an exact and next to an inexact data
    /// source, and the custom markers whose own discoverer may multiply the cases.
    /// </summary>
    private const string CaseCountFixtureSource = """
        namespace Fixture;

        using System.Collections.Generic;
        using Xunit;

        public sealed class ScenarioFactAttribute : FactAttribute
        {
        }

        public sealed class ScenarioTheoryAttribute : TheoryAttribute
        {
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

            public static IEnumerable<object[]> EmptyProperty => new object[][] { };

            public static IEnumerable<object[]> SizedArrayProperty => new object[4][];

            public static IEnumerable<object[]> Method() =>
                new[] { new object[] { 1 }, new object[] { 2 }, new object[] { 3 }, new object[] { 4 } };

            public static IEnumerable<object[]> Iterator()
            {
                yield return new object[] { 1 };
                yield return new object[] { 2 };
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

            [ScenarioTheory]
            public void CustomMarkerTheoryWithoutData()
            {
            }
        }
        """;

    /// <summary>
    /// A <c>FactAttribute</c> of the project itself, which shares nothing with the framework but its
    /// simple name and must therefore never mark a test.
    /// </summary>
    private const string UnrelatedFixtureSource = """
        namespace Fixture;

        using System;

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class FactAttribute : Attribute
        {
        }

        public class UnrelatedCases
        {
            [Fact]
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
            Describe(CreateXunitV2Fixture()),
            Describe(CreateCaseCountFixture()),
            Describe(CreateUnrelatedFixture()),
            Describe(CompilationFactory.Create(UnrelatedFixtureSource)),
        };

        _ = await Assert
            .That(string.Join(" / ", errors.Distinct(StringComparer.Ordinal)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    public async Task FrameworkName_Recognizer_NamesTheFramework()
    {
        var recognizer = CreateRecognizer(CreateXunitV2Fixture());

        _ = await Assert.That(recognizer.FrameworkName).IsEqualTo(FrameworkName);
    }

    /// <summary>
    /// Every shape a test method can take is discovered, in declaration order and without duplicates. The
    /// override of the abstract test is not listed again, because the attribute sits on the declaration
    /// the override replaces.
    /// </summary>
    [Test]
    public async Task FindTestMethods_EveryTestShape_IsDiscoveredInDeclarationOrder()
    {
        var compilation = CreateXunitV2Fixture();

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
    [Arguments("StaticTest", true)]
    [Arguments("GenericTest", true)]
    [Arguments("PrivateTest", true)]
    [Arguments("PlainMethod", false)]
    [Arguments("InlineDataOnlyMethod", false)]
    [Arguments("MemberDataOnlyMethod", false)]
    [Arguments("ClassDataOnlyMethod", false)]
    public async Task IsTestMethod_Method_IsClassifiedByItsAttributes(string methodName, bool expected)
    {
        var compilation = CreateXunitV2Fixture();
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, CasesTypeName, methodName);

        _ = await Assert.That(recognizer.IsTestMethod(method)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("AbstractTest")]
    [Arguments("InheritedTest")]
    public async Task IsTestMethod_AbstractAndInheritedMethod_IsClassifiedAsATest(string methodName)
    {
        var compilation = CreateXunitV2Fixture();
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, AbstractCasesTypeName, methodName);

        _ = await Assert.That(recognizer.IsTestMethod(method)).IsTrue();
    }

    /// <summary>
    /// A data source marks no test on its own. <c>[InlineData]</c>, <c>[MemberData]</c> and
    /// <c>[ClassData]</c> derive from <c>Xunit.Sdk.DataAttribute</c>, not from <c>FactAttribute</c>, and
    /// version 2 requires <c>[Theory]</c> next to them; a recogniser accepting them would put methods on the
    /// test surface that no test run ever executes.
    /// </summary>
    [Test]
    public async Task IsTestMethod_DataSourceAttributeWithoutAMarker_IsNotClassifiedAsATest()
    {
        var compilation = CreateXunitV2Fixture();
        var recognizer = CreateRecognizer(compilation);
        var dataOnly = new[] { "InlineDataOnlyMethod", "MemberDataOnlyMethod", "ClassDataOnlyMethod" };

        var recognized = dataOnly
            .Where(methodName => recognizer.IsTestMethod(FindMethod(compilation, CasesTypeName, methodName)))
            .ToArray();

        _ = await Assert.That(string.Join("|", recognized)).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// An attribute that only shares the simple name is not a test attribute, no matter whether the
    /// framework is referenced at all - the recogniser compares symbols, never names.
    /// </summary>
    [Test]
    public async Task IsTestMethod_FactAttributeFromAnUnrelatedNamespace_IsNotClassifiedAsATest()
    {
        var compilation = CreateUnrelatedFixture();
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, "Fixture.UnrelatedCases", "LooksLikeATest");

        using (Assert.Multiple())
        {
            _ = await Assert.That(recognizer.IsTestMethod(method)).IsFalse();
            _ = await Assert.That(new XunitV2TestMethodRecognizer(null).IsTestMethod(method)).IsFalse();
        }
    }

    /// <summary>
    /// A recogniser whose attribute type could not be resolved finds no test rather than throwing, and it
    /// does not guess from the simple name either: judging fails closed, so a compilation whose tests
    /// cannot be seen is never judged at all.
    /// </summary>
    [Test]
    public async Task IsTestMethod_RecognizerWithoutAnAttributeType_FindsNoTest()
    {
        var compilation = CreateXunitV2Fixture();
        var recognizer = new XunitV2TestMethodRecognizer(null);

        var fact = FindMethod(compilation, CasesTypeName, "FactTest");
        var derived = FindMethod(compilation, CasesTypeName, "DerivedAttributeTest");
        var plain = FindMethod(compilation, CasesTypeName, "PlainMethod");

        using (Assert.Multiple())
        {
            _ = await Assert.That(recognizer.FrameworkName).IsEqualTo(FrameworkName);
            _ = await Assert.That(recognizer.IsTestMethod(fact)).IsFalse();
            _ = await Assert.That(recognizer.IsTestMethod(derived)).IsFalse();
            _ = await Assert.That(recognizer.IsTestMethod(plain)).IsFalse();
        }
    }

    [Test]
    public async Task FindTestMethods_RecognizerWithoutAnAttributeType_FindsNothing()
    {
        var compilation = CreateXunitV2Fixture();

        var found = TestMethodDiscovery.FindTestMethods(
            compilation,
            new XunitV2TestMethodRecognizer(null),
            CancellationToken.None
        );

        _ = await Assert.That(found.Length).IsEqualTo(0);
    }

    [Test]
    public async Task IsTestMethod_MethodIsNull_ThrowsArgumentNullException()
    {
        var compilation = CreateXunitV2Fixture();
        var recognizer = CreateRecognizer(compilation);
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
    /// Every counting rule, on the shape that states it. The expectation is the string form of the count, so
    /// that the value and its exactness are asserted in one place: <c>3</c> is exactly three cases, <c>1+</c>
    /// is a lower bound of one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A parameterless <c>[Fact]</c> is one case and deliberately not exempt: its inputs are hardcoded in the
    /// body, which is exactly as narrow as a single <c>[InlineData]</c> row. A <c>[Theory]</c> without any
    /// data source is <em>no</em> case at all, because discovery finds no data and version 2 fails the theory
    /// with <c>No data found</c> instead of running anything - inventing a case there would let the heuristic
    /// name a test case that never runs.
    /// </para>
    /// <para>
    /// A member data source is exact only where its rows are written out literally in the compilation. An
    /// iterator, an array created by length alone and a member that does not exist all stay a lower bound,
    /// because only executing the member would give the answer and an analyzer must not execute anything. A
    /// custom marker degrades the count to a lower bound as well: its test-case discoverer may multiply the
    /// cases, which is no hypothetical - version 3 ships exactly that in <c>[CulturedFact]</c>.
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
    [Arguments("EmptyMemberData", "0")]
    [Arguments("MethodMemberData", "4")]
    [Arguments("FieldMemberData", "5")]
    [Arguments("InheritedMemberData", "2")]
    [Arguments("SizedArrayMemberData", "1+")]
    [Arguments("IteratorMemberData", "1+")]
    [Arguments("MissingMemberData", "1+")]
    [Arguments("ClassDataTheory", "1+")]
    [Arguments("InlineDataAndLiteralMemberData", "3")]
    [Arguments("InlineDataAndIteratorMemberData", "3+")]
    [Arguments("CustomMarkerFact", "1+")]
    [Arguments("CustomMarkerTheory", "2+")]
    [Arguments("CustomMarkerTheoryWithoutData", "0")]
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
        var bound = recognizer.GetTestCaseCount(FindMethod(compilation, CountCasesTypeName, "ClassDataTheory"));

        using (Assert.Multiple())
        {
            _ = await Assert.That(exact.Value).IsEqualTo(3);
            _ = await Assert.That(exact.IsExact).IsTrue();
            _ = await Assert.That(bound.Value).IsEqualTo(1);
            _ = await Assert.That(bound.IsExact).IsFalse();
        }
    }

    /// <summary>
    /// Every method of the counting fixture is a test of version 2, which is what makes the counts above mean
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
    /// A recogniser without a resolved attribute type cannot see the data sources either, so it answers the
    /// lower bound that suppresses every finding built on it, instead of the exact three cases the inline
    /// data rows would otherwise be.
    /// </summary>
    [Test]
    public async Task GetTestCaseCount_RecognizerWithoutAnAttributeType_AnswersALowerBoundOfOne()
    {
        var compilation = CreateCaseCountFixture();
        var recognizer = new XunitV2TestMethodRecognizer(null);
        var method = FindMethod(compilation, CountCasesTypeName, "ThreeInlineData");

        _ = await Assert.That(recognizer.GetTestCaseCount(method).ToString()).IsEqualTo("1+");
    }

    [Test]
    public async Task GetTestCaseCount_MethodIsNull_ThrowsArgumentNullException()
    {
        var compilation = CreateCaseCountFixture();
        var recognizer = CreateRecognizer(compilation);
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

    private static CSharpCompilation CreateXunitV2Fixture() =>
        CompilationFactory.Create(XunitFixtureSource, TestFramework.XunitV2, filePath: "Cases.cs");

    private static CSharpCompilation CreateCaseCountFixture() =>
        CompilationFactory.Create(CaseCountFixtureSource, TestFramework.XunitV2, filePath: "CountCases.cs");

    private static CSharpCompilation CreateUnrelatedFixture() =>
        CompilationFactory.Create(UnrelatedFixtureSource, TestFramework.XunitV2, filePath: "Unrelated.cs");

    private static XunitV2TestMethodRecognizer CreateRecognizer(Compilation compilation) =>
        new XunitV2TestMethodRecognizer(XunitV2TestFrameworkProbe.GetTestAttributeType(compilation));

    private static IMethodSymbol FindMethod(Compilation compilation, string typeName, string methodName) =>
        compilation.GetTypeByMetadataName(typeName)!.GetMembers(methodName).OfType<IMethodSymbol>().First();

    private static string Describe(IEnumerable<IMethodSymbol> methods) =>
        string.Join("|", methods.Select(method => method.ContainingType.Name + "." + method.Name));

    private static string Describe(Compilation compilation) =>
        DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation));
}
