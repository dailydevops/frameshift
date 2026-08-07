namespace NetEvolve.FrameShift.Tests.Unit.Analyzers;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Analyzers;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Proves that <see cref="TestSurfaceAnalysis.GetTestsWithoutProductionReference" /> shares its result
/// across calls for the same compilation and framework instead of walking
/// <see cref="TestSurfaceCollector.FindTestsWithoutProductionReference" /> again for every caller — the
/// O(F^2) walk <c>CollectReportsOfPrecedingFrameworks</c> used to repeat once per later analyzer, on top of
/// each framework's own walk of itself.
/// </summary>
/// <remarks>
/// <see cref="ImmutableArray{T}" /> equality is reference equality of the backing array, never a
/// structural comparison of its elements. Two independently computed results would therefore never
/// compare equal even when they describe the very same test methods, which is exactly what makes it a
/// reliable witness here: a second call that returns an <c>==</c> result to the first one did not run the
/// walk again, it was handed the cached array.
/// </remarks>
public class TestSurfaceAnalysisCacheTests
{
    private const string TestSource = """
        namespace Tests;

        using TUnit.Core;

        public class CalculatorTests
        {
            [Test]
            public void TouchesNoProduction()
            {
            }
        }
        """;

    [Test]
    public async Task GetTestsWithoutProductionReference_CalledTwiceForSameCompilationAndFramework_ReturnsTheCachedResult()
    {
        var compilation = CreateTest();
        var recognizerOne = CreateRecognizer(compilation);
        var recognizerTwo = CreateRecognizer(compilation);

        var first = TestSurfaceAnalysis.GetTestsWithoutProductionReference(
            compilation,
            TUnitTestFrameworkProbe.Instance,
            recognizerOne,
            CancellationToken.None
        );

        // Passing a fresh recognizer the second time is deliberate: if the cache re-ran the walk it would
        // do so through this very recognizer, and the test would still pass by accident. Only a cache hit
        // never touches it at all.
        var second = TestSurfaceAnalysis.GetTestsWithoutProductionReference(
            compilation,
            TUnitTestFrameworkProbe.Instance,
            recognizerTwo,
            CancellationToken.None
        );

        _ = await Assert.That(first == second).IsTrue();
    }

    [Test]
    public async Task GetTestsWithoutProductionReference_CalledForDifferentFrameworks_ComputesEachOnItsOwn()
    {
        var compilation = CreateTest();
        var tunitRecognizer = CreateRecognizer(compilation);
        var nunitRecognizer = new NUnitTestMethodRecognizer(ImmutableArray<INamedTypeSymbol>.Empty);

        var tunitResult = TestSurfaceAnalysis.GetTestsWithoutProductionReference(
            compilation,
            TUnitTestFrameworkProbe.Instance,
            tunitRecognizer,
            CancellationToken.None
        );
        var nunitResult = TestSurfaceAnalysis.GetTestsWithoutProductionReference(
            compilation,
            NUnitTestFrameworkProbe.Instance,
            nunitRecognizer,
            CancellationToken.None
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(tunitResult.Length).IsEqualTo(1);
            _ = await Assert.That(nunitResult.Length).IsEqualTo(0);
        }
    }

    [Test]
    public async Task GetTestsWithoutProductionReference_CalledForDifferentCompilations_DoesNotShareTheOtherCompilationsResult()
    {
        var compilationOne = CreateTest();
        var compilationTwo = CreateTest();

        var first = TestSurfaceAnalysis.GetTestsWithoutProductionReference(
            compilationOne,
            TUnitTestFrameworkProbe.Instance,
            CreateRecognizer(compilationOne),
            CancellationToken.None
        );
        var second = TestSurfaceAnalysis.GetTestsWithoutProductionReference(
            compilationTwo,
            TUnitTestFrameworkProbe.Instance,
            CreateRecognizer(compilationTwo),
            CancellationToken.None
        );

        _ = await Assert.That(first == second).IsFalse();
    }

    private static CSharpCompilation CreateTest() => CompilationFactory.Create(TestSource, TestFramework.TUnit);

    private static TUnitTestMethodRecognizer CreateRecognizer(Compilation compilation) =>
        new TUnitTestMethodRecognizer(TUnitTestFrameworkProbe.GetTestAttributeType(compilation));
}
