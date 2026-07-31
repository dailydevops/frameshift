namespace NetEvolve.FrameShift.Tests.Unit;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Tests.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers how an NUnit test method is recognised: by an attribute implementing
/// <c>ISimpleTestBuilder</c> or <c>ITestBuilder</c>, which is the rule NUnit's own discovery applies.
/// </summary>
/// <remarks>
/// <para>
/// Every attribute NUnit ships that builds tests from a method is asserted individually, because a
/// recogniser keyed on the three obvious names silently misses <c>[Theory]</c> and the combining
/// strategies, and every method it misses drops its production references from the manifest and makes the
/// production side report reachable code as untested.
/// </para>
/// <para>
/// The counter-direction is asserted just as explicitly: the attributes that decorate a test without
/// making a method one — <c>[Repeat]</c>, <c>[Retry]</c>, <c>[Values]</c>, <c>[SetUp]</c>,
/// <c>[TearDown]</c>, <c>[Category]</c> and the class-level <c>[TestFixture]</c> — must all be refused, as
/// must a look-alike from an assembly that is not NUnit's.
/// </para>
/// <para>
/// The shapes a test method can take — static, generic, inherited, abstract and private — are covered
/// deliberately: whichever of them the recogniser dropped would silently shrink the recorded test surface
/// and make the production side claim mutations are unreachable when they are not.
/// </para>
/// <para>
/// The case count is covered shape by shape, and in two groups that must never be mixed up. An exact count
/// is asserted as the number <em>and</em> as being exact, because only an exact one may ever contribute to
/// a finding. A lower bound is asserted as the number <em>and</em> as not being exact, because the whole
/// point of a bound is that it suppresses the finding: NUnit generates the cases of a source, a theory or a
/// pairwise combination while discovering tests, and mistaking any of those for a single case would report
/// a gap that is not there.
/// </para>
/// </remarks>
public class NUnitTestMethodRecognizerTests
{
    private const string CasesTypeName = "Fixture.Cases";
    private const string CountCasesTypeName = "Fixture.CountCases";
    private const string MarkerCasesTypeName = "Fixture.MarkerCases";
    private const string NonMarkerCasesTypeName = "Fixture.NonMarkerCases";
    private const string LookAlikeCasesTypeName = "Fixture.LookAlikeCases";

    private const string FrameworkAssemblyName = "nunit.framework.satellite";
    private const string ForeignAssemblyName = "Foreign.Satellite";

    private const string CasesPath = "Cases.cs";
    private const string SatellitePath = "Satellite.cs";

    private const string ExpectedTestMethods =
        "Cases.PlainTest|Cases.TestCaseTest|Cases.TestCaseSourceTest|Cases.DerivedAttributeTest|"
        + "Cases.StaticTest|Cases.GenericTest|Cases.PrivateTest|AbstractCases.AbstractTest|"
        + "AbstractCases.InheritedTest";

    private const string NUnitFixtureSource = """
        namespace Fixture;

        using System.Collections.Generic;
        using NUnit.Framework;

        public sealed class ScenarioTestAttribute : TestAttribute
        {
        }

        public class Cases
        {
            [Test]
            public void PlainTest()
            {
            }

            [TestCase(1)]
            public void TestCaseTest(int value)
            {
            }

            [TestCaseSource(nameof(Values))]
            public void TestCaseSourceTest(int value)
            {
            }

            [ScenarioTest]
            public void DerivedAttributeTest()
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

            [Test]
            private void PrivateTest()
            {
            }

            public void PlainMethod()
            {
            }

            public static IEnumerable<int> Values()
            {
                return new int[] { 1 };
            }
        }

        public abstract class AbstractCases
        {
            [Test]
            public abstract void AbstractTest();

            [Test]
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

        [TestFixture]
        public class FixtureOnlyCases
        {
            public void NotATest()
            {
            }
        }
        """;

