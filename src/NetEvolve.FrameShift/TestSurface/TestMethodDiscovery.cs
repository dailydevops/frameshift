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

    /// <summary>
    /// Finds all test methods exactly as <see cref="FindTestMethods" /> does and pairs each one with the
    /// number of test cases it contributes, so that a caller records both in a single pass.
    /// </summary>
    /// <param name="compilation">The compilation to scan, usually a test project.</param>
    /// <param name="recognizer">
    /// The recogniser deciding which methods are test methods and counting their cases.
    /// </param>
    /// <param name="cancellationToken">A token to observe while scanning.</param>
    /// <returns>
    /// The discovered test methods with their case counts, in declaration order and without duplicates.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="compilation" /> or <paramref name="recognizer" /> is <see langword="null" />.
    /// </exception>
    /// <remarks>
    /// The recogniser is asked for a count exactly once per discovered method, and only for a method it
    /// has accepted, which is the order
    /// <see cref="ITestMethodRecognizer.GetTestCaseCount(IMethodSymbol)" /> requires. Counting is
    /// deliberately kept out of <see cref="FindTestMethods" />: a caller that only needs the
    /// methods, such as the one reporting a test without a single production reference, must not pay for
    /// the attribute inspection a count costs.
    /// </remarks>
    public static ImmutableArray<(IMethodSymbol Method, TestCaseCount CaseCount)> FindTestMethodsWithCaseCounts(
        Compilation compilation,
        ITestMethodRecognizer recognizer,
        CancellationToken cancellationToken
    )
    {
        var testMethods = FindTestMethods(compilation, recognizer, cancellationToken);
        var builder = ImmutableArray.CreateBuilder<(IMethodSymbol Method, TestCaseCount CaseCount)>(testMethods.Length);

        foreach (var testMethod in testMethods)
        {
            cancellationToken.ThrowIfCancellationRequested();

            builder.Add((testMethod, recognizer.GetTestCaseCount(testMethod)));
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
        foreach (var method in EnumerateDeclaredMethods(compilation, syntaxTree, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (recognizer.IsTestMethod(method) && seen.Add(method))
            {
                builder.Add(method);
            }
        }
    }

    /// <summary>
    /// Yields the method symbol of every decorated method declaration of <paramref name="syntaxTree" />,
    /// in declaration order.
    /// </summary>
    /// <param name="compilation">The compilation the tree belongs to.</param>
    /// <param name="syntaxTree">The tree to walk.</param>
    /// <param name="cancellationToken">A token to observe while binding.</param>
    /// <returns>The bound methods, lazily.</returns>
    /// <remarks>
    /// <para>
    /// Only method declarations are walked, and only those carrying at least one attribute list, because
    /// every test of every supported framework is a method marked by an attribute. A local function is
    /// consequently never offered, however it is decorated, and a tree without a single decorated method
    /// never pays for a semantic model: the model is created on first use, at most once per tree, by a
    /// <see cref="Lazy{T}" /> that needs no synchronisation because it never leaves this call.
    /// </para>
    /// <para>
    /// A declaration the compiler cannot bind to a method contributes nothing, which is what the closing
    /// <see cref="Enumerable.OfType{TResult}(System.Collections.IEnumerable)" /> takes care of. The
    /// discovery runs inside an analyzer, therefore on code that does not compile, and no shape of broken
    /// source may make it throw.
    /// </para>
    /// </remarks>
    private static IEnumerable<IMethodSymbol> EnumerateDeclaredMethods(
        Compilation compilation,
        SyntaxTree syntaxTree,
        CancellationToken cancellationToken
    )
    {
        var semanticModel = new Lazy<SemanticModel>(
            () => compilation.GetSemanticModel(syntaxTree),
            isThreadSafe: false
        );

        return syntaxTree
            .GetRoot(cancellationToken)
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(declaration => declaration.AttributeLists.Count > 0)
            .Select(declaration => semanticModel.Value.GetDeclaredSymbol(declaration, cancellationToken))
            .OfType<IMethodSymbol>();
    }
}
