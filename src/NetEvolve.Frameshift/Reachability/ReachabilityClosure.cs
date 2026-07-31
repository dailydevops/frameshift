namespace NetEvolve.Frameshift.Reachability;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.Frameshift.TestSurface;

/// <summary>
/// Turns the flat list of production members recorded in a test-surface manifest into the transitive
/// set of production members that a test can actually reach.
/// </summary>
/// <remarks>
/// <para>
/// The closure runs on the production side, and it has to. The test compilation sees the production
/// assemblies as metadata references only: it can name the members a test touches directly, but it
/// cannot see a single production method body, so it cannot know which further members those touched
/// members call. That call graph exists only in the production compilation, which in turn cannot see
/// the tests at all. The manifest therefore transports the seed, and this class performs the
/// expansion here, where the syntax trees of the production code are available.
/// </para>
/// <para>
/// The expansion is a breadth-first walk: every reachable member whose declaration lives in this
/// compilation is parsed, every invocation, property, indexer, event, field or object creation its
/// executable code resolves to is added, and the newly added members are queued in turn. Virtual and
/// interface dispatch is approximated by adding the implementations and overrides declared in this
/// compilation for every reachable virtual, abstract or interface member.
/// </para>
/// <para>
/// Known limitations, all of them deliberate, because the analysis must stay a pure, side effect free
/// compile time operation:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// Reflection is not followed. A member that is only ever invoked through
/// <c>Type.GetMethod</c>, <c>Activator</c> or an expression tree compiled at run time looks
/// unreachable.
/// </description>
/// </item>
/// <item>
/// <description>
/// Dependency injection is not followed. A registration that binds an abstraction to an
/// implementation at run time is invisible; only the syntactic implementation and override
/// relationships described above bridge that gap.
/// </description>
/// </item>
/// <item>
/// <description>
/// Dynamic dispatch through delegates that are stored in fields, properties or collections is
/// not followed. The delegate creation is recorded where it appears, the later invocation
/// through the field is not connected to it.
/// </description>
/// </item>
/// <item>
/// <description>
/// Source generated code is not followed as a producer of reachability. Generated trees are part
/// of the compilation and are walked like any other tree, but the generator itself is never run
/// or reasoned about, and generated members that no source code references stay unreachable.
/// </description>
/// </item>
/// <item>
/// <description>
/// Members without a declaring syntax in this compilation, most notably implicitly declared
/// default constructors, contribute no outgoing references, so field and property initializers
/// that only such a constructor would execute are not reached through it.
/// </description>
/// </item>
/// </list>
/// <para>
/// Every limitation errs on the side of reporting a gap that a human can dismiss, instead of silently
/// claiming coverage that does not exist.
/// </para>
/// </remarks>
internal static class ReachabilityClosure
{
    /// <summary>
    /// Computes the reachable set of <paramref name="compilation" /> for the surface recorded in
    /// <paramref name="manifest" />.
    /// </summary>
    /// <param name="compilation">The production compilation that owns the syntax trees to walk.</param>
    /// <param name="manifest">The manifest produced by a previous pass over the test compilation.</param>
    /// <param name="cancellationToken">A token observed on every iteration of the walk.</param>
    /// <returns>
    /// The transitively closed set of reachable members, or <see cref="ReachableSymbolSet.Empty" /> if
    /// the manifest records no production reference at all.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="compilation" /> or <paramref name="manifest" /> is <see langword="null" />.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    public static ReachableSymbolSet Compute(
        Compilation compilation,
        TestSurfaceManifest manifest,
        CancellationToken cancellationToken
    )
    {
        if (compilation is null)
        {
            throw new ArgumentNullException(nameof(compilation));
        }

        if (manifest is null)
        {
            throw new ArgumentNullException(nameof(manifest));
        }

        if (manifest.ReferencedMemberIds.IsEmpty)
        {
            return ReachableSymbolSet.Empty;
        }

        var walker = new ClosureWalker(compilation, cancellationToken);

        walker.Seed(manifest);
        walker.Expand();

        return walker.CreateResult();
    }

    /// <summary>
    /// Holds the mutable state of a single closure computation. The state never outlives the
    /// <see cref="Compute" /> call that created it, which keeps the analyzers using this class
    /// stateless and thread-safe.
    /// </summary>
    private sealed class ClosureWalker
    {
        private readonly Compilation _compilation;
        private readonly CancellationToken _cancellationToken;
        private readonly Dictionary<SyntaxTree, SemanticModel> _semanticModels =
            new Dictionary<SyntaxTree, SemanticModel>();

        private readonly HashSet<ISymbol> _reachable = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        private readonly HashSet<ISymbol> _dispatchHandled = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        private readonly Queue<ISymbol> _pending = new Queue<ISymbol>();

        private List<INamedTypeSymbol>? _declaredTypes;

        /// <summary>
        /// Initializes a new instance of the <see cref="ClosureWalker" /> class.
        /// </summary>
        /// <param name="compilation">The production compilation to walk.</param>
        /// <param name="cancellationToken">A token observed on every iteration.</param>
        public ClosureWalker(Compilation compilation, CancellationToken cancellationToken)
        {
            _compilation = compilation;
            _cancellationToken = cancellationToken;
        }

        /// <summary>
        /// Resolves the recorded member ids against this compilation and queues everything that maps to
        /// a symbol of it. Ids that do not resolve are ignored, because a manifest may describe another
        /// version of the assembly.
        /// </summary>
        /// <param name="manifest">The manifest holding the recorded ids.</param>
        public void Seed(TestSurfaceManifest manifest)
        {
            foreach (var referencedMemberId in manifest.ReferencedMemberIds)
            {
                _cancellationToken.ThrowIfCancellationRequested();

                var symbols = DocumentationCommentId.GetSymbolsForDeclarationId(referencedMemberId, _compilation);

                foreach (var symbol in symbols)
                {
                    // A seeded member is reachable like any other, so the dispatch approximation has to
                    // run for it as well. A test that calls an interface or virtual member directly is
                    // the most common shape there is, and without this the implementations behind that
                    // abstraction would all be reported as gaps.
                    HandleReference(symbol);
                }
            }
        }

        /// <summary>
        /// Drains the work queue until no new member becomes reachable. The visited set guards against
        /// the cycles that recursion and mutually recursive members create.
        /// </summary>
        public void Expand()
        {
            while (_pending.Count > 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();

                ExpandMember(_pending.Dequeue());
            }
        }

        /// <summary>
        /// Materializes the computed set.
        /// </summary>
        /// <returns>The immutable reachable set.</returns>
        public ReachableSymbolSet CreateResult() => new ReachableSymbolSet(_reachable);

        private void ExpandMember(ISymbol member)
        {
            foreach (var syntaxReference in member.DeclaringSyntaxReferences)
            {
                _cancellationToken.ThrowIfCancellationRequested();

                if (!_compilation.ContainsSyntaxTree(syntaxReference.SyntaxTree))
                {
                    continue;
                }

                var semanticModel = GetSemanticModel(syntaxReference.SyntaxTree);
                var declaration = syntaxReference.GetSyntax(_cancellationToken);

                foreach (var executableNode in GetExecutableNodes(declaration))
                {
                    WalkExecutableNode(semanticModel, executableNode);
                }
            }
        }

        private void WalkExecutableNode(SemanticModel semanticModel, SyntaxNode executableNode)
        {
            foreach (var node in executableNode.DescendantNodesAndSelf(node => node is not AttributeListSyntax))
            {
                _cancellationToken.ThrowIfCancellationRequested();

                HandleNode(semanticModel, node);
            }
        }

        private void HandleNode(SemanticModel semanticModel, SyntaxNode node)
        {
            if (node is LocalFunctionStatementSyntax localFunction)
            {
                Add(semanticModel.GetDeclaredSymbol(localFunction, _cancellationToken));
                return;
            }

            if (node is not (ExpressionSyntax or ConstructorInitializerSyntax))
            {
                return;
            }

            var symbolInfo = semanticModel.GetSymbolInfo(node, _cancellationToken);

            if (symbolInfo.Symbol is not null)
            {
                HandleReference(symbolInfo.Symbol);
                return;
            }

            // An unresolved reference, for example an ambiguous method group conversion, is treated
            // generously: recording all candidates can only widen the reachable set, and a member that
            // is wrongly considered reachable costs a missed hint, never a false gap report.
            foreach (var candidate in symbolInfo.CandidateSymbols)
            {
                HandleReference(candidate);
            }
        }

        private void HandleReference(ISymbol symbol)
        {
            Add(symbol);
            AddDispatchTargets(symbol);
        }

        private void Add(ISymbol? symbol)
        {
            if (symbol is null)
            {
                return;
            }

            var definition = ReachableSymbolSet.NormalizeDefinition(symbol);

            if (!IsDeclaredInThisCompilation(definition) || !_reachable.Add(definition))
            {
                return;
            }

            _pending.Enqueue(definition);

            AddRelatedMembers(definition);
        }

        /// <summary>
        /// Adds the members that share a declaration with <paramref name="definition" /> and therefore
        /// share its reachability: the two halves of a partial method and the accessors of a property or
        /// event, whose bodies are separate declarations of their own.
        /// </summary>
        /// <param name="definition">The member that just became reachable.</param>
        private void AddRelatedMembers(ISymbol definition)
        {
            switch (definition)
            {
                case IMethodSymbol method:
                    Add(method.PartialDefinitionPart);
                    Add(method.PartialImplementationPart);
                    Add(method.AssociatedSymbol);
                    break;

                case IPropertySymbol property:
                    Add(property.GetMethod);
                    Add(property.SetMethod);
                    break;

                case IEventSymbol @event:
                    Add(@event.AddMethod);
                    Add(@event.RemoveMethod);
                    Add(@event.RaiseMethod);
                    break;

                default:
                    break;
            }
        }

        /// <summary>
        /// Approximates virtual and interface dispatch for <paramref name="symbol" />.
        /// </summary>
        /// <param name="symbol">The referenced symbol, in the form the semantic model resolved it.</param>
        /// <remarks>
        /// A call through an abstraction can end up in any implementation, and which one it is cannot be
        /// decided at compile time. Every implementation declared in this compilation is therefore
        /// considered reachable. The search is syntactic and stays inside this compilation: overrides in
        /// other assemblies, and implementations chosen through configuration or dependency injection,
        /// are outside of what a single compilation can observe.
        /// </remarks>
        private void AddDispatchTargets(ISymbol symbol)
        {
            var containingType = symbol.ContainingType;

            if (containingType is null || !_dispatchHandled.Add(symbol))
            {
                return;
            }

            if (containingType.TypeKind == TypeKind.Interface)
            {
                AddInterfaceImplementations(symbol, containingType);
                return;
            }

            if (symbol.IsVirtual || symbol.IsAbstract || symbol.IsOverride)
            {
                AddOverrides(symbol);
            }
        }

        private void AddInterfaceImplementations(ISymbol interfaceMember, INamedTypeSymbol interfaceType)
        {
            foreach (var type in GetDeclaredTypes().Where(candidate => Implements(candidate, interfaceType)))
            {
                _cancellationToken.ThrowIfCancellationRequested();

                Add(type.FindImplementationForInterfaceMember(interfaceMember));
            }
        }

        private void AddOverrides(ISymbol member)
        {
            foreach (var type in GetDeclaredTypes())
            {
                _cancellationToken.ThrowIfCancellationRequested();

                var candidates = type.GetMembers(member.Name).Where(candidate => Overrides(candidate, member));

                foreach (var candidate in candidates)
                {
                    Add(candidate);
                }
            }
        }

        private List<INamedTypeSymbol> GetDeclaredTypes()
        {
            if (_declaredTypes is null)
            {
                var declaredTypes = new List<INamedTypeSymbol>();

                CollectDeclaredTypes(_compilation.Assembly.GlobalNamespace, declaredTypes);

                _declaredTypes = declaredTypes;
            }

            return _declaredTypes;
        }

        private void CollectDeclaredTypes(INamespaceOrTypeSymbol container, List<INamedTypeSymbol> declaredTypes)
        {
            foreach (var member in container.GetMembers())
            {
                _cancellationToken.ThrowIfCancellationRequested();

                switch (member)
                {
                    case INamespaceSymbol containedNamespace:
                        CollectDeclaredTypes(containedNamespace, declaredTypes);
                        break;

                    case INamedTypeSymbol containedType:
                        declaredTypes.Add(containedType);
                        CollectDeclaredTypes(containedType, declaredTypes);
                        break;

                    default:
                        break;
                }
            }
        }

        private bool IsDeclaredInThisCompilation(ISymbol symbol) =>
            symbol.ContainingAssembly is not null
            && SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, _compilation.Assembly);

        private SemanticModel GetSemanticModel(SyntaxTree syntaxTree)
        {
            if (!_semanticModels.TryGetValue(syntaxTree, out var semanticModel))
            {
                semanticModel = _compilation.GetSemanticModel(syntaxTree);
                _semanticModels.Add(syntaxTree, semanticModel);
            }

            return semanticModel;
        }

        private static bool Implements(INamedTypeSymbol type, INamedTypeSymbol interfaceType) =>
            type.AllInterfaces.Any(candidate =>
                SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, interfaceType.OriginalDefinition)
            );

        /// <summary>
        /// Determines whether <paramref name="candidate" /> overrides <paramref name="member" />,
        /// directly or through an intermediate override.
        /// </summary>
        /// <param name="candidate">The member that may be an override.</param>
        /// <param name="member">The virtual, abstract or overriding member that was referenced.</param>
        /// <returns><see langword="true" /> if the chain of overridden members reaches the member.</returns>
        private static bool Overrides(ISymbol candidate, ISymbol member)
        {
            var current = GetOverriddenMember(candidate);

            while (current is not null)
            {
                if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, member.OriginalDefinition))
                {
                    return true;
                }

                current = GetOverriddenMember(current);
            }

            return false;
        }

        private static ISymbol? GetOverriddenMember(ISymbol symbol) =>
            symbol switch
            {
                IMethodSymbol method => method.OverriddenMethod,
                IPropertySymbol property => property.OverriddenProperty,
                IEventSymbol @event => @event.OverriddenEvent,
                _ => null,
            };

        /// <summary>
        /// Returns the nodes of <paramref name="declaration" /> that contain executable code, i.e. the
        /// only places a reference to another member can appear.
        /// </summary>
        /// <param name="declaration">The declaring syntax of a reachable member.</param>
        /// <returns>
        /// The executable nodes, empty for declarations without code of their own such as type
        /// declarations, whose members are reached through their own references instead.
        /// </returns>
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
                BasePropertyDeclarationSyntax property => GetPropertyNodes(property),
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

        private static IEnumerable<SyntaxNode> GetPropertyNodes(BasePropertyDeclarationSyntax property) =>
            property switch
            {
                PropertyDeclarationSyntax declaration => GetPropertyNodes(
                    declaration.ExpressionBody,
                    declaration.Initializer
                ),
                IndexerDeclarationSyntax indexer => GetPropertyNodes(indexer.ExpressionBody, initializer: null),
                _ => [],
            };

        private static IEnumerable<SyntaxNode> GetPropertyNodes(
            ArrowExpressionClauseSyntax? expressionBody,
            EqualsValueClauseSyntax? initializer
        )
        {
            if (expressionBody is not null)
            {
                yield return expressionBody.Expression;
            }

            foreach (var node in GetInitializerNodes(initializer))
            {
                yield return node;
            }
        }

        private static IEnumerable<SyntaxNode> GetBodyNodes(
            BlockSyntax? body,
            ArrowExpressionClauseSyntax? expressionBody
        )
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
    }
}