    /// <summary>
    /// One method per attribute of the framework that builds tests from a method, and one per attribute
    /// that decorates a method without making it a test. Derivations of the less obvious markers are
    /// included, because a specialisation inherits the builder interface from its base attribute.
    /// </summary>
    private const string MarkerFixtureSource = """
        namespace Fixture;

        using System.Collections.Generic;
        using NUnit.Framework;

        public sealed class ScenarioTestAttribute : TestAttribute
        {
        }

        public sealed class ScenarioTheoryAttribute : TheoryAttribute
        {
        }

        public sealed class ScenarioTestCaseSourceAttribute : TestCaseSourceAttribute
        {
            public ScenarioTestCaseSourceAttribute(string sourceName)
                : base(sourceName)
            {
            }
        }

        public class MarkerCases
        {
            [Test]
            public void TestMarked()
            {
            }

            [TestCase(1)]
            public void TestCaseMarked(int value)
            {
            }

            [TestCaseSource(nameof(Values))]
            public void TestCaseSourceMarked(int value)
            {
            }

            [Theory]
            public void TheoryMarked(bool value)
            {
            }

            [Combinatorial]
            public void CombinatorialMarked([Values(1, 2)] int value)
            {
            }

            [Pairwise]
            public void PairwiseMarked([Values(1, 2)] int value)
            {
            }

            [Sequential]
            public void SequentialMarked([Values(1, 2)] int value)
            {
            }

            [ScenarioTest]
            public void DerivedFromTestMarked()
            {
            }

            [ScenarioTheory]
            public void DerivedFromTheoryMarked(bool value)
            {
            }

            [ScenarioTestCaseSource(nameof(Values))]
            public void DerivedFromTestCaseSourceMarked(int value)
            {
            }

            public static IEnumerable<int> Values()
            {
                return new int[] { 1 };
            }
        }

        public class NonMarkerCases
        {
            [Repeat(2)]
            public void RepeatOnly()
            {
            }

            [Retry(2)]
            public void RetryOnly()
            {
            }

            [Category("slow")]
            public void CategoryOnly()
            {
            }

            [Description("documented")]
            public void DescriptionOnly()
            {
            }

            [Order(1)]
            public void OrderOnly()
            {
            }

            [SetUp]
            public void SetUpOnly()
            {
            }

            [TearDown]
            public void TearDownOnly()
            {
            }

            [Explicit]
            public void ExplicitOnly()
            {
            }

            public void ValuesOnly([Values(1, 2)] int value)
            {
            }

            public void Undecorated()
            {
            }
        }
        """;

