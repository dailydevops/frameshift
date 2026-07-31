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
/// behaviour is informational (<c>FSH0002</c>), and a manifest that cannot be used is reported as
/// such (<c>FSH0003</c>).
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
    ];

    /// <summary>
    /// Gets the diagnostics this analyzer can report, which are <c>FSH0001</c>, <c>FSH0002</c> and
    /// <c>FSH0003</c>.
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

        context.RegisterSemanticModelAction(modelContext =>
            AnalyzeSemanticModel(modelContext, reachable, compiler, options)
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
    /// <param name="compiler">The mutant compiler shared by all files, which memoises its results.</param>
    /// <param name="options">The configuration of the current compilation.</param>
    private static void AnalyzeSemanticModel(
        SemanticModelAnalysisContext context,
        ReachableSymbolSet reachable,
        MutantCompiler compiler,
        FrameShiftOptions options
    )
    {
        var cancellationToken = context.CancellationToken;
        var semanticModel = context.SemanticModel;
        var tree = semanticModel.SyntaxTree;
        var root = tree.GetRoot(cancellationToken);
        var states = new Dictionary<ISymbol, MemberState>(SymbolEqualityComparer.Default);

        foreach (var mutation in MutantGenerator.CreateMutations(root, semanticModel, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var member = semanticModel.GetEnclosingSymbol(mutation.Location.SourceSpan.Start, cancellationToken);
            if (member is null)
            {
                continue;
            }

            var state = GetOrCreateState(states, member, reachable);
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

            return;
        }

        if (!state.IsReachable)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Descriptors.UnreachableMutationPoint, mutation.Location, mutation.DisplayName)
            );
        }
    }

    /// <summary>
    /// Returns the state of <paramref name="member" />, computing its reachability on first use so that
    /// the lookup is paid once per member instead of once per mutation.
    /// </summary>
    /// <param name="states">The per-file state map.</param>
    /// <param name="member">The member enclosing the current mutation point.</param>
    /// <param name="reachable">The reachable set computed once for the whole compilation.</param>
    /// <returns>The state of the member.</returns>
    private static MemberState GetOrCreateState(
        Dictionary<ISymbol, MemberState> states,
        ISymbol member,
        ReachableSymbolSet reachable
    )
    {
        if (!states.TryGetValue(member, out var state))
        {
            state = new MemberState(reachable.ContainsEnclosing(member));
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
    private static ManifestReadResult ReadManifests(
        ImmutableArray<AdditionalText> files,
        CancellationToken cancellationToken
    )
    {
        var testMethodIds = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var referencedMemberIds = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
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

            testMethodIds.UnionWith(manifest.TestMethodIds);
            referencedMemberIds.UnionWith(manifest.ReferencedMemberIds);
            parsedFiles.Add(file);
        }

        var merged = new TestSurfaceManifest(testMethodIds.ToImmutable(), referencedMemberIds.ToImmutable());

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
        private int _considered;

        /// <summary>
        /// Initializes a new instance of the <see cref="MemberState" /> class.
        /// </summary>
        /// <param name="isReachable">Whether the member, or a member enclosing it, is reachable.</param>
        public MemberState(bool isReachable) => IsReachable = isReachable;

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
}
