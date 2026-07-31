namespace NetEvolve.Frameshift.Tests.Unit;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.Frameshift.Tests.Infrastructure;
using NetEvolve.Frameshift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers how an NUnit test method is recognised: by <c>[Test]</c>, <c>[TestCase]</c> and
/// <c>[TestCaseSource]</c>, by an attribute deriving from one of them, and never by <c>[TestFixture]</c>,
/// which marks the class rather than the method.
/// </summary>
/// <remarks>
/// The shapes a test method can take — static, generic, inherited, abstract and private — are covered
/// deliberately: whichever of them the recogniser dropped would silently shrink the recorded test surface
/// and make the production side claim mutations are unreachable when they are not.
/// </remarks>
public class NUnitTestMethodRecognizerTests
{
    private const string CasesTypeName = "Fixture.Cases";

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

    [Test]
    public async Task Fixtures_EveryCompilation_CompilesWithoutErrors()
    {
        var errors = new[]
        {
            Describe(CreateNUnitFixture()),
            Describe(CompilationFactory.Create(UnrelatedFixtureSource, TestFramework.NUnit)),
            Describe(CompilationFactory.Create(UnrelatedFixtureSource)),
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
    /// Without the resolved attribute types only the name rule is left, and it has to carry the
    /// recognition of the real framework attributes on its own.
    /// </summary>
    [Test]
    [Arguments("PlainTest")]
    [Arguments("TestCaseTest")]
    [Arguments("TestCaseSourceTest")]
    [Arguments("DerivedAttributeTest")]
    public async Task IsTestMethod_RecognizerWithoutAnyAttributeType_FallsBackToTheNameRule(string methodName)
    {
        var compilation = CreateNUnitFixture();
        var recognizer = new NUnitTestMethodRecognizer([]);
        var method = FindMethod(compilation, CasesTypeName, methodName);

        _ = await Assert.That(recognizer.IsTestMethod(method)).IsTrue();
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

    [Test]
    public async Task IsTestMethod_MethodIsNull_ThrowsArgumentNullException()
    {
        var recognizer = new NUnitTestMethodRecognizer([]);
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

    private static CSharpCompilation CreateNUnitFixture() =>
        CompilationFactory.Create(NUnitFixtureSource, TestFramework.NUnit, filePath: "Cases.cs");

    private static NUnitTestMethodRecognizer CreateRecognizer(Compilation compilation) =>
        new NUnitTestMethodRecognizer(NUnitTestFrameworkProbe.GetTestAttributeTypes(compilation));

    private static IMethodSymbol FindMethod(Compilation compilation, string typeName, string methodName) =>
        compilation.GetTypeByMetadataName(typeName)!.GetMembers(methodName).OfType<IMethodSymbol>().First();

    private static string Describe(IEnumerable<IMethodSymbol> methods) =>
        string.Join("|", methods.Select(method => method.ContainingType.Name + "." + method.Name));

    private static string Describe(Compilation compilation) =>
        DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation));
}
