namespace NetEvolve.FrameShift.Tests.Unit;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Tests.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers which methods <see cref="MSTestTestMethodRecognizer" /> calls a test. Everything downstream —
/// the recorded test surface, <c>FSH0004</c>, and ultimately which mutants are considered covered —
/// rests on this single decision, so both directions are asserted: every specialisation MSTest and its
/// users derive from <c>TestMethodAttribute</c> has to be accepted, and everything else has to be refused
/// — an attribute that merely carries the same name, the data sources that feed an existing test, the
/// fixture attributes that sit on a method without being one, and the class-level attribute.
/// </summary>
/// <remarks>
/// <para>
/// The marker really is the base type here, which is why <c>[STATestMethod]</c> is accepted without being
/// named anywhere in production code, while <c>[DataRow]</c> and <c>[DynamicData]</c> are refused on their
/// own however test-like they look.
/// </para>
/// <para>
/// The case count is covered shape by shape, and in two groups that must never be mixed up. An exact count
/// is asserted as the number <em>and</em> as being exact, because only an exact one may ever contribute to
/// a finding. A lower bound is asserted as the number <em>and</em> as not being exact, because the whole
/// point of a bound is that it suppresses the finding: MSTest resolves a dynamic data source while
/// discovering tests, and mistaking such a source for a single case would report a gap that is not there.
/// </para>
/// </remarks>
public class MSTestMethodRecognizerTests
{
    private const string FrameworkAssemblyName = "Microsoft.VisualStudio.TestPlatform.TestFramework.Satellite";
    private const string ForeignAssemblyName = "Foreign.Satellite";

    private const string CasesTypeName = "Fixture.Cases";
    private const string CountCasesTypeName = "Fixture.CountCases";
    private const string DecoratedTestName = "DecoratedTest";
    private const string PlainMethodName = "PlainMethod";

    private const string CasesPath = "Cases.cs";
    private const string CountCasesPath = "CountCases.cs";
    private const string ForeignPath = "Foreign.cs";
    private const string SatellitePath = "Satellite.cs";

    /// <summary>
    /// Every shape the recogniser has to judge, in one class that is itself marked with
    /// <c>[TestClass]</c> — a class-level attribute must never turn an undecorated method into a test.
    /// </summary>
    private const string CasesSource = """
        namespace Fixture;

        using System.Collections.Generic;
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

            [STATestMethod]
            public void SingleThreadedApartmentTest()
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

            [DataRow(1)]
            public void DataRowOnlyMethod(int value)
            {
            }

            [DynamicData(nameof(Values))]
            public void DynamicDataOnlyMethod(int value)
            {
            }

            [TestInitialize]
            public void InitializeOnlyMethod()
            {
            }

            [TestCleanup]
            public void CleanupOnlyMethod()
            {
            }

            [TestCategory("slow")]
            public void CategoryOnlyMethod()
            {
            }

            public void UndecoratedMethod()
            {
            }

            public static IEnumerable<object[]> Values
            {
                get { yield return new object[] { 1 }; }
            }
        }
        """;

