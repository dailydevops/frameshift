namespace NetEvolve.FrameShift.Tests.Unit.TestSurface;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Exercises the bridge between the two passes with a real two-assembly setup: a production assembly
/// that is only visible as metadata, and a test assembly compiled against it. Everything the
/// production side later relies on is asserted here, most importantly that every recorded id is a
/// documentation comment declaration id that resolves against the production compilation again, and
/// that every recorded member is attributed to every test that reaches it.
/// </summary>
/// <remarks>
/// The fixture deliberately avoids <see langword="var"/>, predefined type keywords and operators inside the code
/// that is walked. Those all bind to members of the framework assemblies, which are outside the test
/// assembly and would therefore be recorded as production references, drowning the interesting ids in
/// noise.
/// </remarks>
public class TestSurfaceCollectorTests
{
    private const string ProductionAssemblyName = "ProductionAssembly";

    private const string ProductionSource = """
        namespace Production;

        public sealed class Calculator
        {
            public static readonly int Zero = 0;

            private readonly int _offset;

            public Calculator(int offset) => _offset = offset;

            public int Factor { get; set; }

            public int Add(int left, int right) => left + right + _offset;

            public Calculator Self() => this;

            public int Untouched(int value) => value * Factor;
        }

        public static class Helpers
        {
            public static int Double(int value) => value + value;

            public static int Triple(int value) => value + value + value;
        }

        public static class Shared
        {
            public static int Touched() => 1;

            public static int FromDualUseHelper() => 2;

            public static int OnlyFromNonTest() => 3;
        }
        """;

    private const string TestSource = """
        namespace Tests;

        using TUnit.Core;

        public class CalculatorTests
        {
            [Test]
            public void CallsProductionMemberDirectly()
            {
                Production.Calculator calculator = new Production.Calculator(1);
                _ = calculator.Add(1, 2);
            }

            [Test]
            public void CallsProductionMemberThroughHelper() => _ = DoubleThroughHelper();

            [Test]
            public void ReadsPropertyAndField()
            {
                Production.Calculator calculator = new Production.Calculator(2);
                calculator.Factor = Production.Calculator.Zero;
                _ = calculator.Factor;
            }

            [Test]
            public void WalksMutuallyRecursiveHelpers() => _ = Down(new Production.Calculator(3));

            [Test]
            public void OnlyTouchesLocalState() => Noop();

            [Test]
            public void SharesHelperFirst() => _ = TouchShared();

            [Test]
            public void SharesHelperSecond() => _ = TouchShared();

            [Test]
            public void CallsDualUseHelper() => _ = DualUseHelper();

            [Test]
            [Arguments(1)]
            [Arguments(2)]
            [Arguments(3)]
            public void RunsThreeCases(int value) => _ = Production.Helpers.Triple(value);

            private static int DoubleThroughHelper() => Production.Helpers.Double(21);

            private static int TouchShared() => Production.Shared.Touched();

            private static int DualUseHelper() => Production.Shared.FromDualUseHelper();

            private static int NotATest()
            {
                _ = Production.Shared.OnlyFromNonTest();

                return DualUseHelper();
            }

            private static Production.Calculator Down(Production.Calculator calculator) => Up(calculator.Self());

            private static Production.Calculator Up(Production.Calculator calculator) => Down(calculator.Self());

            private static void Noop()
            {
            }
        }
        """;

    private const string CallsDualUseHelperId = "M:Tests.CalculatorTests.CallsDualUseHelper";

    private const string CallsProductionMemberDirectlyId = "M:Tests.CalculatorTests.CallsProductionMemberDirectly";

    private const string OnlyTouchesLocalStateId = "M:Tests.CalculatorTests.OnlyTouchesLocalState";

    private const string RunsThreeCasesId = "M:Tests.CalculatorTests.RunsThreeCases(System.Int32)";

    private const string SharesHelperFirstId = "M:Tests.CalculatorTests.SharesHelperFirst";

    private const string SharesHelperSecondId = "M:Tests.CalculatorTests.SharesHelperSecond";