    /// <summary>
    /// One method per shape a case count can be derived from, and one source member per shape a sequence
    /// length can be read from. The methods whose count is a lower bound are declared right next to the
    /// exact ones on purpose: <c>[TestCaseSource]</c> of a computed sequence looks exactly like
    /// <c>[TestCaseSource]</c> of a listed one at the call site, and only the declaration of the source
    /// decides.
    /// </summary>
    private const string CountFixtureSource = """
        namespace Fixture;

        using System.Collections.Generic;
        using System.Linq;
        using NUnit.Framework;

        public sealed class ScenarioTestAttribute : TestAttribute
        {
        }

        public sealed class ScenarioTestCaseSourceAttribute : TestCaseSourceAttribute
        {
            public ScenarioTestCaseSourceAttribute(string sourceName)
                : base(sourceName)
            {
            }
        }

        public static class ExternalSource
        {
            public static readonly int[] Rows = new int[] { 1, 2 };
        }

        public sealed class TypeSource : List<int>
        {
            public TypeSource()
            {
                Add(1);
            }
        }

        public class CountCases
        {
            [Test]
            public void Parameterless()
            {
            }

            [ScenarioTest]
            public void DerivedMarker()
            {
            }

            [Test]
            [Repeat(5)]
            public void Repeated()
            {
            }

            [Test]
            [Retry(3)]
            public void Retried()
            {
            }

            [TestCase(1)]
            public void SingleRow(int value)
            {
            }

            [TestCase(1)]
            [TestCase(2)]
            [TestCase(3)]
            public void ThreeRows(int value)
            {
            }

            [TestCaseSource(nameof(ArrayRows))]
            public void SourceFromArrayField(int value)
            {
            }

            [TestCaseSource(nameof(CollectionRows))]
            public void SourceFromCollectionProperty(int value)
            {
            }

            [TestCaseSource(nameof(ListRows))]
            public void SourceFromInitializedList(int value)
            {
            }

            [TestCaseSource(nameof(ReturnedRows))]
            public void SourceFromReturningMethod(int value)
            {
            }

            [TestCaseSource(nameof(YieldedRows))]
            public void SourceFromYieldingMethod(int value)
            {
            }

            [ScenarioTestCaseSource(nameof(ArrayRows))]
            public void SourceFromDerivedAttribute(int value)
            {
            }

            [TestCaseSource(typeof(ExternalSource), nameof(ExternalSource.Rows))]
            public void SourceFromAnotherType(int value)
            {
            }

            [TestCase(1)]
            [TestCase(2)]
            [TestCaseSource(nameof(ArrayRows))]
            public void RowsAndSource(int value)
            {
            }

            [Test]
            public void OneValuedParameter([Values(1, 2)] int value)
            {
            }

            [Test]
            public void TwoValuedParameters([Values(1, 2)] int first, [Values(3, 4, 5)] int second)
            {
            }

            [Combinatorial]
            public void CombinatorialParameters([Values(1, 2)] int first, [Values(3, 4, 5)] int second)
            {
            }

            [Sequential]
            public void SequentialParameters([Values(1, 2)] int first, [Values(3, 4, 5)] int second)
            {
            }

            [Test]
            public void FourValuedParameter([Values(1, 2, 3, 4)] int value)
            {
            }

            [Test]
            public void RangedParameter([Range(1, 3)] int value)
            {
            }

            [Test]
            public void SteppedRangeParameter([Range(0, 10, 5)] int value)
            {
            }

            [Test]
            public void DescendingRangeParameter([Range(3, 1, -1)] int value)
            {
            }

            [Test]
            public void TwiceRangedParameter([Range(1, 2)] [Range(5, 6)] int value)
            {
            }

            [Test]
            public void CountedRandomParameter([Random(4)] int value)
            {
            }

            [Test]
            public void BoundedRandomParameter([Random(1, 10, 2)] int value)
            {
            }

            [Test]
            public void ValueSourcedParameter([ValueSource(nameof(ArrayRows))] int value)
            {
            }

            [Theory]
            public void TheoryDriven(bool value)
            {
            }

            [TestCaseSource(nameof(ComputedRows))]
            public void SourceFromComputation(int value)
            {
            }

            [TestCaseSource(nameof(SpreadRows))]
            public void SourceFromSpread(int value)
            {
            }

            [TestCaseSource(nameof(ConditionallyYieldedRows))]
            public void SourceFromConditionalYield(int value)
            {
            }

            [TestCaseSource(typeof(TypeSource))]
            public void SourceFromAType(int value)
            {
            }

            [TestCaseSource("Absent")]
            public void SourceFromAnAbsentMember(int value)
            {
            }

            [TestCase(1)]
            [TestCaseSource(nameof(ComputedRows))]
            public void RowAndComputedSource(int value)
            {
            }

            [Pairwise]
            public void PairwiseParameters([Values(1, 2)] int first, [Values(3, 4, 5)] int second)
            {
            }

            [Test]
            public void ParameterWithoutData(int value)
            {
            }

            [Test]
            public void PartiallyValuedParameters([Values(1, 2)] int first, int second)
            {
            }

            [Test]
            public void EmptyValuesParameter([Values] bool value)
            {
            }

            [Test]
            public void FloatingRangeParameter([Range(0.0, 1.0, 0.25)] double value)
            {
            }

            [Test]
            public void AbsentValueSourceParameter([ValueSource("Absent")] int value)
            {
            }

            public static readonly int[] ArrayRows = new int[] { 1, 2, 3 };

            public static readonly List<int> ListRows = new List<int> { 1, 2, 3, 4 };

            public static IEnumerable<int> CollectionRows => [1, 2];

            public static IEnumerable<int> ReturnedRows()
            {
                return new int[] { 1, 2, 3, 4, 5 };
            }

            public static IEnumerable<int> YieldedRows()
            {
                yield return 1;
                yield return 2;
            }

            public static IEnumerable<int> ComputedRows()
            {
                return Enumerable.Range(1, 3);
            }

            public static IEnumerable<int> SpreadRows()
            {
                return [.. ArrayRows];
            }

            public static IEnumerable<int> ConditionallyYieldedRows()
            {
                yield return 1;

                if (ArrayRows.Length > 2)
                {
                    yield return 2;
                }
            }
        }
        """;

