namespace NetEvolve.FrameShift.Tests.Integration.Generation;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using NetEvolve.FrameShift.Generation;
using NetEvolve.FrameShift.Tests.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Drives <see cref="TestSurfaceManifestGenerator" /> over the data-driven, awkward-shaped test methods
/// that <see cref="TestSurfaceManifestGeneratorTests" /> deliberately keeps out of its own fixtures: the
/// combinations of data-source attributes, matrices and value generators each recognised framework has to
/// turn into a test-case count.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TestSurfaceManifestGeneratorTests" /> proves the shape of the manifest — the block comment,
/// the ordering, the byte-identical re-run — with one plain test per framework. It deliberately avoids the
/// long tail of case-counting shapes, because adding them there would blur what that file is about. This
/// class is that long tail: for every recognised framework, one fixture whose test methods carry the
/// data-source shapes their <c>ITestMethodRecognizer</c> has to fold into a <see cref="TestCaseCount" />,
/// each one asserted against the exact count (or lower bound) documented on the recogniser itself.
/// </para>
/// <para>
/// Every fixture is run through the real generator via <see cref="GeneratorRunner" />, exactly like
/// <see cref="TestSurfaceManifestGeneratorTests" /> does, and the emitted manifest is parsed back with
/// <see cref="TestSurfaceManifestReader" /> so that the assertions read <see cref="TestSurfaceManifest" />
/// values rather than raw manifest text. A test method is looked up by the unique substring of its name in
/// the parsed <see cref="TestSurfaceManifest.TestMethodIds" />, because the exact documentation comment id
/// of an overload-free method already differs only by its parameter list, which is not what any of these
/// tests are about.
/// </para>
/// </remarks>
public class TestSurfaceEdgeCaseTests
{
    private const string ProductionAssemblyName = "ProductionAssembly";
    private const string TestAssemblyName = "TestAssembly";
    private const string ProductionPath = "Production.cs";
    private const string TestPath = "CalculatorTests.cs";

    private const string ProductionSource = """
        namespace Fixture;

        public static class Calculator
        {
            public static int Add(int left, int right)
            {
                return left + right;
            }
        }
        """;

    /// <summary>
    /// One MSTest test per shape <see cref="MSTestTestMethodRecognizer" /> has to count: several
    /// <c>[DataRow]</c> applications, a <c>[DynamicData]</c> referencing a method that only yields, one
    /// summing both kinds, a data source whose length depends on a loop and therefore cannot be read, and
    /// three ways of naming the referenced sequence — an expression-bodied property, a property with a
    /// block-bodied getter, and a member of another type named explicitly through the attribute's second
    /// constructor argument.
    /// </summary>
    private const string MSTestSource = """
        namespace Tests;

        using System.Collections.Generic;
        using Microsoft.VisualStudio.TestTools.UnitTesting;

        [TestClass]
        public class CalculatorTests
        {
            public static IEnumerable<object[]> GetRows()
            {
                yield return new object[] { 1 };
                yield return new object[] { 2 };
                yield return new object[] { 3 };
            }

            public static IEnumerable<object[]> GetComputedRows()
            {
                for (var i = 0; i < 4; i++)
                {
                    yield return new object[] { i };
                }
            }

            public static IEnumerable<object[]> RowsFromProperty => new object[][] { new object[] { 1 }, new object[] { 2 } };

            public static IEnumerable<object[]> RowsFromGetter
            {
                get
                {
                    return new List<object[]> { new object[] { 1 }, new object[] { 2 }, new object[] { 3 } };
                }
            }

            [TestMethod]
            [DataRow(1)]
            [DataRow(2)]
            [DataRow(3)]
            public void DataRow_ThreeRows_IsExactlyThree(int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }

            [TestMethod]
            [DynamicData(nameof(GetRows))]
            public void DynamicData_MethodWithThreeYields_IsExactlyThree(int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }

            [TestMethod]
            [DataRow(1)]
            [DynamicData(nameof(GetRows))]
            public void DataRowAndDynamicData_SumIsExactlyFour(int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }

            [TestMethod]
            [DynamicData(nameof(GetComputedRows))]
            public void DynamicData_ComputedLoop_IsAtLeastOne(int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }

            [TestMethod]
            [DynamicData(nameof(RowsFromProperty))]
            public void DynamicData_ExpressionBodiedProperty_IsExactlyTwo(int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }

            [TestMethod]
            [DynamicData(nameof(RowsFromGetter))]
            public void DynamicData_BlockGetterProperty_IsExactlyThree(int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }

            [TestMethod]
            [DynamicData(nameof(RemoteRows.Get), typeof(RemoteRows))]
            public void DynamicData_ExternalDeclaringType_IsExactlyTwo(int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }
        }

        public static class RemoteRows
        {
            public static IEnumerable<object[]> Get()
            {
                return new[] { new object[] { 1 }, new object[] { 2 } };
            }
        }
        """;

