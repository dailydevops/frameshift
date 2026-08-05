namespace NetEvolve.FrameShift.Tests.Integration.Analyzers;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Analyzers;
using NetEvolve.FrameShift.Diagnostics;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Proves through the complete two-pass cycle that every test marker of every supported framework version
/// really does contribute the production code its tests exercise, and that a data-source attribute alone
/// never does.
/// </summary>
/// <remarks>
/// <para>
/// A recogniser that misses a marker has no visible symptom on the test side: no diagnostic is missing, no
/// manifest is malformed, the test method is simply not there. The symptom appears one pass later and one
/// project away, as an <c>FSH0001</c> the production analyzer reports for a member that is in truth
/// covered. No recogniser unit test can reach that symptom, because none of them runs the production
/// analyzer, so every claim in this class is made through the whole cycle: collect the surface of a real
/// test compilation, serialise the manifest exactly as it would be checked in, feed it to
/// <see cref="MutationCoverageAnalyzer" /> and read the gaps.
/// </para>
/// <para>
/// <b>The shape of every marker fixture.</b> The production fixture carries one static method per marker of
/// the framework under test, named after that marker, plus <c>ExercisedByNothing</c>, which no test ever
/// names. Every method body is one binary arithmetic expression and therefore a mutation point. The test
/// fixture exercises exactly one production method per marker, so the whole cycle can be asserted as a
/// single exact set: the gaps are <c>ExercisedByNothing</c> and nothing else. A marker that stops being
/// recognised adds its own method to that set and names itself in the failure, and the deliberately
/// uncovered method proves in the same breath that the analyzer was awake and the manifest usable —
/// without it, "no gap" could just as well mean "nothing was analysed at all".
/// </para>
/// <para>
/// <b>The negative direction is the worse one.</b> A recogniser that over-matches turns a method carrying
/// nothing but <c>[Arguments]</c>, <c>[InlineData]</c>, <c>[DataRow]</c> or <c>[Repeat]</c> into a test,
/// records the production code that method names, and silences a real gap for good. Nobody ever sees a
/// warning that is not emitted, so that failure is strictly worse than a false one, and the data-source
/// fixtures assert the exact opposite outcome: the member only a data-source-carrying method names stays
/// reported.
/// </para>
/// <para>
/// NUnit is the one framework where a data-source attribute <em>is</em> a marker — <c>[TestCase]</c> and
/// <c>[TestCaseSource]</c> build tests on their own, which is why they are listed among its markers here
/// and its negative fixture uses <c>[Repeat]</c>, <c>[Retry]</c>, <c>[Category]</c> and a parameter-level
/// <c>[Values]</c> instead.
/// </para>
/// <para>
/// The xUnit.net v3 cases are guarded by <c>FRAMESHIFT_XUNIT_V3</c>, because that package ships no assets
/// for net6.0 and net7.0. Everything else runs on all eight target frameworks: the fixtures are plain
/// source text, the manifest is derived from that text alone, and no assertion depends on a line number.
/// </para>
/// </remarks>
public class TestMarkerRecognitionTests
{
    private const string TUnitFramework = TUnitTestFrameworkProbe.Name;
    private const string XunitV2Framework = XunitV2TestFrameworkProbe.Name;
    private const string XunitV3Framework = XunitV3TestFrameworkProbe.Name;
    private const string NUnitFramework = NUnitTestFrameworkProbe.Name;
    private const string MSTestFramework = MSTestTestFrameworkProbe.Name;

    private const string ProductionAssemblyName = "ProductionAssembly";
    private const string TestAssemblyName = "TestAssembly";
    private const string ProductionPath = "Production.cs";
    private const string TestPath = "MarkerTests.cs";

    private const char LineFeedCharacter = '\n';
    private const string Separator = "|";

    /// <summary>
    /// The prefix of a manifest line recording a method of the production fixture, so that the recorded
    /// members can be read back as plain method names.
    /// </summary>
    private const string CalculatorReferencePrefix = "R M:Fixture.Calculator.";

    /// <summary>
    /// The name reported for a gap that sits in no method declaration at all, which would mean the
    /// production fixture no longer looks the way this class assumes.
    /// </summary>
    private const string UnattributedGap = "<no method>";

    /// <summary>
    /// The production method no test of a marker fixture ever names.
    /// </summary>
    private const string NothingExercised = "ExercisedByNothing";

    /// <summary>
    /// The production method the single marked test of a data-source fixture names.
    /// </summary>
    private const string ExercisedMember = "Exercised";

    /// <summary>
    /// The production method only the data-source-carrying methods name, and which therefore has to stay
    /// reported as a gap.
    /// </summary>
    private const string UntouchedMember = "Untouched";

    private const string TUnitMarkerMembers = "ExercisedByDerivedMarker|ExercisedByDynamicTestBuilder|ExercisedByTest";

