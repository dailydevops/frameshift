namespace NetEvolve.Frameshift.Tests.Unit;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.Frameshift.Tests.Infrastructure;
using NetEvolve.Frameshift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Exercises the bridge between the two passes with a real two-assembly setup: a production assembly
/// that is only visible as metadata, and a test assembly compiled against it. Everything the
/// production side later relies on is asserted here, most importantly that every recorded id is a
/// documentation comment declaration id that resolves against the production compilation again.
/// </summary>
/// <remarks>
/// The fixture deliberately avoids <c>var</c>, predefined type keywords and operators inside the code
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

            private static int DoubleThroughHelper() => Production.Helpers.Double(21);

            private static Production.Calculator Down(Production.Calculator calculator) => Up(calculator.Self());

            private static Production.Calculator Up(Production.Calculator calculator) => Down(calculator.Self());

            private static void Noop()
            {
            }
        }
        """;

    [Test]
    public async Task Fixtures_BothAssemblies_CompileWithoutErrors()
    {
        var production = CreateProduction();
        var test = CreateTest(production);

        _ = await Assert
            .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(production)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        _ = await Assert
            .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(test)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    public async Task Collect_TestMethods_AreRecordedByDeclarationId()
    {
        var manifest = TestSurfaceCollector.Collect(CreateTest(CreateProduction()), CancellationToken.None);

        _ = await Assert
            .That(Join(manifest.TestMethodIds))
            .IsEqualTo(
                "M:Tests.CalculatorTests.CallsProductionMemberDirectly|"
                    + "M:Tests.CalculatorTests.CallsProductionMemberThroughHelper|"
                    + "M:Tests.CalculatorTests.OnlyTouchesLocalState|"
                    + "M:Tests.CalculatorTests.ReadsPropertyAndField|"
                    + "M:Tests.CalculatorTests.WalksMutuallyRecursiveHelpers"
            );
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
        var manifest = TestSurfaceCollector.Collect(CreateTest(CreateProduction()), CancellationToken.None);

        _ = await Assert.That(manifest.ReferencedMemberIds.Contains(expectedId)).IsTrue();
    }

    [Test]
    public async Task Collect_MemberReachedOnlyThroughATestHelper_IsRecorded()
    {
        var manifest = TestSurfaceCollector.Collect(CreateTest(CreateProduction()), CancellationToken.None);

        _ = await Assert
            .That(manifest.ReferencedMemberIds.Contains("M:Production.Helpers.Double(System.Int32)~System.Int32"))
            .IsTrue();
    }

    [Test]
    public async Task Collect_MutuallyRecursiveTestHelpers_TerminateAndAreWalked()
    {
        var manifest = TestSurfaceCollector.Collect(CreateTest(CreateProduction()), CancellationToken.None);

        _ = await Assert
            .That(manifest.ReferencedMemberIds.Contains("M:Production.Calculator.Self~Production.Calculator"))
            .IsTrue();
    }

    [Test]
    public async Task Collect_MemberNoTestTouches_IsNotRecorded()
    {
        var manifest = TestSurfaceCollector.Collect(CreateTest(CreateProduction()), CancellationToken.None);

        _ = await Assert
            .That(manifest.ReferencedMemberIds.Contains("M:Production.Calculator.Untouched(System.Int32)~System.Int32"))
            .IsFalse();
    }

    [Test]
    public async Task Collect_MembersOfTheTestAssembly_AreNotRecordedAsProduction()
    {
        var manifest = TestSurfaceCollector.Collect(CreateTest(CreateProduction()), CancellationToken.None);

        var ownMembers = manifest.ReferencedMemberIds.Where(id =>
            id.Contains("Tests.", StringComparison.Ordinal) || id.Contains("TUnit.", StringComparison.Ordinal)
        );

        _ = await Assert.That(Join(ownMembers)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Collect_ReferencedMemberIds_AreExactlyTheProductionSurface()
    {
        var manifest = TestSurfaceCollector.Collect(CreateTest(CreateProduction()), CancellationToken.None);

        _ = await Assert
            .That(Join(manifest.ReferencedMemberIds))
            .IsEqualTo(
                "F:Production.Calculator.Zero|"
                    + "M:Production.Calculator.#ctor(System.Int32)|"
                    + "M:Production.Calculator.Add(System.Int32,System.Int32)~System.Int32|"
                    + "M:Production.Calculator.Self~Production.Calculator|"
                    + "M:Production.Helpers.Double(System.Int32)~System.Int32|"
                    + "P:Production.Calculator.Factor|"
                    + "T:Production.Calculator|"
                    + "T:Production.Helpers"
            );
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
        var manifest = TestSurfaceCollector.Collect(CreateTest(production), CancellationToken.None);

        var unresolved = manifest.ReferencedMemberIds.Where(id =>
            DocumentationCommentId.GetSymbolsForDeclarationId(id, production).IsEmpty
        );

        _ = await Assert.That(manifest.ReferencedMemberIds.Count).IsEqualTo(8);
        _ = await Assert.That(Join(unresolved)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Collect_RecordedTestIds_ResolveBackToTestSymbols()
    {
        var test = CreateTest(CreateProduction());
        var manifest = TestSurfaceCollector.Collect(test, CancellationToken.None);

        var unresolved = manifest.TestMethodIds.Where(id =>
            DocumentationCommentId.GetSymbolsForDeclarationId(id, test).IsEmpty
        );

        _ = await Assert.That(Join(unresolved)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task FindTestsWithoutProductionReference_ReturnsOnlyTheTestWithoutSurface()
    {
        var test = CreateTest(CreateProduction());

        var withoutReference = TestSurfaceCollector.FindTestsWithoutProductionReference(test, CancellationToken.None);

        _ = await Assert.That(Join(withoutReference.Select(method => method.Name))).IsEqualTo("OnlyTouchesLocalState");
    }

    [Test]
    public async Task Collect_CompilationWithoutTests_ReturnsAnEmptyManifest()
    {
        var production = CreateProduction();

        var manifest = TestSurfaceCollector.Collect(production, CancellationToken.None);

        _ = await Assert.That(manifest.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Collect_CompilationIsNull_ThrowsArgumentNullException()
    {
        var threw = ThrowsArgumentNull(() => _ = TestSurfaceCollector.Collect(null!, CancellationToken.None));

        _ = await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task FindTestsWithoutProductionReference_CompilationIsNull_ThrowsArgumentNullException()
    {
        var threw = ThrowsArgumentNull(() =>
            _ = TestSurfaceCollector.FindTestsWithoutProductionReference(null!, CancellationToken.None)
        );

        _ = await Assert.That(threw).IsTrue();
    }

    /// <summary>
    /// The overloads taking a recogniser reject a <see langword="null" /> compilation as well. They do so
    /// through the test-method discovery they evaluate first, which is the reason the shared analysis
    /// carries no null check of its own.
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

    private static CSharpCompilation CreateProduction() =>
        CompilationFactory.Create(ProductionSource, ProductionAssemblyName, filePath: "Production.cs");

    private static CSharpCompilation CreateTest(Compilation production) =>
        CompilationFactory.Create(
            TestSource,
            includeTUnit: true,
            additionalReferences: [production.ToMetadataReference()],
            filePath: "CalculatorTests.cs"
        );

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
}
