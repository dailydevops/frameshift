namespace NetEvolve.FrameShift.TestSurface;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

/// <summary>
/// Convenience entry point for discovering the TUnit test methods of a compilation. It combines
/// <see cref="TUnitTestFrameworkProbe" /> with <see cref="TestMethodDiscovery" />, so that callers that
/// only care about TUnit do not have to handle the probe themselves.
/// </summary>
/// <remarks>
/// A compilation that does not use TUnit yields no test methods instead of an error: absence of the
/// framework is a normal outcome, and every caller treats "no tests recognised" as a reason to stay
/// silent.
/// </remarks>
internal static class TUnitTestDiscovery
{
    /// <summary>
    /// Determines whether <paramref name="method" /> is a TUnit test method.
    /// </summary>
    /// <param name="method">The method to inspect.</param>
    /// <param name="compilation">The compilation <paramref name="method" /> belongs to.</param>
    /// <returns>
    /// <see langword="true" /> if the method carries a test attribute; otherwise
    /// <see langword="false" />.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="method" /> or <paramref name="compilation" /> is <see langword="null" />.
    /// </exception>
    public static bool IsTestMethod(IMethodSymbol method, Compilation compilation)
    {
        if (method is null)
        {
            throw new ArgumentNullException(nameof(method));
        }

        if (compilation is null)
        {
            throw new ArgumentNullException(nameof(compilation));
        }

        return TUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation)?.IsTestMethod(method) == true;
    }

    /// <summary>
    /// Finds all TUnit test methods declared in the syntax trees of <paramref name="compilation" />,
    /// in declaration order and without duplicates.
    /// </summary>
    /// <param name="compilation">The compilation to scan, usually a test project.</param>
    /// <param name="cancellationToken">A token to observe while scanning.</param>
    /// <returns>
    /// The discovered test methods, or an empty array if the compilation does not use TUnit.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="compilation" /> is <see langword="null" />.</exception>
    public static ImmutableArray<IMethodSymbol> FindTestMethods(
        Compilation compilation,
        CancellationToken cancellationToken
    )
    {
        if (compilation is null)
        {
            throw new ArgumentNullException(nameof(compilation));
        }

        var recognizer = TUnitTestFrameworkProbe.Instance.TryCreateRecognizer(compilation);

        return recognizer is null
            ? []
            : TestMethodDiscovery.FindTestMethods(compilation, recognizer, cancellationToken);
    }

    /// <summary>
    /// Resolves the well-known TUnit test attribute type of <paramref name="compilation" />, which is
    /// <see langword="null" /> if the compilation does not reference TUnit.
    /// </summary>
    /// <param name="compilation">The compilation to resolve the type in.</param>
    /// <returns>The resolved attribute type, or <see langword="null" />.</returns>
    internal static INamedTypeSymbol? GetTestAttributeType(Compilation compilation) =>
        TUnitTestFrameworkProbe.GetTestAttributeType(compilation);

    /// <summary>
    /// Determines whether <paramref name="method" /> carries a test attribute, using a test attribute
    /// type that was resolved once for the whole compilation.
    /// </summary>
    /// <param name="method">The method to inspect.</param>
    /// <param name="testAttributeType">
    /// The resolved <c>TUnit.Core.TestAttribute</c> type, or <see langword="null" /> if it could not
    /// be resolved.
    /// </param>
    /// <returns>
    /// <see langword="true" /> if the method carries a test attribute; otherwise
    /// <see langword="false" />.
    /// </returns>
    internal static bool IsTestMethod(IMethodSymbol method, INamedTypeSymbol? testAttributeType) =>
        new TUnitTestMethodRecognizer(testAttributeType).IsTestMethod(method);
}
