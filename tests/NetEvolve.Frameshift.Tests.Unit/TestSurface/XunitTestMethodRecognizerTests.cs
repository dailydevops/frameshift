namespace NetEvolve.Frameshift.Tests.Unit;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.Frameshift.Tests.Infrastructure;
using NetEvolve.Frameshift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers how an xUnit test method is recognised: by <c>[Fact]</c>, by <c>[Theory]</c>, by an attribute
/// that derives from <c>FactAttribute</c>, and never by an attribute that only happens to carry the same
/// simple name. Every expectation is checked against version 2 and version 3 alike, because the two ship
/// identical type names from differently named assemblies.
/// </summary>
/// <remarks>
/// The shapes a test method can take — static, generic, inherited, abstract and private — are covered
/// deliberately: whichever of them the recogniser dropped would silently shrink the recorded test surface
/// and make the production side claim mutations are unreachable when they are not.
/// </remarks>
public class XunitTestMethodRecognizerTests
{
    private const string XunitV2Scenario = "xUnit v2";
    private const string XunitV3Scenario = "xUnit v3";

    private const string CasesTypeName = "Fixture.Cases";

    private const string ExpectedTestMethods =
        "Cases.FactTest|Cases.TheoryTest|Cases.DerivedAttributeTest|Cases.StaticTest|"
        + "Cases.GenericTest|Cases.PrivateTest|AbstractCases.AbstractTest|AbstractCases.InheritedTest";

