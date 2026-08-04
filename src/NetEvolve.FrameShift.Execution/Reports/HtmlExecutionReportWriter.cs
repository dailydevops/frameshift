namespace NetEvolve.FrameShift.Execution.Reports;

using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

/// <summary>
/// Renders an <see cref="ExecutionReport" /> as a single, self-contained HTML document.
/// </summary>
/// <remarks>
/// Everything - markup, styling, the dark-mode variant - lives inline in the one string this class
/// returns. A developer who ran the CLI against a scratch checkout has no web server and no network
/// access to fetch a stylesheet or a font from; the report has to render correctly the moment it is
/// double-clicked from a file explorer, which rules out anything the document does not carry with it.
/// </remarks>
internal static class HtmlExecutionReportWriter
{
    /// <summary>
    /// Builds the HTML document for <paramref name="report" />.
    /// </summary>
    /// <param name="report">The report to render.</param>
    /// <returns>A complete, self-contained HTML document as a string.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="report" /> is <see langword="null" />.</exception>
    public static string Write(ExecutionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();

        AppendHead(builder);
        AppendSummary(builder, report.Score);
        AppendSections(builder, report);
        AppendFoot(builder);

        return builder.ToString();
    }

    private static void AppendHead(StringBuilder builder) =>
        _ = builder
            .Append(
                """
                <!doctype html>
                <html lang="en">
                <head>
                <meta charset="utf-8">
                <title>Mutation execution report</title>
                <style>
                """
            )
            .Append(Style)
            .Append(
                """
                </style>
                </head>
                <body>
                <h1>Mutation execution report</h1>
                """
            );

    private static void AppendSections(StringBuilder builder, ExecutionReport report)
    {
        if (report.IsClean)
        {
            _ = builder.Append(
                """
                <p class="clean">Nothing left to do: every mutant was killed.</p>
                """
            );

            return;
        }

        AppendSection(
            builder,
            "Survived mutants",
            "No test failed against these mutants - each one is a gap in test coverage to close.",
            report.SurvivedMutants
        );
        AppendSection(
            builder,
            "Build-failed mutants",
            "These mutants never became a real program at all - fix the mutant generator or the test harness.",
            report.BuildFailedMutants
        );
        AppendSection(
            builder,
            "Timed-out mutants",
            "These mutants' test host had to be killed for exceeding its timeout - raise the timeout or investigate slow tests.",
            report.TimedOutMutants
        );
    }

    private static void AppendFoot(StringBuilder builder) =>
        _ = builder.Append(
            """
            </body>
            </html>
            """
        );

    private static void AppendSummary(StringBuilder builder, MutationScore score)
    {
        var percentage = string.Create(CultureInfo.InvariantCulture, $"{score.Score:P1}");

        _ = builder
            .Append("""<section class="summary">""")
            .Append("""<p class="score">Mutation score: """)
            .Append(percentage)
            .Append("</p><ul>")
            .Append("<li>Killed: ")
            .Append(score.Killed)
            .Append("</li>")
            .Append("<li>Survived: ")
            .Append(score.Survived)
            .Append("</li>")
            .Append("<li>Build failed: ")
            .Append(score.BuildFailed)
            .Append("</li>")
            .Append("<li>Timed out: ")
            .Append(score.TimedOut)
            .Append("</li>")
            .Append("</ul></section>");
    }

    private static void AppendSection(
        StringBuilder builder,
        string heading,
        string description,
        ImmutableArray<MutantExecutionResult> results
    )
    {
        if (results.IsEmpty)
        {
            return;
        }

        _ = builder
            .Append("<section><h2>")
            .Append(WebUtility.HtmlEncode(heading))
            .Append(" (")
            .Append(results.Length)
            .Append(")</h2><p>")
            .Append(WebUtility.HtmlEncode(description))
            .Append("</p><table><thead><tr><th>File</th><th>Line</th><th>Mutation</th></tr></thead><tbody>");

        foreach (var mutation in results.Select(result => result.Mutation))
        {
            var lineSpan = mutation.Location.GetLineSpan();
            var fileName = Path.GetFileName(lineSpan.Path);
            var line = lineSpan.StartLinePosition.Line + 1;

            _ = builder
                .Append("<tr><td>")
                .Append(WebUtility.HtmlEncode(fileName))
                .Append("</td><td>")
                .Append(line)
                .Append("</td><td>")
                .Append(WebUtility.HtmlEncode(mutation.DisplayName))
                .Append("</td></tr>");
        }

        _ = builder.Append("</tbody></table></section>");
    }

    private const string Style = """
        :root {
            color-scheme: light dark;
        }
        body {
            font-family: system-ui, sans-serif;
            margin: 2rem;
            background: #ffffff;
            color: #1a1a1a;
        }
        h1 {
            margin-bottom: 0.5rem;
        }
        section {
            margin-bottom: 1.5rem;
        }
        table {
            border-collapse: collapse;
            width: 100%;
        }
        th, td {
            border: 1px solid #cccccc;
            padding: 0.4rem 0.6rem;
            text-align: left;
        }
        th {
            background: #f0f0f0;
        }
        .score {
            font-size: 1.2rem;
            font-weight: bold;
        }
        .clean {
            font-size: 1.1rem;
        }
        @media (prefers-color-scheme: dark) {
            body {
                background: #121212;
                color: #e6e6e6;
            }
            th, td {
                border-color: #444444;
            }
            th {
                background: #1f1f1f;
            }
        }
        """;
}
