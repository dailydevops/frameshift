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
/// </remarks>
public class NUnitTestMethodRecognizerTests
{
    private const string CasesTypeName = "Fixture.Cases";
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

    private static CSharpCompilation CreateNUnitFixture() =>
        CompilationFactory.Create(NUnitFixtureSource, TestFramework.NUnit, filePath: CasesPath);

    private static CSharpCompilation CreateMarkerFixture() =>
        CompilationFactory.Create(MarkerFixtureSource, TestFramework.NUnit, filePath: CasesPath);

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