    /// <summary>
    /// The fixture compiles against both major versions unchanged, because <c>Xunit.FactAttribute</c>,
    /// <c>Xunit.TheoryAttribute</c> and <c>Xunit.InlineDataAttribute</c> exist in either of them.
    /// </summary>
    private const string XunitFixtureSource = """
        namespace Fixture;

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

            public void PlainMethod()
            {
            }
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
    [Arguments(XunitV2Scenario)]
    [Arguments(XunitV3Scenario)]
    public async Task Fixtures_EveryCompilation_CompilesWithoutErrors(string version)
    {
        var errors = new[]
        {
            Describe(CreateXunitFixture(version)),
            Describe(CompilationFactory.Create(UnrelatedFixtureSource, ToFramework(version))),
            Describe(CompilationFactory.Create(UnrelatedFixtureSource)),
        };

        _ = await Assert
            .That(string.Join(" / ", errors.Distinct(StringComparer.Ordinal)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    [Arguments(XunitV2Scenario)]
    [Arguments(XunitV3Scenario)]
    public async Task FrameworkName_Recognizer_NamesTheFramework(string version)
    {
        var recognizer = CreateRecognizer(CreateXunitFixture(version));

        _ = await Assert.That(recognizer.FrameworkName).IsEqualTo("xUnit");
    }

    /// <summary>
    /// Every shape a test method can take is discovered, in declaration order and without duplicates. The
    /// override of the abstract test is not listed again, because the attribute sits on the declaration
    /// the override replaces.
    /// </summary>
    /// <param name="version">The version of the framework the compilation references.</param>
    [Test]
    [Arguments(XunitV2Scenario)]
    [Arguments(XunitV3Scenario)]
    public async Task FindTestMethods_EveryTestShape_IsDiscoveredInDeclarationOrder(string version)
    {
        var compilation = CreateXunitFixture(version);

        var found = TestMethodDiscovery.FindTestMethods(
            compilation,
            CreateRecognizer(compilation),
            CancellationToken.None
        );

        _ = await Assert.That(Describe(found)).IsEqualTo(ExpectedTestMethods);
    }

    [Test]
    [Arguments(XunitV2Scenario, "FactTest", true)]
    [Arguments(XunitV2Scenario, "TheoryTest", true)]
    [Arguments(XunitV2Scenario, "DerivedAttributeTest", true)]
    [Arguments(XunitV2Scenario, "StaticTest", true)]
    [Arguments(XunitV2Scenario, "GenericTest", true)]
    [Arguments(XunitV2Scenario, "PrivateTest", true)]
    [Arguments(XunitV2Scenario, "PlainMethod", false)]
    [Arguments(XunitV3Scenario, "FactTest", true)]
    [Arguments(XunitV3Scenario, "TheoryTest", true)]
    [Arguments(XunitV3Scenario, "DerivedAttributeTest", true)]
    [Arguments(XunitV3Scenario, "StaticTest", true)]
    [Arguments(XunitV3Scenario, "GenericTest", true)]
    [Arguments(XunitV3Scenario, "PrivateTest", true)]
    [Arguments(XunitV3Scenario, "PlainMethod", false)]
    public async Task IsTestMethod_Method_IsClassifiedByItsAttributes(string version, string methodName, bool expected)
    {
        var compilation = CreateXunitFixture(version);
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, CasesTypeName, methodName);

        _ = await Assert.That(recognizer.IsTestMethod(method)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(XunitV2Scenario, "AbstractTest")]
    [Arguments(XunitV2Scenario, "InheritedTest")]
    [Arguments(XunitV3Scenario, "AbstractTest")]
    [Arguments(XunitV3Scenario, "InheritedTest")]
    public async Task IsTestMethod_AbstractAndInheritedMethod_IsClassifiedAsATest(string version, string methodName)
    {
        var compilation = CreateXunitFixture(version);
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, "Fixture.AbstractCases", methodName);

        _ = await Assert.That(recognizer.IsTestMethod(method)).IsTrue();
    }

    /// <summary>
    /// An attribute that only shares the simple name is not a test attribute, no matter whether the
    /// framework is referenced at all — and where it is not, no recogniser exists in the first place.
    /// </summary>
    /// <param name="version">The version of the framework the compilation references.</param>
    [Test]
    [Arguments(XunitV2Scenario)]
    [Arguments(XunitV3Scenario)]
    public async Task IsTestMethod_FactAttributeFromAnUnrelatedNamespace_IsNotClassifiedAsATest(string version)
    {
        var compilation = CompilationFactory.Create(UnrelatedFixtureSource, ToFramework(version));
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, "Fixture.UnrelatedCases", "LooksLikeATest");

        _ = await Assert.That(recognizer.IsTestMethod(method)).IsFalse();
        _ = await Assert.That(new XunitTestMethodRecognizer(null).IsTestMethod(method)).IsFalse();
    }

    /// <summary>
    /// When the well-known attribute type could not be resolved — which is what happens to a compilation
    /// referencing both major versions — only the name rule is left, and it has to carry the recognition
    /// of the real framework attributes on its own.
    /// </summary>
    /// <param name="version">The version of the framework the compilation references.</param>
    [Test]
    [Arguments(XunitV2Scenario)]
    [Arguments(XunitV3Scenario)]
    public async Task IsTestMethod_RecognizerWithoutAnAttributeType_FallsBackToTheNameRule(string version)
    {
        var compilation = CreateXunitFixture(version);
        var recognizer = new XunitTestMethodRecognizer(null);

        var fact = FindMethod(compilation, CasesTypeName, "FactTest");
        var theory = FindMethod(compilation, CasesTypeName, "TheoryTest");
        var derived = FindMethod(compilation, CasesTypeName, "DerivedAttributeTest");
        var plain = FindMethod(compilation, CasesTypeName, "PlainMethod");

        _ = await Assert.That(recognizer.IsTestMethod(fact)).IsTrue();
        _ = await Assert.That(recognizer.IsTestMethod(theory)).IsTrue();
        _ = await Assert.That(recognizer.IsTestMethod(derived)).IsTrue();
        _ = await Assert.That(recognizer.IsTestMethod(plain)).IsFalse();
    }

    [Test]
    public async Task IsTestMethod_MethodIsNull_ThrowsArgumentNullException()
    {
        var recognizer = new XunitTestMethodRecognizer(null);
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

    private static CSharpCompilation CreateXunitFixture(string version) =>
        CompilationFactory.Create(XunitFixtureSource, ToFramework(version), filePath: "Cases.cs");

    private static TestFramework ToFramework(string version) =>
        version switch
        {
            XunitV2Scenario => TestFramework.XunitV2,
            XunitV3Scenario => TestFramework.XunitV3,
            _ => throw new ArgumentOutOfRangeException(nameof(version), version, "Unknown version."),
        };

    private static XunitTestMethodRecognizer CreateRecognizer(Compilation compilation) =>
        new XunitTestMethodRecognizer(XunitTestFrameworkProbe.GetTestAttributeType(compilation));

    private static IMethodSymbol FindMethod(Compilation compilation, string typeName, string methodName) =>
        compilation.GetTypeByMetadataName(typeName)!.GetMembers(methodName).OfType<IMethodSymbol>().First();

    private static string Describe(IEnumerable<IMethodSymbol> methods) =>
        string.Join("|", methods.Select(method => method.ContainingType.Name + "." + method.Name));

    private static string Describe(Compilation compilation) =>
        DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation));
}