    /// <summary>
    /// One NUnit test per shape <see cref="NUnitTestMethodRecognizer" /> has to count: several
    /// <c>[TestCase]</c> applications, a <c>[TestCaseSource]</c> naming a method of the fixture and one
    /// naming a member of another type, the cross product two <c>[Values]</c> parameters produce, a
    /// <c>[ValueSource]</c> reading a field, both <c>[Range]</c> overloads, both counted <c>[Random]</c>
    /// shapes, the two combining strategies, and a bare <c>[Theory]</c>.
    /// </summary>
    private const string NUnitSource = """
        namespace Tests;

        using System.Collections.Generic;
        using NUnit.Framework;

        public class CalculatorTests
        {
            private static readonly int[] Numbers = [7, 8, 9];

            public static IEnumerable<object[]> Rows()
            {
                yield return new object[] { 1, 2 };
                yield return new object[] { 3, 4 };
            }

            [TestCase(1)]
            [TestCase(2)]
            [TestCase(3)]
            public void TestCase_ThreeCases_IsExactlyThree(int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }

            [TestCaseSource(nameof(Rows))]
            public void TestCaseSource_TwoRows_IsExactlyTwo(int left, int right)
            {
                _ = Fixture.Calculator.Add(left, right);
            }

            [TestCaseSource(typeof(RemoteRows), nameof(RemoteRows.Get))]
            public void TestCaseSource_ExternalTypeMember_IsExactlyTwo(int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }

            [Test]
            public void Values_CrossProduct_IsExactlySix([Values(1, 2, 3)] int a, [Values(4, 5)] int b)
            {
                _ = Fixture.Calculator.Add(a, b);
            }

            [Test]
            public void ValueSource_FromField_IsExactlyThree([ValueSource(nameof(Numbers))] int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }

            [Test]
            public void Range_TwoArgument_IsExactlyFive([Range(1, 5)] int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }

            [Test]
            public void Range_ThreeArgumentStep_IsExactlyThree([Range(0, 4, 2)] int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }

            [Test]
            public void Random_FixedCount_IsExactlyFour([Random(1, 10, 4)] int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }

            [Sequential]
            public void Sequential_TakesTheLongestSet([Values(1, 2, 3)] int a, [Values(4, 5)] int b)
            {
                _ = Fixture.Calculator.Add(a, b);
            }

            [Pairwise]
            public void Pairwise_IsLowerBoundOfTheLongestSet([Values(1, 2)] int a, [Values(3, 4)] int b)
            {
                _ = Fixture.Calculator.Add(a, b);
            }

            [Theory]
            public void Theory_IsLowerBoundOfOne(bool flag)
            {
                _ = Fixture.Calculator.Add(flag ? 1 : 0, 1);
            }
        }

        public static class RemoteRows
        {
            public static IEnumerable<object[]> Get()
            {
                yield return new object[] { 1 };
                yield return new object[] { 2 };
            }
        }
        """;

    /// <summary>
    /// One TUnit test per shape <see cref="TUnitTestMethodRecognizer" /> has to count: several
    /// <c>[Arguments]</c> rows, the cross product two <c>[Matrix]</c> parameters produce, a matrix parameter
    /// excluding one of its own values, a <c>[MethodDataSource]</c> naming a literal sequence and one naming
    /// a computed one, and a <c>[Repeat]</c> proven not to multiply the single inline row it sits next to.
    /// </summary>
    private const string TUnitSource = """
        namespace Tests;

        using System.Collections.Generic;
        using TUnit.Core;

        public class CalculatorTests
        {
            public static int[] LiteralRows() => new[] { 1, 2, 3 };

            public static IEnumerable<int[]> ComputedRows()
            {
                for (var i = 0; i < 4; i++)
                {
                    yield return new[] { i };
                }
            }

            [Test]
            [Arguments(1)]
            [Arguments(2)]
            [Arguments(3)]
            public void Arguments_ThreeRows_IsExactlyThree(int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }

            [Test]
            [MatrixDataSource]
            public void Matrix_CrossProduct_IsExactlySix([Matrix(1, 2, 3)] int a, [Matrix(4, 5)] int b)
            {
                _ = Fixture.Calculator.Add(a, b);
            }

            [Test]
            [MatrixDataSource]
            public void Matrix_WithExcludedValue_IsAtLeastOne(
                [Matrix(1, 2, 3, Excluding = new object[] { 2 })] int a,
                [Matrix(4, 5)] int b
            )
            {
                _ = Fixture.Calculator.Add(a, b);
            }

            [Test]
            [MethodDataSource(nameof(LiteralRows))]
            public void MethodDataSource_LiteralSequence_IsExactlyThree(int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }

            [Test]
            [MethodDataSource(nameof(ComputedRows))]
            public void MethodDataSource_ComputedLoop_IsAtLeastOne(int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }

            [Test]
            [Repeat(5)]
            [Arguments(1)]
            public void Repeat_DoesNotMultiplyTheCount_IsExactlyOne(int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }
        }
        """;

