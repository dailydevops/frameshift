namespace NetEvolve.FrameShift.Tests.Unit.Equivalence;

using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Equivalence;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Tests <see cref="UnreachableCodeDiagnosticsCache" />: every call for the same syntax tree and span
/// has to share one underlying immutable diagnostics array instead of recomputing it, and two different
/// trees or spans must never be conflated into the same cache entry.
/// </summary>
public class UnreachableCodeDiagnosticsCacheTests
{
    // Each member contains a statement after an unconditional return, so the compiler reports a real
    // CS0162 "unreachable code" diagnostic for it. The fixture deliberately avoids an empty diagnostics
    // array: an empty ImmutableArray<Diagnostic> is the very same static instance every time it is
    // produced, which would make two independently computed empty results compare equal by reference
    // for a reason that has nothing to do with the cache.
    private const string Source = """
        public class Sample
        {
            public int Compute(int value)
            {
                return value;
                var unused = value + 1;
            }

            public int ComputeOther(int value)
            {
                return value;
                var unused = value - 1;
            }
        }
        """;

    [Test]
    public async Task GetDiagnostics_CalledTwiceForTheSameTreeAndSpan_ReturnsTheSameCachedArray()
    {
        var (_, model, _) = CompilationFactory.CreateWithModel(Source);
        var cache = new UnreachableCodeDiagnosticsCache();
        var root = await model.SyntaxTree.GetRootAsync().ConfigureAwait(false);
        var span = root.Span;

        var first = cache.GetDiagnostics(model, span, CancellationToken.None);
        var second = cache.GetDiagnostics(model, span, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(first.IsEmpty).IsFalse();

            // ImmutableArray<T>.Equals compares the underlying array reference, so equality here proves
            // the second call was answered from the cache instead of asking the semantic model again.
            _ = await Assert.That(first.Equals(second)).IsTrue();
        }
    }

    [Test]
    public async Task GetDiagnostics_CalledForDifferentSpansOfTheSameTree_AreCachedIndependently()
    {
        var (_, model, tree) = CompilationFactory.CreateWithModel(Source);
        var cache = new UnreachableCodeDiagnosticsCache();
        var root = await tree.GetRootAsync().ConfigureAwait(false);
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        var firstMember = methods.First(method =>
            string.Equals(method.Identifier.Text, "Compute", StringComparison.Ordinal)
        );
        var secondMember = methods.First(method =>
            string.Equals(method.Identifier.Text, "ComputeOther", StringComparison.Ordinal)
        );

        var first = cache.GetDiagnostics(model, firstMember.Span, CancellationToken.None);
        var second = cache.GetDiagnostics(model, secondMember.Span, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(first.IsEmpty).IsFalse();
            _ = await Assert.That(second.IsEmpty).IsFalse();

            // Different spans must not collide on the same cache entry, even though both live in the
            // same syntax tree.
            _ = await Assert.That(first.Equals(second)).IsFalse();
        }
    }

    [Test]
    public async Task GetDiagnostics_CalledForDifferentTreesWithTheSameSpan_AreCachedIndependently()
    {
        var (_, firstModel, firstTree) = CompilationFactory.CreateWithModel(Source, filePath: "First.cs");
        var (_, secondModel, secondTree) = CompilationFactory.CreateWithModel(Source, filePath: "Second.cs");
        var cache = new UnreachableCodeDiagnosticsCache();

        // Both fixtures use the very same source text, so the two trees report matching spans; only the
        // tree identity tells the two lookups apart.
        var firstRoot = await firstTree.GetRootAsync().ConfigureAwait(false);
        var secondRoot = await secondTree.GetRootAsync().ConfigureAwait(false);

        var first = cache.GetDiagnostics(firstModel, firstRoot.Span, CancellationToken.None);
        var second = cache.GetDiagnostics(secondModel, secondRoot.Span, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(first.IsEmpty).IsFalse();
            _ = await Assert.That(second.IsEmpty).IsFalse();
            _ = await Assert.That(first.Equals(second)).IsFalse();
        }
    }
}