    private const string XunitMarkerMembers = "ExercisedByDerivedMarker|ExercisedByFact|ExercisedByTheory";

    private const string XunitV3MarkerMembers =
        "ExercisedByCulturedFact|ExercisedByCulturedTheory|ExercisedByDerivedMarker|ExercisedByFact|"
        + "ExercisedByMarkerInterface|ExercisedByTheory";

    private const string NUnitMarkerMembers =
        "ExercisedByCombinatorial|ExercisedByDerivedMarker|ExercisedByPairwise|ExercisedBySequential|"
        + "ExercisedByTest|ExercisedByTestCase|ExercisedByTestCaseSource|ExercisedByTheory";

    private const string MSTestMarkerMembers =
        "ExercisedByDataTestMethod|ExercisedByDerivedMarker|ExercisedByStaTestMethod|ExercisedByTestMethod";

    /// <summary>
    /// The production assembly of the central regression case and of every data-source fixture: one method
    /// a marked test exercises and one method no test does, both of them mutable.
    /// </summary>
    private const string PairProductionSource = """
        namespace Fixture;

        public static class Calculator
        {
            public static int Exercised(int left, int right)
            {
                return left + right;
            }

            public static int Untouched(int left, int right)
            {
                return left + right;
            }
        }
        """;

    private const string TUnitProductionSource = """
        namespace Fixture;

        public static class Calculator
        {
            public static int ExercisedByTest(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedByDynamicTestBuilder(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedByDerivedMarker(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedByNothing(int left, int right)
            {
                return left + right;
            }
        }
        """;

    private const string XunitProductionSource = """
        namespace Fixture;

        public static class Calculator
        {
            public static int ExercisedByFact(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedByTheory(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedByDerivedMarker(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedByNothing(int left, int right)
            {
                return left + right;
            }
        }
        """;

    private const string XunitV3ProductionSource = """
        namespace Fixture;

        public static class Calculator
        {
            public static int ExercisedByFact(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedByTheory(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedByDerivedMarker(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedByCulturedFact(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedByCulturedTheory(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedByMarkerInterface(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedByNothing(int left, int right)
            {
                return left + right;
            }
        }
        """;

    private const string NUnitProductionSource = """
        namespace Fixture;

        public static class Calculator
        {
            public static int ExercisedByTest(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedByTestCase(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedByTestCaseSource(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedByTheory(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedByCombinatorial(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedByPairwise(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedBySequential(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedByDerivedMarker(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedByNothing(int left, int right)
            {
                return left + right;
            }
        }
        """;

    private const string MSTestProductionSource = """
        namespace Fixture;

        public static class Calculator
        {
            public static int ExercisedByTestMethod(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedByDataTestMethod(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedByStaTestMethod(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedByDerivedMarker(int left, int right)
            {
                return left + right;
            }

            public static int ExercisedByNothing(int left, int right)
            {
                return left + right;
            }
        }
        """;

    /// <summary>
    /// The fixture of the central regression case: a TUnit test marked with the second marker of the
    /// framework, <c>[DynamicTestBuilder]</c>, and nothing else.
    /// </summary>
    private const string TUnitDynamicBuilderTestSource = """
        namespace Tests;

        using TUnit.Core;

        public class DynamicBuilderTests
        {
            [DynamicTestBuilder]
            public void BuildsTestsDynamically()
            {
                _ = Fixture.Calculator.Exercised(2, 3);
            }
        }
        """;

    /// <summary>
    /// One TUnit test per marker: the sealed <c>[Test]</c>, the second shipped marker
    /// <c>[DynamicTestBuilder]</c>, and a user-defined marker deriving from the latter — which is the only
    /// way a project can declare a marker at all, because the constructor of <c>BaseTestAttribute</c> is
    /// internal to the framework.
    /// </summary>
    private const string TUnitMarkerTestSource = """
        namespace Tests;

        using TUnit.Core;

        public sealed class ScenarioTestAttribute : DynamicTestBuilderAttribute
        {
        }

        public class MarkerTests
        {
            [Test]
            public void TestMarker()
            {
                _ = Fixture.Calculator.ExercisedByTest(2, 3);
            }

            [DynamicTestBuilder]
            public void DynamicTestBuilderMarker()
            {
                _ = Fixture.Calculator.ExercisedByDynamicTestBuilder(2, 3);
            }

            [ScenarioTest]
            public void DerivedMarker()
            {
                _ = Fixture.Calculator.ExercisedByDerivedMarker(2, 3);
            }
        }
        """;

