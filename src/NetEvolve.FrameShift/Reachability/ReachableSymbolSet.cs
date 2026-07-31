namespace NetEvolve.FrameShift.Reachability;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

/// <summary>
/// An immutable set of the production members that are reachable from the recorded test surface,
/// used by the production side analyzer to decide whether a mutation point is covered by at least
/// one test, and by which tests.
/// </summary>
/// <remarks>
/// <para>
/// Every symbol is stored in its normalized form, see <see cref="NormalizeDefinition(ISymbol)" />, so
/// that constructed generics, substituted members of generic types and reduced extension method
/// invocations all answer the same question as their declaration.
/// </para>
/// <para>
/// Every symbol also carries the documentation comment ids of the test methods that reach it, unioned
/// over all paths the closure followed. The attribution may be empty, which says "reachable, but the
/// manifest recorded no test for it" and never "reachable by no test": membership and attribution are
/// two separate questions, and a caller that sums test case counts has to treat an empty attribution
/// as unknown instead of as zero.
/// </para>
/// <para>
/// Instances are immutable and therefore safe to share between concurrent analyzer callbacks.
/// </para>
/// </remarks>
internal sealed class ReachableSymbolSet
{
    private static readonly ImmutableHashSet<string> _noTests = ImmutableHashSet<string>.Empty.WithComparer(
        StringComparer.Ordinal
    );

    private static readonly ReachableSymbolSet _empty = new ReachableSymbolSet(
        ImmutableDictionary.Create<ISymbol, ImmutableHashSet<string>>(SymbolEqualityComparer.Default)
    );

