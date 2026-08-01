namespace NetEvolve.FrameShift.Tests.Unit;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Tests.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Pins down the behaviour <c>TestSurfaceAnalysis.CollectReportsOfPrecedingFrameworks</c> exploits without
/// needing to share anything: every call to
/// <see cref="TestSurfaceCollector.FindTestsWithoutProductionReference" /> walks the compilation again and
/// hands back a freshly built array, even when an earlier call already walked the very same
/// (compilation, framework) pair.
/// </summary>
/// <remarks>
/// <see cref="ImmutableArray{T}" /> equality is reference equality of the backing array, never a
/// structural comparison of its elements. Two calls that describe the very same test methods therefore
/// still compare unequal here, because each one built its own array from scratch — that repetition, once
/// this analysis coordinates several framework analyzers on one compilation, is exactly the O(F^2) work
/// the issue is about. A caching fix is expected to make an equivalent pair of calls compare equal
/// instead, because the second one would then be handed the first one's array rather than recomputing it.
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
    public async Task FindTestsWithoutProductionReference_CalledTwiceForTheSameCompilationAndFramework_RecomputesInsteadOfSharing()
    {
        var compilation = CreateTest();
        var recognizerOne = CreateRecognizer(compilation);
        var recognizerTwo = CreateRecognizer(compilation);

        var first = TestSurfaceCollector.FindTestsWithoutProductionReference(
            compilation,
            recognizerOne,
            CancellationToken.None
        );
        var second = TestSurfaceCollector.FindTestsWithoutProductionReference(
            compilation,
            recognizerTwo,
            CancellationToken.None
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(first.Length).IsEqualTo(second.Length);
            _ = await Assert.That(first == second).IsFalse();
        }
    }

    private static CSharpCompilation CreateTest() => CompilationFactory.Create(TestSource, TestFramework.TUnit);

    private static TUnitTestMethodRecognizer CreateRecognizer(Compilation compilation) =>
        new TUnitTestMethodRecognizer(TUnitTestFrameworkProbe.GetTestAttributeType(compilation));
}