    /// <summary>
    /// A TUnit fixture in which only one method is a test. The others carry a data source and nothing else,
    /// which TUnit does not run: <c>ArgumentsAttribute</c> and <c>MethodDataSourceAttribute</c> derive from
    /// <see cref="Attribute" /> and implement the framework's data-source interface, never a marker, so
    /// <c>[Test]</c> stays required next to them.
    /// </summary>
    private const string TUnitDataSourceTestSource = """
        namespace Tests;

        using System.Collections.Generic;
        using TUnit.Core;

        public class DataSourceTests
        {
            [Test]
            public void MarkedTest()
            {
                _ = Fixture.Calculator.Exercised(2, 3);
            }

            [Arguments(1)]
            public void ArgumentsOnly(int value)
            {
                _ = Fixture.Calculator.Untouched(value, 3);
            }

            [MethodDataSource(nameof(Rows))]
            public void MethodDataSourceOnly(int value)
            {
                _ = Fixture.Calculator.Untouched(value, 3);
            }

            public static IEnumerable<int> Rows()
            {
                yield return 1;
            }
        }
        """;

    /// <summary>
    /// One xUnit.net test per marker of version 2: <c>[Fact]</c>, <c>[Theory]</c>, and an attribute deriving
    /// from <c>FactAttribute</c>. Version 2 has no marker outside that chain — its <c>FactAttribute</c>
    /// implements no interface at all, so derivation is the only way to mark a test.
    /// </summary>
    /// <remarks>
    /// The names are spelled exactly as a version 3 fixture would spell them; which version they bind to is
    /// decided by the referenced assembly alone.
    /// </remarks>
    private const string XunitMarkerTestSource = """
        namespace Tests;

        using Xunit;

        public sealed class ScenarioFactAttribute : FactAttribute
        {
        }

        public class MarkerTests
        {
            [Fact]
            public void FactMarker()
            {
                _ = Fixture.Calculator.ExercisedByFact(2, 3);
            }

            [Theory]
            [InlineData(1)]
            public void TheoryMarker(int value)
            {
                _ = Fixture.Calculator.ExercisedByTheory(value, 3);
            }

            [ScenarioFact]
            public void DerivedMarker()
            {
                _ = Fixture.Calculator.ExercisedByDerivedMarker(2, 3);
            }
        }
        """;

    /// <summary>
    /// An xUnit.net fixture in which only <c>[Fact]</c> marks a test, usable for both major versions. The
    /// data-source attributes derive from the framework's <c>DataAttribute</c> and never from
    /// <c>FactAttribute</c>, and they implement the data-source interface rather than the marker interface,
    /// so a method carrying one of them alone is run by neither version.
    /// </summary>
    private const string XunitDataSourceTestSource = """
        namespace Tests;

        using System.Collections.Generic;
        using Xunit;

        public class DataSourceTests
        {
            [Fact]
            public void MarkedTest()
            {
                _ = Fixture.Calculator.Exercised(2, 3);
            }

            [InlineData(1)]
            public void InlineDataOnly(int value)
            {
                _ = Fixture.Calculator.Untouched(value, 3);
            }

            [MemberData(nameof(Rows))]
            public void MemberDataOnly(int value)
            {
                _ = Fixture.Calculator.Untouched(value, 3);
            }

            [ClassData(typeof(RowSource))]
            public void ClassDataOnly(int value)
            {
                _ = Fixture.Calculator.Untouched(value, 3);
            }

            public static IEnumerable<object[]> Rows => new[] { new object[] { 1 } };
        }

        public sealed class RowSource
        {
        }
        """;

    /// <summary>
    /// One xUnit.net test per marker of version 3, which has three more than version 2:
    /// <c>[CulturedFact]</c>, <c>[CulturedTheory]</c> and — the case no base-type rule can see at all — an
    /// attribute that merely implements <c>Xunit.v3.IFactAttribute</c> and shares no base type with
    /// <c>FactAttribute</c>. Version 3 keys discovery on that interface, so such an attribute is a marker,
    /// and a recogniser hooking the shipped class instead of the interface misses it.
    /// </summary>
    /// <remarks>
    /// The constant is declared unconditionally, because it is nothing but text; only the tests using it are
    /// guarded by <c>FRAMESHIFT_XUNIT_V3</c>, since compiling it needs the real <c>xunit.v3.core</c>.
    /// </remarks>
    private const string XunitV3MarkerTestSource = """
        namespace Tests;

        using System;
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

        public class MarkerTests
        {
            [Fact]
            public void FactMarker()
            {
                _ = Fixture.Calculator.ExercisedByFact(2, 3);
            }

            [Theory]
            [InlineData(1)]
            public void TheoryMarker(int value)
            {
                _ = Fixture.Calculator.ExercisedByTheory(value, 3);
            }

            [ScenarioFact]
            public void DerivedMarker()
            {
                _ = Fixture.Calculator.ExercisedByDerivedMarker(2, 3);
            }

            [CulturedFact(new string[] { "en-US" })]
            public void CulturedFactMarker()
            {
                _ = Fixture.Calculator.ExercisedByCulturedFact(2, 3);
            }

            [CulturedTheory(new string[] { "en-US" })]
            [InlineData(1)]
            public void CulturedTheoryMarker(int value)
            {
                _ = Fixture.Calculator.ExercisedByCulturedTheory(value, 3);
            }

            [MarkerInterfaceFact]
            public void MarkerInterfaceMarker()
            {
                _ = Fixture.Calculator.ExercisedByMarkerInterface(2, 3);
            }
        }
        """;

