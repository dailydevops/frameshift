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

        _ = await Assert.That(CreateRecognizer(compilation).IsTestMethod(method)).IsTrue();
        _ = await Assert.That(new XunitV3TestMethodRecognizer(null, markerInterface).IsTestMethod(method)).IsTrue();
        _ = await Assert.That(new XunitV3TestMethodRecognizer(attributeType, null).IsTestMethod(method)).IsFalse();
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

        _ = await Assert.That(new XunitV3TestMethodRecognizer(attributeType, null).IsTestMethod(method)).IsTrue();
        _ = await Assert.That(new XunitV3TestMethodRecognizer(null, markerInterface).IsTestMethod(method)).IsTrue();
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

        _ = await Assert.That(recognizer.IsTestMethod(method)).IsFalse();
        _ = await Assert.That(new XunitV3TestMethodRecognizer(null, null).IsTestMethod(method)).IsFalse();
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

        _ = await Assert.That(recognizer.IsTestMethod(fact)).IsFalse();
        _ = await Assert.That(recognizer.IsTestMethod(theory)).IsFalse();
        _ = await Assert.That(recognizer.IsTestMethod(derived)).IsFalse();
        _ = await Assert.That(recognizer.IsTestMethod(markerInterface)).IsFalse();
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
        var recognizer = new XunitV3TestMethodRecognizer(null, null);

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
