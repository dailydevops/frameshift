namespace NetEvolve.FrameShift.Mutations;

using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

/// <summary>
/// Verifies that a candidate <see cref="Mutation" /> still yields a compilable program, by rewriting
/// the affected syntax tree and re-binding only that tree against the original compilation.
/// </summary>
/// <remarks>
/// <para>
/// Cost trade-off: re-binding a syntax tree is by far the most expensive step of the whole analyzer.
/// Two decisions keep it affordable. First, only the mutated tree is asked for diagnostics, never the
/// whole compilation. <see cref="Compilation.GetDiagnostics(CancellationToken)" /> would bind every
/// tree of the project for every single mutant, which turns a linear cost into a quadratic one; the
/// price is that a mutation invalidating code in a <em>different</em> file, for example by changing a
/// constant another file depends on, is accepted as viable. That direction of error is harmless,
/// because a false <see cref="MutantViability.Viable" /> only adds a mutation point, it never
/// silences a real one.
/// </para>
/// <para>
/// Second, results are memoised per mutation identity, so the same mutation point reached from
/// several analysis callbacks is bound once. The cache is the only mutable state of this type and is a
/// <see cref="ConcurrentDictionary{TKey, TValue}" />, therefore instances are safe to share across the
/// concurrently executing callbacks of an analyzer. Cancellation is never cached and never swallowed.
/// </para>
/// </remarks>
internal sealed class MutantCompiler
{
    private readonly Compilation _compilation;
    private readonly ConcurrentDictionary<MutantKey, MutantViability> _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="MutantCompiler" /> class for
    /// <paramref name="compilation" />.
    /// </summary>
    /// <param name="compilation">The unmutated compilation every mutant is verified against.</param>
    /// <exception cref="ArgumentNullException"><paramref name="compilation" /> is <see langword="null" />.</exception>
    public MutantCompiler(Compilation compilation)
    {
        _compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        _cache = new ConcurrentDictionary<MutantKey, MutantViability>();
    }

    /// <summary>
    /// Determines whether <paramref name="mutation" /> produces a compilable program when applied to
    /// <paramref name="originalTree" />.
    /// </summary>
    /// <param name="mutation">The candidate mutation to verify.</param>
    /// <param name="originalTree">
    /// The unmutated syntax tree containing <see cref="Mutation.Original" />. It must be one of the
    /// trees of the compilation this instance was created for, otherwise the mutated tree cannot be
    /// swapped in and the mutant is reported as not compiling.
    /// </param>
    /// <param name="cancellationToken">A token to observe while binding the mutated tree.</param>
    /// <returns>
    /// <see cref="MutantViability.Viable" /> if the mutated tree binds without errors; otherwise
    /// <see cref="MutantViability.DoesNotCompile" />.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="mutation" /> or <paramref name="originalTree" /> is <see langword="null" />.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    public MutantViability Verify(Mutation mutation, SyntaxTree originalTree, CancellationToken cancellationToken)
    {
        if (mutation is null)
        {
            throw new ArgumentNullException(nameof(mutation));
        }

        if (originalTree is null)
        {
            throw new ArgumentNullException(nameof(originalTree));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var key = new MutantKey(mutation.OperatorId, originalTree.FilePath, mutation.Location.SourceSpan);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // Deliberately not GetOrAdd: the factory delegate would capture state on every call and a
        // cancellation happening inside it would be observed as a cached result by later callers.
        var viability = VerifyCore(mutation, originalTree, cancellationToken);
        _ = _cache.TryAdd(key, viability);

        return viability;
    }

    /// <summary>
    /// Applies the mutation, swaps the tree into the compilation and looks for errors on the mutated
    /// tree only.
    /// </summary>
    /// <param name="mutation">The candidate mutation to verify.</param>
    /// <param name="originalTree">The unmutated syntax tree containing <see cref="Mutation.Original" />.</param>
    /// <param name="cancellationToken">A token to observe while binding the mutated tree.</param>
    /// <returns>The viability of the mutant.</returns>
    private MutantViability VerifyCore(Mutation mutation, SyntaxTree originalTree, CancellationToken cancellationToken)
    {
        // A replacement that does not parse can never produce a mutant that compiles. It has to be
        // inspected before it is grafted into the tree, because attaching the trivia of the original
        // node drops the syntax diagnostics the parser attached to it.
        if (HasError(mutation.Replacement.GetDiagnostics()))
        {
            return MutantViability.DoesNotCompile;
        }

        SyntaxTree mutatedTree;
        Compilation mutatedCompilation;
        try
        {
            mutatedTree = mutation.ApplyTo(originalTree);
            mutatedCompilation = _compilation.ReplaceSyntaxTree(originalTree, mutatedTree);
        }
        catch (ArgumentException)
        {
            // The mutation does not belong to this tree, or the tree does not belong to this
            // compilation. Either way the mutant cannot be built.
            return MutantViability.DoesNotCompile;
        }

        if (HasError(mutatedTree.GetDiagnostics(cancellationToken)))
        {
            return MutantViability.DoesNotCompile;
        }

        var semanticModel = mutatedCompilation.GetSemanticModel(mutatedTree);
        if (HasError(semanticModel.GetDiagnostics(cancellationToken: cancellationToken)))
        {
            return MutantViability.DoesNotCompile;
        }

        return MutantViability.Viable;
    }

    /// <summary>
    /// Determines whether <paramref name="diagnostics" /> contains at least one error.
    /// </summary>
    /// <param name="diagnostics">The diagnostics of the mutated tree.</param>
    /// <returns><see langword="true" /> if an error is present; otherwise <see langword="false" />.</returns>
    private static bool HasError(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The identity of a mutant, made up of the operator that created it and the exact source
    /// location it rewrites.
    /// </summary>
    private readonly struct MutantKey : IEquatable<MutantKey>
    {
        private readonly string _operatorId;
        private readonly string _filePath;
        private readonly TextSpan _span;

        /// <summary>
        /// Initializes a new instance of the <see cref="MutantKey" /> struct.
        /// </summary>
        /// <param name="operatorId">The operator id of the mutation.</param>
        /// <param name="filePath">The file path of the mutated tree, possibly empty.</param>
        /// <param name="span">The source span the mutation rewrites.</param>
        public MutantKey(string operatorId, string? filePath, TextSpan span)
        {
            _operatorId = operatorId;
            _filePath = filePath ?? string.Empty;
            _span = span;
        }

        /// <summary>
        /// Compares two keys for equality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true" /> if both keys are equal; otherwise <see langword="false" />.</returns>
        public static bool operator ==(MutantKey left, MutantKey right) => left.Equals(right);

        /// <summary>
        /// Compares two keys for inequality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true" /> if both keys differ; otherwise <see langword="false" />.</returns>
        public static bool operator !=(MutantKey left, MutantKey right) => !left.Equals(right);

        /// <inheritdoc />
        public bool Equals(MutantKey other) =>
            _span == other._span
            && string.Equals(_operatorId, other._operatorId, StringComparison.Ordinal)
            && string.Equals(_filePath, other._filePath, StringComparison.Ordinal);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is MutantKey other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(_operatorId);
                hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(_filePath);
                hash = (hash * 31) + _span.GetHashCode();

                return hash;
            }
        }
    }
}
