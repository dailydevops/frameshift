namespace NetEvolve.FrameShift.TestSurface;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Builds a <see cref="TestSurfaceManifest" /> for a test compilation by walking the code that is
/// reachable from its test methods and recording every member that comes from outside the compilation,
/// i.e. from the production assemblies under test.
/// </summary>
/// <remarks>
/// <para>
/// Only executable code is inspected: method bodies, expression bodies, constructor initializers and
/// member initializers. Attribute usages and signatures are deliberately skipped, because they
/// describe the test itself instead of the production code it exercises. The collector keeps no
/// state between calls and is therefore safe to use from concurrent analyzer callbacks.
/// </para>
/// <para>
/// The result is attributed per test: every walked production member is recorded under the test method
/// that reaches it, together with the number of test cases that method declares. Each test is walked
/// from its own entry point with its own visited set, so a helper of the test assembly that two tests
/// share contributes its production members to <em>both</em> of them. Attributing such a member to only
/// the test that happened to be walked first would understate the number of input combinations the
/// member is exercised with, which is exactly the judgement the single-test-case heuristic rests on.
/// </para>
/// </remarks>
internal static class TestSurfaceCollector
{
    /// <summary>
    /// Collects the test surface of <paramref name="compilation" /> for the test methods
    /// <paramref name="recognizer" /> recognises.
    /// </summary>
    /// <param name="compilation">The test compilation to inspect.</param>
    /// <param name="recognizer">The recogniser deciding which methods are test methods.</param>
    /// <param name="cancellationToken">A token to observe while collecting.</param>
    /// <returns>
    /// The manifest describing all discovered test methods, their test-case counts and the production
    /// members each of them references.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="compilation" /> or <paramref name="recognizer" /> is <see langword="null" />.
    /// </exception>
    public static TestSurfaceManifest Collect(
        Compilation compilation,
        ITestMethodRecognizer recognizer,
        CancellationToken cancellationToken
    ) => Build(Analyze(compilation, recognizer, cancellationToken), cancellationToken);

    /// <summary>
    /// Finds the test methods <paramref name="recognizer" /> recognises that do not reference a single
    /// production member, so that a caller can report <c>FSH0004</c> for them.
    /// </summary>
    /// <param name="compilation">The test compilation to inspect.</param>
    /// <param name="recognizer">The recogniser deciding which methods are test methods.</param>
    /// <param name="cancellationToken">A token to observe while collecting.</param>
    /// <returns>The test methods without any production reference, in declaration order.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="compilation" /> or <paramref name="recognizer" /> is <see langword="null" />.
    /// </exception>
    public static ImmutableArray<IMethodSymbol> FindTestsWithoutProductionReference(
        Compilation compilation,
        ITestMethodRecognizer recognizer,
        CancellationToken cancellationToken
    ) =>
        Analyze(compilation, recognizer, cancellationToken)
            .Where(entry => entry.ReferencedMemberIds.IsEmpty)
            .Select(entry => entry.TestMethod)
            .ToImmutableArray();

    /// <summary>
    /// Turns the per-test analysis result into a manifest, keyed by the documentation comment id of each
    /// test method.
    /// </summary>
    /// <param name="entries">The analysed test methods, in discovery order.</param>
    /// <param name="cancellationToken">A token to observe while building.</param>
    /// <returns>The manifest describing the analysed compilation.</returns>
    /// <remarks>
    /// A test method whose declaration id cannot be created is dropped altogether: the production side
    /// addresses everything by that id, so an entry without one could never be matched up again. Every
    /// remaining test contributes to both maps, even when it references no production member at all, so
    /// that the derived <see cref="TestSurfaceManifest.TestMethodIds" /> stays the complete set of tests.
    /// </remarks>
    private static TestSurfaceManifest Build(List<TestSurfaceEntry> entries, CancellationToken cancellationToken)
    {
        var testCaseCounts = ImmutableDictionary.CreateBuilder<string, TestCaseCount>(StringComparer.Ordinal);
        var referencesByTest = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(
            StringComparer.Ordinal
        );

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var testMethodId = DocumentationCommentId.CreateDeclarationId(entry.TestMethod);

            if (string.IsNullOrEmpty(testMethodId))
            {
                continue;
            }

            testCaseCounts[testMethodId!] = entry.CaseCount;
            referencesByTest[testMethodId!] = referencesByTest.TryGetValue(testMethodId!, out var known)
                ? known.Union(entry.ReferencedMemberIds)
                : entry.ReferencedMemberIds;
        }

