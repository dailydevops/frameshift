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
/// The test-side analyzer of Frameshift. It runs on a TUnit test project, discovers the test methods
/// of the compilation, walks the code reachable from them and determines which production members
/// they exercise.
/// </summary>
/// <remarks>
/// <para>
/// This analyzer is what makes the test-surface manifest maintainable: the manifest is a build
/// artifact that is checked in and handed to the production compilation through
/// <c>AdditionalFiles</c>, because a test compilation sees production code only as a metadata
/// reference and therefore cannot mutate it, while a production compilation cannot see the tests at
/// all. Since nothing regenerates the manifest automatically, it silently rots. Whenever a manifest
/// is present, this analyzer compares the recorded test surface with the one it just collected and
/// tells the developer when the two no longer match, so that a stale manifest is noticed at build
/// time instead of quietly claiming coverage that no longer exists.
/// </para>
/// <para>
/// It reports <c>FSH0004</c> for every test method that does not reference a single production
/// member, and <c>FSH0003</c> when the manifest on disk cannot be parsed or has become stale. When
/// no manifest is present at all it stays silent about it, because the test project is free to be
/// the producer of the very first manifest. On a compilation that is not a TUnit test assembly the
/// analyzer does nothing whatsoever.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TestSurfaceAnalyzer : DiagnosticAnalyzer
{
    private const string TestFrameworkAssemblyPrefix = "TUnit";
    private const string TestAttributeMetadataName = "TUnit.Core.TestAttribute";

    private static readonly ImmutableArray<DiagnosticDescriptor> _supportedDiagnostics =
    [
        Descriptors.InvalidTestSurfaceManifest,
        Descriptors.TestWithoutProductionReference,
    ];

    /// <summary>
    /// Gets the diagnostics this analyzer can report, namely <c>FSH0003</c> and <c>FSH0004</c>.
    /// </summary>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => _supportedDiagnostics;

    /// <summary>
    /// Registers the analysis callbacks. All state is kept in the scope of a single compilation, so
    /// that the analyzer stays stateless, thread-safe and free of any cached per-compilation data.
    /// </summary>
    /// <param name="context">The context used to register the callbacks.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context" /> is <see langword="null" />.</exception>
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationAction(OnCompilation);
    }

    /// <summary>
    /// Determines whether <paramref name="compilation" /> is a TUnit test assembly. Both the
    /// referenced assembly name and the well-known test attribute must be present, so that the
    /// analyzer remains completely silent on production projects.
    /// </summary>
    /// <param name="compilation">The compilation to classify.</param>
    /// <returns>
    /// <see langword="true" /> if the compilation is a TUnit test assembly; otherwise
    /// <see langword="false" />.
    /// </returns>
    private static bool IsTestCompilation(Compilation compilation) =>
        compilation.ReferencedAssemblyNames.Any(name =>
            name.Name.StartsWith(TestFrameworkAssemblyPrefix, StringComparison.Ordinal)
        ) && compilation.GetTypeByMetadataName(TestAttributeMetadataName) is not null;

    /// <summary>
    /// Decides whether the current compilation is analyzed at all and, if so, collects its test
    /// surface, reports the tests without any production reference and compares the result with the
    /// manifest on disk. The work runs on the whole compilation, where every syntax tree is available.
    /// </summary>
    /// <param name="context">The context of the analyzed compilation.</param>
    private static void OnCompilation(CompilationAnalysisContext context)
    {
        var options = FrameshiftOptions.Read(context.Options.AnalyzerConfigOptionsProvider.GlobalOptions);

        if (!options.IsEnabled || !IsTestCompilation(context.Compilation))
        {
            return;
        }

        var collected = TestSurfaceCollector.Collect(context.Compilation, context.CancellationToken);

        ReportTestsWithoutProductionReference(context);
        CompareWithManifestOnDisk(context, collected);
    }

    /// <summary>
    /// Reports <c>FSH0004</c> once for every test method that references no production member.
    /// </summary>
    /// <param name="context">The context of the finished compilation.</param>
    private static void ReportTestsWithoutProductionReference(CompilationAnalysisContext context)
    {
        var testMethods = TestSurfaceCollector.FindTestsWithoutProductionReference(
            context.Compilation,
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
    /// <param name="context">The context of the finished compilation.</param>
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
    /// <param name="context">The context of the finished compilation.</param>
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
    /// <param name="context">The context of the finished compilation.</param>
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
