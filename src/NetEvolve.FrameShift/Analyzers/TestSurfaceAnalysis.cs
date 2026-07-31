namespace NetEvolve.FrameShift.Analyzers;

using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using NetEvolve.FrameShift.Configuration;
using NetEvolve.FrameShift.Diagnostics;
using NetEvolve.FrameShift.TestSurface;

/// <summary>
/// The framework-neutral test-side analysis of FrameShift. A framework-specific analyzer hands its
/// <see cref="ITestFrameworkProbe" /> to <see cref="Execute(CompilationAnalysisContext, ITestFrameworkProbe)" />;
/// everything after the probe is shared by every supported test framework.
/// </summary>
/// <remarks>
/// <para>
/// The analysis switches itself off as early and as completely as it can. It does nothing unless the
/// probe recognises its framework AND at least one test method is actually discovered. Recognising no
/// test is treated as "this analysis has no authority over this compilation", not as "this compilation
/// has no tests": a project whose tests cannot be seen must never be judged, because every judgement
/// would be a false one. In that state no diagnostic of any kind is produced, not even about a manifest.
/// </para>
/// <para>
/// That rule is what keeps several framework analyzers side by side harmless: each stays silent on the
/// compilations that are not its own.
/// </para>
/// <para>
/// A test project may of course use more than one framework at the same time, and then more than one
/// analyzer is legitimately awake on the very same compilation. There is still only one test-surface
/// manifest, so the manifest is handled once and only once, by the framework that comes first in
/// <see cref="TestFrameworkProbeRegistry.All" /> among those that are awake — awake meaning that the
/// probe recognises the framework AND at least one of its test methods is discovered. Every other
/// framework skips the manifest entirely, so <c>FSH0003</c> is reported once instead of once per
/// referenced framework.
/// </para>
/// <para>
/// The manifest is compared against the UNION of the test surfaces of all awake frameworks, never
/// against the surface of the leading one alone. A mixed project records the tests of every framework in
/// its single manifest, so judging it by one framework's view would report it as stale purely because
/// the other framework's tests are invisible from there. The union cannot double-count: it is a set of
/// documentation comment ids, so a test method two awake frameworks both recognise contributes the very
/// same id twice and therefore once.
/// </para>
/// <para>
/// <c>FSH0004</c> is raised per framework, because it names an individual test method and every
/// framework sees its own set of them — but it is reported at most once per test method. Two awake
/// frameworks may describe overlapping sets of test methods, and then only the framework that comes
/// first in <see cref="TestFrameworkProbeRegistry.All" /> among those that see a given method reports
/// it; every later one leaves it alone. The rule deduplicates by what the developer would actually read
/// in the error list — the reported location together with the reported symbol — and deliberately not by
/// probe, framework name or framework version, because the point is that one line of source code is
/// complained about once.
/// </para>
/// <para>
/// That overlap is the ordinary case rather than an exotic one, because a probe identifies a framework
/// <em>version</em>: one framework can be split across two probes, as xUnit is, whose registry entries
/// sit next to each other and which can well recognise the same test method. Without the rule above, a
/// project that references both xUnit major versions would hear about each of its tests twice, once per
/// version, although there is only one test. The same holds — more rarely — for a method that carries
/// the test attributes of two genuinely different frameworks at once.
/// </para>
/// <para>
/// Determining what an earlier framework reports means collecting its <c>FSH0004</c> candidates as well.
/// That costs nothing in the common case, because a project using a single framework has exactly one
/// awake framework and there is nothing ahead of it to ask; only a project that really does mix
/// frameworks pays for the extra walk, and only in the analyzers that are not the first awake one.
/// </para>
/// </remarks>
internal static class TestSurfaceAnalysis
{
    /// <summary>
    /// Runs the test-side analysis for the framework <paramref name="probe" /> detects. Reports
    /// <c>FSH0004</c> for the tests of that framework that no earlier awake framework already reports,
    /// and — only when the framework leads the manifest comparison — <c>FSH0003</c> for the manifest of
    /// the whole project.
    /// </summary>
    /// <param name="context">The context of the analyzed compilation.</param>
    /// <param name="probe">The probe detecting the test framework.</param>
    public static void Execute(CompilationAnalysisContext context, ITestFrameworkProbe probe)
    {
        var options = FrameShiftOptions.Read(context.Options.AnalyzerConfigOptionsProvider.GlobalOptions);

        if (!options.IsEnabled)
        {
            return;
        }

        var recognizer = probe.TryCreateRecognizer(context.Compilation);

        if (recognizer is null)
        {
            return;
        }

        var testMethods = TestMethodDiscovery.FindTestMethods(
            context.Compilation,
            recognizer,
            context.CancellationToken
        );

        if (testMethods.IsEmpty)
        {
            return;
        }

        var awake = FindAwakeFrameworks(context);

        ReportTestsWithoutProductionReference(context, probe, recognizer, awake);

        if (!LeadsManifestComparison(probe, awake))
        {
            return;
        }

        CompareWithManifestOnDisk(context, CollectUnion(context, probe, recognizer, awake));
    }