    private readonly ImmutableDictionary<ISymbol, ImmutableHashSet<string>> _symbols;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReachableSymbolSet" /> class without any attribution.
    /// </summary>
    /// <param name="symbols">
    /// The reachable symbols. They are normalized and de-duplicated with
    /// <see cref="SymbolEqualityComparer.Default" />.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="symbols" /> is <see langword="null" />.</exception>
    public ReachableSymbolSet(IEnumerable<ISymbol> symbols)
    {
        if (symbols is null)
        {
            throw new ArgumentNullException(nameof(symbols));
        }

        var builder = CreateBuilder();

        foreach (var symbol in symbols.Where(symbol => symbol is not null))
        {
            Merge(builder, symbol, _noTests);
        }

        _symbols = builder.ToImmutable();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReachableSymbolSet" /> class from an already
    /// normalized attribution.
    /// </summary>
    /// <param name="symbols">The test method ids per reachable symbol.</param>
    /// <remarks>
    /// Private on purpose. A second public constructor would make <c>new ReachableSymbolSet(null!)</c>
    /// ambiguous, and the attribution needs normalizing anyway, which is what
    /// <see cref="FromAttribution(IReadOnlyDictionary{ISymbol, ImmutableHashSet{string}})" /> does.
    /// </remarks>
    private ReachableSymbolSet(ImmutableDictionary<ISymbol, ImmutableHashSet<string>> symbols) => _symbols = symbols;

    /// <summary>
    /// Gets the set without any reachable symbol, describing a production compilation that no test
    /// touches at all.
    /// </summary>
    public static ReachableSymbolSet Empty => _empty;

    /// <summary>
    /// Gets the number of distinct reachable symbols.
    /// </summary>
    public int Count => _symbols.Count;

    /// <summary>
    /// Gets a value indicating whether the set does not contain a single reachable symbol.
    /// </summary>
    public bool IsEmpty => _symbols.IsEmpty;

    /// <summary>
    /// Creates a set from the attribution the reachability closure computed.
    /// </summary>
    /// <param name="attribution">
    /// The documentation comment ids of the test methods reaching a symbol, keyed by that symbol. Keys
    /// are normalized and de-duplicated with <see cref="SymbolEqualityComparer.Default" />; two keys
    /// that normalize to the same definition contribute the union of their test ids.
    /// </param>
    /// <returns>The attributed set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="attribution" /> is <see langword="null" />.</exception>
    public static ReachableSymbolSet FromAttribution(IReadOnlyDictionary<ISymbol, ImmutableHashSet<string>> attribution)
    {
        if (attribution is null)
        {
            throw new ArgumentNullException(nameof(attribution));
        }

        var builder = CreateBuilder();

        foreach (var entry in attribution)
        {
            Merge(builder, entry.Key, entry.Value);
        }

        return new ReachableSymbolSet(builder.ToImmutable());
    }

    /// <summary>
    /// Normalizes <paramref name="symbol" /> to the form the set stores.
    /// </summary>
    /// <param name="symbol">The symbol to normalize.</param>
    /// <returns>
    /// The original definition of <paramref name="symbol" />, or the original definition of the
    /// unreduced method for an extension method that was invoked in reduced form.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="symbol" /> is <see langword="null" />.</exception>
    public static ISymbol NormalizeDefinition(ISymbol symbol)
    {
        if (symbol is null)
        {
            throw new ArgumentNullException(nameof(symbol));
        }

        return symbol switch
        {
            IMethodSymbol { ReducedFrom: not null } method => method.ReducedFrom.OriginalDefinition,
            _ => symbol.OriginalDefinition,
        };
    }

    /// <summary>
    /// Determines whether <paramref name="symbol" /> itself is reachable.
    /// </summary>
    /// <param name="symbol">The symbol to look up.</param>
    /// <returns><see langword="true" /> if the symbol is reachable; otherwise <see langword="false" />.</returns>
    /// <remarks>
    /// A property or event accessor also counts as reachable when the property or event it belongs to
    /// is reachable, because a reference to <c>Value</c> and a reference to <c>get_Value</c> describe
    /// the same test surface.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="symbol" /> is <see langword="null" />.</exception>
    public bool Contains(ISymbol symbol)
    {
        if (symbol is null)
        {
            throw new ArgumentNullException(nameof(symbol));
        }

        return ContainsCore(symbol);
    }

    /// <summary>
    /// Determines whether <paramref name="symbol" /> or one of its enclosing members is reachable, so
    /// that a mutation inside a lambda, a local function or a nested local function is attributed to
    /// the member that contains it.
    /// </summary>
    /// <param name="symbol">The symbol enclosing a mutation point, may be <see langword="null" />.</param>
    /// <returns>
    /// <see langword="true" /> if the symbol or any enclosing member up to, but excluding, the
    /// containing type is reachable; otherwise <see langword="false" />.
    /// </returns>
    /// <remarks>
    /// The walk deliberately stops before the containing type. A reachable type therefore never makes
    /// all of its members reachable, which would hide exactly the gaps this analysis is looking for.
    /// </remarks>
    public bool ContainsEnclosing(ISymbol? symbol) => symbol is not null && EnclosingChain(symbol).Any(ContainsCore);

    /// <summary>
    /// Gets the documentation comment ids of the test methods that reach <paramref name="symbol" />.
    /// </summary>
    /// <param name="symbol">The symbol to look up.</param>
    /// <returns>
    /// The attributed test method ids, empty if the symbol is unreachable or if it is reachable without
    /// any recorded test.
    /// </returns>
    /// <remarks>
    /// The attribution of a property or event accessor includes the attribution of the property or event
    /// it belongs to, for the same reason <see cref="Contains(ISymbol)" /> accepts it as reachable.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="symbol" /> is <see langword="null" />.</exception>
    public ImmutableHashSet<string> GetTestIds(ISymbol symbol)
    {
        if (symbol is null)
        {
            throw new ArgumentNullException(nameof(symbol));
        }

        return GetTestIdsCore(symbol);
    }

    /// <summary>
    /// Gets the documentation comment ids of the test methods that reach <paramref name="symbol" /> or
    /// any of its enclosing members, so that a mutation inside a lambda or a local function is
    /// attributed to the tests reaching the member that contains it.
    /// </summary>
    /// <param name="symbol">The symbol enclosing a mutation point, may be <see langword="null" />.</param>
    /// <returns>
    /// The union of the attributed test method ids along the chain up to, but excluding, the containing
    /// type; empty if nothing along that chain carries an attribution.
    /// </returns>
    /// <remarks>
    /// The whole chain contributes instead of only its first attributed link. A local function that a
    /// test reaches directly and an enclosing member that another test reaches are two ways into the
    /// same code, and dropping either would understate the number of input combinations the code is
    /// exercised with, which is the one error direction this attribution exists to avoid.
    /// </remarks>
    public ImmutableHashSet<string> GetEnclosingTestIds(ISymbol? symbol)
    {
        if (symbol is null)
        {
            return _noTests;
        }

        var testIds = _noTests;

        foreach (var candidate in EnclosingChain(symbol))
        {
            testIds = Union(testIds, GetTestIdsCore(candidate));
        }

        return testIds;
    }

    private static ImmutableDictionary<ISymbol, ImmutableHashSet<string>>.Builder CreateBuilder() =>
        ImmutableDictionary.CreateBuilder<ISymbol, ImmutableHashSet<string>>(SymbolEqualityComparer.Default);

    private static void Merge(
        ImmutableDictionary<ISymbol, ImmutableHashSet<string>>.Builder builder,
        ISymbol symbol,
        ImmutableHashSet<string> testIds
    )
    {
        var definition = NormalizeDefinition(symbol);
        var normalized = testIds.WithComparer(StringComparer.Ordinal);

        builder[definition] = builder.TryGetValue(definition, out var known) ? Union(known, normalized) : normalized;
    }

    private static ImmutableHashSet<string> Union(ImmutableHashSet<string> left, ImmutableHashSet<string> right)
    {
        if (right.IsEmpty)
        {
            return left;
        }

        return left.IsEmpty ? right : left.Union(right);
    }

    /// <summary>
    /// Yields <paramref name="symbol" /> and every member enclosing it, stopping before the containing
    /// type.
    /// </summary>
    /// <param name="symbol">The symbol to start at.</param>
    /// <returns>The symbol itself followed by its enclosing members, outwards.</returns>
    private static IEnumerable<ISymbol> EnclosingChain(ISymbol symbol)
    {
        yield return symbol;

        var current = symbol.ContainingSymbol;

        while (current is not null and not ITypeSymbol and not INamespaceSymbol)
        {
            yield return current;

            current = current.ContainingSymbol;
        }
    }

    private bool ContainsCore(ISymbol symbol)
    {
        var definition = NormalizeDefinition(symbol);

        if (_symbols.ContainsKey(definition))
        {
            return true;
        }

        return definition is IMethodSymbol { AssociatedSymbol: not null } accessor
            && _symbols.ContainsKey(NormalizeDefinition(accessor.AssociatedSymbol));
    }

    private ImmutableHashSet<string> GetTestIdsCore(ISymbol symbol)
    {
        var definition = NormalizeDefinition(symbol);
        var testIds = _symbols.TryGetValue(definition, out var direct) ? direct : _noTests;

        if (
            definition is IMethodSymbol { AssociatedSymbol: not null } accessor
            && _symbols.TryGetValue(NormalizeDefinition(accessor.AssociatedSymbol), out var associated)
        )
        {
            testIds = Union(testIds, associated);
        }

        return testIds;
    }
}
