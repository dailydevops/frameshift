namespace NetEvolve.Frameshift.TestSurface;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Builds a <see cref="TestSurfaceManifest" /> for a test compilation by walking the code that is
/// reachable from its TUnit test methods and recording every member that comes from outside the
/// compilation, i.e. from the production assemblies under test.
/// </summary>
/// <remarks>
/// Only executable code is inspected: method bodies, expression bodies, constructor initializers and
/// member initializers. Attribute usages and signatures are deliberately skipped, because they
/// describe the test itself instead of the production code it exercises. The collector keeps no
/// state between calls and is therefore safe to use from concurrent analyzer callbacks.
/// </remarks>
internal static class TestSurfaceCollector
{
    /// <summary>
    /// Collects the test surface of <paramref name="compilation" />.
    /// </summary>
    /// <param name="compilation">The test compilation to inspect.</param>
    /// <param name="cancellationToken">A token to observe while collecting.</param>
    /// <returns>
    /// The manifest describing all discovered test methods and the production members they reference.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="compilation" /> is <see langword="null" />.</exception>
    public static TestSurfaceManifest Collect(Compilation compilation, CancellationToken cancellationToken)
    {
        var results = Analyze(compilation, cancellationToken);
        var testMethodIds = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var referencedMemberIds = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);

        foreach (var (testMethod, memberIds) in results)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var testMethodId = DocumentationCommentId.CreateDeclarationId(testMethod);

            if (!string.IsNullOrEmpty(testMethodId))
            {
                _ = testMethodIds.Add(testMethodId!);
            }

