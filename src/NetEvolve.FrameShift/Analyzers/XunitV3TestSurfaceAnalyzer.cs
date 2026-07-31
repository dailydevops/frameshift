namespace NetEvolve.FrameShift.Analyzers;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NetEvolve.FrameShift.Diagnostics;
using NetEvolve.FrameShift.TestSurface;

/// <summary>
/// The test-side analyzer of FrameShift for xUnit version 3. It runs on an xUnit v3 test project,
/// discovers the test methods of the compilation, walks the code reachable from them and determines which
/// production members they exercise.
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
/// The analyzer shuts down entirely — reporting nothing whatsoever — unless xUnit version 3 is detected
/// in the compilation and at least one of its test methods is actually discovered. Version 2 has its own
/// analyzer, because the two major versions declare their test attribute under the identical metadata
/// name and only a probe bound to one assembly can tell them apart; a project referencing both is
/// therefore judged by both analyzers, each one over its own tests, while the manifest is handled exactly
/// once. See <see cref="TestSurfaceAnalysis" /> for that rule.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class XunitV3TestSurfaceAnalyzer : DiagnosticAnalyzer
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
        TestSurfaceAnalysis.Execute(context, XunitV3TestFrameworkProbe.Instance);
}