    /// <summary>
    /// A <c>TestAttribute</c> of the project itself, which shares nothing with the framework but its
    /// simple name and must therefore never mark a test.
    /// </summary>
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

    /// <summary>
    /// A satellite declaring interfaces under the framework's exact full names, so that a fixture can
    /// control which assembly the builder interfaces are declared in and thereby exercise the name-based
    /// rule on its own.
    /// </summary>
    private const string SatelliteSource = """
        namespace NUnit.Framework.Interfaces;

        public interface ISimpleTestBuilder
        {
        }

        public interface ITestBuilder
        {
        }
        """;

    private const string SatelliteConsumerSource = """
        namespace Fixture;

        using System;
        using NUnit.Framework.Interfaces;

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class SimpleBuilderAttribute : Attribute, ISimpleTestBuilder
        {
        }

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class BuilderAttribute : Attribute, ITestBuilder
        {
        }

        public class LookAlikeCases
        {
            [SimpleBuilder]
            public void SimpleBuilderMarked()
            {
            }

            [Builder]
            public void BuilderMarked()
            {
            }

            public void Undecorated()
            {
            }
        }
        """;

    [Test]
    public async Task Fixtures_EveryCompilation_CompilesWithoutErrors()
    {
        var errors = new[]
        {
            Describe(CreateNUnitFixture()),
            Describe(CreateMarkerFixture()),
            Describe(CreateCountFixture()),
            Describe(CompilationFactory.Create(UnrelatedFixtureSource, TestFramework.NUnit)),
            Describe(CompilationFactory.Create(UnrelatedFixtureSource)),
            Describe(CreateSatelliteConsumer(FrameworkAssemblyName)),
            Describe(CreateSatelliteConsumer(ForeignAssemblyName)),
        };

        _ = await Assert
            .That(string.Join(" / ", errors.Distinct(StringComparer.Ordinal)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    public async Task FrameworkName_Recognizer_NamesTheFramework()
    {
        var recognizer = CreateRecognizer(CreateNUnitFixture());

        _ = await Assert.That(recognizer.FrameworkName).IsEqualTo("NUnit");
    }

    /// <summary>
    /// Every shape a test method can take is discovered, in declaration order and without duplicates. The
    /// override of the abstract test is not listed again, because the attribute sits on the declaration
    /// the override replaces, and the class carrying only <c>[TestFixture]</c> contributes nothing.
    /// </summary>
    [Test]
    public async Task FindTestMethods_EveryTestShape_IsDiscoveredInDeclarationOrder()
    {
        var compilation = CreateNUnitFixture();

        var found = TestMethodDiscovery.FindTestMethods(
            compilation,
            CreateRecognizer(compilation),
            CancellationToken.None
        );

        _ = await Assert.That(Describe(found)).IsEqualTo(ExpectedTestMethods);
    }

    [Test]
    [Arguments("PlainTest", true)]
    [Arguments("TestCaseTest", true)]
    [Arguments("TestCaseSourceTest", true)]
    [Arguments("DerivedAttributeTest", true)]
    [Arguments("StaticTest", true)]
    [Arguments("GenericTest", true)]
    [Arguments("PrivateTest", true)]
    [Arguments("PlainMethod", false)]
    [Arguments("Values", false)]
    public async Task IsTestMethod_Method_IsClassifiedByItsAttributes(string methodName, bool expected)
    {
        var compilation = CreateNUnitFixture();
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, CasesTypeName, methodName);

        _ = await Assert.That(recognizer.IsTestMethod(method)).IsEqualTo(expected);
    }

    /// <summary>
    /// Each attribute of the framework that implements <c>ISimpleTestBuilder</c> or <c>ITestBuilder</c>
    /// makes the method a test, including <c>[Theory]</c> and the three combining strategies, which no list
    /// of the obvious attribute names covers, and including a user-written specialisation of each.
    /// </summary>
    /// <param name="methodName">The name of the method under judgement.</param>
    [Test]
    [Arguments("TestMarked")]
    [Arguments("TestCaseMarked")]
    [Arguments("TestCaseSourceMarked")]
    [Arguments("TheoryMarked")]
    [Arguments("CombinatorialMarked")]
    [Arguments("PairwiseMarked")]
    [Arguments("SequentialMarked")]
    [Arguments("DerivedFromTestMarked")]
    [Arguments("DerivedFromTheoryMarked")]
    [Arguments("DerivedFromTestCaseSourceMarked")]
    public async Task IsTestMethod_EveryTestBuilderAttribute_IsClassifiedAsATest(string methodName)
    {
        var compilation = CreateMarkerFixture();
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, MarkerCasesTypeName, methodName);

        _ = await Assert.That(recognizer.IsTestMethod(method)).IsTrue();
    }

    /// <summary>
    /// The attributes that configure or describe a test build no test of their own. Accepting any of them
    /// would turn a set-up method or a plain helper into recorded test surface.
    /// </summary>
    /// <param name="methodName">The name of the method under judgement.</param>
    [Test]
    [Arguments("RepeatOnly")]
    [Arguments("RetryOnly")]
    [Arguments("CategoryOnly")]
    [Arguments("DescriptionOnly")]
    [Arguments("OrderOnly")]
    [Arguments("SetUpOnly")]
    [Arguments("TearDownOnly")]
    [Arguments("ExplicitOnly")]
    [Arguments("ValuesOnly")]
    [Arguments("Undecorated")]
    public async Task IsTestMethod_AttributeThatBuildsNoTest_IsNotClassifiedAsATest(string methodName)
    {
        var compilation = CreateMarkerFixture();
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, NonMarkerCasesTypeName, methodName);

        _ = await Assert.That(recognizer.IsTestMethod(method)).IsFalse();
    }

