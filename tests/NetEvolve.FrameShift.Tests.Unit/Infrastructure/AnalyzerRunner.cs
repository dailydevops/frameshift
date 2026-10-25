namespace NetEvolve.FrameShift.Tests.Unit.Infrastructure;

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

/// <summary>
/// Runs one <see cref="DiagnosticAnalyzer" /> over a compilation and returns what it reported.
/// </summary>
/// <remarks>
/// Roslyn does not let an analyzer exception escape; it turns it into an <c>AD0001</c> diagnostic and
/// carries on. Returning that diagnostic like any other would let a test pass while the analyzer under
/// test actually crashed, therefore every run fails loudly with the full analyzer failure messages.
/// </remarks>
internal static class AnalyzerRunner
{
    /// <summary>
    /// The identifier Roslyn reports an analyzer exception under.
    /// </summary>
    public const string AnalyzerFailureId = "AD0001";

    /// <summary>
    /// Runs <paramref name="analyzer" /> over <paramref name="compilation" />.
    /// </summary>
    /// <param name="analyzer">The analyzer under test.</param>
    /// <param name="compilation">The compilation to analyse.</param>
    /// <param name="additionalFiles">The <c>AdditionalFiles</c> the analyzer sees, for example a manifest.</param>
    /// <param name="globalOptions">
    /// The global analyzer configuration, keyed by the <c>build_property.*</c> names the compiler
    /// generates for a <c>CompilerVisibleProperty</c>.
    /// </param>
    /// <param name="cancellationToken">A token to observe while analysing.</param>
    /// <returns>Every diagnostic the analyzer reported, in the order Roslyn returned them.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="analyzer" /> or <paramref name="compilation" /> is <see langword="null" />.
    /// </exception>
    /// <exception cref="InvalidOperationException">The analyzer threw an exception.</exception>
    [SuppressMessage(
        "Major Bug",
        "S8949:The overload accepting a 'CancellationToken' should be used",
        Justification = "The WithAnalyzers overload taking a token is obsolete; GetAnalyzerDiagnosticsAsync observes it."
    )]
    public static async Task<ImmutableArray<Diagnostic>> RunAsync(
        DiagnosticAnalyzer analyzer,
        Compilation compilation,
        IEnumerable<AdditionalText>? additionalFiles = null,
        IReadOnlyDictionary<string, string>? globalOptions = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(compilation);

        // The CancellationToken-accepting overload of Compilation.WithAnalyzers is already obsolete on
        // every supported Roslyn variant (4.8.0 and up), not only from 4.14.0 onward as once assumed here
        // - the token-less overload is what every tier expects. This method still observes the token, via
        // GetAnalyzerDiagnosticsAsync below.
        var options = CreateOptions(additionalFiles, globalOptions);
        var withAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create(analyzer), options);
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken).ConfigureAwait(false);

        ThrowOnAnalyzerFailure(diagnostics);

        return diagnostics;
    }

    /// <summary>
    /// Runs <paramref name="analyzer" /> and keeps only the diagnostics with
    /// <paramref name="diagnosticId" />.
    /// </summary>
    /// <param name="analyzer">The analyzer under test.</param>
    /// <param name="compilation">The compilation to analyse.</param>
    /// <param name="diagnosticId">The identifier to keep, for example <c>FSH0001</c>.</param>
    /// <param name="additionalFiles">The <c>AdditionalFiles</c> the analyzer sees, for example a manifest.</param>
    /// <param name="globalOptions">The global analyzer configuration, keyed by the <c>build_property.*</c> names.</param>
    /// <param name="cancellationToken">A token to observe while analysing.</param>
    /// <returns>The matching diagnostics, possibly empty.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="analyzer" />, <paramref name="compilation" /> or <paramref name="diagnosticId" />
    /// is <see langword="null" />.
    /// </exception>
    /// <exception cref="InvalidOperationException">The analyzer threw an exception.</exception>
    public static async Task<ImmutableArray<Diagnostic>> RunAsync(
        DiagnosticAnalyzer analyzer,
        Compilation compilation,
        string diagnosticId,
        IEnumerable<AdditionalText>? additionalFiles = null,
        IReadOnlyDictionary<string, string>? globalOptions = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(diagnosticId);

        var diagnostics = await RunAsync(analyzer, compilation, additionalFiles, globalOptions, cancellationToken)
            .ConfigureAwait(false);

        return OfId(diagnostics, diagnosticId);
    }

    /// <summary>
    /// Keeps the diagnostics with the given identifier.
    /// </summary>
    /// <param name="diagnostics">The diagnostics to filter.</param>
    /// <param name="id">The identifier to keep, for example <c>FSH0004</c>.</param>
    /// <returns>The matching diagnostics, possibly empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id" /> is <see langword="null" />.</exception>
    public static ImmutableArray<Diagnostic> OfId(ImmutableArray<Diagnostic> diagnostics, string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (diagnostics.IsDefaultOrEmpty)
        {
            return [];
        }

        return [.. diagnostics.Where(diagnostic => string.Equals(diagnostic.Id, id, StringComparison.Ordinal))];
    }

    /// <summary>
    /// Builds the analyzer options from the additional files and the global configuration.
    /// </summary>
    /// <param name="additionalFiles">The additional files, or <see langword="null" /> for none.</param>
    /// <param name="globalOptions">The global configuration, or <see langword="null" /> for none.</param>
    /// <returns>The created options.</returns>
    private static AnalyzerOptions CreateOptions(
        IEnumerable<AdditionalText>? additionalFiles,
        IReadOnlyDictionary<string, string>? globalOptions
    )
    {
        var files = additionalFiles is null ? ImmutableArray<AdditionalText>.Empty : additionalFiles.ToImmutableArray();

        return new AnalyzerOptions(files, new TestAnalyzerConfigOptions(globalOptions).AsProvider());
    }

    /// <summary>
    /// Fails the current test when the analyzer crashed instead of reporting.
    /// </summary>
    /// <param name="diagnostics">The diagnostics of the run.</param>
    /// <exception cref="InvalidOperationException">At least one analyzer exception was reported.</exception>
    private static void ThrowOnAnalyzerFailure(ImmutableArray<Diagnostic> diagnostics)
    {
        var failures = OfId(diagnostics, AnalyzerFailureId);

        if (failures.IsEmpty)
        {
            return;
        }

        var messages = failures.Select(failure => failure.GetMessage(CultureInfo.InvariantCulture));

        throw new InvalidOperationException(
            "The analyzer threw an exception, which Roslyn reported as "
                + AnalyzerFailureId
                + ":"
                + Environment.NewLine
                + string.Join(Environment.NewLine, messages)
        );
    }
}