    /// <summary>
    /// One xUnit v2 test per shape <see cref="XunitTestCaseCounter" /> has to count: a plain <c>[Fact]</c>,
    /// several <c>[InlineData]</c> rows, a <c>[MemberData]</c> naming a method of the fixture and one naming
    /// a member of another type through <c>MemberType</c>, a data source whose length depends on a loop, a
    /// bare <c>[Theory]</c> without any data source at all, and a custom marker deriving <c>[Fact]</c> that
    /// degrades an otherwise exact count to a lower bound.
    /// </summary>
    private const string XunitV2Source = """
        namespace Tests;

        using System.Collections.Generic;
        using Xunit;

        public class CalculatorTests
        {
            public static IEnumerable<object[]> Rows()
            {
                return new[] { new object[] { 1 }, new object[] { 2 } };
            }

            public static IEnumerable<object[]> ComputedRows()
            {
                for (var i = 0; i < 3; i++)
                {
                    yield return new object[] { i };
                }
            }

            [Fact]
            public void Fact_IsExactlyOne()
            {
                _ = Fixture.Calculator.Add(1, 1);
            }

            [Theory]
            [InlineData(1)]
            [InlineData(2)]
            [InlineData(3)]
            public void InlineData_ThreeRows_IsExactlyThree(int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }

            [Theory]
            [MemberData(nameof(Rows))]
            public void MemberData_TwoRows_IsExactlyTwo(int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }

            [Theory]
            [MemberData(nameof(RemoteRows.Get), MemberType = typeof(RemoteRows))]
            public void MemberData_ExternalMemberType_IsExactlyTwo(int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }

            [Theory]
            [MemberData(nameof(ComputedRows))]
            public void MemberData_ComputedLoop_IsAtLeastOne(int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }

            [Theory]
            public void Theory_WithoutData_IsExactlyZero(int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }

            [CustomFact]
            [InlineData(1)]
            [InlineData(2)]
            public void CustomMarkerWithInlineData_IsAtLeastTwo(int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }
        }

        public sealed class CustomFactAttribute : FactAttribute
        {
        }

        public static class RemoteRows
        {
            public static IEnumerable<object[]> Get()
            {
                yield return new object[] { 1 };
                yield return new object[] { 2 };
            }
        }
        """;

#if FRAMESHIFT_XUNIT_V3
    /// <summary>
    /// The xUnit v3 counterpart of <see cref="XunitV2Source" />, narrowed to the shapes that are identical
    /// between the two major versions: <see cref="XunitTestCaseCounter" /> shares its rules across both, so
    /// this fixture exists only to prove the same rules apply when the recogniser is built for
    /// <c>xunit.v3.core</c> instead of <c>xunit.core</c>.
    /// </summary>
    private const string XunitV3Source = """
        namespace Tests;

        using System.Collections.Generic;
        using Xunit;

        public class CalculatorTests
        {
            public static IEnumerable<object[]> Rows()
            {
                yield return new object[] { 1 };
                yield return new object[] { 2 };
                yield return new object[] { 3 };
            }

            [Theory]
            [InlineData(1)]
            [InlineData(2)]
            public void InlineData_TwoRows_IsExactlyTwo(int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }

            [Theory]
            [MemberData(nameof(Rows))]
            public void MemberData_ThreeRows_IsExactlyThree(int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }

            [Theory]
            public void Theory_WithoutData_IsExactlyZero(int value)
            {
                _ = Fixture.Calculator.Add(value, value);
            }
        }
        """;
#endif