    /// <summary>
    /// Determines which of the registered frameworks are awake on the analyzed compilation, meaning that
    /// their probe recognises them and that at least one of their test methods is actually discovered.
    /// </summary>
    /// <param name="context">The context of the analyzed compilation.</param>
    /// <returns>
    /// The awake frameworks together with their recognisers, in the order of
    /// <see cref="TestFrameworkProbeRegistry.All" />.
    /// </returns>
    private static ImmutableArray<AwakeFramework> FindAwakeFrameworks(CompilationAnalysisContext context)
    {
        var builder = ImmutableArray.CreateBuilder<AwakeFramework>();

        foreach (var candidate in TestFrameworkProbeRegistry.All)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var recognizer = candidate.TryCreateRecognizer(context.Compilation);

            if (recognizer is null)
            {
                continue;
            }

            var testMethods = TestMethodDiscovery.FindTestMethods(
                context.Compilation,
                recognizer,
                context.CancellationToken
            );

            if (!testMethods.IsEmpty)
            {
                builder.Add(new AwakeFramework(candidate, recognizer));
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Determines whether <paramref name="probe" /> is the one framework that handles the manifest, which
    /// is the first awake one in the order of <see cref="TestFrameworkProbeRegistry.All" />.
    /// </summary>
    /// <param name="probe">The probe of the running analyzer.</param>
    /// <param name="awake">The awake frameworks, in registry order.</param>
    /// <returns>
    /// <see langword="true" /> if the manifest has to be compared by this run; otherwise
    /// <see langword="false" />.
    /// </returns>
    /// <remarks>
    /// A probe that is not registered at all leads by itself: nothing else would ever look at the
    /// manifest on its behalf, and staying silent would be worse than reporting once.
    /// </remarks>
    private static bool LeadsManifestComparison(ITestFrameworkProbe probe, ImmutableArray<AwakeFramework> awake) =>
        !awake.Any(framework => IsSameFramework(framework.Probe, probe)) || IsSameFramework(awake[0].Probe, probe);

    /// <summary>
    /// Determines whether two probes stand for the same entry of the registry, compared by their type so
    /// that a probe instance created for a test is treated like the registered one.
    /// </summary>
    /// <param name="left">The first probe.</param>
    /// <param name="right">The second probe.</param>
    /// <returns>
    /// <see langword="true" /> if both probes are the same registry entry; otherwise
    /// <see langword="false" />.
    /// </returns>
    /// <remarks>
    /// Two probes of two versions of one framework are two entries and therefore not the same here, and
    /// they must not be: each of them collects a surface of its own, and which of the two leads is decided
    /// by their position in the registry like for any other pair.
    /// </remarks>
    private static bool IsSameFramework(ITestFrameworkProbe left, ITestFrameworkProbe right) =>
        ReferenceEquals(left, right) || left.GetType() == right.GetType();

    /// <summary>
    /// Collects the union of the test surfaces of every awake framework, so that the manifest of a
    /// project using several frameworks is judged by everything it is supposed to contain.
    /// </summary>
    /// <remarks>
    /// The union is built over sets of documentation comment ids, so two awake frameworks that describe the
    /// same test — two versions of one framework, for instance — add the same ids and cannot inflate the
    /// surface. Only the recognisers are deduplicated, and only to avoid walking the running framework
    /// twice.
    /// </remarks>
    /// <param name="context">The context of the analyzed compilation.</param>
    /// <param name="probe">The probe of the running analyzer.</param>
    /// <param name="recognizer">The recogniser of the running analyzer.</param>
    /// <param name="awake">The awake frameworks, in registry order.</param>
    /// <returns>The combined test surface of the compilation.</returns>
    private static TestSurfaceManifest CollectUnion(
        CompilationAnalysisContext context,
        ITestFrameworkProbe probe,
        ITestMethodRecognizer recognizer,
        ImmutableArray<AwakeFramework> awake
    )
    {
        var recognizers = awake
            .Where(framework => !IsSameFramework(framework.Probe, probe))
            .Select(framework => framework.Recognizer)
            .Append(recognizer);

        var testMethodIds = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var referencedMemberIds = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);

        foreach (var current in recognizers)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var collected = TestSurfaceCollector.Collect(context.Compilation, current, context.CancellationToken);

            testMethodIds.UnionWith(collected.TestMethodIds);
            referencedMemberIds.UnionWith(collected.ReferencedMemberIds);
        }

        return new TestSurfaceManifest(testMethodIds.ToImmutable(), referencedMemberIds.ToImmutable());
    }

