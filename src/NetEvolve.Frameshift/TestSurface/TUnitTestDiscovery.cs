namespace NetEvolve.Frameshift.TestSurface;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Discovers the TUnit test methods of a compilation, identified by an attribute named
/// <c>TestAttribute</c> that either is or derives from <c>TUnit.Core.TestAttribute</c>.
/// </summary>
internal static class TUnitTestDiscovery
{
    private const string TestAttributeMetadataName = "TUnit.Core.TestAttribute";
    private const string TestAttributeTypeName = "TestAttribute";
    private const string TestFrameworkAssemblyPrefix = "TUnit";

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

        return IsTestMethod(method, GetTestAttributeType(compilation));
    }

    /// <summary>
    /// Finds all TUnit test methods declared in the syntax trees of <paramref name="compilation" />,
    /// in declaration order and without duplicates.
    /// </summary>
    /// <param name="compilation">The compilation to scan, usually a test project.</param>
    /// <param name="cancellationToken">A token to observe while scanning.</param>
    /// <returns>The discovered test methods.</returns>
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

        var testAttributeType = GetTestAttributeType(compilation);
        var builder = ImmutableArray.CreateBuilder<IMethodSymbol>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var root = syntaxTree.GetRoot(cancellationToken);
            SemanticModel? semanticModel = null;

            foreach (var declaration in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (declaration.AttributeLists.Count == 0)
                {
                    continue;
                }

                semanticModel ??= compilation.GetSemanticModel(syntaxTree);

                if (semanticModel.GetDeclaredSymbol(declaration, cancellationToken) is not IMethodSymbol method)
                {
                    continue;
                }

                if (IsTestMethod(method, testAttributeType) && seen.Add(method))
                {
                    builder.Add(method);
                }
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Resolves the well-known TUnit test attribute type of <paramref name="compilation" />, which is
    /// <see langword="null" /> if the compilation does not reference TUnit.
    /// </summary>
    /// <param name="compilation">The compilation to resolve the type in.</param>
    /// <returns>The resolved attribute type, or <see langword="null" />.</returns>
    internal static INamedTypeSymbol? GetTestAttributeType(Compilation compilation) =>
        compilation.GetTypeByMetadataName(TestAttributeMetadataName);

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
        method.GetAttributes().Any(attribute => IsTestAttribute(attribute.AttributeClass, testAttributeType));

    private static bool IsTestAttribute(INamedTypeSymbol? attributeClass, INamedTypeSymbol? testAttributeType)
    {
        for (var current = attributeClass; current is not null; current = current.BaseType)
        {
            var definition = current.OriginalDefinition;

            if (testAttributeType is not null && SymbolEqualityComparer.Default.Equals(definition, testAttributeType))
            {
                return true;
            }

            if (
                string.Equals(definition.Name, TestAttributeTypeName, StringComparison.Ordinal)
                && IsTestFrameworkAssembly(definition.ContainingAssembly)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTestFrameworkAssembly(IAssemblySymbol? assembly) =>
        assembly is not null && assembly.Name.StartsWith(TestFrameworkAssemblyPrefix, StringComparison.Ordinal);
}