    [Test]
    public async Task Fixtures_EveryCompilation_CompileWithoutErrors()
    {
        var production = CreateProduction();
        List<string> described =
        [
            Describe(production),
            Describe(CreateTest(TestFramework.MSTest, MSTestSource, production)),
            Describe(CreateTest(TestFramework.NUnit, NUnitSource, production)),
            Describe(CreateTest(TestFramework.TUnit, TUnitSource, production)),
            Describe(CreateTest(TestFramework.XunitV2, XunitV2Source, production)),
        ];
#if FRAMESHIFT_XUNIT_V3
        described.Add(Describe(CreateTest(TestFramework.XunitV3, XunitV3Source, production)));
#endif

        _ = await Assert
            .That(string.Join("|", described.Distinct(StringComparer.Ordinal)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    public async Task Generate_MSTestDataSourceShapes_MatchTheDocumentedCounts()
    {
        var manifest = CollectManifest(TestFramework.MSTest, MSTestSource);

        using (Assert.Multiple())
        {
            _ = await AssertCount(manifest, "DataRow_ThreeRows_IsExactlyThree", TestCaseCount.Exact(3))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "DynamicData_MethodWithThreeYields_IsExactlyThree", TestCaseCount.Exact(3))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "DataRowAndDynamicData_SumIsExactlyFour", TestCaseCount.Exact(4))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "DynamicData_ComputedLoop_IsAtLeastOne", TestCaseCount.AtLeast(1))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "DynamicData_ExpressionBodiedProperty_IsExactlyTwo", TestCaseCount.Exact(2))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "DynamicData_BlockGetterProperty_IsExactlyThree", TestCaseCount.Exact(3))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "DynamicData_ExternalDeclaringType_IsExactlyTwo", TestCaseCount.Exact(2))
                .ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Generate_NUnitDataSourceShapes_MatchTheDocumentedCounts()
    {
        var manifest = CollectManifest(TestFramework.NUnit, NUnitSource);

        using (Assert.Multiple())
        {
            _ = await AssertCount(manifest, "TestCase_ThreeCases_IsExactlyThree", TestCaseCount.Exact(3))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "TestCaseSource_TwoRows_IsExactlyTwo", TestCaseCount.Exact(2))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "TestCaseSource_ExternalTypeMember_IsExactlyTwo", TestCaseCount.Exact(2))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "Values_CrossProduct_IsExactlySix", TestCaseCount.Exact(6))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "ValueSource_FromField_IsExactlyThree", TestCaseCount.Exact(3))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "Range_TwoArgument_IsExactlyFive", TestCaseCount.Exact(5))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "Range_ThreeArgumentStep_IsExactlyThree", TestCaseCount.Exact(3))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "Random_FixedCount_IsExactlyFour", TestCaseCount.Exact(4))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "Sequential_TakesTheLongestSet", TestCaseCount.Exact(3))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "Pairwise_IsLowerBoundOfTheLongestSet", TestCaseCount.AtLeast(2))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "Theory_IsLowerBoundOfOne", TestCaseCount.AtLeast(1)).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Generate_TUnitDataSourceShapes_MatchTheDocumentedCounts()
    {
        var manifest = CollectManifest(TestFramework.TUnit, TUnitSource);

        using (Assert.Multiple())
        {
            _ = await AssertCount(manifest, "Arguments_ThreeRows_IsExactlyThree", TestCaseCount.Exact(3))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "Matrix_CrossProduct_IsExactlySix", TestCaseCount.Exact(6))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "Matrix_WithExcludedValue_IsAtLeastOne", TestCaseCount.AtLeast(1))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "MethodDataSource_LiteralSequence_IsExactlyThree", TestCaseCount.Exact(3))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "MethodDataSource_ComputedLoop_IsAtLeastOne", TestCaseCount.AtLeast(1))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "Repeat_DoesNotMultiplyTheCount_IsExactlyOne", TestCaseCount.Exact(1))
                .ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Generate_XunitV2DataSourceShapes_MatchTheDocumentedCounts()
    {
        var manifest = CollectManifest(TestFramework.XunitV2, XunitV2Source);

        using (Assert.Multiple())
        {
            _ = await AssertCount(manifest, "Fact_IsExactlyOne", TestCaseCount.Exact(1)).ConfigureAwait(false);
            _ = await AssertCount(manifest, "InlineData_ThreeRows_IsExactlyThree", TestCaseCount.Exact(3))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "MemberData_TwoRows_IsExactlyTwo", TestCaseCount.Exact(2))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "MemberData_ExternalMemberType_IsExactlyTwo", TestCaseCount.Exact(2))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "MemberData_ComputedLoop_IsAtLeastOne", TestCaseCount.AtLeast(1))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "Theory_WithoutData_IsExactlyZero", TestCaseCount.Exact(0))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "CustomMarkerWithInlineData_IsAtLeastTwo", TestCaseCount.AtLeast(2))
                .ConfigureAwait(false);
        }
    }