        return new TestSurfaceManifest(testCaseCounts.ToImmutable(), referencesByTest.ToImmutable());
    }

    /// <summary>
    /// Walks the code reachable from every test method and records the production members it references,
    /// together with the number of test cases the method declares.
    /// </summary>
    /// <param name="compilation">The test compilation to inspect.</param>
    /// <param name="recognizer">The recogniser deciding which methods are test methods.</param>
    /// <param name="cancellationToken">A token to observe while collecting.</param>
    /// <returns>One entry per test method, in discovery order.</returns>
    /// <remarks>
    /// Neither argument needs a null check here: both are passed to the test-method discovery first,
    /// which rejects a <see langword="null" /> argument under the very same parameter name.
    /// </remarks>
    private static List<TestSurfaceEntry> Analyze(
        Compilation compilation,
        ITestMethodRecognizer recognizer,
        CancellationToken cancellationToken
    )
    {
        var testMethods = TestMethodDiscovery.FindTestMethods(compilation, recognizer, cancellationToken);
        var entries = new List<TestSurfaceEntry>(testMethods.Length);
        var semanticModels = new Dictionary<SyntaxTree, SemanticModel>();

        foreach (var testMethod in testMethods)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var referencedMemberIds = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);

            WalkReachableCode(compilation, testMethod, semanticModels, referencedMemberIds, cancellationToken);

            entries.Add(
                new TestSurfaceEntry(
                    testMethod,
                    recognizer.GetTestCaseCount(testMethod),
                    referencedMemberIds.ToImmutable()
                )
            );
        }

        return entries;
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
        var pending = new Stack<ISymbol>();
        var entryPoint = testMethod.OriginalDefinition;

        _ = visited.Add(entryPoint);
        pending.Push(entryPoint);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var member = pending.Pop();

            foreach (var syntaxReference in member.DeclaringSyntaxReferences)
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
        Stack<ISymbol> pending,
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
        Stack<ISymbol> pending,
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
            foreach (var traversable in GetTraversableMembers(definition).Where(visited.Add))
            {
                pending.Push(traversable);
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

    /// <summary>
    /// Yields the declarations that have to be walked when <paramref name="symbol" /> is used.
    /// </summary>
    /// <param name="symbol">The referenced member.</param>
    /// <returns>The member itself and, for a property or an event, its accessors.</returns>
    /// <remarks>
    /// <para>
    /// The member itself is part of the result, because the declaration of a property or a field can
    /// carry an initializer, and that initializer runs whenever the declaring type is created. Only
    /// walking the accessors would miss it.
    /// </para>
    /// <para>
    /// An event contributes its add and its remove accessor. The raise accessor of
    /// <see cref="IEventSymbol.RaiseMethod" /> is deliberately not consulted: C# has no syntax for one,
    /// and this traversal only ever reaches symbols declared in the C# syntax of the analysed
    /// compilation, so it is always <see langword="null" /> here.
    /// </para>
    /// </remarks>
    private static IEnumerable<ISymbol> GetTraversableMembers(ISymbol symbol)
    {
        if (IsTraversable(symbol))
        {
            yield return symbol.OriginalDefinition;
        }

        switch (symbol)
        {
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

                break;

            default:
                break;
        }
    }

    private static bool IsTraversable(ISymbol? symbol) =>
        symbol is not null && symbol.OriginalDefinition.DeclaringSyntaxReferences.Length > 0;

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

    /// <summary>
    /// The analysis result of a single test method: the method itself, the number of test cases it
    /// declares and the production members reachable from it.
    /// </summary>
    private readonly struct TestSurfaceEntry
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TestSurfaceEntry" /> struct.
        /// </summary>
        /// <param name="testMethod">The analysed test method.</param>
        /// <param name="caseCount">The number of test cases the method declares.</param>
        /// <param name="referencedMemberIds">
        /// The documentation comment ids of the production members reachable from the method.
        /// </param>
        public TestSurfaceEntry(
            IMethodSymbol testMethod,
            TestCaseCount caseCount,
            ImmutableHashSet<string> referencedMemberIds
        )
        {
            TestMethod = testMethod;
            CaseCount = caseCount;
            ReferencedMemberIds = referencedMemberIds;
        }

        /// <summary>
        /// Gets the analysed test method.
        /// </summary>
        public IMethodSymbol TestMethod { get; }

        /// <summary>
        /// Gets the number of test cases the method declares.
        /// </summary>
        public TestCaseCount CaseCount { get; }

        /// <summary>
        /// Gets the documentation comment ids of the production members reachable from the method.
        /// </summary>
        public ImmutableHashSet<string> ReferencedMemberIds { get; }
    }
}
