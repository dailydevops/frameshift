#if FRAMESHIFT_XUNIT_V3
namespace NetEvolve.FrameShift.Tests.Unit;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Tests.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers how an xUnit.net v3 test method is recognised: by <c>[Fact]</c>, by <c>[Theory]</c>, by an
/// attribute that derives from <c>FactAttribute</c>, and never by an attribute that only happens to carry
/// the same simple name — not even when that attribute derives from the <c>FactAttribute</c> of version 2.
/// </summary>
/// <remarks>
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

    private const string VersionTwoAssemblyName = "Helper.VersionTwo";
    private const string VersionThreeAssemblyName = "Helper.VersionThree";

    private const string ExpectedTestMethods =
        "Cases.FactTest|Cases.TheoryTest|Cases.DerivedAttributeTest|Cases.StaticTest|"
        + "Cases.GenericTest|Cases.PrivateTest|AbstractCases.AbstractTest|AbstractCases.InheritedTest";

    /// <summary>
    /// The fixture spells the attributes exactly as a version 2 fixture would, because
    /// <c>Xunit.FactAttribute</c>, <c>Xunit.TheoryAttribute</c> and <c>Xunit.InlineDataAttribute</c> exist in
    /// both major versions. Which version the names bind to is decided by the referenced assembly alone.
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
    [Arguments("StaticTest", true)]
    [Arguments("GenericTest", true)]
    [Arguments("PrivateTest", true)]
    [Arguments("PlainMethod", false)]
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
    /// An attribute that only shares the simple name is not a test attribute, no matter whether the
    /// framework is referenced at all. The judgement rests on symbol identity, never on a name.
    /// </summary>
    [Test]
    public async Task IsTestMethod_FactAttributeFromAnUnrelatedNamespace_IsNotClassifiedAsATest()
    {
        var compilation = CompilationFactory.Create(UnrelatedFixtureSource, TestFramework.XunitV3);
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, "Fixture.UnrelatedCases", "LooksLikeATest");

        _ = await Assert.That(recognizer.IsTestMethod(method)).IsFalse();
        _ = await Assert.That(new XunitV3TestMethodRecognizer(null).IsTestMethod(method)).IsFalse();
    }

    /// <summary>
    /// Judging a method fails closed. Without the resolved attribute type — the framework is referenced by
    /// name but its metadata is unavailable — there is no positive evidence for anything, and the recogniser
    /// recognises nothing instead of guessing from a name.
    /// </summary>
    [Test]
    public async Task IsTestMethod_RecognizerWithoutAnAttributeType_RecognizesNothing()
    {
        var compilation = CreateXunitFixture();
        var recognizer = new XunitV3TestMethodRecognizer(null);

        var fact = FindMethod(compilation, CasesTypeName, "FactTest");
        var theory = FindMethod(compilation, CasesTypeName, "TheoryTest");
        var derived = FindMethod(compilation, CasesTypeName, "DerivedAttributeTest");
        var plain = FindMethod(compilation, CasesTypeName, "PlainMethod");

        _ = await Assert.That(recognizer.IsTestMethod(fact)).IsFalse();
        _ = await Assert.That(recognizer.IsTestMethod(theory)).IsFalse();
        _ = await Assert.That(recognizer.IsTestMethod(derived)).IsFalse();
        _ = await Assert.That(recognizer.IsTestMethod(plain)).IsFalse();
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

        _ = await Assert.That(versionThree.IsTestMethod(versionThreeTest)).IsTrue();
        _ = await Assert.That(versionThree.IsTestMethod(versionTwoTest)).IsFalse();
        _ = await Assert.That(versionTwo.IsTestMethod(versionTwoTest)).IsTrue();
        _ = await Assert.That(versionTwo.IsTestMethod(versionThreeTest)).IsFalse();
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
        var recognizer = new XunitV3TestMethodRecognizer(null);

        var exception = Assert.Throws<ArgumentNullException>(() => _ = recognizer.IsTestMethod(null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("method");
    }

    private static CSharpCompilation CreateXunitFixture() =>
        CompilationFactory.Create(XunitFixtureSource, TestFramework.XunitV3, filePath: "Cases.cs");

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
        new XunitV3TestMethodRecognizer(XunitV3TestFrameworkProbe.GetTestAttributeType(compilation));

    private static IMethodSymbol FindMethod(Compilation compilation, string typeName, string methodName) =>
        compilation.GetTypeByMetadataName(typeName)!.GetMembers(methodName).OfType<IMethodSymbol>().First();

    private static string Describe(IEnumerable<IMethodSymbol> methods) =>
        string.Join("|", methods.Select(method => method.ContainingType.Name + "." + method.Name));

    private static string Describe(Compilation compilation) =>
        DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation));
}
#endif
