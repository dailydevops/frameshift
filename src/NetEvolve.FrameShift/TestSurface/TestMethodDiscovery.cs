namespace NetEvolve.FrameShift.TestSurface;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Finds the test methods declared in a compilation, framework-neutrally: which methods count is
/// decided entirely by the supplied <see cref="ITestMethodRecognizer" />.
/// </summary>
internal static class TestMethodDiscovery
{
    /// <summary>
    /// Finds all test methods declared in the syntax trees of <paramref name="compilation" />, in
    /// declaration order and without duplicates.
    /// </summary>
    /// <param name="compilation">The compilation to scan, usually a test project.</param>
    /// <param name="recognizer">The recogniser deciding which methods are test methods.</param>
    /// <param name="cancellationToken">A token to observe while scanning.</param>
    /// <returns>The discovered test methods.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="compilation" /> or <paramref name="recognizer" /> is <see langword="null" />.
    /// </exception>
    public static ImmutableArray<IMethodSymbol> FindTestMethods(
        Compilation compilation,
        ITestMethodRecognizer recognizer,
        CancellationToken cancellationToken
    )
    {
        if (compilation is null)
        {
            throw new ArgumentNullException(nameof(compilation));
        }

        if (recognizer is null)
        {
            throw new ArgumentNullException(nameof(recognizer));
        }

        var builder = ImmutableArray.CreateBuilder<IMethodSymbol>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CollectFromSyntaxTree(compilation, recognizer, syntaxTree, builder, seen, cancellationToken);
        }

        return builder.ToImmutable();
    }

    private static void CollectFromSyntaxTree(
        Compilation compilation,
        ITestMethodRecognizer recognizer,
        SyntaxTree syntaxTree,
        ImmutableArray<IMethodSymbol>.Builder builder,
        HashSet<ISymbol> seen,
        CancellationToken cancellationToken
    )
    {
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

            if (recognizer.IsTestMethod(method) && seen.Add(method))
            {
                builder.Add(method);
            }
        }
    }
}
