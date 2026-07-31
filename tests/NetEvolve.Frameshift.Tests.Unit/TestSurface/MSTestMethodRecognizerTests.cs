namespace NetEvolve.Frameshift.Tests.Unit;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.Frameshift.Tests.Infrastructure;
using NetEvolve.Frameshift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers which methods <see cref="MSTestTestMethodRecognizer" /> calls a test. Everything downstream —
/// the recorded test surface, <c>FSH0004</c>, and ultimately which mutants are considered covered —
/// rests on this single decision, so both directions are asserted: the specialisations MSTest and its
/// users derive from <c>TestMethodAttribute</c> have to be accepted, and anything that merely carries
/// the same name has to be refused.
/// </summary>
public class MSTestMethodRecognizerTests
{
    private const string FrameworkAssemblyName = "Microsoft.VisualStudio.TestPlatform.TestFramework.Satellite";
    private const string ForeignAssemblyName = "Foreign.Satellite";

    private const string CasesTypeName = "Fixture.Cases";
    private const string DecoratedTestName = "DecoratedTest";
    private const string PlainMethodName = "PlainMethod";

    private const string CasesPath = "Cases.cs";
    private const string ForeignPath = "Foreign.cs";
    private const string SatellitePath = "Satellite.cs";

    /// <summary>
    /// Every shape the recogniser has to judge, in one class that is itself marked with
    /// <c>[TestClass]</c> — a class-level attribute must never turn an undecorated method into a test.
    /// </summary>
    private const string CasesSource = """
        namespace Fixture;

        using Microsoft.VisualStudio.TestTools.UnitTesting;

        public class CustomTestMethodAttribute : TestMethodAttribute
        {
        }

        [TestClass]
        public class Cases
        {
            [TestMethod]
            public void PlainTest()
            {
            }

            [DataTestMethod]
            [DataRow(1)]
            public void DataDrivenTest(int value)
            {
            }

            [CustomTestMethod]
            public void DerivedAttributeTest()
            {
            }

            [Foreign.TestMethod]
            public void ForeignAttributeTest()
            {
            }

            public void UndecoratedMethod()
            {
            }
        }
        """;

    /// <summary>
    /// An attribute that carries the well-known simple name in a namespace and an assembly of its own.
    /// </summary>
    private const string ForeignSource = """
        namespace Foreign;

        using System;

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class TestMethodAttribute : Attribute
        {
        }
        """;

    /// <summary>
    /// A satellite declaring the well-known attribute, so that a fixture can control which assembly the
    /// attribute is declared in and thereby exercise the name-based rule on its own.
    /// </summary>
    private const string SatelliteSource = """
        namespace Microsoft.VisualStudio.TestTools.UnitTesting;

        using System;

        [AttributeUsage(AttributeTargets.Method)]
        public class TestMethodAttribute : Attribute
        {
        }
        """;

    private const string SatelliteConsumerSource = """
        namespace Fixture;

        using Microsoft.VisualStudio.TestTools.UnitTesting;

        public class Cases
        {
            [TestMethod]
            public void DecoratedTest()
            {
            }

            public void PlainMethod()
            {
            }
        }
        """;

    [Test]
    public async Task FrameworkName_Recognizer_NamesTheFramework()
    {
        var recognizer = new MSTestTestMethodRecognizer(null);

        _ = await Assert.That(recognizer.FrameworkName).IsEqualTo("MSTest");
    }

