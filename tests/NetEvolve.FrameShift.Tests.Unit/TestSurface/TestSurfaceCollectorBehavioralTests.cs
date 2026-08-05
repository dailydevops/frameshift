namespace NetEvolve.FrameShift.Tests.Unit.TestSurface;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Exercises the behavioral classification the collector layers on top of plain reachability: a
/// production member is only ever behaviorally referenced when a test both invokes it and calls a
/// recognised, non-trivial assertion somewhere in its reachable code.
/// </summary>
/// <remarks>
/// The fixture, like <see cref="TestSurfaceCollectorTests" />, avoids <see langword="var"/> and predefined type
/// keywords inside the walked test code, so that framework members do not pollute the ids under test.
/// </remarks>
public class TestSurfaceCollectorBehavioralTests
{
    private const string ProductionAssemblyName = "ProductionAssembly";

    private const string ProductionSource = """
        namespace Production;

        public sealed class Calculator
        {
            public int Add(int left, int right) => left + right;
        }
        """;

    private const string TestSource = """
        namespace Tests;

        using System;
        using TUnit.Core;
        using TUnit.Assertions;
        using TUnit.Assertions.Extensions;

        public class CalculatorTests
        {
            [Test]
            public void TakesDelegateReferenceAndAssertsItIsNotNull()
            {
                Func<int, int, int> reference = new Production.Calculator().Add;

                if (reference is null)
                {
                    throw new InvalidOperationException();
                }
            }

            [Test]
            public void InvokesAndAssertsOnResult()
            {
                int result = new Production.Calculator().Add(1, 2);

                Assert.That(result).IsEqualTo(3).GetAwaiter().GetResult();
            }

            [Test]
            public void InvokesWithoutAnyAssertion()
            {
                _ = new Production.Calculator().Add(1, 2);
            }
        }
        """;

    private const string AddMemberId = "M:Production.Calculator.Add(System.Int32,System.Int32)~System.Int32";

    private const string CtorMemberId = "M:Production.Calculator.#ctor";

    private const string DelegateTestId = "M:Tests.CalculatorTests.TakesDelegateReferenceAndAssertsItIsNotNull";

    private const string AssertingTestId = "M:Tests.CalculatorTests.InvokesAndAssertsOnResult";

    private const string UnassertedTestId = "M:Tests.CalculatorTests.InvokesWithoutAnyAssertion";

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

    /// <summary>
    /// This is the acceptance criterion of the behavioral classification: a test that only takes a
    /// method reference and asserts <c>IsNotNull</c> must record the referenced member as reachable, so
    /// FSH0001 still stays silent, but must never record it as behaviorally referenced. A bare delegate
    /// capture cleared 700+ real-world FSH0001 warnings in exactly this shape without asserting a single
    /// thing about the referenced method's behaviour.
    /// </summary>
    [Test]
    public async Task DelegateReferenceWithOnlyATrivialNullCheck_IsReachableButNotBehavioral()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        using (Assert.Multiple())
        {
            _ = await Assert.That(manifest.ReferencesByTest[DelegateTestId].Contains(AddMemberId)).IsTrue();
            _ = await Assert.That(manifest.BehavioralReferencesByTest[DelegateTestId].Contains(AddMemberId)).IsFalse();
        }
    }

    [Test]
    public async Task InvocationFollowedByANonTrivialAssertion_IsBehavioral()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        _ = await Assert.That(manifest.BehavioralReferencesByTest[AssertingTestId].Contains(AddMemberId)).IsTrue();
    }

    [Test]
    public async Task InvocationWithoutAnyAssertion_IsReachableButNotBehavioral()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        using (Assert.Multiple())
        {
            _ = await Assert.That(manifest.ReferencesByTest[UnassertedTestId].Contains(AddMemberId)).IsTrue();
            _ = await Assert
                .That(manifest.BehavioralReferencesByTest[UnassertedTestId].Contains(AddMemberId))
                .IsFalse();
        }
    }

    /// <summary>
    /// The constructor call inside the assertion-bearing test is itself an invocation, and the test does
    /// call a recognised assertion, so the constructor is behavioral too - the assertion does not have to
    /// sit textually next to the specific reference it covers.
    /// </summary>
    [Test]
    public async Task ConstructorInvokedInAnAssertingTest_IsBehavioral()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        _ = await Assert.That(manifest.BehavioralReferencesByTest[AssertingTestId].Contains(CtorMemberId)).IsTrue();
    }

    /// <summary>
    /// The constructor is invoked by the delegate-reference test as well (to obtain the instance the
    /// method group is taken from), but that test only ever calls the trivial null check, so the
    /// constructor is not behavioral there either.
    /// </summary>
    [Test]
    public async Task ConstructorInvokedInTheDelegateOnlyTest_IsNotBehavioral()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        _ = await Assert.That(manifest.BehavioralReferencesByTest[DelegateTestId].Contains(CtorMemberId)).IsFalse();
    }

    [Test]
    public async Task BehavioralReferencedMemberIds_IsASubsetOfReferencedMemberIds()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        var notReferenced = manifest.BehavioralReferencedMemberIds.Where(id =>
            !manifest.ReferencedMemberIds.Contains(id)
        );

        _ = await Assert.That(Join(notReferenced)).IsEqualTo(string.Empty);
    }

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
            additionalReferences:
            [
                production.ToMetadataReference(),
                MetadataReference.CreateFromFile(typeof(TUnit.Assertions.Assert).Assembly.Location),
            ],
            filePath: "CalculatorTests.cs"
        );

    private static string Join(IEnumerable<string> values) =>
        string.Join("|", values.OrderBy(value => value, StringComparer.Ordinal));
}
