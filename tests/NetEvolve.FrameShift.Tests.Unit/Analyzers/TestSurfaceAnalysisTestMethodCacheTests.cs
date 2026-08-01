namespace NetEvolve.FrameShift.Tests.Unit;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Analyzers;
using NetEvolve.FrameShift.Tests.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Proves that <see cref="TestSurfaceAnalysis.GetTestMethods" /> shares its discovery result across calls
/// for the same compilation and framework instead of walking
/// <see cref="TestMethodDiscovery.FindTestMethods" /> again for every caller — the walk
/// <c>FindAwakeFrameworks</c> used to repeat, once per registered framework, for every analyzer instance
/// awake on the same compilation.
/// </summary>
/// <remarks>
/// <see cref="ImmutableArray{T}" /> equality is reference equality of the backing array, never a structural
/// comparison of its elements. Two independently computed results would therefore never compare equal even
/// when they describe the very same test methods, which is exactly what makes it a reliable witness here: a
/// second call that returns an <c>==</c> result to the first one did not run the walk again, it was handed
/// the cached array.
/// </remarks>
public class TestSurfaceAnalysisTestMethodCacheTests
{
    private const string TestSource = """
        namespace Tests;

        using TUnit.Core;

        public class CalculatorTests
        {
            [Test]
            public void Adds()
            {
            }
        }
        """;

    [Test]
    public async Task GetTestMethods_CalledTwiceForSameCompilationAndFramework_ReturnsTheCachedResult()
    {
        var compilation = CreateTest();
        var recognizerOne = CreateRecognizer(compilation);
        var recognizerTwo = CreateRecognizer(compilation);

        var first = TestSurfaceAnalysis.GetTestMethods(
            compilation,
            TUnitTestFrameworkProbe.Instance,
            recognizerOne,
            CancellationToken.None
        );

        // Passing a fresh recognizer the second time is deliberate: if the cache re-ran the walk it would
        // do so through this very recognizer, and the test would still pass by accident. Only a cache hit
        // never touches it at all.
        var second = TestSurfaceAnalysis.GetTestMethods(
            compilation,
            TUnitTestFrameworkProbe.Instance,
            recognizerTwo,
            CancellationToken.None
        );

        _ = await Assert.That(first == second).IsTrue();
    }

    [Test]
    public async Task GetTestMethods_CalledForDifferentFrameworks_ComputesEachOnItsOwn()
    {
        var compilation = CreateTest();
        var tunitRecognizer = CreateRecognizer(compilation);
        var nunitRecognizer = new NUnitTestMethodRecognizer(ImmutableArray<INamedTypeSymbol>.Empty);

        var tunitResult = TestSurfaceAnalysis.GetTestMethods(
            compilation,
            TUnitTestFrameworkProbe.Instance,
            tunitRecognizer,
            CancellationToken.None
        );
        var nunitResult = TestSurfaceAnalysis.GetTestMethods(
            compilation,
            NUnitTestFrameworkProbe.Instance,
            nunitRecognizer,
            CancellationToken.None
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(tunitResult.Length).IsEqualTo(1);
            _ = await Assert.That(nunitResult.Length).IsEqualTo(0);
            _ = await Assert.That(tunitResult == nunitResult).IsFalse();
        }
    }

    [Test]
    public async Task GetTestMethods_CalledForDifferentCompilations_DoesNotShareTheOtherCompilationsResult()
    {
        var compilationOne = CreateTest();
        var compilationTwo = CreateTest();

        var first = TestSurfaceAnalysis.GetTestMethods(
            compilationOne,
            TUnitTestFrameworkProbe.Instance,
            CreateRecognizer(compilationOne),
            CancellationToken.None
        );
        var second = TestSurfaceAnalysis.GetTestMethods(
            compilationTwo,
            TUnitTestFrameworkProbe.Instance,
            CreateRecognizer(compilationTwo),
            CancellationToken.None
        );

        _ = await Assert.That(first == second).IsFalse();
    }

    /// <summary>
    /// Two probes recognising the same framework version-by-version, as xUnit's two probes do, must still
    /// only pay for one discovery walk each: the second one asking about a framework already resolved by
    /// the first gets the cached result, never a fresh walk.
    /// </summary>
    [Test]
    public async Task GetTestMethods_CalledThroughExecuteForTwoAwakeFrameworks_TheLaterAnalyzerReusesTheEarlierWalk()
    {
        const string dualFrameworkSource = """
            namespace Tests;

            using TUnit.Core;

            public class CalculatorTests
            {
                [Test]
                public void Adds()
                {
                }
            }

            public class OtherTests
            {
                [NUnit.Framework.Test]
                public void Multiplies()
                {
                }
            }
            """;

        var compilation = CompilationFactory.Create(dualFrameworkSource, TestFramework.All);

        var tunitRecognizer = TUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation)!;
        var nunitRecognizer = NUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation)!;

        var tunitFirst = TestSurfaceAnalysis.GetTestMethods(
            compilation,
            TUnitTestFrameworkProbe.Instance,
            tunitRecognizer,
            CancellationToken.None
        );

        // Simulates the NUnit analyzer's own Execute call asking about NUnit's own framework directly...
        var nunitFirst = TestSurfaceAnalysis.GetTestMethods(
            compilation,
            NUnitTestFrameworkProbe.Instance,
            nunitRecognizer,
            CancellationToken.None
        );

        // ...and then simulates the TUnit analyzer's FindAwakeFrameworks asking about NUnit's registry
        // entry again with a freshly created recogniser, to decide whether NUnit is awake too.
        var nunitSecond = TestSurfaceAnalysis.GetTestMethods(
            compilation,
            NUnitTestFrameworkProbe.Instance,
            NUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation)!,
            CancellationToken.None
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(tunitFirst.Length).IsEqualTo(1);
            _ = await Assert.That(nunitFirst.Length).IsEqualTo(1);
            _ = await Assert.That(nunitFirst == nunitSecond).IsTrue();
        }
    }

    private static CSharpCompilation CreateTest() => CompilationFactory.Create(TestSource, TestFramework.TUnit);

    private static TUnitTestMethodRecognizer CreateRecognizer(Compilation compilation) =>
        new TUnitTestMethodRecognizer(TUnitTestFrameworkProbe.GetTestAttributeType(compilation));
}