    /// <summary>
    /// The same set of methods once more, judged by a recogniser that has nothing but the name-based rule.
    /// The fallback must draw the very same line, because a compilation in which the interfaces cannot be
    /// resolved is otherwise analysed against a different notion of what a test is.
    /// </summary>
    /// <param name="typeName">The type declaring the method.</param>
    /// <param name="methodName">The name of the method under judgement.</param>
    /// <param name="expected">Whether the method is an NUnit test.</param>
    [Test]
    [Arguments(MarkerCasesTypeName, "TestMarked", true)]
    [Arguments(MarkerCasesTypeName, "TestCaseMarked", true)]
    [Arguments(MarkerCasesTypeName, "TestCaseSourceMarked", true)]
    [Arguments(MarkerCasesTypeName, "TheoryMarked", true)]
    [Arguments(MarkerCasesTypeName, "CombinatorialMarked", true)]
    [Arguments(MarkerCasesTypeName, "PairwiseMarked", true)]
    [Arguments(MarkerCasesTypeName, "SequentialMarked", true)]
    [Arguments(MarkerCasesTypeName, "DerivedFromTestMarked", true)]
    [Arguments(MarkerCasesTypeName, "DerivedFromTheoryMarked", true)]
    [Arguments(MarkerCasesTypeName, "DerivedFromTestCaseSourceMarked", true)]
    [Arguments(NonMarkerCasesTypeName, "RepeatOnly", false)]
    [Arguments(NonMarkerCasesTypeName, "RetryOnly", false)]
    [Arguments(NonMarkerCasesTypeName, "CategoryOnly", false)]
    [Arguments(NonMarkerCasesTypeName, "SetUpOnly", false)]
    [Arguments(NonMarkerCasesTypeName, "ValuesOnly", false)]
    [Arguments(NonMarkerCasesTypeName, "Undecorated", false)]
    public async Task IsTestMethod_RecognizerWithoutAnyInterfaceType_FallsBackToTheNameRule(
        string typeName,
        string methodName,
        bool expected
    )
    {
        var compilation = CreateMarkerFixture();
        var recognizer = new NUnitTestMethodRecognizer([]);
        var method = FindMethod(compilation, typeName, methodName);

        _ = await Assert.That(recognizer.IsTestMethod(method)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("AbstractTest")]
    [Arguments("InheritedTest")]
    public async Task IsTestMethod_AbstractAndInheritedMethod_IsClassifiedAsATest(string methodName)
    {
        var compilation = CreateNUnitFixture();
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, "Fixture.AbstractCases", methodName);

        _ = await Assert.That(recognizer.IsTestMethod(method)).IsTrue();
    }

