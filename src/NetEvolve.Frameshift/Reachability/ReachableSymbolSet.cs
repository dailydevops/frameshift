namespace NetEvolve.Frameshift.Reachability;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

/// <summary>
/// An immutable set of the production members that are reachable from the recorded test surface,
/// used by the production side analyzer to decide whether a mutation point is covered by at least
/// one test.
/// </summary>
/// <remarks>
/// <para>
/// Every symbol is stored in its normalized form, see <see cref="NormalizeDefinition(ISymbol)" />, so
/// that constructed generics, substituted members of generic types and reduced extension method
/// invocations all answer the same question as their declaration.
/// </para>
/// <para>
/// Instances are immutable and therefore safe to share between concurrent analyzer callbacks.
/// </para>
/// </remarks>
internal sealed class ReachableSymbolSet
{
    private static readonly ReachableSymbolSet _empty = new ReachableSymbolSet([]);

    private readonly ImmutableHashSet<ISymbol> _symbols;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReachableSymbolSet" /> class.
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

        var builder = ImmutableHashSet.CreateBuilder<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var symbol in symbols.Where(symbol => symbol is not null))
        {
            _ = builder.Add(NormalizeDefinition(symbol));
        }

        _symbols = builder.ToImmutable();
    }

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
    public bool ContainsEnclosing(ISymbol? symbol)
    {
        if (symbol is null)
        {
            return false;
        }

        if (ContainsCore(symbol))
        {
            return true;
        }

        var current = symbol.ContainingSymbol;

        while (current is not null and not ITypeSymbol and not INamespaceSymbol)
        {
            if (ContainsCore(current))
            {
                return true;
            }

            current = current.ContainingSymbol;
        }

        return false;
    }

    private bool ContainsCore(ISymbol symbol)
    {
        var definition = NormalizeDefinition(symbol);

        if (_symbols.Contains(definition))
        {
            return true;
        }

        return definition is IMethodSymbol { AssociatedSymbol: not null } accessor
            && _symbols.Contains(NormalizeDefinition(accessor.AssociatedSymbol));
    }
}
