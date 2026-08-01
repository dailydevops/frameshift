namespace NetEvolve.FrameShift.Tests.Unit.Mutations.RegularExpressions;

using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Mutations.RegularExpressions;
using NetEvolve.FrameShift.Tests.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers <see cref="RegexPatternCache" />, the memoization that lets every operator of the regular
/// expression pattern family ask about the very same string literal without locating, validating and
/// tokenizing its pattern more than once.
/// </summary>
/// <remarks>
/// The cache holds no counter of its own, so a test cannot observe how many times
/// <see cref="RegexPatternResolution.TryResolve" /> ran underneath it directly. What it can observe -
/// and what memoization actually promises a caller - is that two lookups of the same node hand back the
/// very same <see cref="RegexPatternResolution" /> instance, while two different nodes never share one.
/// Reference equality is exactly the signal that the second lookup did not recompute anything.
/// </remarks>
public class RegexPatternCacheTests
{
    private const string TwoLiteralsSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex First() => new Regex("^a$");

            internal static Regex Second() => new Regex("b+");
        }
        """;

    private const string OneViableOneNotSource = """
        namespace Fixtures;

        using System;
        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Viable() => new Regex("^a$");

            internal static void NotAPattern() => Console.WriteLine(/*!*/"^a$");
        }
        """;

    [Test]
    public async Task GetOrResolve_SameNodeAskedTwice_ReturnsTheSameCachedInstance()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(TwoLiteralsSource);
        var node = SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(tree);
        var cache = new RegexPatternCache();

        var first = cache.GetOrResolve(node, semanticModel, CancellationToken.None);
        var second = cache.GetOrResolve(node, semanticModel, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(first).IsNotNull();
            _ = await Assert.That(ReferenceEquals(first, second)).IsTrue();
        }
    }

    /// <summary>
    /// Two different literals of the same tree never collapse into one cache entry: each keeps its own
    /// pattern, and asking about one never answers for the other.
    /// </summary>
    [Test]
    public async Task GetOrResolve_DifferentNodes_ReturnIndependentResolutions()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(TwoLiteralsSource);
        var literals = SyntaxNodeLocator.FindAll<LiteralExpressionSyntax>(tree);
        var first = literals.First(literal => string.Equals(literal.Token.ValueText, "^a$", StringComparison.Ordinal));
        var second = literals.First(literal => string.Equals(literal.Token.ValueText, "b+", StringComparison.Ordinal));
        var cache = new RegexPatternCache();

        var firstResolution = cache.GetOrResolve(first, semanticModel, CancellationToken.None);
        var secondResolution = cache.GetOrResolve(second, semanticModel, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(firstResolution!.Site.Pattern).IsEqualTo("^a$");
            _ = await Assert.That(secondResolution!.Site.Pattern).IsEqualTo("b+");
            _ = await Assert.That(ReferenceEquals(firstResolution, secondResolution)).IsFalse();
        }
    }

    /// <summary>
    /// A literal that is no viable pattern site resolves to <see langword="null" />, and that answer is
    /// cached exactly like a viable one: asking twice never re-runs the locator for a literal already known
    /// to be no site.
    /// </summary>
    [Test]
    public async Task GetOrResolve_NodeThatIsNoPatternSite_CachesNullConsistently()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(OneViableOneNotSource);
        var notASite = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        var cache = new RegexPatternCache();

        var first = cache.GetOrResolve(notASite, semanticModel, CancellationToken.None);
        var second = cache.GetOrResolve(notASite, semanticModel, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(first).IsNull();
            _ = await Assert.That(second).IsNull();
        }
    }

    [Test]
    public async Task GetOrResolve_NodeIsNull_ThrowsArgumentNullException()
    {
        var (_, semanticModel, _) = CompilationFactory.CreateWithModel(TwoLiteralsSource);
        var cache = new RegexPatternCache();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            cache.GetOrResolve(null!, semanticModel, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("node");
    }

    [Test]
    public async Task GetOrResolve_SemanticModelIsNull_ThrowsArgumentNullException()
    {
        var (_, _, tree) = CompilationFactory.CreateWithModel(TwoLiteralsSource);
        var node = SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(tree);
        var cache = new RegexPatternCache();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            cache.GetOrResolve(node, null!, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("semanticModel");
    }

    [Test]
    public async Task GetOrResolve_CancellationRequestedOnFirstLookup_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(TwoLiteralsSource);
        var node = SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(tree);
        var cache = new RegexPatternCache();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            cache.GetOrResolve(node, semanticModel, cancellation.Token)
        );

        _ = await Assert.That(exception).IsNotNull();
    }
}
