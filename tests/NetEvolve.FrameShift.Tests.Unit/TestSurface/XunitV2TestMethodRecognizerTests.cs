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

    private const string ExpectedTestMethods =
        "Cases.FactTest|Cases.TheoryTest|Cases.DerivedAttributeTest|Cases.StaticTest|"
        + "Cases.GenericTest|Cases.PrivateTest|AbstractCases.AbstractTest|AbstractCases.InheritedTest";

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
    public async Task Fixtures_EveryCompilation_CompilesWithoutErrors()
    {
        var errors = new[]
        {
            Describe(CreateXunitV2Fixture()),
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
    /// An attribute that only shares the simple name is not a test attribute, no matter whether the
    /// framework is referenced at all - the recogniser compares symbols, never names.
    /// </summary>
    [Test]
    public async Task IsTestMethod_FactAttributeFromAnUnrelatedNamespace_IsNotClassifiedAsATest()
    {
        var compilation = CreateUnrelatedFixture();
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, "Fixture.UnrelatedCases", "LooksLikeATest");

        _ = await Assert.That(recognizer.IsTestMethod(method)).IsFalse();
        _ = await Assert.That(new XunitV2TestMethodRecognizer(null).IsTestMethod(method)).IsFalse();
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

        _ = await Assert.That(recognizer.FrameworkName).IsEqualTo(FrameworkName);
        _ = await Assert.That(recognizer.IsTestMethod(fact)).IsFalse();
        _ = await Assert.That(recognizer.IsTestMethod(derived)).IsFalse();
        _ = await Assert.That(recognizer.IsTestMethod(plain)).IsFalse();
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

    private static CSharpCompilation CreateXunitV2Fixture() =>
        CompilationFactory.Create(XunitFixtureSource, TestFramework.XunitV2, filePath: "Cases.cs");

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
