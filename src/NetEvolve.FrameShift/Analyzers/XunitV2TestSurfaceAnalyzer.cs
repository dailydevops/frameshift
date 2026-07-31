namespace NetEvolve.FrameShift.Analyzers;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NetEvolve.FrameShift.Diagnostics;
using NetEvolve.FrameShift.TestSurface;

/// <summary>
/// The test-side analyzer of FrameShift for xUnit v2. It runs on a compilation that references
/// <c>xunit.core</c>, discovers the test methods of that compilation, walks the code reachable from them
/// and determines which production members they exercise.
/// </summary>
/// <remarks>
/// <para>
/// This analyzer is what makes the test-surface manifest maintainable: the manifest is a build artifact
/// that is checked in and handed to the production compilation through <c>AdditionalFiles</c>, because a
/// test compilation sees production code only as a metadata reference and therefore cannot mutate it,
/// while a production compilation cannot see the tests at all. Since nothing regenerates the manifest
/// automatically, it silently rots. Whenever a manifest is present, this analyzer compares the recorded
/// test surface with the one it just collected and tells the developer when the two no longer match, so
/// that a stale manifest is noticed at build time instead of quietly claiming coverage that no longer
/// exists.
/// </para>
/// <para>
/// It reports <c>FSH0004</c> for every test method that does not reference a single production member,
/// and <c>FSH0003</c> when the manifest on disk cannot be parsed or has become stale. When no manifest
/// is present at all it stays silent about it, because the test project is free to be the producer of
/// the very first manifest.
/// </para>
/// <para>
/// The analyzer shuts down entirely — reporting nothing whatsoever — unless xUnit v2 is detected in the
/// compilation and at least one xUnit v2 test method is actually discovered. The two xUnit major
/// versions get one analyzer each, because they declare test attributes of identical metadata names in
/// different assemblies and only a version-specific probe can tell them apart exactly. Every framework
/// gets its own analyzer built on the same shared analysis, and each one stays silent on compilations
/// that are not its own. In a project that uses several frameworks at once — including one that uses
/// both xUnit versions — only one of the analyzers judges the manifest; see
/// <see cref="TestSurfaceAnalysis" /> for that rule.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class XunitV2TestSurfaceAnalyzer : DiagnosticAnalyzer
{
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

    private static void OnCompilation(CompilationAnalysisContext context) =>
        TestSurfaceAnalysis.Execute(context, XunitV2TestFrameworkProbe.Instance);
}