            foreach (var memberId in memberIds)
            {
                _ = referencedMemberIds.Add(memberId);
            }
        }

        return new TestSurfaceManifest(testMethodIds.ToImmutable(), referencedMemberIds.ToImmutable());
    }

    /// <summary>
    /// Finds the test methods of <paramref name="compilation" /> that do not reference a single
    /// production member, so that a caller can report <c>FSH0004</c> for them.
    /// </summary>
    /// <param name="compilation">The test compilation to inspect.</param>
    /// <param name="cancellationToken">A token to observe while collecting.</param>
    /// <returns>The test methods without any production reference, in declaration order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="compilation" /> is <see langword="null" />.</exception>
    public static ImmutableArray<IMethodSymbol> FindTestsWithoutProductionReference(
        Compilation compilation,
        CancellationToken cancellationToken
    ) =>
        Analyze(compilation, cancellationToken)
            .Where(result => result.ReferencedMemberIds.IsEmpty)
            .Select(result => result.TestMethod)
            .ToImmutableArray();

    private static List<(IMethodSymbol TestMethod, ImmutableHashSet<string> ReferencedMemberIds)> Analyze(
        Compilation compilation,
        CancellationToken cancellationToken
    )
    {
        if (compilation is null)
        {
            throw new ArgumentNullException(nameof(compilation));
        }

        var results = new List<(IMethodSymbol TestMethod, ImmutableHashSet<string> ReferencedMemberIds)>();
        var semanticModels = new Dictionary<SyntaxTree, SemanticModel>();

        foreach (var testMethod in TUnitTestDiscovery.FindTestMethods(compilation, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var referencedMemberIds = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);

            WalkReachableCode(compilation, testMethod, semanticModels, referencedMemberIds, cancellationToken);

            results.Add((testMethod, referencedMemberIds.ToImmutable()));
        }

        return results;
    }

    private static void WalkReachableCode(
        Compilation compilation,
        IMethodSymbol testMethod,
        Dictionary<SyntaxTree, SemanticModel> semanticModels,
        ImmutableHashSet<string>.Builder referencedMemberIds,
        CancellationToken cancellationToken
    )
    {
        var visited = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var pending = new Stack<IMethodSymbol>();
        var entryPoint = testMethod.OriginalDefinition;

        _ = visited.Add(entryPoint);
        pending.Push(entryPoint);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var method = pending.Pop();

            foreach (var syntaxReference in method.DeclaringSyntaxReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!compilation.ContainsSyntaxTree(syntaxReference.SyntaxTree))
                {
                    continue;
                }

                var semanticModel = GetSemanticModel(compilation, semanticModels, syntaxReference.SyntaxTree);
                var declaration = syntaxReference.GetSyntax(cancellationToken);

                foreach (var executableNode in GetExecutableNodes(declaration))
                {
                    WalkExecutableNode(
                        compilation,
                        semanticModel,
                        executableNode,
                        visited,
                        pending,
                        referencedMemberIds,
                        cancellationToken
                    );
                }
            }
        }
    }

    private static void WalkExecutableNode(
        Compilation compilation,
        SemanticModel semanticModel,
        SyntaxNode executableNode,
        HashSet<ISymbol> visited,
        Stack<IMethodSymbol> pending,
        ImmutableHashSet<string>.Builder referencedMemberIds,
        CancellationToken cancellationToken
    )
    {
        foreach (var node in executableNode.DescendantNodesAndSelf(node => node is not AttributeListSyntax))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (node is not (ExpressionSyntax or ConstructorInitializerSyntax))
            {
                continue;
            }

            var symbolInfo = semanticModel.GetSymbolInfo(node, cancellationToken);
            var symbol = symbolInfo.Symbol;

            if (symbol is null && symbolInfo.CandidateSymbols.Length == 1)
            {
                symbol = symbolInfo.CandidateSymbols[0];
            }

            if (symbol is null)
            {
                continue;
            }

            HandleSymbol(compilation, symbol, visited, pending, referencedMemberIds);
        }
    }

    private static void HandleSymbol(
        Compilation compilation,
        ISymbol symbol,
        HashSet<ISymbol> visited,
        Stack<IMethodSymbol> pending,
        ImmutableHashSet<string>.Builder referencedMemberIds
    )
    {
        var definition = Normalize(symbol);

        if (!IsRecordableKind(definition))
        {
            return;
        }

        var containingAssembly = definition.ContainingAssembly;

        if (containingAssembly is null)
        {
            return;
        }

        if (SymbolEqualityComparer.Default.Equals(containingAssembly, compilation.Assembly))
        {
            foreach (var method in GetTraversableMethods(definition).Where(visited.Add))
            {
                pending.Push(method);
            }

            return;
        }

        var declarationId = DocumentationCommentId.CreateDeclarationId(definition);

        if (!string.IsNullOrEmpty(declarationId))
        {
            _ = referencedMemberIds.Add(declarationId!);
        }
    }

    private static ISymbol Normalize(ISymbol symbol) =>
        symbol switch
        {
            IMethodSymbol { ReducedFrom: not null } method => method.ReducedFrom.OriginalDefinition,
            _ => symbol.OriginalDefinition,
        };

    private static bool IsRecordableKind(ISymbol symbol) =>
        symbol.Kind
            is SymbolKind.Method
                or SymbolKind.Property
                or SymbolKind.Field
                or SymbolKind.Event
                or SymbolKind.NamedType;

    private static IEnumerable<IMethodSymbol> GetTraversableMethods(ISymbol symbol)
    {
        switch (symbol)
        {
            case IMethodSymbol method:
                if (IsTraversable(method))
                {
                    yield return method.OriginalDefinition;
                }

                break;

            case IPropertySymbol property:
                if (IsTraversable(property.GetMethod))
                {
                    yield return property.GetMethod!.OriginalDefinition;
                }

                if (IsTraversable(property.SetMethod))
                {
                    yield return property.SetMethod!.OriginalDefinition;
                }

                break;

            case IEventSymbol @event:
                if (IsTraversable(@event.AddMethod))
                {
                    yield return @event.AddMethod!.OriginalDefinition;
                }

                if (IsTraversable(@event.RemoveMethod))
                {
                    yield return @event.RemoveMethod!.OriginalDefinition;
                }

                if (IsTraversable(@event.RaiseMethod))
                {
                    yield return @event.RaiseMethod!.OriginalDefinition;
                }

                break;

            default:
                break;
        }
    }

    private static bool IsTraversable(IMethodSymbol? method) =>
        method is not null && method.OriginalDefinition.DeclaringSyntaxReferences.Length > 0;

    private static IEnumerable<SyntaxNode> GetExecutableNodes(SyntaxNode declaration) =>
        declaration switch
        {
            BaseMethodDeclarationSyntax method => GetMethodNodes(method),
            LocalFunctionStatementSyntax localFunction => GetBodyNodes(
                localFunction.Body,
                localFunction.ExpressionBody
            ),
            AccessorDeclarationSyntax accessor => GetBodyNodes(accessor.Body, accessor.ExpressionBody),
            ArrowExpressionClauseSyntax arrowExpression => [arrowExpression.Expression],
            PropertyDeclarationSyntax property => GetPropertyNodes(property),
            VariableDeclaratorSyntax variable => GetInitializerNodes(variable.Initializer),
            _ => [],
        };

    private static IEnumerable<SyntaxNode> GetMethodNodes(BaseMethodDeclarationSyntax method)
    {
        var initializer = (method as ConstructorDeclarationSyntax)?.Initializer;

        if (initializer is not null)
        {
            yield return initializer;
        }

        foreach (var node in GetBodyNodes(method.Body, method.ExpressionBody))
        {
            yield return node;
        }
    }

    private static IEnumerable<SyntaxNode> GetPropertyNodes(PropertyDeclarationSyntax property)
    {
        if (property.ExpressionBody is not null)
        {
            yield return property.ExpressionBody.Expression;
        }

        foreach (var node in GetInitializerNodes(property.Initializer))
        {
            yield return node;
        }
    }

    private static IEnumerable<SyntaxNode> GetBodyNodes(BlockSyntax? body, ArrowExpressionClauseSyntax? expressionBody)
    {
        if (body is not null)
        {
            yield return body;
        }

        if (expressionBody is not null)
        {
            yield return expressionBody.Expression;
        }
    }

    private static IEnumerable<SyntaxNode> GetInitializerNodes(EqualsValueClauseSyntax? initializer) =>
        initializer is null ? [] : [initializer.Value];

    private static SemanticModel GetSemanticModel(
        Compilation compilation,
        Dictionary<SyntaxTree, SemanticModel> semanticModels,
        SyntaxTree syntaxTree
    )
    {
        if (!semanticModels.TryGetValue(syntaxTree, out var semanticModel))
        {
            semanticModel = compilation.GetSemanticModel(syntaxTree);
            semanticModels.Add(syntaxTree, semanticModel);
        }

        return semanticModel;
    }
}
