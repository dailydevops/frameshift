namespace NetEvolve.FrameShift.Analyzers;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NetEvolve.FrameShift.Configuration;
using NetEvolve.FrameShift.Diagnostics;
using NetEvolve.FrameShift.Equivalence;
using NetEvolve.FrameShift.Mutations;
using NetEvolve.FrameShift.Reachability;
using NetEvolve.FrameShift.TestSurface;

/// <summary>
/// The production side of FrameShift: it reports the mutation points of the analysed production
/// compilation that no test can reach, without ever executing a test.
/// </summary>
/// <remarks>
/// <para>
/// The analysis is split into two passes because neither compilation can see the whole picture. A test
/// compilation references the production assembly as metadata only, so it can name the production
/// members its <c>[Test]</c> methods touch, but it owns no production syntax tree it could mutate. A
/// production compilation owns every syntax tree and the complete call graph, but it cannot see a
/// single test. The first pass therefore records the touched members as a test-surface manifest, and
/// this second pass consumes that manifest as an <see cref="AdditionalText" />.
/// </para>
/// <para>
/// Per compilation this analyzer reads and merges the manifests, seeds the reachable set from the
/// recorded member ids, closes it transitively over the production call graph, and then walks every
/// syntax tree once: each candidate mutation is verified to still compile, classified as trivial or
/// meaningful, and finally attributed to its enclosing member. A meaningful mutant inside an
/// unreachable member is a testing gap (<c>FSH0001</c>), a mutant that cannot change observable
/// behaviour is informational (<c>FSH0002</c>), a manifest that cannot be used is reported as
/// such (<c>FSH0003</c>), and a meaningful mutant inside a member that a single test case reaches is a
/// weak-test-data hint (<c>FSH0006</c>).
/// </para>
/// <para>
/// The three mutation diagnostics are mutually exclusive, in this order of precedence:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// A mutant that cannot change observable behaviour reports <c>FSH0002</c> and nothing else. No test
/// data can ever distinguish it from the original code, so counting the test cases that reach it would
/// only add noise to a finding that is not a gap in the first place. This is independent of
/// <see cref="FrameShiftOptions.ReportTrivialMutants" />: suppressing the informational
/// <c>FSH0002</c> output must not turn the very same mutant into an <c>FSH0006</c> hint.
/// </description>
/// </item>
/// <item>
/// <description>
/// A meaningful mutant inside an unreachable member reports <c>FSH0001</c> and never <c>FSH0006</c>.
/// <c>FSH0006</c> refines "covered" into "thinly covered", which presupposes coverage; a member no test
/// reaches has no test case to count.
/// </description>
/// </item>
/// <item>
/// <description>
/// A meaningful mutant inside a reachable member reports <c>FSH0006</c> when exactly one test case
/// reaches that member, see <see cref="TestCaseAttribution" />.
/// </description>
/// </item>
/// </list>
/// <para>
/// <c>FSH0006</c> has no MSBuild switch of its own, deliberately: it is informational and the standard
/// <c>dotnet_diagnostic.FSH0006.severity</c> configuration already silences it. Its cost is paid lazily
/// instead, because the per-test attribution is built the first time a mutation point actually asks for
/// it and never for a compilation without a single exactly-one-case test.
/// </para>
/// <para>
/// A project without a manifest stays completely silent, because it has not opted in; the build assets
/// of the package warn about the missing setup instead.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MutationCoverageAnalyzer : DiagnosticAnalyzer
{
    private const string UnreadableManifestReason =
        "the content of the additional file is not available to the analyzer";

    private const string UnknownParseErrorReason = "the file is not a well-formed test-surface manifest";

    private const string EmptyManifestReason =
        "the manifest does not record a single referenced production member, so it is empty or stale; "
        + "rebuild the test project to regenerate it";

    private const string UnresolvedManifestReason =
        "none of the production members recorded in the manifest exist in this compilation, so the "
        + "manifest belongs to a different project or is stale; rebuild the test project to regenerate it";

    private static readonly ImmutableArray<DiagnosticDescriptor> _supportedDiagnostics =
    [
        Descriptors.UnreachableMutationPoint,
        Descriptors.TrivialMutant,
        Descriptors.InvalidTestSurfaceManifest,
        Descriptors.SingleTestCaseMutationPoint,
    ];

    /// <summary>
    /// Gets the diagnostics this analyzer can report, which are <c>FSH0001</c>, <c>FSH0002</c>,
    /// <c>FSH0003</c> and <c>FSH0006</c>.
    /// </summary>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => _supportedDiagnostics;

    /// <summary>
    /// Registers the analysis actions. All per-compilation state is created inside the compilation
    /// start action and captured by the registered callbacks, so that the analyzer instance itself
    /// stays stateless and safe to run concurrently.
    /// </summary>
    /// <param name="context">The context to register the actions with.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context" /> is <see langword="null" />.</exception>
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    /// <summary>
    /// Prepares the per-compilation state, meaning the options, the merged manifest, the reachable set
    /// and the shared mutant compiler, and registers the per-file analysis.
    /// </summary>
    /// <param name="context">The compilation start context.</param>
    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var options = FrameShiftOptions.Read(context.Options.AnalyzerConfigOptionsProvider.GlobalOptions);
        if (!options.IsEnabled)
        {
            return;
        }

        var manifestFiles = GetManifestFiles(context.Options.AdditionalFiles);
        if (manifestFiles.IsEmpty)
        {
            // The project has not opted in. The build assets warn about the missing setup, an
            // analyzer diagnostic here would only duplicate that warning for every consumer.
            return;
        }

        var result = ReadManifests(manifestFiles, context.CancellationToken);
        var reachable = ReachabilityClosure.Compute(context.Compilation, result.Manifest, context.CancellationToken);
        var problems = CollectProblems(result, reachable);

        if (!problems.IsEmpty)
        {
            // A compilation start action cannot report diagnostics itself, so the collected manifest
            // problems are handed to the end of the compilation.
            context.RegisterCompilationEndAction(endContext => ReportProblems(endContext, problems));
        }

        if (reachable.IsEmpty)
        {
            // Reporting a gap for every mutation point of the whole compilation would drown the build
            // in warnings and would blame the code for a manifest problem. The real cause is named
            // once by the diagnostics above instead.
            return;
        }

        var compiler = new MutantCompiler(context.Compilation);
        var attribution = new TestCaseAttribution(context.Compilation, result.Manifest);

        context.RegisterSemanticModelAction(modelContext =>
            AnalyzeSemanticModel(modelContext, reachable, attribution, compiler, options)
        );
    }

    /// <summary>
    /// Determines the manifest diagnostics of the compilation, adding the diagnostics that explain an
    /// unusable manifest whenever the closure did not yield a single reachable member.
    /// </summary>
    /// <param name="result">The outcome of reading the manifest files.</param>
    /// <param name="reachable">The reachable set computed from the manifest.</param>
    /// <returns>The diagnostics to report, possibly empty.</returns>
    private static ImmutableArray<Diagnostic> CollectProblems(
        ManifestReadResult result,
        ReachableSymbolSet reachable
    ) => reachable.IsEmpty ? result.Problems.AddRange(CreateUnusableManifestProblems(result)) : result.Problems;

    /// <summary>
    /// Analyses a single syntax tree. The callback runs once per file and may run concurrently with
    /// itself, therefore every piece of mutable state it needs is a local of this method.
    /// </summary>
    /// <param name="context">The semantic model context of the analysed file.</param>
    /// <param name="reachable">The reachable set computed once for the whole compilation.</param>
    /// <param name="attribution">The per-test attribution shared by all files, which memoises its results.</param>
    /// <param name="compiler">The mutant compiler shared by all files, which memoises its results.</param>
    /// <param name="options">The configuration of the current compilation.</param>
    private static void AnalyzeSemanticModel(
        SemanticModelAnalysisContext context,
        ReachableSymbolSet reachable,
        TestCaseAttribution attribution,
        MutantCompiler compiler,
        FrameShiftOptions options
    )
    {
        var cancellationToken = context.CancellationToken;
        var semanticModel = context.SemanticModel;
        var tree = semanticModel.SyntaxTree;
        var root = tree.GetRoot(cancellationToken);
        var states = new Dictionary<ISymbol, MemberState>(SymbolEqualityComparer.Default);

        foreach (var mutation in MutantGenerator.CreateMutations(root, semanticModel, options, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var member = semanticModel.GetEnclosingSymbol(mutation.Location.SourceSpan.Start, cancellationToken);
            if (member is null)
            {
                continue;
            }

            var state = GetOrCreateState(states, member, reachable, attribution);
            if (!state.TryConsume(options.MaxMutantsPerMember))
            {
                // The member has exhausted its budget. Skipping before the expensive verification
                // keeps a pathological file from stalling the build.
                continue;
            }

            AnalyzeMutation(context, mutation, tree, state, compiler, options);
        }
    }

    /// <summary>
    /// Verifies, classifies and finally reports a single candidate mutation.
    /// </summary>
    /// <param name="context">The semantic model context of the analysed file.</param>
    /// <param name="mutation">The candidate mutation.</param>
    /// <param name="tree">The unmutated syntax tree the mutation belongs to.</param>
    /// <param name="state">The state of the member enclosing the mutation point.</param>
    /// <param name="compiler">The mutant compiler shared by the whole compilation.</param>
    /// <param name="options">The configuration of the current compilation.</param>
    private static void AnalyzeMutation(
        SemanticModelAnalysisContext context,
        Mutation mutation,
        SyntaxTree tree,
        MemberState state,
        MutantCompiler compiler,
        FrameShiftOptions options
    )
    {
        var cancellationToken = context.CancellationToken;

        if (
            options.VerifyMutantCompilation
            && compiler.Verify(mutation, tree, cancellationToken) != MutantViability.Viable
        )
        {
            // A mutant that does not build is not a mutation of the program at all, so it can neither
            // be a gap nor a trivial mutant.
            return;
        }

        var verdict = EquivalenceClassifier.Classify(mutation, context.SemanticModel, cancellationToken);
        if (verdict.IsTrivial)
        {
            if (options.ReportTrivialMutants)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Descriptors.TrivialMutant,
                        mutation.Location,
                        mutation.DisplayName,
                        verdict.Reason
                    )
                );
            }

            // A mutant that cannot change observable behaviour is not made interesting by weak test
            // data, so the single-test-case hint is suppressed here as well, whether or not the
            // informational diagnostic above was reported.
            return;
        }

        if (!state.IsReachable)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Descriptors.UnreachableMutationPoint, mutation.Location, mutation.DisplayName)
            );

            return;
        }

        ReportSingleTestCase(context, mutation, state, cancellationToken);
    }

    /// <summary>
    /// Reports <c>FSH0006</c> for a meaningful mutant inside a reachable member that exactly one exact
    /// test case reaches.
    /// </summary>
    /// <param name="context">The semantic model context of the analysed file.</param>
    /// <param name="mutation">The candidate mutation, already known to be viable and meaningful.</param>
    /// <param name="state">The state of the member enclosing the mutation point.</param>
    /// <param name="cancellationToken">A token observed while the attribution is built.</param>
    private static void ReportSingleTestCase(
        SemanticModelAnalysisContext context,
        Mutation mutation,
        MemberState state,
        CancellationToken cancellationToken
    )
    {
        var testMethodId = state.GetSingleTestMethodId(cancellationToken);

        if (testMethodId is null)
        {
            return;
        }

        context.ReportDiagnostic(
            Diagnostic.Create(
                Descriptors.SingleTestCaseMutationPoint,
                mutation.Location,
                mutation.DisplayName,
                TestCaseAttribution.Describe(testMethodId)
            )
        );
    }

    /// <summary>
    /// Returns the state of <paramref name="member" />, computing its reachability on first use so that
    /// the lookup is paid once per member instead of once per mutation.
    /// </summary>
    /// <param name="states">The per-file state map.</param>
    /// <param name="member">The member enclosing the current mutation point.</param>
    /// <param name="reachable">The reachable set computed once for the whole compilation.</param>
    /// <param name="attribution">The per-test attribution of the whole compilation.</param>
    /// <returns>The state of the member.</returns>
    private static MemberState GetOrCreateState(
        Dictionary<ISymbol, MemberState> states,
        ISymbol member,
        ReachableSymbolSet reachable,
        TestCaseAttribution attribution
    )
    {
        if (!states.TryGetValue(member, out var state))
        {
            state = new MemberState(member, reachable.ContainsEnclosing(member), attribution);
            states.Add(member, state);
        }

        return state;
    }

    /// <summary>
    /// Selects the additional files that are test-surface manifests.
    /// </summary>
    /// <param name="additionalFiles">All additional files visible to the analyzer.</param>
    /// <returns>The manifest files, possibly empty.</returns>
    private static ImmutableArray<AdditionalText> GetManifestFiles(ImmutableArray<AdditionalText> additionalFiles) =>
        [.. additionalFiles.Where(IsManifestFile)];

    /// <summary>
    /// Determines whether <paramref name="file" /> is named like a test-surface manifest.
    /// </summary>
    /// <param name="file">The additional file to inspect.</param>
    /// <returns><see langword="true" /> if the file is a manifest; otherwise <see langword="false" />.</returns>
    private static bool IsManifestFile(AdditionalText file) =>
        file is not null
        && file.Path.EndsWith(TestSurfaceManifestFormat.FileSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads every manifest file and merges the successfully parsed ones by unioning their id sets.
    /// </summary>
    /// <param name="files">The manifest files to read.</param>
    /// <param name="cancellationToken">A token observed between the files.</param>
    /// <returns>The merged manifest, the files it was built from and one diagnostic per unusable file.</returns>
    /// <remarks>
    /// The merge unites the per-test entries instead of the flat id sets, so that the attribution behind
    /// <c>FSH0006</c> still knows which test reached what after several manifests were combined. The
    /// derived <see cref="TestSurfaceManifest.TestMethodIds" /> and
    /// <see cref="TestSurfaceManifest.ReferencedMemberIds" /> are exactly the unions the previous flat
    /// merge produced.
    /// </remarks>
    private static ManifestReadResult ReadManifests(
        ImmutableArray<AdditionalText> files,
        CancellationToken cancellationToken
    )
    {
        var manifests = ImmutableArray.CreateBuilder<TestSurfaceManifest>(files.Length);
        var parsedFiles = ImmutableArray.CreateBuilder<AdditionalText>(files.Length);
        var problems = ImmutableArray.CreateBuilder<Diagnostic>();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = file.GetText(cancellationToken);
            if (text is null)
            {
                problems.Add(CreateManifestProblem(file, UnreadableManifestReason));

                continue;
            }

            if (!TestSurfaceManifestReader.TryRead(text, out var manifest, out var error))
            {
                problems.Add(CreateManifestProblem(file, error ?? UnknownParseErrorReason));

                continue;
            }

            manifests.Add(manifest);
            parsedFiles.Add(file);
        }

        var merged = TestSurfaceManifest.Merge(manifests.ToImmutable());

        return new ManifestReadResult(merged, parsedFiles.ToImmutable(), problems.ToImmutable());
    }

    /// <summary>
    /// Creates one diagnostic per successfully parsed manifest that did not contribute a single
    /// reachable member, explaining which of the two possible causes applies.
    /// </summary>
    /// <param name="result">The outcome of reading the manifest files.</param>
    /// <returns>The diagnostics to report, possibly empty.</returns>
    private static ImmutableArray<Diagnostic> CreateUnusableManifestProblems(ManifestReadResult result)
    {
        if (result.ParsedFiles.IsEmpty)
        {
            // Every file already produced its own parse diagnostic.
            return [];
        }

        var reason = result.Manifest.ReferencedMemberIds.IsEmpty ? EmptyManifestReason : UnresolvedManifestReason;

        return [.. result.ParsedFiles.Select(file => CreateManifestProblem(file, reason))];
    }

    /// <summary>
    /// Creates an <c>FSH0003</c> diagnostic for <paramref name="file" />.
    /// </summary>
    /// <param name="file">The manifest file the problem belongs to.</param>
    /// <param name="reason">The explanation shown as the second message argument.</param>
    /// <returns>The diagnostic, located on the manifest file itself.</returns>
    private static Diagnostic CreateManifestProblem(AdditionalText file, string reason) =>
        Diagnostic.Create(
            Descriptors.InvalidTestSurfaceManifest,
            Location.Create(file.Path, default, default),
            file.Path,
            reason
        );

    /// <summary>
    /// Reports the manifest problems that were collected while the compilation was set up.
    /// </summary>
    /// <param name="context">The context of the finished compilation.</param>
    /// <param name="problems">The diagnostics to report.</param>
    private static void ReportProblems(CompilationAnalysisContext context, ImmutableArray<Diagnostic> problems)
    {
        foreach (var problem in problems)
        {
            context.ReportDiagnostic(problem);
        }
    }

    /// <summary>
    /// The mutable per-member bookkeeping of a single file: whether the member is reachable and how
    /// many of its mutations have already been considered.
    /// </summary>
    private sealed class MemberState
    {
        private readonly ISymbol _member;
        private readonly TestCaseAttribution _attribution;

        private int _considered;
        private bool _isAttributed;
        private string? _singleTestMethodId;

        /// <summary>
        /// Initializes a new instance of the <see cref="MemberState" /> class.
        /// </summary>
        /// <param name="member">The member enclosing the mutation points this state belongs to.</param>
        /// <param name="isReachable">Whether the member, or a member enclosing it, is reachable.</param>
        /// <param name="attribution">The per-test attribution of the whole compilation.</param>
        public MemberState(ISymbol member, bool isReachable, TestCaseAttribution attribution)
        {
            _member = member;
            _attribution = attribution;
            IsReachable = isReachable;
        }

        /// <summary>
        /// Gets a value indicating whether the member, or a member enclosing it, is reachable from a
        /// discovered test.
        /// </summary>
        public bool IsReachable { get; }

        /// <summary>
        /// Consumes one unit of the mutation budget of the member.
        /// </summary>
        /// <param name="limit">The maximum number of mutations considered for one member.</param>
        /// <returns>
        /// <see langword="true" /> if the mutation may be analysed; <see langword="false" /> if the
        /// budget is exhausted.
        /// </returns>
        public bool TryConsume(int limit)
        {
            if (_considered >= limit)
            {
                return false;
            }

            _considered++;

            return true;
        }

        /// <summary>
        /// Returns the documentation comment id of the only test method that reaches the member, if the
        /// member is reached by exactly one test case.
        /// </summary>
        /// <param name="cancellationToken">A token observed while the attribution is built.</param>
        /// <returns>
        /// The id of the single test method, or <see langword="null" /> when the sum of the case counts
        /// reaching the member is not exactly one or when any contributing count is a lower bound.
        /// </returns>
        /// <remarks>
        /// The answer is a property of the member, not of the mutation point, so it is computed once per
        /// member. This instance is a local of a single semantic model callback, therefore the memoisation
        /// needs no synchronisation of its own.
        /// </remarks>
        public string? GetSingleTestMethodId(CancellationToken cancellationToken)
        {
            if (!_isAttributed)
            {
                _singleTestMethodId = _attribution.FindSingleTestMethodId(_member, cancellationToken);
                _isAttributed = true;
            }

            return _singleTestMethodId;
        }
    }

    /// <summary>
    /// The outcome of reading the test-surface manifests of a compilation.
    /// </summary>
    private sealed class ManifestReadResult
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ManifestReadResult" /> class.
        /// </summary>
        /// <param name="manifest">The union of all successfully parsed manifests.</param>
        /// <param name="parsedFiles">The files that were parsed successfully.</param>
        /// <param name="problems">One diagnostic per file that could not be used.</param>
        public ManifestReadResult(
            TestSurfaceManifest manifest,
            ImmutableArray<AdditionalText> parsedFiles,
            ImmutableArray<Diagnostic> problems
        )
        {
            Manifest = manifest;
            ParsedFiles = parsedFiles;
            Problems = problems;
        }

        /// <summary>
        /// Gets the union of all successfully parsed manifests.
        /// </summary>
        public TestSurfaceManifest Manifest { get; }

        /// <summary>
        /// Gets the files that were parsed successfully.
        /// </summary>
        public ImmutableArray<AdditionalText> ParsedFiles { get; }

        /// <summary>
        /// Gets one diagnostic per file that could not be used.
        /// </summary>
        public ImmutableArray<Diagnostic> Problems { get; }
    }

    /// <summary>
    /// Answers, per production member, whether exactly one test case reaches it, which is the trigger of
    /// <c>FSH0006</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule is an aggregation over all test methods reaching the member: the sum of their case counts
    /// has to be exactly one and every contributing count has to be exact. That leaves precisely one
    /// shape, which is what this class exploits instead of materialising the sum. A count that is exact
    /// and zero contributes nothing at all and can be ignored; a count that is exact and greater than one,
    /// or a lower bound of any value, makes the aggregate fail as soon as it contributes at all. So the
    /// tests are partitioned once: the exactly-one-case tests are kept individually, everything that can
    /// only disqualify a member is folded into a single seed set. A member is reported when no
    /// disqualifying test reaches it and exactly one of the exactly-one-case tests does.
    /// </para>
    /// <para>
    /// Which members a single test reaches is again a transitive question over the production call graph,
    /// so one closure per kept test is computed, plus one for the disqualifying seeds. That is the price of
    /// attributing a member to a test at all, and it is paid lazily: nothing is computed for a compilation
    /// without an exactly-one-case test, and nothing is computed until the first meaningful mutant inside a
    /// reachable member asks for it.
    /// </para>
    /// <para>
    /// Instances are shared by the concurrently running per-file callbacks, so the lazy build is guarded by
    /// a lock. A build that is cancelled publishes nothing and is simply retried by the next caller.
    /// </para>
    /// </remarks>
    private sealed class TestCaseAttribution
    {
        /// <summary>
        /// The prefix a documentation comment id of a method carries.
        /// </summary>
        private const string MethodIdPrefix = "M:";

        /// <summary>
        /// The empty test method id set of the throwaway manifests the closures are seeded with. Only the
        /// referenced members matter there, the closure never looks at the test ids.
        /// </summary>
        private static readonly ImmutableHashSet<string> _noTestMethodIds = ImmutableHashSet.Create<string>(
            StringComparer.Ordinal
        );

        private readonly Compilation _compilation;
        private readonly ImmutableArray<SingleCaseTest> _singleCaseTests;
        private readonly ImmutableHashSet<string> _disqualifyingMemberIds;
        private readonly object _gate = new object();

        private ImmutableArray<ReachingTest> _reachingTests;
        private ReachableSymbolSet? _disqualified;

        /// <summary>
        /// Initializes a new instance of the <see cref="TestCaseAttribution" /> class by partitioning the
        /// recorded test methods into the ones that can trigger <c>FSH0006</c> and the ones that can only
        /// suppress it.
        /// </summary>
        /// <param name="compilation">The production compilation the closures are computed over.</param>
        /// <param name="manifest">The merged manifest holding the per-test entries.</param>
        public TestCaseAttribution(Compilation compilation, TestSurfaceManifest manifest)
        {
            _compilation = compilation;

            var singleCaseTests = ImmutableArray.CreateBuilder<SingleCaseTest>();
            var disqualifyingMemberIds = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);

            foreach (var references in manifest.ReferencesByTest.Where(entry => !entry.Value.IsEmpty))
            {
                var count = GetCaseCount(manifest, references.Key);

                if (count.IsExact && count.Value == 1)
                {
                    singleCaseTests.Add(new SingleCaseTest(references.Key, references.Value));
                }
                else if (!count.IsExact || count.Value > 1)
                {
                    disqualifyingMemberIds.UnionWith(references.Value);
                }
            }

            _singleCaseTests = singleCaseTests.ToImmutable();
            _disqualifyingMemberIds = disqualifyingMemberIds.ToImmutable();
        }

        /// <summary>
        /// Turns the documentation comment id of a test method into the form a message shows.
        /// </summary>
        /// <param name="testMethodId">The documentation comment id of the test method.</param>
        /// <returns>The id without its <c>M:</c> prefix.</returns>
        public static string Describe(string testMethodId) =>
            testMethodId.StartsWith(MethodIdPrefix, StringComparison.Ordinal)
                ? testMethodId.Substring(MethodIdPrefix.Length)
                : testMethodId;

        /// <summary>
        /// Determines the single test method reaching <paramref name="member" />.
        /// </summary>
        /// <param name="member">The member enclosing a mutation point, known to be reachable.</param>
        /// <param name="cancellationToken">A token observed while the attribution is built.</param>
        /// <returns>
        /// The documentation comment id of the only test method whose only test case reaches
        /// <paramref name="member" />, or <see langword="null" /> when no such attribution exists.
        /// </returns>
        public string? FindSingleTestMethodId(ISymbol member, CancellationToken cancellationToken)
        {
            if (_singleCaseTests.IsEmpty)
            {
                return null;
            }

            EnsureBuilt(cancellationToken);

            if (_disqualified!.ContainsEnclosing(member))
            {
                return null;
            }

            string? single = null;

            foreach (var test in _reachingTests.Where(test => test.Closure.ContainsEnclosing(member)))
            {
                if (single is not null)
                {
                    // Two test cases reach the member, so its inputs are not a single combination.
                    return null;
                }

                single = test.TestMethodId;
            }

            return single;
        }

        /// <summary>
        /// Computes the closures of the partitioned tests on first use.
        /// </summary>
        /// <param name="cancellationToken">A token observed between the closures.</param>
        private void EnsureBuilt(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_disqualified is not null)
                {
                    return;
                }

                var reachingTests = ImmutableArray.CreateBuilder<ReachingTest>(_singleCaseTests.Length);

                foreach (var test in _singleCaseTests)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var closure = Close(test.ReferencedMemberIds, cancellationToken);

                    if (!closure.IsEmpty)
                    {
                        reachingTests.Add(new ReachingTest(test.TestMethodId, closure));
                    }
                }

                var disqualified = Close(_disqualifyingMemberIds, cancellationToken);

                _reachingTests = reachingTests.ToImmutable();

                // Published last, because it is the flag every reader checks.
                _disqualified = disqualified;
            }
        }

        /// <summary>
        /// Closes one seed set over the production call graph.
        /// </summary>
        /// <param name="referencedMemberIds">The documentation comment ids seeding the closure.</param>
        /// <param name="cancellationToken">A token observed while walking.</param>
        /// <returns>The reachable set of the seed, empty for an empty seed.</returns>
        private ReachableSymbolSet Close(
            ImmutableHashSet<string> referencedMemberIds,
            CancellationToken cancellationToken
        ) =>
            referencedMemberIds.IsEmpty
                ? ReachableSymbolSet.Empty
                : ReachabilityClosure.Compute(
                    _compilation,
                    new TestSurfaceManifest(_noTestMethodIds, referencedMemberIds),
                    cancellationToken
                );

        /// <summary>
        /// Reads the case count of one test method.
        /// </summary>
        /// <param name="manifest">The merged manifest holding the per-test entries.</param>
        /// <param name="testMethodId">The documentation comment id of the test method.</param>
        /// <returns>
        /// The recorded count, or a lower bound of one for a test method the manifest lists without a
        /// count, which is the conservative reading: an unknown number of cases must never trigger the
        /// diagnostic.
        /// </returns>
        private static TestCaseCount GetCaseCount(TestSurfaceManifest manifest, string testMethodId) =>
            manifest.TestCaseCounts.TryGetValue(testMethodId, out var count) ? count : TestCaseCount.AtLeast(1);

        /// <summary>
        /// A test method declaring exactly one test case, together with the production members it
        /// references directly.
        /// </summary>
        private sealed class SingleCaseTest
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="SingleCaseTest" /> class.
            /// </summary>
            /// <param name="testMethodId">The documentation comment id of the test method.</param>
            /// <param name="referencedMemberIds">The ids of the members the test references directly.</param>
            public SingleCaseTest(string testMethodId, ImmutableHashSet<string> referencedMemberIds)
            {
                TestMethodId = testMethodId;
                ReferencedMemberIds = referencedMemberIds;
            }

            /// <summary>
            /// Gets the documentation comment id of the test method.
            /// </summary>
            public string TestMethodId { get; }

            /// <summary>
            /// Gets the ids of the production members the test method references directly.
            /// </summary>
            public ImmutableHashSet<string> ReferencedMemberIds { get; }
        }

        /// <summary>
        /// A test method declaring exactly one test case, together with the members that case reaches.
        /// </summary>
        private sealed class ReachingTest
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="ReachingTest" /> class.
            /// </summary>
            /// <param name="testMethodId">The documentation comment id of the test method.</param>
            /// <param name="closure">The members the test method reaches transitively.</param>
            public ReachingTest(string testMethodId, ReachableSymbolSet closure)
            {
                TestMethodId = testMethodId;
                Closure = closure;
            }

            /// <summary>
            /// Gets the documentation comment id of the test method.
            /// </summary>
            public string TestMethodId { get; }

            /// <summary>
            /// Gets the members the test method reaches transitively.
            /// </summary>
            public ReachableSymbolSet Closure { get; }
        }
    }
}