    /// <summary>
    /// <c>DataTestMethodAttribute</c> and any user-written specialisation derive from
    /// <c>TestMethodAttribute</c> and are therefore found by walking the attribute base chain, while
    /// <c>TestClassAttribute</c> is not in that chain and an unrelated <c>TestMethodAttribute</c> is
    /// declared outside the framework.
    /// </summary>
    /// <param name="methodName">The name of the method under judgement.</param>
    /// <param name="expected">Whether the method is an MSTest test.</param>
    [Test]
    [Arguments("PlainTest", true)]
    [Arguments("DataDrivenTest", true)]
    [Arguments("DerivedAttributeTest", true)]
    [Arguments("ForeignAttributeTest", false)]
    [Arguments("UndecoratedMethod", false)]
    public async Task IsTestMethod_MethodOfTheFixture_IsClassifiedByItsAttribute(string methodName, bool expected)
    {
        var compilation = CreateCases();
        var recognizer = MSTestTestFrameworkProbe.Instance.TryCreateRecognizer(compilation)!;

        var actual = recognizer.IsTestMethod(FindMethod(compilation, methodName));

        _ = await Assert.That(actual).IsEqualTo(expected);
    }

    [Test]
    public async Task IsTestMethod_MethodIsNull_ThrowsArgumentNullException()
    {
        var recognizer = new MSTestTestMethodRecognizer(null);

        var exception = Assert.Throws<ArgumentNullException>(() => recognizer.IsTestMethod(null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("method");
    }

    /// <summary>
    /// A recogniser built without the well-known attribute type has only the name-based rule left, which
    /// accepts an attribute called <c>TestMethodAttribute</c> when it is declared in a framework
    /// assembly.
    /// </summary>
    [Test]
    public async Task IsTestMethod_WithoutTheWellKnownType_UsesTheNameRule()
    {
        var compilation = CreateSatelliteConsumer(FrameworkAssemblyName);
        var recognizer = new MSTestTestMethodRecognizer(null);

        _ = await Assert.That(recognizer.IsTestMethod(FindMethod(compilation, DecoratedTestName))).IsTrue();
        _ = await Assert.That(recognizer.IsTestMethod(FindMethod(compilation, PlainMethodName))).IsFalse();
    }

    /// <summary>
    /// The name alone is never enough: the very same attribute name declared outside the framework must
    /// leave the method unrecognised.
    /// </summary>
    [Test]
    public async Task IsTestMethod_WithoutTheWellKnownType_RejectsAForeignAssembly()
    {
        var compilation = CreateSatelliteConsumer(ForeignAssemblyName);
        var recognizer = new MSTestTestMethodRecognizer(null);

        _ = await Assert.That(recognizer.IsTestMethod(FindMethod(compilation, DecoratedTestName))).IsFalse();
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
                public void DecoratedTest()
                {
                }
            }
            """;

        var compilation = CompilationFactory.Create(source, filePath: CasesPath);
        var recognizer = new MSTestTestMethodRecognizer(null);

        _ = await Assert.That(recognizer.IsTestMethod(FindMethod(compilation, DecoratedTestName))).IsFalse();
    }

    [Test]
    public async Task Fixtures_EveryCompilation_CompilesWithoutErrors()
    {
        var errors = new[]
        {
            Describe(CreateCases()),
            Describe(CreateSatelliteConsumer(FrameworkAssemblyName)),
            Describe(CreateSatelliteConsumer(ForeignAssemblyName)),
        };

        _ = await Assert
            .That(string.Join(" / ", errors.Distinct(StringComparer.Ordinal)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    private static CSharpCompilation CreateCases() =>
        CompilationFactory.Create([(CasesPath, CasesSource), (ForeignPath, ForeignSource)], TestFramework.MSTest);

    private static CSharpCompilation CreateSatelliteConsumer(string satelliteAssemblyName)
    {
        var satellite = CompilationFactory.Create(SatelliteSource, satelliteAssemblyName, filePath: SatellitePath);

        return CompilationFactory.Create(
            SatelliteConsumerSource,
            additionalReferences: [satellite.ToMetadataReference()],
            filePath: CasesPath
        );
    }

    private static IMethodSymbol FindMethod(Compilation compilation, string methodName) =>
        compilation.GetTypeByMetadataName(CasesTypeName)!.GetMembers(methodName).OfType<IMethodSymbol>().First();

    private static string Describe(Compilation compilation) =>
        DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation));
}