    /// <summary>
    /// One method per shape a case count can be derived from, and one source member per shape a sequence
    /// length can be read from. The methods whose count is a lower bound are declared right next to the
    /// exact ones on purpose: <c>[DynamicData]</c> of a computed sequence looks exactly like
    /// <c>[DynamicData]</c> of a listed one at the call site, and only the declaration of the source
    /// decides.
    /// </summary>
    private const string CountCasesSource = """
        namespace Fixture;

        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Reflection;
        using Microsoft.VisualStudio.TestTools.UnitTesting;

        public sealed class ScenarioRowAttribute : DataRowAttribute
        {
            public ScenarioRowAttribute(int value)
                : base(value)
            {
            }
        }

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class RowsFromNowhereAttribute : Attribute, ITestDataSource
        {
            public IEnumerable<object[]> GetData(MethodInfo methodInfo)
            {
                yield return new object[] { 1 };
            }

            public string GetDisplayName(MethodInfo methodInfo, object[] data)
            {
                return "row";
            }
        }

        public static class ExternalSource
        {
            public static readonly object[][] Rows = new object[][]
            {
                new object[] { 1 },
                new object[] { 2 },
            };
        }

        [TestClass]
        public class CountCases
        {
            [TestMethod]
            public void Parameterless()
            {
            }

            [TestMethod]
            [Retry(3)]
            public void Retried()
            {
            }

            [DataTestMethod]
            [DataRow(1)]
            public void SingleRow(int value)
            {
            }

            [DataTestMethod]
            [DataRow(1)]
            [DataRow(2)]
            [DataRow(3)]
            public void ThreeRows(int value)
            {
            }

            [DataTestMethod]
            [ScenarioRow(1)]
            [ScenarioRow(2)]
            public void DerivedRows(int value)
            {
            }

            [TestMethod(UnfoldingStrategy = TestDataSourceUnfoldingStrategy.Fold)]
            [DataRow(1)]
            [DataRow(2)]
            public void FoldedRows(int value)
            {
            }

            [DataTestMethod]
            [DynamicData(nameof(ArrayRows))]
            public void SourceFromArrayField(int value)
            {
            }

            [DataTestMethod]
            [DynamicData(nameof(CollectionRows))]
            public void SourceFromCollectionProperty(int value)
            {
            }

            [DataTestMethod]
            [DynamicData(nameof(ReturnedRows), DynamicDataSourceType.Method)]
            public void SourceFromReturningMethod(int value)
            {
            }

            [DataTestMethod]
            [DynamicData(nameof(YieldedRows), DynamicDataSourceType.Method)]
            public void SourceFromYieldingMethod(int value)
            {
            }

            [DataTestMethod]
            [DynamicData(nameof(ExternalSource.Rows), typeof(ExternalSource))]
            public void SourceFromAnotherType(int value)
            {
            }

            [DataTestMethod]
            [DataRow(1)]
            [DataRow(2)]
            [DynamicData(nameof(ArrayRows))]
            public void RowsAndSource(int value)
            {
            }

            [DataTestMethod]
            [DynamicData(nameof(ComputedRows), DynamicDataSourceType.Method)]
            public void SourceFromComputation(int value)
            {
            }

            [DataTestMethod]
            [DynamicData("Absent")]
            public void SourceFromAnAbsentMember(int value)
            {
            }

            [TestMethod]
            [DataSource("Connected")]
            public void SourceFromAnExternalTable()
            {
            }

            [TestMethod]
            [RowsFromNowhere]
            public void SourceFromACustomAttribute(int value)
            {
            }

            [DataTestMethod]
            [DataRow(1)]
            [DynamicData(nameof(ComputedRows), DynamicDataSourceType.Method)]
            public void RowAndComputedSource(int value)
            {
            }

            public static readonly object[][] ArrayRows = new object[][]
            {
                new object[] { 1 },
                new object[] { 2 },
                new object[] { 3 },
            };

            public static IEnumerable<object[]> CollectionRows =>
                [new object[] { 1 }, new object[] { 2 }];

            public static IEnumerable<object[]> ReturnedRows()
            {
                return new object[][]
                {
                    new object[] { 1 },
                    new object[] { 2 },
                    new object[] { 3 },
                    new object[] { 4 },
                };
            }

            public static IEnumerable<object[]> YieldedRows()
            {
                yield return new object[] { 1 };
                yield return new object[] { 2 };
            }

            public static IEnumerable<object[]> ComputedRows()
            {
                return ArrayRows.Where(row => row.Length > 0);
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

    /// <summary>
    /// A consumer whose own attribute derives from the satellite's, so that the recogniser has to walk the
    /// base chain before the name-based rule can apply: the attribute on the method is declared in the
    /// compilation itself and only its base type sits in a framework assembly.
    /// </summary>
    private const string SatelliteDerivedConsumerSource = """
        namespace Fixture;

        using Microsoft.VisualStudio.TestTools.UnitTesting;

        public class CustomTestMethodAttribute : TestMethodAttribute
        {
        }

