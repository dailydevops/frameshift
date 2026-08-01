namespace NetEvolve.FrameShift.Mutations;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

/// <summary>
/// Caches the well-known type symbols every mutation operator resolves through
/// <see cref="Compilation.GetTypeByMetadataName(string)" />, so that a given metadata name is looked up
/// at most once per <see cref="Compilation" />, no matter how many candidate syntax nodes an operator
/// examines.
/// </summary>
/// <remarks>
/// <para>
/// A single node kind - a member access, an invocation, an object creation - can occur thousands of
/// times in a compilation unit, and every occurrence used to repeat the very same metadata table lookup
/// for the very same well-known type. <see cref="Compilation" /> is immutable and the lookup is
/// deterministic for it, so the result never goes stale; caching it once and reusing it for every
/// subsequent candidate node removes work that scaled with the size of the analysed source without
/// changing a single answer.
/// </para>
/// <para>
/// The cache is keyed first by <see cref="Compilation" /> reference identity, through a
/// <see cref="ConditionalWeakTable{TKey, TValue}" /> so an entry never outlives the compilation it was
/// resolved from, and second by the metadata name itself, through a <see cref="ConcurrentDictionary{TKey,TValue}" />
/// so concurrent analyzer callbacks can read and populate it safely. Both a hit and a miss - a metadata
/// name that resolves to <see langword="null" /> because the referenced assembly is absent - are cached,
/// so an operator running against a compilation that does not reference a given type never repeats the
/// failed lookup either.
/// </para>
/// </remarks>
internal static class WellKnownTypeCache
{
    private static readonly ConditionalWeakTable<Compilation, ConcurrentDictionary<string, INamedTypeSymbol?>> _cache =
        new();

    /// <summary>
    /// Resolves, or returns the already resolved, well-known type symbol for <paramref name="metadataName" />
    /// in <paramref name="compilation" />.
    /// </summary>
    /// <param name="compilation">The compilation the symbol is resolved in.</param>
    /// <param name="metadataName">The fully qualified metadata name of the type.</param>
    /// <returns>
    /// The resolved type symbol, or <see langword="null" /> when <paramref name="compilation" /> does not
    /// reference a type of that name.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="compilation" /> or <paramref name="metadataName" /> is <see langword="null" />.
    /// </exception>
    public static INamedTypeSymbol? GetType(Compilation compilation, string metadataName)
    {
        if (compilation is null)
        {
            throw new ArgumentNullException(nameof(compilation));
        }

        if (metadataName is null)
        {
            throw new ArgumentNullException(nameof(metadataName));
        }

        var perCompilation = _cache.GetValue(
            compilation,
            static _ => new ConcurrentDictionary<string, INamedTypeSymbol?>(StringComparer.Ordinal)
        );

        return perCompilation.GetOrAdd(metadataName, name => compilation.GetTypeByMetadataName(name));
    }
}
