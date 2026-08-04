namespace NetEvolve.FrameShift.Execution.Reports;

using System.Collections.Immutable;
using System.Globalization;

/// <summary>
/// Renders an <see cref="ExecutionReport" /> as a plain-text "what to do next" list.
/// </summary>
/// <remarks>
/// The report deliberately says nothing about mutants that were killed: a clean run needs no follow-up
/// action, and repeating every kill would drown the handful of lines a caller actually has to act on in
/// noise proportional to the size of the test suite instead of the size of the problem.
/// </remarks>
internal static class ConsoleExecutionReportWriter
{
    /// <summary>
    /// Writes the report's next-step sections to <paramref name="output" />.
    /// </summary>
    /// <param name="output">Where the report is written.</param>
    /// <param name="report">The report to render.</param>
    /// <param name="cancellationToken">A token observed once per item written.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="output" /> or <paramref name="report" /> is <see langword="null" />.
    /// </exception>
    public static async Task WriteAsync(
        TextWriter output,
        ExecutionReport report,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(report);

        await output.WriteLineAsync().ConfigureAwait(false);
        await output.WriteLineAsync("Next steps:").ConfigureAwait(false);

        if (report.IsClean)
        {
            await output.WriteLineAsync("  Nothing to do - every mutant was killed or excluded.").ConfigureAwait(false);

            return;
        }

        await WriteSectionAsync(
                output,
                "Survived mutants (missing test coverage)",
                report.SurvivedMutants,
                cancellationToken
            )
            .ConfigureAwait(false);
        await WriteSectionAsync(
                output,
                "Build-failed mutants (fix the mutant or the test harness)",
                report.BuildFailedMutants,
                cancellationToken
            )
            .ConfigureAwait(false);
        await WriteSectionAsync(
                output,
                "Timed-out mutants (raise --timeout-seconds or investigate slow tests)",
                report.TimedOutMutants,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task WriteSectionAsync(
        TextWriter output,
        string heading,
        ImmutableArray<MutantExecutionResult> results,
        CancellationToken cancellationToken
    )
    {
        if (results.IsEmpty)
        {
            return;
        }

        await output
            .WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"  {heading} ({results.Length}):"))
            .ConfigureAwait(false);

        foreach (var result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await output.WriteLineAsync(DescribeResult(result)).ConfigureAwait(false);
        }
    }

    private static string DescribeResult(MutantExecutionResult result)
    {
        var lineSpan = result.Mutation.Location.GetLineSpan();
        var fileName = Path.GetFileName(lineSpan.Path);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"    - {fileName}:{lineSpan.StartLinePosition.Line + 1} '{result.Mutation.DisplayName}'"
        );
    }
}
