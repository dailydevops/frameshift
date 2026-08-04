namespace NetEvolve.FrameShift.Execution;

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using NetEvolve.FrameShift.Mutations;

/// <summary>
/// Renders an <see cref="ExecutionReport" /> as GitHub-flavored Markdown.
/// </summary>
/// <remarks>
/// This is the one writer whose output is never a whole document by itself: it gets appended straight
/// into a GitHub Actions job summary or dropped into a pull request comment written by someone else, so
/// every heading uses <c>##</c>/<c>###</c> rather than a top-level <c>#</c> to nest under a caller's own
/// heading instead of competing with it. A mutation's <see cref="Mutation.DisplayName" /> is arbitrary
/// text a mutation strategy chose, not markup a document author wrote, so a literal <c>|</c> would be
/// read as a table cell delimiter and a literal backtick would break out of the inline code span wrapping
/// it; both are neutralized before rendering rather than trusted as-is.
/// </remarks>
internal static class MarkdownExecutionReportWriter
{
    /// <summary>
    /// Builds the Markdown fragment for <paramref name="report" />.
    /// </summary>
    /// <param name="report">The report to render.</param>
    /// <returns>A GitHub-flavored Markdown fragment as a string.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="report" /> is <see langword="null" />.</exception>
    public static string Write(ExecutionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();

        _ = builder.Append("## Mutation execution report").Append('\n');

        AppendSummary(builder, report.Score);

        if (report.IsClean)
        {
            _ = builder.Append('\n').Append("Nothing left to do - every mutant was killed or excluded.").Append('\n');

            return builder.ToString();
        }

        AppendSection(builder, "Survived mutants (missing test coverage)", report.SurvivedMutants);
        AppendSection(builder, "Build-failed mutants (fix the mutant or the test harness)", report.BuildFailedMutants);
        AppendSection(
            builder,
            "Timed-out mutants (raise --timeout-seconds or investigate slow tests)",
            report.TimedOutMutants
        );

        return builder.ToString();
    }

    private static void AppendSummary(StringBuilder builder, MutationScore score)
    {
        var percentage = string.Create(CultureInfo.InvariantCulture, $"{score.Score:P1}");

        _ = builder.Append('\n').Append("Mutation score: ").Append(percentage).Append('\n').Append('\n');

        AppendTable(
            builder,
            ["Killed", "Survived", "Build failed", "Timed out"],
            [
                [
                    score.Killed.ToString(CultureInfo.InvariantCulture),
                    score.Survived.ToString(CultureInfo.InvariantCulture),
                    score.BuildFailed.ToString(CultureInfo.InvariantCulture),
                    score.TimedOut.ToString(CultureInfo.InvariantCulture),
                ],
            ]
        );
    }

    private static void AppendSection(
        StringBuilder builder,
        string heading,
        ImmutableArray<MutantExecutionResult> results
    )
    {
        if (results.IsEmpty)
        {
            return;
        }

        _ = builder
            .Append('\n')
            .Append("### ")
            .Append(heading)
            .Append(" (")
            .Append(results.Length)
            .Append(')')
            .Append('\n')
            .Append('\n');

        var rows = new string[results.Length][];

        for (var index = 0; index < results.Length; index++)
        {
            var lineSpan = results[index].Mutation.Location.GetLineSpan();
            var fileName = Escape(Path.GetFileName(lineSpan.Path));
            var line = lineSpan.StartLinePosition.Line + 1;
            var displayName = Escape(results[index].Mutation.DisplayName);

            rows[index] =
            [
                string.Create(CultureInfo.InvariantCulture, $"`{fileName}:{line}`"),
                string.Create(CultureInfo.InvariantCulture, $"`{displayName}`"),
            ];
        }

        AppendTable(builder, ["Location", "Mutation"], rows);
    }

    private static string Escape(string value) =>
        value.Replace('`', '\'').Replace("|", "&#124;", StringComparison.Ordinal);

    /// <summary>
    /// Renders a GitHub-flavored Markdown table, padding every cell in a column to that column's widest
    /// cell so the raw Markdown source - not just its rendered form - lines up when read as plain text.
    /// </summary>
    private static void AppendTable(StringBuilder builder, string[] headers, string[][] rows)
    {
        var widths = new int[headers.Length];

        for (var column = 0; column < headers.Length; column++)
        {
            widths[column] = headers[column].Length;
        }

        foreach (var row in rows)
        {
            for (var column = 0; column < headers.Length; column++)
            {
                widths[column] = Math.Max(widths[column], row[column].Length);
            }
        }

        AppendRow(builder, headers, widths);

        var separator = new string[headers.Length];
        for (var column = 0; column < headers.Length; column++)
        {
            separator[column] = new string('-', widths[column]);
        }

        AppendRow(builder, separator, widths);

        foreach (var row in rows)
        {
            AppendRow(builder, row, widths);
        }
    }

    private static void AppendRow(StringBuilder builder, string[] cells, int[] widths)
    {
        _ = builder.Append('|');

        for (var column = 0; column < cells.Length; column++)
        {
            _ = builder.Append(' ').Append(cells[column].PadRight(widths[column])).Append(" |");
        }

        _ = builder.Append('\n');
    }
}
