namespace NetEvolve.FrameShift.Reachability;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.TestSurface;

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
/// The walk also carries attribution. A seed does not only say "some test reaches this member", it
/// says which test methods reach it, and every member the walk expands to inherits the attribution of
/// the members it was reached from, unioned over all paths that lead to it. That union is what allows
/// a caller to sum the test case counts of exactly those tests that reach a mutation point, instead of
/// only knowing that the point is reached at all. Understating the attribution would understate the
/// number of input combinations a member is exercised with, so the union is deliberately maximal: a
/// member reached from two seeds carries both, and a member reached along a longer path never loses
/// what a shorter path already gave it.
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
    /// The transitively closed set of reachable members, attributed to the test methods that reach
    /// them, or <see cref="ReachableSymbolSet.Empty" /> if the manifest records no production reference
    /// at all.
    /// </returns>
    /// <remarks>
    /// The seed is taken from both <see cref="TestSurfaceManifest.ReferencedMemberIds" /> and the keys
    /// of the inverted <see cref="TestSurfaceManifest.ReferencesByTest" />. The former is the union of
    /// the latter, so the two agree for every manifest this analyzer writes; reading both means that a
    /// manifest carrying references without attribution still produces the very same reachable set,
    /// just without test ids to report.
    /// </remarks>
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

        var attribution = InvertReferences(manifest.ReferencesByTest, cancellationToken);
        var referencedMemberIds = manifest.ReferencedMemberIds.Union(attribution.Keys);

        if (referencedMemberIds.IsEmpty)
        {
            return ReachableSymbolSet.Empty;
        }

        return Compute(compilation, referencedMemberIds, attribution, cancellationToken);
    }

    /// <summary>
    /// Computes the reachable set of <paramref name="compilation" /> for a test-to-references map,
    /// without going through a manifest.
    /// </summary>
    /// <param name="compilation">The production compilation that owns the syntax trees to walk.</param>
    /// <param name="referencesByTest">
    /// The documentation comment ids of the production members each test method references, keyed by
    /// the documentation comment id of the test method.
    /// </param>
    /// <param name="cancellationToken">A token observed on every iteration of the walk.</param>
    /// <returns>
    /// The transitively closed set of reachable members, attributed to the test methods that reach
    /// them, or <see cref="ReachableSymbolSet.Empty" /> if no test references a production member.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="compilation" /> or <paramref name="referencesByTest" /> is <see langword="null" />.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    public static ReachableSymbolSet ComputeFromReferences(
        Compilation compilation,
        ImmutableDictionary<string, ImmutableHashSet<string>> referencesByTest,
        CancellationToken cancellationToken
    )
    {
        if (compilation is null)
        {
            throw new ArgumentNullException(nameof(compilation));
        }

        if (referencesByTest is null)
        {
            throw new ArgumentNullException(nameof(referencesByTest));
        }

        var attribution = InvertReferences(referencesByTest, cancellationToken);

        if (attribution.Count == 0)
        {
            return ReachableSymbolSet.Empty;
        }

        return Compute(compilation, attribution.Keys, attribution, cancellationToken);
    }

    private static ReachableSymbolSet Compute(
        Compilation compilation,
        IEnumerable<string> referencedMemberIds,
        Dictionary<string, ImmutableHashSet<string>> attribution,
        CancellationToken cancellationToken
    )
    {
        var walker = new ClosureWalker(compilation, cancellationToken);

        walker.Seed(referencedMemberIds, attribution);
        walker.Expand();

        return walker.CreateResult();
    }

    /// <summary>
    /// Inverts the test-to-references map into the references-to-tests map the seeding needs.
    /// </summary>
    /// <param name="referencesByTest">The recorded references of every test method.</param>
    /// <param name="cancellationToken">A token observed once per test method.</param>
    /// <returns>
    /// The ids of the test methods referencing a member, keyed by the documentation comment id of that
    /// member and compared ordinally.
    /// </returns>
    private static Dictionary<string, ImmutableHashSet<string>> InvertReferences(
        ImmutableDictionary<string, ImmutableHashSet<string>> referencesByTest,
        CancellationToken cancellationToken
    )
    {
        var builders = new Dictionary<string, ImmutableHashSet<string>.Builder>(StringComparer.Ordinal);

        foreach (var reference in referencesByTest)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var referencedMemberId in reference.Value)
            {
                if (!builders.TryGetValue(referencedMemberId, out var builder))
                {
                    builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
                    builders.Add(referencedMemberId, builder);
                }

                _ = builder.Add(reference.Key);
            }
        }

        var attribution = new Dictionary<string, ImmutableHashSet<string>>(builders.Count, StringComparer.Ordinal);

        foreach (var builder in builders)
        {
            attribution.Add(builder.Key, builder.Value.ToImmutable());
        }

        return attribution;
    }

    /// <summary>
    /// Holds the mutable state of a single closure computation. The state never outlives the
    /// <see cref="Compute(Compilation, TestSurfaceManifest, CancellationToken)" /> call that created
    /// it, which keeps the analyzers using this class stateless and thread-safe.
    /// </summary>
    private sealed class ClosureWalker
    {
        private static readonly ImmutableHashSet<string> _noTests = ImmutableHashSet<string>.Empty.WithComparer(
            StringComparer.Ordinal
        );

        private static readonly ISymbol[] _noTargets = [];

        private readonly Compilation _compilation;
        private readonly CancellationToken _cancellationToken;
        private readonly Dictionary<SyntaxTree, SemanticModel> _semanticModels =
            new Dictionary<SyntaxTree, SemanticModel>();

        private readonly Dictionary<ISymbol, ImmutableHashSet<string>> _attribution = new Dictionary<
            ISymbol,
            ImmutableHashSet<string>
        >(SymbolEqualityComparer.Default);

        private readonly Dictionary<ISymbol, ISymbol[]> _outgoing = new Dictionary<ISymbol, ISymbol[]>(
            SymbolEqualityComparer.Default
        );

        private readonly Dictionary<ISymbol, ISymbol[]> _dispatchTargets = new Dictionary<ISymbol, ISymbol[]>(
            SymbolEqualityComparer.Default
        );

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
        /// a symbol of it, attributed to the test methods that recorded the id. Ids that do not resolve
        /// are ignored, because a manifest may describe another version of the assembly.
        /// </summary>
        /// <param name="referencedMemberIds">The recorded ids of the referenced production members.</param>
        /// <param name="attribution">The test method ids per referenced production member id.</param>
        public void Seed(
            IEnumerable<string> referencedMemberIds,
            Dictionary<string, ImmutableHashSet<string>> attribution
        )
        {
            foreach (var referencedMemberId in referencedMemberIds)
            {
                _cancellationToken.ThrowIfCancellationRequested();

                var tests = attribution.TryGetValue(referencedMemberId, out var attributed) ? attributed : _noTests;
                var symbols = DocumentationCommentId.GetSymbolsForDeclarationId(referencedMemberId, _compilation);

                foreach (var symbol in symbols)
                {
                    SeedSymbol(symbol, tests);
                }
            }
        }

        /// <summary>
        /// Drains the work queue until no member becomes reachable and no attribution grows any more.
        /// The recorded attribution guards against the cycles that recursion and mutually recursive
        /// members create: a member is only queued again when a path actually widened its set of test
        /// methods, and those sets only ever grow inside the finite set of seeded test ids.
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
        public ReachableSymbolSet CreateResult() => ReachableSymbolSet.FromAttribution(_attribution);

        /// <summary>
        /// Seeds a single resolved symbol.
        /// </summary>
        /// <param name="symbol">The symbol a recorded id resolved to.</param>
        /// <param name="tests">The ids of the test methods that recorded it.</param>
        /// <remarks>
        /// A seeded member is reachable like any other, so the dispatch approximation has to run for it
        /// as well. A test that calls an interface or virtual member directly is the most common shape
        /// there is, and without this the implementations behind that abstraction would all be reported
        /// as gaps. The dispatch search runs even when the seed itself is declared in another assembly,
        /// which is what connects a test against a foreign abstraction to the local implementation of it.
        /// </remarks>
        private void SeedSymbol(ISymbol symbol, ImmutableHashSet<string> tests)
        {
            Reach(symbol, tests);

            foreach (var dispatchTarget in GetDispatchTargets(symbol))
            {
                Reach(dispatchTarget, tests);
            }
        }

        /// <summary>
        /// Propagates the attribution of <paramref name="member" /> to everything it references.
        /// </summary>
        /// <param name="member">The member that was queued.</param>
        private void ExpandMember(ISymbol member)
        {
            var tests = _attribution[member];

            foreach (var target in GetOutgoingTargets(member))
            {
                Reach(target, tests);
            }
        }

        /// <summary>
        /// Returns the members <paramref name="member" /> references, computing them from its syntax on
        /// the first call and reusing them afterwards.
        /// </summary>
        /// <param name="member">The reachable member to inspect.</param>
        /// <returns>The normalized, locally declared members that inherit the attribution of the member.</returns>
        /// <remarks>
        /// Caching matters twice over. A member is queued again whenever a further path widens its
        /// attribution, and re-walking its syntax and re-resolving every expression of it for that is
        /// pure waste; the deduplicated array also keeps the replay proportional to the number of
        /// distinct references instead of the number of times they are written.
        /// </remarks>
        private ISymbol[] GetOutgoingTargets(ISymbol member)
        {
            if (_outgoing.TryGetValue(member, out var cached))
            {
                return cached;
            }

            var targets = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

            CollectRelatedMembers(member, targets);
            CollectReferences(member, targets);

            cached = targets.Count == 0 ? _noTargets : [.. targets];
            _outgoing.Add(member, cached);

            return cached;
        }

        /// <summary>
        /// Records that <paramref name="symbol" /> is reachable from the tests in
        /// <paramref name="tests" />, queueing it when that widens what is already known about it.
        /// </summary>
        /// <param name="symbol">The reached symbol, in the form the semantic model resolved it.</param>
        /// <param name="tests">The ids of the test methods reaching it along the current path.</param>
        private void Reach(ISymbol? symbol, ImmutableHashSet<string> tests)
        {
            if (symbol is null)
            {
                return;
            }

            var definition = ReachableSymbolSet.NormalizeDefinition(symbol);

            if (!IsDeclaredInThisCompilation(definition))
            {
                return;
            }

            if (!_attribution.TryGetValue(definition, out var known))
            {
                _attribution.Add(definition, tests);
                _pending.Enqueue(definition);

                return;
            }

            var widened = known.Union(tests);

            if (widened.Count == known.Count)
            {
                return;
            }

            _attribution[definition] = widened;
            _pending.Enqueue(definition);
        }

        private void CollectReferences(ISymbol member, HashSet<ISymbol> targets)
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
                    WalkExecutableNode(semanticModel, executableNode, targets);
                }
            }
        }

        private void WalkExecutableNode(
            SemanticModel semanticModel,
            SyntaxNode executableNode,
            HashSet<ISymbol> targets
        )
        {
            foreach (var node in executableNode.DescendantNodesAndSelf(node => node is not AttributeListSyntax))
            {
                _cancellationToken.ThrowIfCancellationRequested();

                HandleNode(semanticModel, node, targets);
            }
        }

        private void HandleNode(SemanticModel semanticModel, SyntaxNode node, HashSet<ISymbol> targets)
        {
            if (node is LocalFunctionStatementSyntax localFunction)
            {
                Record(targets, semanticModel.GetDeclaredSymbol(localFunction, _cancellationToken));
                return;
            }

            if (node is not (ExpressionSyntax or ConstructorInitializerSyntax))
            {
                return;
            }

            var symbolInfo = semanticModel.GetSymbolInfo(node, _cancellationToken);

            if (symbolInfo.Symbol is not null)
            {
                HandleReference(symbolInfo.Symbol, targets);
                return;
            }

            // An unresolved reference, for example an ambiguous method group conversion, is treated
            // generously: recording all candidates can only widen the reachable set, and a member that
            // is wrongly considered reachable costs a missed hint, never a false gap report.
            foreach (var candidate in symbolInfo.CandidateSymbols)
            {
                HandleReference(candidate, targets);
            }
        }

        private void HandleReference(ISymbol symbol, HashSet<ISymbol> targets)
        {
            Record(targets, symbol);

            foreach (var dispatchTarget in GetDispatchTargets(symbol))
            {
                Record(targets, dispatchTarget);
            }
        }

        /// <summary>
        /// Adds <paramref name="symbol" /> to the outgoing references of the member being inspected, if
        /// it is a member of this compilation at all.
        /// </summary>
        /// <param name="targets">The outgoing references collected so far.</param>
        /// <param name="symbol">The referenced symbol, may be <see langword="null" />.</param>
        private void Record(HashSet<ISymbol> targets, ISymbol? symbol)
        {
            if (symbol is null)
            {
                return;
            }

            var definition = ReachableSymbolSet.NormalizeDefinition(symbol);

            if (IsDeclaredInThisCompilation(definition))
            {
                _ = targets.Add(definition);
            }
        }

        /// <summary>
        /// Adds the members that share a declaration with <paramref name="member" /> and therefore
        /// share its reachability and its attribution: the two halves of a partial method and the
        /// accessors of a property or event, whose bodies are separate declarations of their own.
        /// </summary>
        /// <param name="member">The member that became reachable.</param>
        /// <param name="targets">The outgoing references collected so far.</param>
        private void CollectRelatedMembers(ISymbol member, HashSet<ISymbol> targets)
        {
            switch (member)
            {
                case IMethodSymbol method:
                    Record(targets, method.PartialDefinitionPart);
                    Record(targets, method.PartialImplementationPart);
                    Record(targets, method.AssociatedSymbol);
                    break;

                case IPropertySymbol property:
                    Record(targets, property.GetMethod);
                    Record(targets, property.SetMethod);
                    break;

                case IEventSymbol @event:
                    Record(targets, @event.AddMethod);
                    Record(targets, @event.RemoveMethod);
                    Record(targets, @event.RaiseMethod);
                    break;

                default:
                    break;
            }
        }

        /// <summary>
        /// Returns the dispatch targets of <paramref name="symbol" />, computing them once per referenced
        /// symbol and reusing them for every member that references it.
        /// </summary>
        /// <param name="symbol">The referenced symbol, in the form the semantic model resolved it.</param>
        /// <returns>The implementations and overrides a call through the symbol can end up in.</returns>
        private ISymbol[] GetDispatchTargets(ISymbol symbol)
        {
            if (_dispatchTargets.TryGetValue(symbol, out var cached))
            {
                return cached;
            }

            cached = FindDispatchTargets(symbol);
            _dispatchTargets.Add(symbol, cached);

            return cached;
        }

        /// <summary>
        /// Approximates virtual and interface dispatch for <paramref name="symbol" />.
        /// </summary>
        /// <param name="symbol">The referenced symbol, in the form the semantic model resolved it.</param>
        /// <returns>The implementations and overrides declared in this compilation.</returns>
        /// <remarks>
        /// A call through an abstraction can end up in any implementation, and which one it is cannot be
        /// decided at compile time. Every implementation declared in this compilation is therefore
        /// considered reachable, and inherits the attribution of the reference that led here. The search
        /// is syntactic and stays inside this compilation: overrides in other assemblies, and
        /// implementations chosen through configuration or dependency injection, are outside of what a
        /// single compilation can observe.
        /// </remarks>
        private ISymbol[] FindDispatchTargets(ISymbol symbol)
        {
            var containingType = symbol.ContainingType;

            if (containingType is null)
            {
                return _noTargets;
            }

            if (containingType.TypeKind == TypeKind.Interface)
            {
                return FindInterfaceImplementations(symbol, containingType);
            }

            if (symbol.IsVirtual || symbol.IsAbstract || symbol.IsOverride)
            {
                return FindOverrides(symbol);
            }

            return _noTargets;
        }

        private ISymbol[] FindInterfaceImplementations(ISymbol interfaceMember, INamedTypeSymbol interfaceType)
        {
            var implementations = new List<ISymbol>();

            foreach (var type in GetDeclaredTypes().Where(candidate => Implements(candidate, interfaceType)))
            {
                _cancellationToken.ThrowIfCancellationRequested();

                var implementation = type.FindImplementationForInterfaceMember(interfaceMember);

                if (implementation is not null)
                {
                    implementations.Add(implementation);
                }
            }

            return implementations.Count == 0 ? _noTargets : [.. implementations];
        }

        private ISymbol[] FindOverrides(ISymbol member)
        {
            var overrides = new List<ISymbol>();

            foreach (var type in GetDeclaredTypes())
            {
                _cancellationToken.ThrowIfCancellationRequested();

                overrides.AddRange(type.GetMembers(member.Name).Where(candidate => Overrides(candidate, member)));
            }

            return overrides.Count == 0 ? _noTargets : [.. overrides];
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