    /// <summary>
    /// <c>[TestFixture]</c> declares where tests live, never that a method is one. A recogniser that
    /// accepted it would report the whole class as untested surface on the very first build.
    /// </summary>
    [Test]
    public async Task IsTestMethod_MethodOfATestFixtureWithoutItsOwnAttribute_IsNotClassifiedAsATest()
    {
        var compilation = CreateNUnitFixture();
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, "Fixture.FixtureOnlyCases", "NotATest");

        _ = await Assert.That(recognizer.IsTestMethod(method)).IsFalse();
    }

    /// <summary>
    /// An attribute that only shares the simple name is not a test attribute, no matter whether the
    /// framework is referenced at all.
    /// </summary>
    [Test]
    public async Task IsTestMethod_TestAttributeFromAnUnrelatedNamespace_IsNotClassifiedAsATest()
    {
        var compilation = CompilationFactory.Create(UnrelatedFixtureSource, TestFramework.NUnit);
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, "Fixture.UnrelatedCases", "LooksLikeATest");

        _ = await Assert.That(recognizer.IsTestMethod(method)).IsFalse();
        _ = await Assert.That(new NUnitTestMethodRecognizer([]).IsTestMethod(method)).IsFalse();
    }

    /// <summary>
    /// The interface name alone is never enough. An attribute implementing an <c>ISimpleTestBuilder</c> or
    /// <c>ITestBuilder</c> of the framework's own assembly is a test attribute even when the recogniser
    /// could not resolve the interfaces; the very same interface names declared in a foreign assembly must
    /// leave the method unrecognised.
    /// </summary>
    /// <param name="satelliteAssemblyName">The assembly the look-alike interfaces are declared in.</param>
    /// <param name="expected">Whether an attribute implementing them makes the method a test.</param>
    [Test]
    [Arguments(FrameworkAssemblyName, true)]
    [Arguments(ForeignAssemblyName, false)]
    public async Task IsTestMethod_LookAlikeBuilderInterface_IsJudgedByItsDeclaringAssembly(
        string satelliteAssemblyName,
        bool expected
    )
    {
        var compilation = CreateSatelliteConsumer(satelliteAssemblyName);
        var recognizer = new NUnitTestMethodRecognizer([]);

        var simple = FindMethod(compilation, LookAlikeCasesTypeName, "SimpleBuilderMarked");
        var builder = FindMethod(compilation, LookAlikeCasesTypeName, "BuilderMarked");
        var plain = FindMethod(compilation, LookAlikeCasesTypeName, "Undecorated");

        _ = await Assert.That(recognizer.IsTestMethod(simple)).IsEqualTo(expected);
        _ = await Assert.That(recognizer.IsTestMethod(builder)).IsEqualTo(expected);
        _ = await Assert.That(recognizer.IsTestMethod(plain)).IsFalse();
    }

    /// <summary>
    /// A default <c>ImmutableArray</c> is what an uninitialised field hands over, and it must behave like
    /// an empty one instead of throwing from every classification.
    /// </summary>
    [Test]
    public async Task IsTestMethod_RecognizerBuiltFromADefaultArray_BehavesLikeAnEmptyOne()
    {
        var compilation = CreateNUnitFixture();
        var recognizer = new NUnitTestMethodRecognizer(default);

        var test = FindMethod(compilation, CasesTypeName, "PlainTest");
        var plain = FindMethod(compilation, CasesTypeName, "PlainMethod");

        _ = await Assert.That(recognizer.IsTestMethod(test)).IsTrue();
        _ = await Assert.That(recognizer.IsTestMethod(plain)).IsFalse();
    }

    /// <summary>
    /// A method whose attribute cannot be bound at all must not crash the recogniser, because an analyzer
    /// runs on incomplete code all day long.
    /// </summary>
    [Test]
    public async Task IsTestMethod_AttributeThatCannotBeBound_ReturnsFalse()
    {
        var source = """
            namespace Fixture;

            public class Cases
            {
                [Nonexistent]
                public void PlainTest()
                {
                }
            }
            """;

        var compilation = CompilationFactory.Create(source, filePath: CasesPath);
        var recognizer = new NUnitTestMethodRecognizer([]);
        var method = FindMethod(compilation, CasesTypeName, "PlainTest");

        _ = await Assert.That(recognizer.IsTestMethod(method)).IsFalse();
    }

    [Test]
    public async Task IsTestMethod_MethodIsNull_ThrowsArgumentNullException()
    {
        var recognizer = new NUnitTestMethodRecognizer([]);

        var exception = Assert.Throws<ArgumentNullException>(() => recognizer.IsTestMethod(null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("method");
    }

    /// <summary>
    /// Every shape whose number of cases is written down in the source, counted exactly. A parameterless
    /// <c>[Test]</c> is one of them: its inputs are hardcoded in the body, which is exactly as narrow as a
    /// single <c>[TestCase]</c> row, so exempting it would hide the very gap the count exists to expose.
    /// <c>[Repeat]</c> and <c>[Retry]</c> run that one case again with the same arguments and therefore do
    /// not multiply it.
    /// </summary>
    /// <param name="methodName">The name of the method whose cases are counted.</param>
    /// <param name="expected">The exact number of cases.</param>
    [Test]
    [Arguments("Parameterless", 1)]
    [Arguments("DerivedMarker", 1)]
    [Arguments("Repeated", 1)]
    [Arguments("Retried", 1)]
    [Arguments("SingleRow", 1)]
    [Arguments("ThreeRows", 3)]
    [Arguments("SourceFromArrayField", 3)]
    [Arguments("SourceFromCollectionProperty", 2)]
    [Arguments("SourceFromInitializedList", 4)]
    [Arguments("SourceFromReturningMethod", 5)]
    [Arguments("SourceFromYieldingMethod", 2)]
    [Arguments("SourceFromDerivedAttribute", 3)]
    [Arguments("SourceFromAnotherType", 2)]
    [Arguments("RowsAndSource", 5)]
    [Arguments("OneValuedParameter", 2)]
    [Arguments("TwoValuedParameters", 6)]
    [Arguments("CombinatorialParameters", 6)]
    [Arguments("SequentialParameters", 3)]
    [Arguments("FourValuedParameter", 4)]
    [Arguments("RangedParameter", 3)]
    [Arguments("SteppedRangeParameter", 3)]
    [Arguments("DescendingRangeParameter", 3)]
    [Arguments("TwiceRangedParameter", 4)]
    [Arguments("CountedRandomParameter", 4)]
    [Arguments("BoundedRandomParameter", 2)]
    [Arguments("ValueSourcedParameter", 3)]
    public async Task GetTestCaseCount_ShapeThatIsWrittenDown_IsCountedExactly(string methodName, int expected)
    {
        var compilation = CreateCountFixture();
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, CountCasesTypeName, methodName);

        var count = recognizer.GetTestCaseCount(method);

        _ = await Assert.That(count.Value).IsEqualTo(expected);
        _ = await Assert.That(count.IsExact).IsTrue();
    }

    /// <summary>
    /// Every shape whose number of cases the framework only settles while discovering tests, counted as a
    /// lower bound. The bound itself is asserted as well, because it is what a sum built from it reports,
    /// and it must never be smaller than the number of cases that certainly exist.
    /// </summary>
    /// <param name="methodName">The name of the method whose cases are counted.</param>
    /// <param name="expected">The lower bound of the number of cases.</param>
    [Test]
    [Arguments("TheoryDriven", 1)]
    [Arguments("SourceFromComputation", 1)]
    [Arguments("SourceFromSpread", 1)]
    [Arguments("SourceFromConditionalYield", 1)]
    [Arguments("SourceFromAType", 1)]
    [Arguments("SourceFromAnAbsentMember", 1)]
    [Arguments("RowAndComputedSource", 2)]
    [Arguments("PairwiseParameters", 3)]
    [Arguments("ParameterWithoutData", 1)]
    [Arguments("PartiallyValuedParameters", 2)]
    [Arguments("EmptyValuesParameter", 1)]
    [Arguments("FloatingRangeParameter", 1)]
    [Arguments("AbsentValueSourceParameter", 1)]
    public async Task GetTestCaseCount_ShapeThatIsNotWrittenDown_IsALowerBound(string methodName, int expected)
    {
        var compilation = CreateCountFixture();
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, CountCasesTypeName, methodName);

        var count = recognizer.GetTestCaseCount(method);

        _ = await Assert.That(count.Value).IsEqualTo(expected);
        _ = await Assert.That(count.IsExact).IsFalse();
    }

    /// <summary>
    /// The count does not depend on the resolved builder interfaces at all — every attribute it reads is
    /// matched by name and declaring assembly — so a recogniser that has nothing but the name-based rule
    /// must produce the very same numbers. A count that differed there would make the same test surface
    /// report different findings depending on how the compilation resolved.
    /// </summary>
    /// <param name="methodName">The name of the method whose cases are counted.</param>
    /// <param name="expected">The count, written the way <c>ToString</c> writes it.</param>
    [Test]
    [Arguments("Parameterless", "1")]
    [Arguments("ThreeRows", "3")]
    [Arguments("SourceFromArrayField", "3")]
    [Arguments("TwoValuedParameters", "6")]
    [Arguments("TheoryDriven", "1+")]
    [Arguments("PairwiseParameters", "3+")]
    public async Task GetTestCaseCount_RecognizerWithoutAnyInterfaceType_CountsTheSame(
        string methodName,
        string expected
    )
    {
        var compilation = CreateCountFixture();
        var recognizer = new NUnitTestMethodRecognizer([]);
        var method = FindMethod(compilation, CountCasesTypeName, methodName);

        var count = recognizer.GetTestCaseCount(method);

        _ = await Assert.That(count.ToString()).IsEqualTo(expected);
    }

    /// <summary>
    /// An attribute that only shares the simple name of a framework attribute contributes nothing, so the
    /// method counts as the single hardcoded case a body without arguments is.
    /// </summary>
    [Test]
    public async Task GetTestCaseCount_TestAttributeFromAnUnrelatedNamespace_CountsOneHardcodedCase()
    {
        var compilation = CompilationFactory.Create(UnrelatedFixtureSource, TestFramework.NUnit);
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, "Fixture.UnrelatedCases", "LooksLikeATest");

        var count = recognizer.GetTestCaseCount(method);

        _ = await Assert.That(count.Value).IsEqualTo(1);
        _ = await Assert.That(count.IsExact).IsTrue();
    }

    [Test]
    public async Task GetTestCaseCount_MethodIsNull_ThrowsArgumentNullException()
    {
        var recognizer = new NUnitTestMethodRecognizer([]);

        var exception = Assert.Throws<ArgumentNullException>(() => recognizer.GetTestCaseCount(null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("method");
    }

    private static CSharpCompilation CreateNUnitFixture() =>
        CompilationFactory.Create(NUnitFixtureSource, TestFramework.NUnit, filePath: CasesPath);

    private static CSharpCompilation CreateMarkerFixture() =>
        CompilationFactory.Create(MarkerFixtureSource, TestFramework.NUnit, filePath: CasesPath);

    private static CSharpCompilation CreateCountFixture() =>
        CompilationFactory.Create(CountFixtureSource, TestFramework.NUnit, filePath: CasesPath);

    /// <summary>
    /// Compiles the look-alike interfaces into an assembly called
    /// <paramref name="satelliteAssemblyName" /> and builds a compilation referencing it, which is how a
    /// fixture controls the assembly the well-known interface names are declared in.
    /// </summary>
    /// <param name="satelliteAssemblyName">The assembly name of the satellite.</param>
    /// <returns>The consuming compilation.</returns>
    private static CSharpCompilation CreateSatelliteConsumer(string satelliteAssemblyName)
    {
        var satellite = CompilationFactory.Create(SatelliteSource, satelliteAssemblyName, filePath: SatellitePath);

        return CompilationFactory.Create(
            SatelliteConsumerSource,
            additionalReferences: [satellite.ToMetadataReference()],
            filePath: CasesPath
        );
    }

    private static NUnitTestMethodRecognizer CreateRecognizer(Compilation compilation) =>
        new NUnitTestMethodRecognizer(NUnitTestFrameworkProbe.GetTestBuilderInterfaceTypes(compilation));

    private static IMethodSymbol FindMethod(Compilation compilation, string typeName, string methodName) =>
        compilation.GetTypeByMetadataName(typeName)!.GetMembers(methodName).OfType<IMethodSymbol>().First();

    private static string Describe(IEnumerable<IMethodSymbol> methods) =>
        string.Join("|", methods.Select(method => method.ContainingType.Name + "." + method.Name));

    private static string Describe(Compilation compilation) =>
        DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation));
}
