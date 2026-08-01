namespace NetEvolve.FrameShift.Equivalence;

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

/// <summary>
/// Memoises the compiler diagnostics <see cref="EquivalenceClassifier" /> asks for when it looks for
/// unreachable code, so that every mutation candidate belonging to the same member shares one
/// <see cref="SemanticModel.GetDiagnostics(TextSpan?, CancellationToken)" /> call instead of paying for
/// it again.
/// </summary>
/// <remarks>
/// Modeled after <see cref="NetEvolve.FrameShift.Mutations.MutantCompiler" />'s viability cache: the
/// only mutable state is a <see cref="ConcurrentDictionary{TKey, TValue}" />, so instances are safe to
/// share across the concurrently executing callbacks of an analyzer. An instance must not outlive the
/// <see cref="Compilation" /> its semantic models were created from, because two unrelated
/// compilations can hand back the very same <see cref="SyntaxTree" /> instance for an unchanged file
/// and would otherwise share a stale result across them; every instance is therefore scoped to a
/// single compilation, exactly the way <see cref="NetEvolve.FrameShift.Mutations.MutantCompiler" /> is.
/// </remarks>
internal sealed class UnreachableCodeDiagnosticsCache
{
    private readonly ConcurrentDictionary<CacheKey, ImmutableArray<Diagnostic>> _cache = new();

    /// <summary>
    /// Returns the diagnostics of <paramref name="span" /> in <paramref name="semanticModel" />,
    /// computing them once per syntax tree and span and reusing the result for every later caller.
    /// </summary>
    /// <param name="semanticModel">The semantic model to query.</param>
    /// <param name="span">The span to filter the diagnostics to, or <see langword="null" /> for the whole tree.</param>
    /// <param name="cancellationToken">A token to observe while computing the diagnostics.</param>
    /// <returns>The diagnostics of <paramref name="span" />.</returns>
    public ImmutableArray<Diagnostic> GetDiagnostics(
        SemanticModel semanticModel,
        TextSpan? span,
        CancellationToken cancellationToken
    )
    {
        var key = new CacheKey(semanticModel.SyntaxTree, span);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // Deliberately not GetOrAdd: the factory delegate would capture state on every call and a
        // cancellation happening inside it would be observed as a cached result by later callers.
        var diagnostics = semanticModel.GetDiagnostics(span, cancellationToken);
        _ = _cache.TryAdd(key, diagnostics);

        return diagnostics;
    }

    /// <summary>
    /// The identity of a cached lookup, made up of the syntax tree and the span the diagnostics were
    /// filtered to.
    /// </summary>
    private readonly struct CacheKey : IEquatable<CacheKey>
    {
        private readonly SyntaxTree _tree;
        private readonly TextSpan? _span;

        /// <summary>
        /// Initializes a new instance of the <see cref="CacheKey" /> struct.
        /// </summary>
        /// <param name="tree">The syntax tree the diagnostics belong to.</param>
        /// <param name="span">The span the diagnostics were filtered to, or <see langword="null" />.</param>
        public CacheKey(SyntaxTree tree, TextSpan? span)
        {
            _tree = tree;
            _span = span;
        }

        /// <summary>
        /// Compares two keys for equality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true" /> if both keys are equal; otherwise <see langword="false" />.</returns>
        public static bool operator ==(CacheKey left, CacheKey right) => left.Equals(right);

        /// <summary>
        /// Compares two keys for inequality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true" /> if both keys differ; otherwise <see langword="false" />.</returns>
        public static bool operator !=(CacheKey left, CacheKey right) => !left.Equals(right);

        /// <inheritdoc />
        public bool Equals(CacheKey other) => ReferenceEquals(_tree, other._tree) && _span == other._span;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is CacheKey other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = (hash * 31) + RuntimeHelpers.GetHashCode(_tree);
                hash = (hash * 31) + (_span?.GetHashCode() ?? 0);

                return hash;
            }
        }
    }
}