        public class Cases
        {
            [CustomTestMethod]
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
    /// Every marker of the framework derives from <c>TestMethodAttribute</c> — <c>DataTestMethodAttribute</c>
    /// and <c>STATestMethodAttribute</c> do, and so does any user-written specialisation — and each is
    /// therefore found by walking the attribute base chain. Nothing else is a marker: the data sources
    /// <c>DataRowAttribute</c> and <c>DynamicDataAttribute</c> only feed an existing test, the fixture
    /// attributes <c>TestInitializeAttribute</c> and <c>TestCleanupAttribute</c> and the descriptive
    /// <c>TestCategoryAttribute</c> derive straight from <see cref="Attribute" />, <c>TestClassAttribute</c>
    /// marks the class this fixture is, and the unrelated <c>TestMethodAttribute</c> is declared outside the
    /// framework.
    /// </summary>
    /// <param name="methodName">The name of the method under judgement.</param>
    /// <param name="expected">Whether the method is an MSTest test.</param>
    [Test]
    [Arguments("PlainTest", true)]
    [Arguments("DataDrivenTest", true)]
    [Arguments("SingleThreadedApartmentTest", true)]
    [Arguments("DerivedAttributeTest", true)]
    [Arguments("ForeignAttributeTest", false)]
    [Arguments("DataRowOnlyMethod", false)]
    [Arguments("DynamicDataOnlyMethod", false)]
    [Arguments("InitializeOnlyMethod", false)]
    [Arguments("CleanupOnlyMethod", false)]
    [Arguments("CategoryOnlyMethod", false)]
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
    /// The name-based rule has to survive derivation. When the well-known type cannot be resolved — an
    /// ambiguous name, or a compilation carrying nothing but the framework assembly — a user-written
    /// specialisation is recognised only if the base chain is walked down to the framework attribute it
    /// derives from, and refused when that base is declared outside the framework.
    /// </summary>
    /// <param name="satelliteAssemblyName">The assembly name the base attribute is declared in.</param>
    /// <param name="expected">Whether the derived attribute makes the method a test.</param>
    [Test]
    [Arguments(FrameworkAssemblyName, true)]
    [Arguments(ForeignAssemblyName, false)]
    public async Task IsTestMethod_WithoutTheWellKnownType_AppliesTheNameRuleToTheBaseChain(
        string satelliteAssemblyName,
        bool expected
    )
    {
        var compilation = CreateSatelliteConsumer(SatelliteDerivedConsumerSource, satelliteAssemblyName);
        var recognizer = new MSTestTestMethodRecognizer(null);

        _ = await Assert.That(recognizer.IsTestMethod(FindMethod(compilation, DecoratedTestName))).IsEqualTo(expected);
        _ = await Assert.That(recognizer.IsTestMethod(FindMethod(compilation, PlainMethodName))).IsFalse();
    }

    /// <summary>
    /// The recogniser a probe hands out on the assembly rule alone has only the name-based rule left. It
    /// must simply find nothing where no attribute of the framework is in play, which is what makes
    /// detecting the framework generously harmless.
    /// </summary>
    [Test]
    public async Task IsTestMethod_WithoutTheWellKnownType_FindsNoTestsInAPlainCompilation()
    {
        var source = """
            namespace Fixture;

            public class Cases
            {
                public void DecoratedTest()
                {
                }
            }
            """;

        var compilation = CompilationFactory.Create(source, TestFramework.MSTest, filePath: CasesPath);
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

    /// <summary>
    /// Every shape whose number of cases is written down in the source, counted exactly. A
    /// <c>[TestMethod]</c> without any data source is one of them: its inputs are hardcoded in the body,
    /// which is exactly as narrow as a single <c>[DataRow]</c>, so exempting it would hide the very gap the
    /// count exists to expose. <c>[Retry]</c> re-runs that one case with the same arguments and therefore
    /// does not multiply it, and the folding strategy only changes how the rows are reported.
    /// </summary>
    /// <param name="methodName">The name of the method whose cases are counted.</param>
    /// <param name="expected">The exact number of cases.</param>
    [Test]
    [Arguments("Parameterless", 1)]
    [Arguments("Retried", 1)]
    [Arguments("SingleRow", 1)]
    [Arguments("ThreeRows", 3)]
    [Arguments("DerivedRows", 2)]
    [Arguments("FoldedRows", 2)]
    [Arguments("SourceFromArrayField", 3)]
    [Arguments("SourceFromCollectionProperty", 2)]
    [Arguments("SourceFromReturningMethod", 4)]
    [Arguments("SourceFromYieldingMethod", 2)]
    [Arguments("SourceFromAnotherType", 2)]
    [Arguments("RowsAndSource", 5)]
    public async Task GetTestCaseCount_ShapeThatIsWrittenDown_IsCountedExactly(string methodName, int expected)
    {
        var compilation = CreateCountCases();
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, CountCasesTypeName, methodName);

        var count = recognizer.GetTestCaseCount(method);

        _ = await Assert.That(count.Value).IsEqualTo(expected);
        _ = await Assert.That(count.IsExact).IsTrue();
    }

    /// <summary>
    /// Every shape whose number of cases MSTest only settles while discovering tests, counted as a lower
    /// bound. The bound itself is asserted as well, because it is what a sum built from it reports, and it
    /// must never be smaller than the number of cases that certainly exist.
    /// </summary>
    /// <param name="methodName">The name of the method whose cases are counted.</param>
    /// <param name="expected">The lower bound of the number of cases.</param>
    [Test]
    [Arguments("SourceFromComputation", 1)]
    [Arguments("SourceFromAnAbsentMember", 1)]
    [Arguments("SourceFromAnExternalTable", 1)]
    [Arguments("SourceFromACustomAttribute", 1)]
    [Arguments("RowAndComputedSource", 2)]
    public async Task GetTestCaseCount_ShapeThatIsNotWrittenDown_IsALowerBound(string methodName, int expected)
    {
        var compilation = CreateCountCases();
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, CountCasesTypeName, methodName);