    /// <summary>
    /// One NUnit test per marker. Besides <c>[Test]</c>, <c>[TestCase]</c> and <c>[TestCaseSource]</c>,
    /// NUnit builds a test from <c>[Theory]</c> and from each of the three combining strategies, and a
    /// specialisation inherits the builder interface that makes its base a marker.
    /// </summary>
    private const string NUnitMarkerTestSource = """
        namespace Tests;

        using System.Collections.Generic;
        using NUnit.Framework;

        public sealed class ScenarioTestAttribute : TestAttribute
        {
        }

        public class MarkerTests
        {
            [Test]
            public void TestMarker()
            {
                _ = Fixture.Calculator.ExercisedByTest(2, 3);
            }

            [TestCase(1)]
            public void TestCaseMarker(int value)
            {
                _ = Fixture.Calculator.ExercisedByTestCase(value, 3);
            }

            [TestCaseSource(nameof(Rows))]
            public void TestCaseSourceMarker(int value)
            {
                _ = Fixture.Calculator.ExercisedByTestCaseSource(value, 3);
            }

            [Theory]
            public void TheoryMarker(bool flag)
            {
                _ = Fixture.Calculator.ExercisedByTheory(2, 3);
            }

            [Combinatorial]
            public void CombinatorialMarker([Values(1, 2)] int value)
            {
                _ = Fixture.Calculator.ExercisedByCombinatorial(value, 3);
            }

            [Pairwise]
            public void PairwiseMarker([Values(1, 2)] int value)
            {
                _ = Fixture.Calculator.ExercisedByPairwise(value, 3);
            }

            [Sequential]
            public void SequentialMarker([Values(1, 2)] int value)
            {
                _ = Fixture.Calculator.ExercisedBySequential(value, 3);
            }

            [ScenarioTest]
            public void DerivedMarker()
            {
                _ = Fixture.Calculator.ExercisedByDerivedMarker(2, 3);
            }

            public static IEnumerable<int> Rows()
            {
                yield return 1;
            }
        }
        """;

    /// <summary>
    /// An NUnit fixture in which only <c>[Test]</c> marks a test. The others carry an attribute that
    /// configures or describes a test without building one — <c>[Repeat]</c> and <c>[Retry]</c> implement
    /// the repeat interface, <c>[Category]</c> is a property, <c>[Values]</c> feeds a parameter — so none of
    /// them may put the production code they name onto the test surface.
    /// </summary>
    private const string NUnitDataSourceTestSource = """
        namespace Tests;

        using NUnit.Framework;

        public class DataSourceTests
        {
            [Test]
            public void MarkedTest()
            {
                _ = Fixture.Calculator.Exercised(2, 3);
            }

            [Repeat(2)]
            public void RepeatOnly()
            {
                _ = Fixture.Calculator.Untouched(2, 3);
            }

            [Retry(2)]
            public void RetryOnly()
            {
                _ = Fixture.Calculator.Untouched(2, 3);
            }

            [Category("slow")]
            public void CategoryOnly()
            {
                _ = Fixture.Calculator.Untouched(2, 3);
            }

            public void ValuesOnly([Values(1, 2)] int value)
            {
                _ = Fixture.Calculator.Untouched(value, 3);
            }
        }
        """;

    /// <summary>
    /// One MSTest test per marker: <c>[TestMethod]</c>, the two attributes deriving from it that the
    /// framework ships, and a user-defined one. MSTest 4 introduced no marker outside that chain.
    /// </summary>
    private const string MSTestMarkerTestSource = """
        namespace Tests;

        using Microsoft.VisualStudio.TestTools.UnitTesting;

        public sealed class ScenarioTestMethodAttribute : TestMethodAttribute
        {
        }

        [TestClass]
        public class MarkerTests
        {
            [TestMethod]
            public void TestMethodMarker()
            {
                _ = Fixture.Calculator.ExercisedByTestMethod(2, 3);
            }

            [DataTestMethod]
            [DataRow(1)]
            public void DataTestMethodMarker(int value)
            {
                _ = Fixture.Calculator.ExercisedByDataTestMethod(value, 3);
            }

            [STATestMethod]
            public void StaTestMethodMarker()
            {
                _ = Fixture.Calculator.ExercisedByStaTestMethod(2, 3);
            }

            [ScenarioTestMethod]
            public void DerivedMarker()
            {
                _ = Fixture.Calculator.ExercisedByDerivedMarker(2, 3);
            }
        }
        """;