    /// <summary>
    /// Reports <c>FSH0004</c> once for every test method that references no production member and that no
    /// awake framework ahead of <paramref name="probe" /> reports already.
    /// </summary>
    /// <param name="context">The context of the analyzed compilation.</param>
    /// <param name="probe">The probe of the running analyzer.</param>
    /// <param name="recognizer">The recogniser deciding which methods are test methods.</param>
    /// <param name="awake">The awake frameworks, in registry order.</param>
    private static void ReportTestsWithoutProductionReference(
        CompilationAnalysisContext context,
        ITestFrameworkProbe probe,
        ITestMethodRecognizer recognizer,
        ImmutableArray<AwakeFramework> awake
    )
    {
        var reported = CollectReportsOfPrecedingFrameworks(context, probe, awake);

        var testMethods = TestSurfaceCollector.FindTestsWithoutProductionReference(
            context.Compilation,
            recognizer,
            context.CancellationToken
        );

        foreach (var testMethod in testMethods)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var location = GetIdentifierLocation(testMethod, context.CancellationToken);

            if (!reported.Add(CreateReportKey(location, testMethod)))
            {
                continue;
            }

            context.ReportDiagnostic(
                Diagnostic.Create(
                    Descriptors.TestWithoutProductionReference,
                    location,
                    testMethod.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                )
            );
        }
    }

    /// <summary>
    /// Collects the <c>FSH0004</c> reports that the awake frameworks ahead of <paramref name="probe" /> in
    /// registry order produce, so that the running analyzer can leave those test methods to them.
    /// </summary>
    /// <param name="context">The context of the analyzed compilation.</param>
    /// <param name="probe">The probe of the running analyzer.</param>
    /// <param name="awake">The awake frameworks, in registry order.</param>
    /// <returns>The keys of the reports already produced elsewhere.</returns>
    /// <remarks>
    /// A probe that is not awake at all is not part of the registry — it cannot be, having just discovered
    /// tests of its own — and then nobody reports on its behalf, exactly as with the manifest, so it keeps
    /// every report to itself instead of falling silent.
    /// </remarks>
    private static HashSet<(Location Location, string SymbolId)> CollectReportsOfPrecedingFrameworks(
        CompilationAnalysisContext context,
        ITestFrameworkProbe probe,
        ImmutableArray<AwakeFramework> awake
    )
    {
        var reported = new HashSet<(Location Location, string SymbolId)>();

        if (!awake.Any(framework => IsSameFramework(framework.Probe, probe)))
        {
            return reported;
        }

        foreach (var framework in awake.TakeWhile(candidate => !IsSameFramework(candidate.Probe, probe)))
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var testMethods = TestSurfaceCollector.FindTestsWithoutProductionReference(
                context.Compilation,
                framework.Recognizer,
                context.CancellationToken
            );

            foreach (var testMethod in testMethods)
            {
                var location = GetIdentifierLocation(testMethod, context.CancellationToken);

                _ = reported.Add(CreateReportKey(location, testMethod));
            }
        }

        return reported;
    }

    /// <summary>
    /// Builds the key a <c>FSH0004</c> report is deduplicated by, which is what the developer reads: the
    /// location the diagnostic is anchored at and the test method it names.
    /// </summary>
    /// <param name="location">The location the diagnostic is reported at.</param>
    /// <param name="method">The reported test method.</param>
    /// <returns>The key identifying the report.</returns>
    /// <remarks>
    /// The symbol half of the key is its documentation comment id, the same identity the test-surface
    /// manifest is written in, and the display string of the method wherever no id can be created. Two
    /// probes of one framework resolve their attribute types in different assemblies, so they may well
    /// hand out different attribute symbols for the very same method — the method symbol itself, however,
    /// comes from the analysed compilation and is therefore the same for both.
    /// </remarks>
    private static (Location Location, string SymbolId) CreateReportKey(Location location, IMethodSymbol method)
    {
        var declarationId = DocumentationCommentId.CreateDeclarationId(method.OriginalDefinition);

        return (
            location,
            string.IsNullOrEmpty(declarationId)
                ? method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                : declarationId!
        );
    }

    /// <summary>
    /// Resolves the location of the name of <paramref name="method" />, falling back to the first
    /// declaration location and finally to <see cref="Location.None" />.
    /// </summary>
    /// <param name="method">The method to locate.</param>
    /// <param name="cancellationToken">A token to observe while resolving the declaration.</param>
    /// <returns>The location the diagnostic is reported at.</returns>
    private static Location GetIdentifierLocation(IMethodSymbol method, CancellationToken cancellationToken)
    {
        var declaration = method
            .DeclaringSyntaxReferences.Select(reference => reference.GetSyntax(cancellationToken))
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();

        return declaration?.Identifier.GetLocation() ?? method.Locations.FirstOrDefault() ?? Location.None;
    }

    /// <summary>
    /// Compares the freshly collected test surface with the manifest that is checked in next to the
    /// test project, if there is one. A missing manifest is not an error, because the test project may
    /// well be producing its very first one.
    /// </summary>
    /// <param name="context">The context of the analyzed compilation.</param>
    /// <param name="collected">The test surface collected from the current compilation.</param>
    private static void CompareWithManifestOnDisk(CompilationAnalysisContext context, TestSurfaceManifest collected)
    {
        var manifestFile = FindManifest(context.Options.AdditionalFiles, context.CancellationToken);

        if (manifestFile is null)
        {
            return;
        }

        var text = manifestFile.GetText(context.CancellationToken);

        if (text is null)
        {
            ReportInvalidManifest(context, manifestFile.Path, "the content of the file is not available.");

            return;
        }

        if (!TestSurfaceManifestReader.TryRead(text, out var onDisk, out var error))
        {
            ReportInvalidManifest(
                context,
                manifestFile.Path,
                error ?? "the file is not a well-formed test-surface manifest."
            );

            return;
        }

        ReportWhenStale(context, manifestFile.Path, onDisk, collected);
    }

    /// <summary>
    /// Finds the first additional file that is a test-surface manifest, using the same discovery rule
    /// as the production analyzer: the path ends with
    /// <see cref="TestSurfaceManifestFormat.FileSuffix" />.
    /// </summary>
    /// <param name="additionalFiles">The additional files of the compilation.</param>
    /// <param name="cancellationToken">A token to observe while scanning.</param>
    /// <returns>The manifest file, or <see langword="null" /> if there is none.</returns>
    private static AdditionalText? FindManifest(
        ImmutableArray<AdditionalText> additionalFiles,
        CancellationToken cancellationToken
    )
    {
        foreach (var additionalFile in additionalFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (additionalFile.Path.EndsWith(TestSurfaceManifestFormat.FileSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return additionalFile;
            }
        }

        return null;
    }

    /// <summary>
    /// Reports <c>FSH0003</c> when the parsed manifest no longer describes the tests of the current
    /// compilation. Only the id sets are compared, never the text, so that comment lines, ordering or
    /// any other formatting difference can never cause a false positive.
    /// </summary>
    /// <param name="context">The context of the analyzed compilation.</param>
    /// <param name="path">The path of the manifest file.</param>
    /// <param name="onDisk">The manifest as it is checked in.</param>
    /// <param name="collected">The test surface collected from the current compilation.</param>
    private static void ReportWhenStale(
        CompilationAnalysisContext context,
        string path,
        TestSurfaceManifest onDisk,
        TestSurfaceManifest collected
    )
    {
        var added = CountMissingIds(collected, onDisk);
        var removed = CountMissingIds(onDisk, collected);

        if (added == 0 && removed == 0)
        {
            return;
        }

        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "the recorded test surface no longer matches the tests of this project, so the manifest is "
                + "stale and must be regenerated ({0} id(s) added, {1} id(s) removed).",
            added,
            removed
        );

        ReportInvalidManifest(context, path, detail);
    }

    /// <summary>
    /// Counts the documentation comment ids of <paramref name="source" /> that
    /// <paramref name="other" /> does not contain, across both the test methods and the referenced
    /// production members.
    /// </summary>
    /// <param name="source">The manifest supplying the ids.</param>
    /// <param name="other">The manifest the ids are looked up in.</param>
    /// <returns>The number of ids that are missing from <paramref name="other" />.</returns>
    private static int CountMissingIds(TestSurfaceManifest source, TestSurfaceManifest other) =>
        source.TestMethodIds.Except(other.TestMethodIds).Count
        + source.ReferencedMemberIds.Except(other.ReferencedMemberIds).Count;

    /// <summary>
    /// Reports <c>FSH0003</c> for the manifest at <paramref name="path" />, anchored at the file
    /// itself so that the developer is pointed at the artifact that needs attention.
    /// </summary>
    /// <param name="context">The context of the analyzed compilation.</param>
    /// <param name="path">The path of the manifest file.</param>
    /// <param name="detail">The description of the problem.</param>
    private static void ReportInvalidManifest(CompilationAnalysisContext context, string path, string detail) =>
        context.ReportDiagnostic(
            Diagnostic.Create(Descriptors.InvalidTestSurfaceManifest, CreateFileLocation(path), path, detail)
        );

    /// <summary>
    /// Creates a location pointing at the very beginning of the file at <paramref name="path" />.
    /// </summary>
    /// <param name="path">The path of the file.</param>
    /// <returns>The created location.</returns>
    private static Location CreateFileLocation(string path) =>
        Location.Create(path, new TextSpan(0, 0), new LinePositionSpan(new LinePosition(0, 0), new LinePosition(0, 0)));

    /// <summary>
    /// A test framework that is awake on the analyzed compilation, paired with the recogniser its probe
    /// created for that compilation, so that the recogniser is never built twice.
    /// </summary>
    private sealed class AwakeFramework
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AwakeFramework" /> class.
        /// </summary>
        /// <param name="probe">The probe that recognised the framework.</param>
        /// <param name="recognizer">The recogniser the probe created.</param>
        public AwakeFramework(ITestFrameworkProbe probe, ITestMethodRecognizer recognizer)
        {
            Probe = probe;
            Recognizer = recognizer;
        }

        /// <summary>
        /// Gets the probe that recognised the framework.
        /// </summary>
        public ITestFrameworkProbe Probe { get; }

        /// <summary>
        /// Gets the recogniser deciding which methods are test methods of the framework.
        /// </summary>
        public ITestMethodRecognizer Recognizer { get; }
    }
}