        var count = recognizer.GetTestCaseCount(method);

        _ = await Assert.That(count.Value).IsEqualTo(expected);
        _ = await Assert.That(count.IsExact).IsFalse();
    }

    /// <summary>
    /// The count does not depend on the resolved test attribute type at all — every attribute it reads is
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
    [Arguments("SourceFromComputation", "1+")]
    [Arguments("RowAndComputedSource", "2+")]
    public async Task GetTestCaseCount_RecognizerWithoutTheWellKnownType_CountsTheSame(
        string methodName,
        string expected
    )
    {
        var compilation = CreateCountCases();
        var recognizer = new MSTestTestMethodRecognizer(null);
        var method = FindMethod(compilation, CountCasesTypeName, methodName);

        var count = recognizer.GetTestCaseCount(method);

        _ = await Assert.That(count.ToString()).IsEqualTo(expected);
    }

    /// <summary>
    /// An attribute that only shares the simple name of a framework attribute contributes nothing, so the
    /// method counts as the single hardcoded case a body without arguments is.
    /// </summary>
    [Test]
    public async Task GetTestCaseCount_ForeignAttributesOnly_CountsOneHardcodedCase()
    {
        var compilation = CreateCases();
        var recognizer = CreateRecognizer(compilation);
        var method = FindMethod(compilation, CasesTypeName, "ForeignAttributeTest");

        var count = recognizer.GetTestCaseCount(method);

        _ = await Assert.That(count.Value).IsEqualTo(1);
        _ = await Assert.That(count.IsExact).IsTrue();
    }

    [Test]
    public async Task GetTestCaseCount_MethodIsNull_ThrowsArgumentNullException()
    {
        var recognizer = new MSTestTestMethodRecognizer(null);

        var exception = Assert.Throws<ArgumentNullException>(() => recognizer.GetTestCaseCount(null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("method");
    }

    [Test]
    public async Task Fixtures_EveryCompilation_CompilesWithoutErrors()
    {
        var errors = new[]
        {
            Describe(CreateCases()),
            Describe(CreateCountCases()),
            Describe(CreateSatelliteConsumer(FrameworkAssemblyName)),
            Describe(CreateSatelliteConsumer(ForeignAssemblyName)),
            Describe(CreateSatelliteConsumer(SatelliteDerivedConsumerSource, FrameworkAssemblyName)),
            Describe(CreateSatelliteConsumer(SatelliteDerivedConsumerSource, ForeignAssemblyName)),
        };

        _ = await Assert
            .That(string.Join(" / ", errors.Distinct(StringComparer.Ordinal)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    private static CSharpCompilation CreateCases() =>
        CompilationFactory.Create([(CasesPath, CasesSource), (ForeignPath, ForeignSource)], TestFramework.MSTest);

    private static CSharpCompilation CreateSatelliteConsumer(string satelliteAssemblyName) =>
        CreateSatelliteConsumer(SatelliteConsumerSource, satelliteAssemblyName);

    /// <summary>
    /// Compiles the satellite into an assembly called <paramref name="satelliteAssemblyName" /> and builds
    /// a compilation of <paramref name="source" /> that references it, which is how a fixture controls the
    /// assembly the well-known attribute name is declared in.
    /// </summary>
    /// <param name="source">The source of the consuming compilation.</param>
    /// <param name="satelliteAssemblyName">The assembly name of the satellite.</param>
    /// <returns>The consuming compilation.</returns>
    private static CSharpCompilation CreateSatelliteConsumer(string source, string satelliteAssemblyName)
    {
        var satellite = CompilationFactory.Create(SatelliteSource, satelliteAssemblyName, filePath: SatellitePath);

        return CompilationFactory.Create(
            source,
            additionalReferences: [satellite.ToMetadataReference()],
            filePath: CasesPath
        );
    }

    private static CSharpCompilation CreateCountCases() =>
        CompilationFactory.Create(CountCasesSource, TestFramework.MSTest, filePath: CountCasesPath);

    private static MSTestTestMethodRecognizer CreateRecognizer(Compilation compilation) =>
        (MSTestTestMethodRecognizer)MSTestTestFrameworkProbe.Instance.TryCreateRecognizer(compilation)!;

    private static IMethodSymbol FindMethod(Compilation compilation, string methodName) =>
        FindMethod(compilation, CasesTypeName, methodName);

    private static IMethodSymbol FindMethod(Compilation compilation, string typeName, string methodName) =>
        compilation.GetTypeByMetadataName(typeName)!.GetMembers(methodName).OfType<IMethodSymbol>().First();

    private static string Describe(Compilation compilation) =>
        DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation));
}