    /// <summary>
    /// An MSTest fixture in which only <c>[TestMethod]</c> marks a test. <c>DataRowAttribute</c> and
    /// <c>DynamicDataAttribute</c> derive from <see cref="Attribute" /> and implement the framework's
    /// data-source interface, so a method carrying one of them alone is never run.
    /// </summary>
    private const string MSTestDataSourceTestSource = """
        namespace Tests;

        using System.Collections.Generic;
        using Microsoft.VisualStudio.TestTools.UnitTesting;

        [TestClass]
        public class DataSourceTests
        {
            [TestMethod]
            public void MarkedTest()
            {
                _ = Fixture.Calculator.Exercised(2, 3);
            }

            [DataRow(1)]
            public void DataRowOnly(int value)
            {
                _ = Fixture.Calculator.Untouched(value, 3);
            }

            [DynamicData(nameof(Rows))]
            public void DynamicDataOnly(int value)
            {
                _ = Fixture.Calculator.Untouched(value, 3);
            }

            public static IEnumerable<object[]> Rows
            {
                get { yield return new object[] { 1 }; }
            }
        }
        """;

    [Test]
    public async Task Fixtures_EveryCompilation_CompilesWithoutErrors()
    {
        var described = new List<string>
        {
            Describe(CreateProduction(PairProductionSource)),
            Describe(CreateTest(PairProductionSource, TUnitDynamicBuilderTestSource, TestFramework.TUnit)),
        };

        foreach (var framework in GetFrameworks())
        {
            var scenario = GetScenario(framework);

            described.Add(Describe(CreateProduction(scenario.ProductionSource)));
            described.Add(Describe(CreateTest(scenario.ProductionSource, scenario.MarkerSource, scenario.Framework)));
            described.Add(Describe(CreateTest(PairProductionSource, scenario.DataSourceSource, scenario.Framework)));
        }

        _ = await Assert
            .That(string.Join(" / ", described.Distinct(StringComparer.Ordinal)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    /// <summary>
    /// The regression guard for the entire class of defect this suite exists for: a TUnit test method marked
    /// with the framework's <em>second</em> marker, <c>[DynamicTestBuilder]</c>, exercises production code,
    /// and the production analyzer must report no gap there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The recogniser used to match the sealed <c>TUnit.Core.TestAttribute</c> exactly, or an ancestor whose
    /// simple name happened to be <c>TestAttribute</c>. Neither rule catches
    /// <c>DynamicTestBuilderAttribute</c>, although TUnit runs a method marked with it just the same. That
    /// method was therefore no test, the production member it exercises never reached the manifest, and the
    /// production analyzer reported <c>FSH0001</c> for code that is tested — a false gap in a project whose
    /// author has no way of seeing the cause. This is the test that fails on it.
    /// </para>
    /// <para>
    /// The claim is made in both directions at once, because "no gap" alone would also hold for an analysis
    /// that never ran: <c>Untouched</c>, which no test names, is still reported, the manifest really does
    /// record the exercised member, and the manifest itself is never complained about.
    /// </para>
    /// </remarks>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    public async Task RoundTrip_TUnitTestMarkedWithDynamicTestBuilder_ReportsNoGapForTheProductionItExercises()
    {
        var cycle = await RunCycleAsync(
                PairProductionSource,
                TUnitDynamicBuilderTestSource,
                TestFramework.TUnit,
                TUnitTestFrameworkProbe.Instance
            )
            .ConfigureAwait(false);
        var problems = DescribeManifestProblems(cycle.Diagnostics);

        using (Assert.Multiple())
        {
            _ = await Assert.That(DescribeRecordedMembers(cycle.Manifest)).IsEqualTo(ExercisedMember);
            _ = await Assert.That(DescribeGaps(cycle.Diagnostics)).IsEqualTo(UntouchedMember);
            _ = await Assert.That(problems).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    /// <summary>
    /// Every marker of every supported framework version contributes the production code its test
    /// exercises, so the only gap left is the one method no test names at all.
    /// </summary>
    /// <remarks>
    /// The expectation is an exact set rather than a count, so a marker that stops being recognised names
    /// itself in the failure: its production method shows up next to <c>ExercisedByNothing</c>.
    /// </remarks>
    /// <param name="framework">The framework version whose markers are exercised.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    [Arguments(TUnitFramework)]
    [Arguments(XunitV2Framework)]
#if FRAMESHIFT_XUNIT_V3
    [Arguments(XunitV3Framework)]
#endif
    [Arguments(NUnitFramework)]
    [Arguments(MSTestFramework)]
    public async Task RoundTrip_EveryMarkerOfAFramework_ReportsNoGapForTheMethodItsTestExercises(string framework)
    {
        var scenario = GetScenario(framework);
        var cycle = await RunCycleAsync(
                scenario.ProductionSource,
                scenario.MarkerSource,
                scenario.Framework,
                scenario.Probe
            )
            .ConfigureAwait(false);
        var problems = DescribeManifestProblems(cycle.Diagnostics);

        using (Assert.Multiple())
        {
            _ = await Assert.That(DescribeGaps(cycle.Diagnostics)).IsEqualTo(NothingExercised);
            _ = await Assert.That(problems).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    /// <summary>
    /// The cause behind the previous test's effect, pinned at the artefact that travels between the two
    /// passes: the manifest records the production member of every marked test, and of nothing else.
    /// </summary>
    /// <param name="framework">The framework version whose markers are exercised.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    [Arguments(TUnitFramework)]
    [Arguments(XunitV2Framework)]
#if FRAMESHIFT_XUNIT_V3
    [Arguments(XunitV3Framework)]
#endif
    [Arguments(NUnitFramework)]
    [Arguments(MSTestFramework)]
    public async Task Manifest_EveryMarkerOfAFramework_RecordsTheProductionMemberOfItsTest(string framework)
    {
        var scenario = GetScenario(framework);
        var test = CreateTest(scenario.ProductionSource, scenario.MarkerSource, scenario.Framework);
        var manifest = CreateManifest(test, scenario.Probe);

        _ = await Assert.That(DescribeRecordedMembers(manifest)).IsEqualTo(scenario.ExpectedMarkerMembers);
    }

    /// <summary>
    /// The direction that must never be given up: a method carrying nothing but a data source is not a test,
    /// so the production member it names stays reported as a gap.
    /// </summary>
    /// <remarks>
    /// A recogniser that over-matches here would record that member as covered and silence a real gap
    /// permanently. The fixture holds one properly marked test as well, so the manifest is usable and the
    /// analyzer is provably awake; failing to recognise <em>that</em> test would leave no gap reported at all
    /// and fail this assertion just as loudly.
    /// </remarks>
    /// <param name="framework">The framework version whose data-source attributes are used.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    [Arguments(TUnitFramework)]
    [Arguments(XunitV2Framework)]
#if FRAMESHIFT_XUNIT_V3
    [Arguments(XunitV3Framework)]
#endif
    [Arguments(NUnitFramework)]
    [Arguments(MSTestFramework)]
    public async Task RoundTrip_MethodCarryingOnlyADataSource_StillReportsItsProductionMemberAsAGap(string framework)
    {
        var scenario = GetScenario(framework);
        var cycle = await RunCycleAsync(
                PairProductionSource,
                scenario.DataSourceSource,
                scenario.Framework,
                scenario.Probe
            )
            .ConfigureAwait(false);
        var problems = DescribeManifestProblems(cycle.Diagnostics);

        using (Assert.Multiple())
        {
            _ = await Assert.That(DescribeGaps(cycle.Diagnostics)).IsEqualTo(UntouchedMember);
            _ = await Assert.That(problems).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    /// <summary>
    /// The same claim read off the manifest: the member only the data-source-carrying methods name is not
    /// recorded, and the member of the one marked test is.
    /// </summary>
    /// <param name="framework">The framework version whose data-source attributes are used.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    [Arguments(TUnitFramework)]
    [Arguments(XunitV2Framework)]
#if FRAMESHIFT_XUNIT_V3
    [Arguments(XunitV3Framework)]
#endif
    [Arguments(NUnitFramework)]
    [Arguments(MSTestFramework)]
    public async Task Manifest_MethodCarryingOnlyADataSource_DoesNotRecordItsProductionMember(string framework)
    {
        var scenario = GetScenario(framework);
        var test = CreateTest(PairProductionSource, scenario.DataSourceSource, scenario.Framework);
        var manifest = CreateManifest(test, scenario.Probe);

        _ = await Assert.That(DescribeRecordedMembers(manifest)).IsEqualTo(ExercisedMember);
    }

    /// <summary>
    /// The framework versions the fixture check runs for, xUnit.net v3 only where its assemblies exist.
    /// </summary>
    /// <returns>The framework names.</returns>
    private static List<string> GetFrameworks()
    {
        List<string> frameworks = [TUnitFramework, XunitV2Framework];

#if FRAMESHIFT_XUNIT_V3
        frameworks.Add(XunitV3Framework);
#endif

        frameworks.Add(NUnitFramework);
        frameworks.Add(MSTestFramework);

        return frameworks;
    }

    /// <summary>
    /// Selects the fixtures, the reference set, the probe and the expectation of one framework version.
    /// </summary>
    /// <param name="framework">The name of the framework version.</param>
    /// <returns>The scenario of that framework version.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="framework" /> is not a known name.</exception>
    /// <remarks>
    /// The xUnit.net v3 arm is compiled everywhere, exactly like the one of
    /// <see cref="XunitV3TestSurfaceAnalyzer" />: nothing in it needs the framework's assemblies. Only
    /// building a reference set for it does, which is why the callers reaching this arm are guarded.
    /// </remarks>
    private static MarkerScenario GetScenario(string framework) =>
        framework switch
        {
            TUnitFramework => new MarkerScenario(
                TestFramework.TUnit,
                TUnitTestFrameworkProbe.Instance,
                TUnitProductionSource,
                TUnitMarkerTestSource,
                TUnitDataSourceTestSource,
                TUnitMarkerMembers
            ),
            XunitV2Framework => new MarkerScenario(
                TestFramework.XunitV2,
                XunitV2TestFrameworkProbe.Instance,
                XunitProductionSource,
                XunitMarkerTestSource,
                XunitDataSourceTestSource,
                XunitMarkerMembers
            ),
            XunitV3Framework => new MarkerScenario(
                TestFramework.XunitV3,
                XunitV3TestFrameworkProbe.Instance,
                XunitV3ProductionSource,
                XunitV3MarkerTestSource,
                XunitDataSourceTestSource,
                XunitV3MarkerMembers
            ),
            NUnitFramework => new MarkerScenario(
                TestFramework.NUnit,
                NUnitTestFrameworkProbe.Instance,
                NUnitProductionSource,
                NUnitMarkerTestSource,
                NUnitDataSourceTestSource,
                NUnitMarkerMembers
            ),
            MSTestFramework => new MarkerScenario(
                TestFramework.MSTest,
                MSTestTestFrameworkProbe.Instance,
                MSTestProductionSource,
                MSTestMarkerTestSource,
                MSTestDataSourceTestSource,
                MSTestMarkerMembers
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(framework), framework, "Unknown framework version."),
        };

    /// <summary>
    /// Runs the complete cycle: collect the surface of the test assembly, serialise it, and analyse the
    /// production compilation with that text as its only additional file.
    /// </summary>
    /// <param name="productionSource">The source of the production assembly under analysis.</param>
    /// <param name="testSource">The source of the test assembly compiled against it.</param>
    /// <param name="framework">The framework whose assemblies the test assembly references.</param>
    /// <param name="probe">The probe supplying the recogniser of that framework.</param>
    /// <returns>The manifest that was fed in and every diagnostic the production-side analyzer reported.</returns>
    private static async Task<(string Manifest, ImmutableArray<Diagnostic> Diagnostics)> RunCycleAsync(
        string productionSource,
        string testSource,
        TestFramework framework,
        ITestFrameworkProbe probe
    )
    {
        var production = CreateProduction(productionSource);
        var manifest = CreateManifest(CreateTest(productionSource, testSource, framework), probe);

        var diagnostics = await AnalyzerRunner
            .RunAsync(
                new MutationCoverageAnalyzer(),
                production,
                additionalFiles: [new InMemoryAdditionalText(manifest)]
            )
            .ConfigureAwait(false);

        return (manifest, diagnostics);
    }

    private static CSharpCompilation CreateProduction(string productionSource) =>
        CompilationFactory.Create(
            productionSource,
            TestFramework.None,
            ProductionAssemblyName,
            filePath: ProductionPath
        );

    /// <summary>
    /// Builds the test assembly against the production assembly and the assemblies of one test framework.
    /// </summary>
    /// <param name="productionSource">The source of the production assembly it references.</param>
    /// <param name="testSource">The source of the test assembly.</param>
    /// <param name="framework">The framework whose assemblies are referenced.</param>
    /// <returns>The created compilation.</returns>
    private static CSharpCompilation CreateTest(string productionSource, string testSource, TestFramework framework) =>
        CompilationFactory.Create(
            testSource,
            framework,
            TestAssemblyName,
            [CreateProduction(productionSource).ToMetadataReference()],
            TestPath
        );

    /// <summary>
    /// Collects the surface of <paramref name="test" /> and serialises it, exactly as the checked-in manifest
    /// of a real project is written.
    /// </summary>
    /// <param name="test">The test compilation to collect.</param>
    /// <param name="probe">The probe supplying the recogniser.</param>
    /// <returns>The canonical manifest text, ending with a line feed.</returns>
    /// <exception cref="InvalidOperationException">The probe did not recognise the fixture.</exception>
    /// <remarks>
    /// A probe that does not recognise the fixture fails loudly instead of yielding an empty manifest,
    /// because an empty manifest is reported as unusable and no gap expectation below would mean anything.
    /// </remarks>
    private static string CreateManifest(Compilation test, ITestFrameworkProbe probe)
    {
        var recognizer =
            probe.TryCreateRecognizer(test)
            ?? throw new InvalidOperationException(
                $"The probe of '{probe.FrameworkName}' did not recognise the marker fixture."
            );

        return TestSurfaceManifestWriter.Write(TestSurfaceCollector.Collect(test, recognizer, CancellationToken.None));
    }

    /// <summary>
    /// Reduces the reported gaps to the distinct production methods they sit in, ordered by position, so that
    /// an expectation names members instead of line numbers and does not depend on how many operators mutate
    /// the same expression.
    /// </summary>
    /// <param name="diagnostics">The diagnostics of one cycle.</param>
    /// <returns>The names of the methods holding a gap, joined by a bar, empty when there is none.</returns>
    private static string DescribeGaps(ImmutableArray<Diagnostic> diagnostics) =>
        string.Join(
            Separator,
            AnalyzerRunner
                .OfId(diagnostics, DiagnosticIds.UnreachableMutationPoint)
                .OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start)
                .Select(diagnostic => GetEnclosingMethodName(diagnostic))
                .Distinct(StringComparer.Ordinal)
        );

    /// <summary>
    /// Names the production method a diagnostic sits in.
    /// </summary>
    /// <param name="diagnostic">The reported gap.</param>
    /// <returns>The method name, or <see cref="UnattributedGap" /> when there is no enclosing method.</returns>
    private static string GetEnclosingMethodName(Diagnostic diagnostic)
    {
        var tree = diagnostic.Location.SourceTree;

        if (tree is null)
        {
            return UnattributedGap;
        }

        var node = tree.GetRoot(CancellationToken.None).FindNode(diagnostic.Location.SourceSpan);
        var declaration = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();

        return declaration is null ? UnattributedGap : declaration.Identifier.ValueText;
    }

    /// <summary>
    /// Reads back the production methods a manifest records as referenced, in ordinal order.
    /// </summary>
    /// <param name="manifest">The serialised manifest.</param>
    /// <returns>The method names, joined by a bar, empty when none is recorded.</returns>
    /// <remarks>
    /// Only the methods of the production fixture are read. A manifest also records the declaring type
    /// itself, because naming a static method mentions it, and that entry says nothing about which behaviour
    /// a test reaches.
    /// </remarks>
    private static string DescribeRecordedMembers(string manifest) =>
        string.Join(
            Separator,
            manifest
                .Split(LineFeedCharacter)
                .Where(line => line.StartsWith(CalculatorReferencePrefix, StringComparison.Ordinal))
                .Select(line => ToMemberName(line))
                .OrderBy(name => name, StringComparer.Ordinal)
        );

    /// <summary>
    /// Cuts the method name out of a manifest reference line.
    /// </summary>
    /// <param name="line">The line, which starts with <see cref="CalculatorReferencePrefix" />.</param>
    /// <returns>The name without its parameter list.</returns>
    private static string ToMemberName(string line)
    {
        var entry = line.Substring(CalculatorReferencePrefix.Length);

        return entry.Split('(')[0];
    }

    private static string DescribeManifestProblems(ImmutableArray<Diagnostic> diagnostics) =>
        DiagnosticAssertions.Describe(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest));

    private static string Describe(Compilation compilation) =>
        DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation));

    /// <summary>
    /// The fixtures, the reference set, the probe and the expectation of one framework version.
    /// </summary>
    private sealed class MarkerScenario
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MarkerScenario" /> class.
        /// </summary>
        /// <param name="framework">The framework whose assemblies the test fixtures reference.</param>
        /// <param name="probe">The probe supplying the recogniser of that framework version.</param>
        /// <param name="productionSource">The production fixture holding one method per marker.</param>
        /// <param name="markerSource">The test fixture exercising one production method per marker.</param>
        /// <param name="dataSourceSource">The test fixture holding the data-source-only methods.</param>
        /// <param name="expectedMarkerMembers">The production members the marker fixture has to record.</param>
        public MarkerScenario(
            TestFramework framework,
            ITestFrameworkProbe probe,
            string productionSource,
            string markerSource,
            string dataSourceSource,
            string expectedMarkerMembers
        )
        {
            Framework = framework;
            Probe = probe;
            ProductionSource = productionSource;
            MarkerSource = markerSource;
            DataSourceSource = dataSourceSource;
            ExpectedMarkerMembers = expectedMarkerMembers;
        }

        /// <summary>
        /// Gets the framework whose assemblies the test fixtures reference.
        /// </summary>
        public TestFramework Framework { get; }

        /// <summary>
        /// Gets the probe supplying the recogniser of that framework version.
        /// </summary>
        public ITestFrameworkProbe Probe { get; }

        /// <summary>
        /// Gets the production fixture holding one method per marker plus the uncovered one.
        /// </summary>
        public string ProductionSource { get; }

        /// <summary>
        /// Gets the test fixture exercising one production method per marker.
        /// </summary>
        public string MarkerSource { get; }

        /// <summary>
        /// Gets the test fixture whose data-source-only methods must contribute nothing.
        /// </summary>
        public string DataSourceSource { get; }

        /// <summary>
        /// Gets the production members the marker fixture has to record, in ordinal order.
        /// </summary>
        public string ExpectedMarkerMembers { get; }
    }
}
