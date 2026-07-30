namespace NetEvolve.Frameshift.Analyzers;

using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using NetEvolve.Frameshift.Configuration;
using NetEvolve.Frameshift.Diagnostics;
using NetEvolve.Frameshift.TestSurface;

/// <summary>
/// The framework-neutral test-side analysis of Frameshift. A framework-specific analyzer hands its
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
/// </remarks>
internal static class TestSurfaceAnalysis
{
    /// <summary>
    /// Runs the test-side analysis for the framework <paramref name="probe" /> detects.
    /// </summary>
    /// <param name="context">The context of the analyzed compilation.</param>
    /// <param name="probe">The probe detecting the test framework.</param>
    public static void Execute(CompilationAnalysisContext context, ITestFrameworkProbe probe)
    {
        var options = FrameshiftOptions.Read(context.Options.AnalyzerConfigOptionsProvider.GlobalOptions);

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

        ReportTestsWithoutProductionReference(context, recognizer);
        CompareWithManifestOnDisk(
            context,
            TestSurfaceCollector.Collect(context.Compilation, recognizer, context.CancellationToken)
        );
    }

    /// <summary>
    /// Reports <c>FSH0004</c> once for every test method that references no production member.
    /// </summary>
    /// <param name="context">The context of the analyzed compilation.</param>
    /// <param name="recognizer">The recogniser deciding which methods are test methods.</param>
    private static void ReportTestsWithoutProductionReference(
        CompilationAnalysisContext context,
        ITestMethodRecognizer recognizer
    )
    {
        var testMethods = TestSurfaceCollector.FindTestsWithoutProductionReference(
            context.Compilation,
            recognizer,
            context.CancellationToken
        );

        foreach (var testMethod in testMethods)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            context.ReportDiagnostic(
                Diagnostic.Create(
                    Descriptors.TestWithoutProductionReference,
                    GetIdentifierLocation(testMethod, context.CancellationToken),
                    testMethod.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                )
            );
        }
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
}