#if FRAMESHIFT_XUNIT_V3
    [Test]
    public async Task Generate_XunitV3DataSourceShapes_MatchTheDocumentedCounts()
    {
        var manifest = CollectManifest(TestFramework.XunitV3, XunitV3Source);

        using (Assert.Multiple())
        {
            _ = await AssertCount(manifest, "InlineData_TwoRows_IsExactlyTwo", TestCaseCount.Exact(2))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "MemberData_ThreeRows_IsExactlyThree", TestCaseCount.Exact(3))
                .ConfigureAwait(false);
            _ = await AssertCount(manifest, "Theory_WithoutData_IsExactlyZero", TestCaseCount.Exact(0))
                .ConfigureAwait(false);
        }
    }
#endif

    private static async Task<bool> AssertCount(TestSurfaceManifest manifest, string methodName, TestCaseCount expected)
    {
        _ = await Assert.That(manifest.TestCaseCounts[IdOf(manifest, methodName)]).IsEqualTo(expected);

        return true;
    }

    private static TestSurfaceManifest CollectManifest(TestFramework framework, string source)
    {
        var test = CreateTest(framework, source, CreateProduction());
        var (success, error, manifest) = Read(Generate(test));

        if (!success)
        {
            throw new InvalidOperationException($"The generated manifest of '{framework}' does not parse: {error}");
        }

        return manifest;
    }

    /// <summary>
    /// Finds the single test method whose documentation comment id carries <paramref name="methodName" />
    /// as its own member name, rather than as a prefix of a longer one.
    /// </summary>
    /// <param name="manifest">The manifest to search.</param>
    /// <param name="methodName">The unqualified name of the test method.</param>
    /// <returns>The matching documentation comment id.</returns>
    private static string IdOf(TestSurfaceManifest manifest, string methodName) =>
        manifest.TestMethodIds.Single(id =>
            id.EndsWith("." + methodName, StringComparison.Ordinal)
            || id.Contains("." + methodName + "(", StringComparison.Ordinal)
        );

    private static GeneratorRunner.Output Run(Compilation compilation) =>
        GeneratorRunner.Run(new TestSurfaceManifestGenerator(), compilation);

    private static string Generate(Compilation compilation) =>
        Run(compilation).TextOf(TestSurfaceManifestGenerator.HintName);

    private static CSharpCompilation CreateProduction() =>
        CompilationFactory.Create(
            ProductionSource,
            TestFramework.None,
            ProductionAssemblyName,
            filePath: ProductionPath
        );

    private static CSharpCompilation CreateTest(TestFramework framework, string source, Compilation production) =>
        CompilationFactory.Create(
            source,
            framework,
            TestAssemblyName,
            additionalReferences: [production.ToMetadataReference()],
            filePath: TestPath
        );

    /// <summary>
    /// Parses the generated file the way the MSBuild target does: drop the first and the last line, hand
    /// the rest to the reader.
    /// </summary>
    /// <param name="generated">The content of the generated source file.</param>
    /// <returns>Whether the text parsed, the reported error and the parsed manifest.</returns>
    private static (bool Success, string Error, TestSurfaceManifest Manifest) Read(string generated)
    {
        var inner = string.Join("\n", Lines(generated).Skip(1).SkipLast(1)) + "\n";
        var success = TestSurfaceManifestReader.TryRead(SourceText.From(inner), out var manifest, out var error);

        return (success, error ?? string.Empty, manifest);
    }

    /// <summary>
    /// Splits the generated text into its lines, dropping the empty remainder behind the trailing line
    /// feed, which is the end of the last line and not a line of its own.
    /// </summary>
    /// <param name="text">The generated text.</param>
    /// <returns>The lines, without their line endings.</returns>
    private static ImmutableArray<string> Lines(string text)
    {
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();

        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return [.. lines];
    }

    private static string Describe(Compilation compilation) =>
        DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation));
}
