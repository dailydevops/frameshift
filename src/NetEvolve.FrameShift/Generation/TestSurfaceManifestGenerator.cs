namespace NetEvolve.FrameShift.Generation;

using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using NetEvolve.FrameShift.Configuration;
using NetEvolve.FrameShift.TestSurface;

/// <summary>
/// Generates the test-surface manifest of a test project, so that it never has to be written or
/// refreshed by hand.
/// </summary>
/// <remarks>
/// <para>
/// <b>End to end.</b> The manifest is the bridge between the two passes of FrameShift: a test
/// compilation sees production code only as a metadata reference and cannot mutate it, while a
/// production compilation cannot see the tests at all. Until now the manifest was a hand-written file
/// and the test-side analyzer could only complain — with <c>FSH0003</c> — once it had gone stale. This
/// generator closes that loop. It runs on the test project, collects the very same test surface the
/// analyzer collects, and emits it as a generated source file. The MSBuild target
/// <c>FrameShiftWriteTestSurfaceManifest</c>, shipped in <c>NetEvolve.FrameShift.targets</c>, picks that
/// file up after <c>CoreCompile</c> and turns it back into
/// <c>$(MSBuildProjectName).frameshift-tests</c> next to the project file. The production project then
/// consumes that manifest as an <c>AdditionalFiles</c> entry, exactly as before.
/// </para>
/// <para>
/// <b>Why a generated source file and not a written file.</b> A generator must not touch the file
/// system: it has no access to it under <c>EnforceExtendedAnalyzerRules</c>, and a build server may run
/// the compiler in a sandbox. Emitting a source file is the only sanctioned output channel, so the
/// manifest travels as source and MSBuild does the writing.
/// </para>
/// <para>
/// <b>The shape of the emitted file.</b> The whole file is a single block comment: the first line is
/// exactly <c>/*</c>, the last line is exactly <c>*/</c>, and every line in between is a verbatim
/// manifest line. Such a file is valid C# that contributes nothing whatsoever to the compilation, and
/// the MSBuild target recreates the manifest by dropping the first and the last line — no parsing, no
/// string surgery. A manifest line that contained the sequence <c>*/</c> would end the comment early
/// and break the compilation; documentation comment ids cannot contain it, but the generator asserts
/// that defensively and skips such a line instead of emitting a file that does not compile.
/// </para>
/// <para>
/// <b>When it produces nothing.</b> The generator emits no output at all unless at least one registered
/// test framework probe matches the compilation, so a production project never sees a generated file.
/// It is also silent when <c>FrameShiftEnabled</c> is <see langword="false" />.
/// </para>
/// <para>
/// <b>Incrementality.</b> Collecting a test surface means walking every syntax tree with a semantic
/// model, so this generator depends on the whole <see cref="Compilation" />. That defeats
/// incrementality: the generator re-runs on every keystroke in the IDE and on every build, and its cost
/// grows with the size of the test project. This is unavoidable — the test surface is a property of the
/// entire compilation, not of any single syntax node — and it is stated here plainly rather than hidden.
/// If that cost is not wanted, disable FrameShift for the project by setting the MSBuild property
/// <c>FrameShiftEnabled</c> to <c>false</c>, or keep the analysis and only stop the manifest from being
/// written by setting <c>FrameShiftWriteTestSurfaceManifest</c> to <c>false</c>.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class TestSurfaceManifestGenerator : IIncrementalGenerator
{
    /// <summary>
    /// The stable hint name of the single emitted source file. The MSBuild target searches for exactly
    /// this file name below <c>$(CompilerGeneratedFilesOutputPath)</c>, so it must never change.
    /// </summary>
    public const string HintName = "TestSurfaceManifest.g.cs";

    /// <summary>
    /// The first line of the emitted file, opening the block comment that wraps the manifest.
    /// </summary>
    internal const string CommentStart = "/*";

    /// <summary>
    /// The last line of the emitted file, closing the block comment that wraps the manifest.
    /// </summary>
    internal const string CommentEnd = "*/";

    private const char LineFeed = '\n';
    private const char CarriageReturn = '\r';

    /// <summary>
    /// Registers the single source output of this generator, driven by the compilation combined with
    /// the analyzer configuration options that carry the FrameShift MSBuild properties.
    /// </summary>
    /// <param name="context">The context used to register the output.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var provider = context.CompilationProvider.Combine(context.AnalyzerConfigOptionsProvider);

        context.RegisterSourceOutput(
            provider,
            static (productionContext, source) => Execute(productionContext, source.Left, source.Right)
        );
    }

    /// <summary>
    /// Collects the test surface of <paramref name="compilation" /> and emits it, unless FrameShift is
    /// disabled or the compilation belongs to no known test framework.
    /// </summary>
    /// <param name="context">The context the source is added to.</param>
    /// <param name="compilation">The compilation to collect the test surface of.</param>
    /// <param name="optionsProvider">The provider of the analyzer configuration options.</param>
    private static void Execute(
        SourceProductionContext context,
        Compilation compilation,
        AnalyzerConfigOptionsProvider optionsProvider
    )
    {
        var options = FrameShiftOptions.Read(optionsProvider.GlobalOptions);

        if (!options.IsEnabled)
        {
            return;
        }

        var manifest = TryCollect(compilation, context.CancellationToken);

        if (manifest is null)
        {
            return;
        }

        context.AddSource(HintName, SourceText.From(Render(manifest), Encoding.UTF8));
    }

    /// <summary>
    /// Collects the union of the test surfaces of every registered probe that matches
    /// <paramref name="compilation" />.
    /// </summary>
    /// <param name="compilation">The compilation to collect the test surface of.</param>
    /// <param name="cancellationToken">A token to observe while collecting.</param>
    /// <returns>
    /// The collected manifest, or <see langword="null" /> when no probe matches the compilation, which
    /// is the signal that nothing at all must be emitted.
    /// </returns>
    /// <remarks>
    /// A compilation can legitimately match more than one probe, for instance while a test project is
    /// being migrated from one framework to another. Uniting the surfaces keeps such a project fully
    /// described instead of arbitrarily preferring one of the frameworks.
    /// </remarks>
    private static TestSurfaceManifest? TryCollect(Compilation compilation, CancellationToken cancellationToken)
    {
        var testMethodIds = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var referencedMemberIds = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var matched = false;

        foreach (var probe in TestFrameworkProbeRegistry.Matching(compilation))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var recognizer = probe.TryCreateRecognizer(compilation);

            if (recognizer is null)
            {
                continue;
            }

            matched = true;

            var collected = TestSurfaceCollector.Collect(compilation, recognizer, cancellationToken);

            testMethodIds.UnionWith(collected.TestMethodIds);
            referencedMemberIds.UnionWith(collected.ReferencedMemberIds);
        }

        return matched ? new TestSurfaceManifest(testMethodIds.ToImmutable(), referencedMemberIds.ToImmutable()) : null;
    }

    /// <summary>
    /// Renders <paramref name="manifest" /> as a C# file that consists of nothing but one block comment
    /// wrapping the canonical manifest text.
    /// </summary>
    /// <param name="manifest">The manifest to render.</param>
    /// <returns>The content of the emitted source file.</returns>
    private static string Render(TestSurfaceManifest manifest)
    {
        var builder = new StringBuilder();

        _ = builder.Append(CommentStart).Append(LineFeed);

        foreach (var line in TestSurfaceManifestWriter.Write(manifest).Split(LineFeed))
        {
            var manifestLine = line.TrimEnd(CarriageReturn);

            if (!IsEmittable(manifestLine))
            {
                continue;
            }

            _ = builder.Append(manifestLine).Append(LineFeed);
        }

        return builder.Append(CommentEnd).Append(LineFeed).ToString();
    }

    /// <summary>
    /// Determines whether <paramref name="line" /> can be placed inside the block comment.
    /// </summary>
    /// <param name="line">The manifest line to check.</param>
    /// <returns>
    /// <see langword="false" /> for the trailing empty line of the canonical text and for any line that
    /// would close the block comment prematurely; otherwise <see langword="true" />.
    /// </returns>
    private static bool IsEmittable(string line) =>
        line.Length > 0 && line.IndexOf(CommentEnd, StringComparison.Ordinal) < 0;
}