    private const string WalksMutuallyRecursiveHelpersId = "M:Tests.CalculatorTests.WalksMutuallyRecursiveHelpers";

    private const string TouchedId = "M:Production.Shared.Touched~System.Int32";

    private const string FromDualUseHelperId = "M:Production.Shared.FromDualUseHelper~System.Int32";

    private const string SharedHelperSurface = TouchedId + "|T:Production.Shared";

    private const string DualUseHelperSurface = FromDualUseHelperId + "|T:Production.Shared";

    private const string OnlyFromNonTestId = "M:Production.Shared.OnlyFromNonTest~System.Int32";

    private const string AllTestMethodIds =
        CallsDualUseHelperId
        + "|"
        + CallsProductionMemberDirectlyId
        + "|M:Tests.CalculatorTests.CallsProductionMemberThroughHelper|"
        + OnlyTouchesLocalStateId
        + "|M:Tests.CalculatorTests.ReadsPropertyAndField|"
        + RunsThreeCasesId
        + "|"
        + SharesHelperFirstId
        + "|"
        + SharesHelperSecondId
        + "|"
        + WalksMutuallyRecursiveHelpersId;

    [Test]
    public async Task Fixtures_BothAssemblies_CompileWithoutErrors()
    {
        var production = CreateProduction();
        var test = CreateTest(production);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(production)))
                .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
            _ = await Assert
                .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(test)))
                .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    [Test]
    public async Task Collect_TestMethods_AreRecordedByDeclarationId()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        _ = await Assert.That(Join(manifest.TestMethodIds)).IsEqualTo(AllTestMethodIds);
    }

    [Test]
    [Arguments("M:Production.Calculator.Add(System.Int32,System.Int32)~System.Int32")]
    [Arguments("M:Production.Calculator.#ctor(System.Int32)")]
    [Arguments("P:Production.Calculator.Factor")]
    [Arguments("F:Production.Calculator.Zero")]
    [Arguments("T:Production.Calculator")]
    [Arguments("T:Production.Helpers")]
    public async Task Collect_MemberTouchedByATest_IsRecorded(string expectedId)
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        _ = await Assert.That(manifest.ReferencedMemberIds.Contains(expectedId)).IsTrue();
    }

    [Test]
    public async Task Collect_MemberReachedOnlyThroughATestHelper_IsRecorded()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        _ = await Assert
            .That(manifest.ReferencedMemberIds.Contains("M:Production.Helpers.Double(System.Int32)~System.Int32"))
            .IsTrue();
    }

    [Test]
    public async Task Collect_MutuallyRecursiveTestHelpers_TerminateAndAreWalked()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        _ = await Assert
            .That(manifest.ReferencedMemberIds.Contains("M:Production.Calculator.Self~Production.Calculator"))
            .IsTrue();
    }

    [Test]
    public async Task Collect_MemberNoTestTouches_IsNotRecorded()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        _ = await Assert
            .That(manifest.ReferencedMemberIds.Contains("M:Production.Calculator.Untouched(System.Int32)~System.Int32"))
            .IsFalse();
    }

    [Test]
    public async Task Collect_MembersOfTheTestAssembly_AreNotRecordedAsProduction()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        var ownMembers = manifest.ReferencedMemberIds.Where(id =>
            id.Contains("Tests.", StringComparison.Ordinal) || id.Contains("TUnit.", StringComparison.Ordinal)
        );

        _ = await Assert.That(Join(ownMembers)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Collect_ReferencedMemberIds_AreExactlyTheProductionSurface()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        _ = await Assert
            .That(Join(manifest.ReferencedMemberIds))
            .IsEqualTo(
                "F:Production.Calculator.Zero|"
                    + "M:Production.Calculator.#ctor(System.Int32)|"
                    + "M:Production.Calculator.Add(System.Int32,System.Int32)~System.Int32|"
                    + "M:Production.Calculator.Self~Production.Calculator|"
                    + "M:Production.Helpers.Double(System.Int32)~System.Int32|"
                    + "M:Production.Helpers.Triple(System.Int32)~System.Int32|"
                    + "M:Production.Shared.FromDualUseHelper~System.Int32|"
                    + "M:Production.Shared.Touched~System.Int32|"
                    + "P:Production.Calculator.Factor|"
                    + "T:Production.Calculator|"
                    + "T:Production.Helpers|"
                    + "T:Production.Shared"
            );
    }

    /// <summary>
    /// The union of the per-test attributions is what the flat set of referenced members has to be: it is
    /// derived from the very same data, so a member attributed to nobody could never appear in it.
    /// </summary>
    [Test]
    public async Task Collect_ReferencedMemberIds_AreTheUnionOfThePerTestAttributions()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        var union = manifest
            .ReferencesByTest.Values.SelectMany(references => references)
            .Distinct(StringComparer.Ordinal);

        _ = await Assert.That(Join(union)).IsEqualTo(Join(manifest.ReferencedMemberIds));
    }

    /// <summary>
    /// Every discovered test contributes an attribution entry and a test-case count, including the test
    /// that reaches no production member at all. The keys of both maps are therefore exactly the set of
    /// test methods.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Collect_EveryDiscoveredTest_HasAnEntryInBothMaps(bool references)
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        var keys = references ? manifest.ReferencesByTest.Keys : manifest.TestCaseCounts.Keys;

        _ = await Assert.That(Join(keys)).IsEqualTo(AllTestMethodIds);
    }

    [Test]
    public async Task Collect_TestCallingProductionDirectly_IsAttributedExactlyItsOwnSurface()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        _ = await Assert
            .That(ReferencesOf(manifest, CallsProductionMemberDirectlyId))
            .IsEqualTo(
                "M:Production.Calculator.#ctor(System.Int32)|"
                    + "M:Production.Calculator.Add(System.Int32,System.Int32)~System.Int32|"
                    + "T:Production.Calculator"
            );
    }

    /// <summary>
    /// The case a naive attribution gets wrong: two tests call the same helper of the test assembly, and
    /// the production member behind it belongs to both of them, not to whichever test was walked first.
    /// Attributing it to one test only would understate the number of input combinations the member is
    /// exercised with and produce a single-test-case finding that is plainly false.
    /// </summary>
    [Test]
    [Arguments(SharesHelperFirstId)]
    [Arguments(SharesHelperSecondId)]
    public async Task Collect_HelperSharedByTwoTests_IsAttributedToBothOfThem(string testMethodId)
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        _ = await Assert.That(ReferencesOf(manifest, testMethodId)).IsEqualTo(SharedHelperSurface);
    }

    /// <summary>
    /// A helper that a test and a non-test method both call is attributed to the test, while the member
    /// only the non-test method reaches stays out of the surface entirely: the traversal starts at test
    /// methods, never at arbitrary methods of the test assembly.
    /// </summary>
    [Test]
    public async Task Collect_HelperCalledByATestAndByANonTestMethod_IsAttributedToTheTestOnly()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        using (Assert.Multiple())
        {
            _ = await Assert.That(ReferencesOf(manifest, CallsDualUseHelperId)).IsEqualTo(DualUseHelperSurface);
            _ = await Assert.That(manifest.ReferencedMemberIds.Contains(OnlyFromNonTestId)).IsFalse();
        }
    }

    [Test]
    public async Task Collect_MutuallyRecursiveHelpers_AreAttributedToTheEnteringTest()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        _ = await Assert
            .That(ReferencesOf(manifest, WalksMutuallyRecursiveHelpersId))
            .IsEqualTo(
                "M:Production.Calculator.#ctor(System.Int32)|"
                    + "M:Production.Calculator.Self~Production.Calculator|"
                    + "T:Production.Calculator"
            );
    }

    [Test]
    public async Task Collect_TestWithoutProductionReference_IsAttributedAnEmptySet()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        _ = await Assert.That(ReferencesOf(manifest, OnlyTouchesLocalStateId)).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// A member another test reaches must not leak into an unrelated attribution, because the whole point
    /// of the map is to tell the tests apart.
    /// </summary>
    [Test]
    public async Task Collect_MemberOfAnotherTest_IsNotAttributed()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(manifest.ReferencesByTest[SharesHelperFirstId].Contains(FromDualUseHelperId))
                .IsFalse();
            _ = await Assert.That(manifest.ReferencesByTest[CallsDualUseHelperId].Contains(TouchedId)).IsFalse();
        }
    }

    /// <summary>
    /// A parameterless test is one case, and three inline data attributes are three: the collector reports
    /// what the recogniser answers for the method instead of deriving a count of its own.
    /// </summary>
    [Test]
    [Arguments(OnlyTouchesLocalStateId, "1")]
    [Arguments(RunsThreeCasesId, "3")]
    public async Task Collect_TestCaseCounts_AreTakenFromTheRecognizer(string testMethodId, string expected)
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        _ = await Assert.That(manifest.TestCaseCounts[testMethodId].ToString()).IsEqualTo(expected);
    }

    /// <summary>
    /// The collector passes a lower bound through unchanged as well. A recogniser that answers
    /// <c>2+</c> must reach the manifest as <c>2+</c>, because collapsing it to an exact count would let
    /// the single-test-case heuristic fire on a number that is only a floor.
    /// </summary>
    [Test]
    [Arguments(SharesHelperFirstId, "1")]
    [Arguments(CallsDualUseHelperId, "2+")]
    public async Task Collect_RecognizerAnswersALowerBound_IsReportedAsALowerBound(string testMethodId, string expected)
    {
        var test = CreateTest(CreateProduction());
        var recognizer = new StubCountingRecognizer(CreateRecognizer(test));

        var manifest = TestSurfaceCollector.Collect(test, recognizer, CancellationToken.None);

        _ = await Assert.That(manifest.TestCaseCounts[testMethodId].ToString()).IsEqualTo(expected);
    }

    /// <summary>
    /// The production pass feeds every recorded id back through
    /// <see cref="DocumentationCommentId.GetSymbolsForDeclarationId(string, Compilation)" />. An id that
    /// does not resolve silently drops a member out of the reachable set, so the round trip is asserted
    /// for every single id instead of a sample.
    /// </summary>
    [Test]
    public async Task Collect_RecordedIds_ResolveBackToProductionSymbols()
    {
        var production = CreateProduction();
        var manifest = CollectSurface(CreateTest(production));

        var unresolved = manifest.ReferencedMemberIds.Where(id =>
            DocumentationCommentId.GetSymbolsForDeclarationId(id, production).IsEmpty
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(manifest.ReferencedMemberIds.Count).IsEqualTo(12);
            _ = await Assert.That(Join(unresolved)).IsEqualTo(string.Empty);
        }
    }

    [Test]
    public async Task Collect_RecordedTestIds_ResolveBackToTestSymbols()
    {
        var test = CreateTest(CreateProduction());
        var manifest = CollectSurface(test);

        var unresolved = manifest.TestMethodIds.Where(id =>
            DocumentationCommentId.GetSymbolsForDeclarationId(id, test).IsEmpty
        );

        _ = await Assert.That(Join(unresolved)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task FindTestsWithoutProductionReference_ReturnsOnlyTheTestWithoutSurface()
    {
        var test = CreateTest(CreateProduction());

        var withoutReference = TestSurfaceCollector.FindTestsWithoutProductionReference(
            test,
            CreateRecognizer(test),
            CancellationToken.None
        );

        _ = await Assert.That(Join(withoutReference.Select(method => method.Name))).IsEqualTo("OnlyTouchesLocalState");
    }

    /// <summary>
    /// A compilation the recogniser accepts nothing in produces an empty manifest instead of an error:
    /// absence of tests is a normal outcome, and every caller treats it as a reason to stay silent.
    /// </summary>
    [Test]
    public async Task Collect_CompilationWithoutTests_ReturnsAnEmptyManifest()
    {
        var production = CreateProduction();

        var manifest = CollectSurface(production);

        using (Assert.Multiple())
        {
            _ = await Assert.That(manifest.IsEmpty).IsTrue();
            _ = await Assert.That(manifest.ReferencesByTest.Count).IsEqualTo(0);
            _ = await Assert.That(manifest.TestCaseCounts.Count).IsEqualTo(0);
        }
    }

    /// <summary>
    /// A <see langword="null" /> compilation is rejected through the test-method discovery the collector
    /// evaluates first, which is the reason the shared analysis carries no null check of its own.
    /// </summary>
    [Test]
    public async Task Collect_CompilationIsNullWithARecognizer_ThrowsArgumentNullException()
    {
        var recognizer = new TUnitTestMethodRecognizer(testAttributeType: null);

        var threw = ThrowsArgumentNull(() =>
            _ = TestSurfaceCollector.Collect(null!, recognizer, CancellationToken.None)
        );

        _ = await Assert.That(threw).IsTrue();
    }

    /// <inheritdoc cref="Collect_CompilationIsNullWithARecognizer_ThrowsArgumentNullException" />
    [Test]
    public async Task FindTestsWithoutProductionReference_CompilationIsNullWithARecognizer_ThrowsArgumentNullException()
    {
        var recognizer = new TUnitTestMethodRecognizer(testAttributeType: null);

        var threw = ThrowsArgumentNull(() =>
            _ = TestSurfaceCollector.FindTestsWithoutProductionReference(null!, recognizer, CancellationToken.None)
        );

        _ = await Assert.That(threw).IsTrue();
    }

    /// <summary>
    /// Collects the test surface of <paramref name="test" /> with a TUnit recogniser, which is how every
    /// production caller reaches the collector.
    /// </summary>
    /// <param name="test">The compilation to inspect.</param>
    /// <returns>The collected manifest.</returns>
    private static TestSurfaceManifest CollectSurface(Compilation test) =>
        TestSurfaceCollector.Collect(test, CreateRecognizer(test), CancellationToken.None);

    private static TUnitTestMethodRecognizer CreateRecognizer(Compilation compilation) =>
        new TUnitTestMethodRecognizer(TUnitTestFrameworkProbe.GetTestAttributeType(compilation));

    private static CSharpCompilation CreateProduction() =>
        CompilationFactory.Create(ProductionSource, ProductionAssemblyName, filePath: "Production.cs");

    private static CSharpCompilation CreateTest(Compilation production) =>
        CompilationFactory.Create(
            TestSource,
            includeTUnit: true,
            additionalReferences: [production.ToMetadataReference()],
            filePath: "CalculatorTests.cs"
        );

    private static string ReferencesOf(TestSurfaceManifest manifest, string testMethodId) =>
        Join(manifest.ReferencesByTest[testMethodId]);

    private static string Join(IEnumerable<string> values) =>
        string.Join("|", values.OrderBy(value => value, StringComparer.Ordinal));

    private static bool ThrowsArgumentNull(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentNullException)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// A recogniser that recognises exactly the tests of the fixture, but answers a case count of its own:
    /// an exact <c>1</c> for the two tests sharing a helper and a lower bound of <c>2</c> for everything
    /// else. It isolates the collector from the counting rules of any concrete framework.
    /// </summary>
    private sealed class StubCountingRecognizer : ITestMethodRecognizer
    {
        private const string ExactPrefix = "Shares";

        private readonly ITestMethodRecognizer _inner;

        /// <summary>
        /// Initializes a new instance of the <see cref="StubCountingRecognizer" /> class.
        /// </summary>
        /// <param name="inner">The recogniser deciding which methods are tests.</param>
        public StubCountingRecognizer(ITestMethodRecognizer inner) => _inner = inner;

        /// <inheritdoc />
        public string FrameworkName => _inner.FrameworkName;

        /// <inheritdoc />
        public bool IsTestMethod(IMethodSymbol method) => _inner.IsTestMethod(method);

        /// <inheritdoc />
        public TestCaseCount GetTestCaseCount(IMethodSymbol method) =>
            method.Name.StartsWith(ExactPrefix, StringComparison.Ordinal)
                ? TestCaseCount.Exact(1)
                : TestCaseCount.AtLeast(2);
    }
}
