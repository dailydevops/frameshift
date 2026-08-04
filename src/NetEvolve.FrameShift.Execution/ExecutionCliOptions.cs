namespace NetEvolve.FrameShift.Execution;

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;

/// <summary>
/// The parsed command-line invocation of the execution CLI: which already-built test project to run
/// mutants against, which file inside it is the production assembly, and which source files to
/// recompile and mutate.
/// </summary>
internal sealed class ExecutionCliOptions
{
    private const string TestOutputFlag = "--test-output";
    private const string ProductionAssemblyFlag = "--production-dll";
    private const string TestAssemblyFlag = "--test-dll";
    private const string SourceFlag = "--source";
    private const string TimeoutFlag = "--timeout-seconds";
    private const string ReportFormatFlag = "--report-format";
    private const string ReportPathFlag = "--report-path";

    private const int DefaultTimeoutSeconds = 60;
    private const string ConsoleReportFormatValue = "console";
    private const string HtmlReportFormatValue = "html";
    private const string MarkdownReportFormatValue = "markdown";
    private const string GitHubSummaryReportFormatValue = "github-summary";

    private ExecutionCliOptions(
        string testOutputDirectory,
        string productionAssemblyFileName,
        string testAssemblyFileName,
        ImmutableArray<string> sourceFilePaths,
        TimeSpan timeout,
        ReportFormat reportFormat,
        string? reportPath
    )
    {
        TestOutputDirectory = testOutputDirectory;
        ProductionAssemblyFileName = productionAssemblyFileName;
        TestAssemblyFileName = testAssemblyFileName;
        SourceFilePaths = sourceFilePaths;
        Timeout = timeout;
        ReportFormat = reportFormat;
        ReportPath = reportPath;
    }

    /// <summary>
    /// Gets the build output directory of the test project, containing the already-compiled test
    /// assembly and every assembly it depends on, including the unmutated production assembly.
    /// </summary>
    public string TestOutputDirectory { get; }

    /// <summary>
    /// Gets the file name (not a path) of the production assembly inside
    /// <see cref="TestOutputDirectory" />.
    /// </summary>
    public string ProductionAssemblyFileName { get; }

    /// <summary>
    /// Gets the file name (not a path) of the test assembly inside <see cref="TestOutputDirectory" />.
    /// </summary>
    public string TestAssemblyFileName { get; }

    /// <summary>
    /// Gets the production source files to recompile and generate mutations from.
    /// </summary>
    public ImmutableArray<string> SourceFilePaths { get; }

    /// <summary>
    /// Gets the time to wait for the test host of a single mutant before it is killed.
    /// </summary>
    public TimeSpan Timeout { get; }

    /// <summary>
    /// Gets the format the end-of-run report is written in. Defaults to <see cref="ReportFormat.Console" />.
    /// </summary>
    public ReportFormat ReportFormat { get; }

    /// <summary>
    /// Gets the file the end-of-run report is written to, or <see langword="null" /> to write a console
    /// or Markdown report to the same <see cref="TextWriter" /> the run's progress is written to. Required
    /// when <see cref="ReportFormat" /> is <see cref="ReportFormat.Html" />. Ignored when
    /// <see cref="ReportFormat" /> is <see cref="ReportFormat.GitHubSummary" />, which always appends to
    /// the file named by the <c>GITHUB_STEP_SUMMARY</c> environment variable instead.
    /// </summary>
    public string? ReportPath { get; }

    /// <summary>
    /// The usage text printed on a parse failure or an explicit <c>--help</c>.
    /// </summary>
    public static string Usage =>
        $"""
            Usage: frameshift {TestOutputFlag} <dir> {ProductionAssemblyFlag} <file.dll> {TestAssemblyFlag} <file.dll> {SourceFlag} <file.cs> [{SourceFlag} <file.cs> ...] [{TimeoutFlag} <seconds>] [{ReportFormatFlag} <console|html|markdown|github-summary>] [{ReportPathFlag} <file>]

              {TestOutputFlag}         The build output directory of the test project (contains the test
                                       assembly, the production assembly and every dependency of both).
              {ProductionAssemblyFlag}       The file name of the production assembly inside that directory,
                                       e.g. MyApp.dll. Recompiled fresh from --source; the copy already in
                                       the output directory is never read.
              {TestAssemblyFlag}             The file name of the test assembly inside that directory.
              {SourceFlag}              A production source file to compile and generate mutations from.
                                       Repeatable.
              {TimeoutFlag}    How long to wait for the test host of a single mutant before it is
                                       killed and the mutant is reported as timed out. Defaults to {DefaultTimeoutSeconds}.
              {ReportFormatFlag}      The format of the end-of-run report: '{ConsoleReportFormatValue}', '{HtmlReportFormatValue}',
                                       '{MarkdownReportFormatValue}' or '{GitHubSummaryReportFormatValue}'. Defaults to '{ConsoleReportFormatValue}'.
              {ReportPathFlag}        The file the end-of-run report is written to. Required for
                                       '{HtmlReportFormatValue}'; optional for '{ConsoleReportFormatValue}' and '{MarkdownReportFormatValue}', which write
                                       to the console when omitted; ignored for '{GitHubSummaryReportFormatValue}', which always
                                       appends to the file named by the GITHUB_STEP_SUMMARY environment variable.
            """;

    /// <summary>
    /// Parses the command-line arguments of a mutation run.
    /// </summary>
    /// <param name="args">The raw command-line arguments.</param>
    /// <param name="options">The parsed options, or <see langword="null" /> on failure.</param>
    /// <param name="error">The description of the problem, or <see langword="null" /> on success.</param>
    /// <returns><see langword="true" /> if <paramref name="args" /> parsed successfully.</returns>
    public static bool TryParse(
        string[] args,
        [NotNullWhen(true)] out ExecutionCliOptions? options,
        [NotNullWhen(false)] out string? error
    )
    {
        ArgumentNullException.ThrowIfNull(args);

        options = null;

        if (!TryParseFlags(args, out var raw, out error))
        {
            return false;
        }

        if (!TryValidate(raw, out error))
        {
            return false;
        }

        options = new ExecutionCliOptions(
            raw.TestOutputDirectory!,
            raw.ProductionAssemblyFileName!,
            raw.TestAssemblyFileName!,
            raw.SourceFilePaths.ToImmutable(),
            TimeSpan.FromSeconds(raw.TimeoutSeconds),
            raw.ReportFormat,
            raw.ReportPath
        );

        return true;
    }

    private static bool TryParseFlags(string[] args, out RawOptions raw, [NotNullWhen(false)] out string? error)
    {
        raw = new RawOptions
        {
            SourceFilePaths = ImmutableArray.CreateBuilder<string>(),
            TimeoutSeconds = DefaultTimeoutSeconds,
            ReportFormat = ReportFormat.Console,
        };

        var index = 0;

        while (index < args.Length)
        {
            var flag = args[index];

            if (!TryReadValue(args, ref index, out var value))
            {
                error = $"The flag '{flag}' requires a value.";

                return false;
            }

            switch (flag)
            {
                case TestOutputFlag:
                    raw.TestOutputDirectory = value;
                    break;
                case ProductionAssemblyFlag:
                    raw.ProductionAssemblyFileName = value;
                    break;
                case TestAssemblyFlag:
                    raw.TestAssemblyFileName = value;
                    break;
                case SourceFlag:
                    raw.SourceFilePaths.Add(value);
                    break;
                case TimeoutFlag:
                    if (
                        !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeoutSeconds)
                        || timeoutSeconds <= 0
                    )
                    {
                        error = $"'{value}' is not a positive number of seconds.";

                        return false;
                    }

                    raw.TimeoutSeconds = timeoutSeconds;
                    break;
                case ReportFormatFlag:
                    if (!TryParseReportFormat(value, out var reportFormat, out error))
                    {
                        return false;
                    }

                    raw.ReportFormat = reportFormat;
                    break;
                case ReportPathFlag:
                    raw.ReportPath = value;
                    break;
                default:
                    error = $"Unrecognised argument '{flag}'.";

                    return false;
            }
        }

        error = null;

        return true;
    }

    private static bool TryParseReportFormat(
        string value,
        out ReportFormat reportFormat,
        [NotNullWhen(false)] out string? error
    )
    {
        if (string.Equals(value, ConsoleReportFormatValue, StringComparison.OrdinalIgnoreCase))
        {
            reportFormat = ReportFormat.Console;
        }
        else if (string.Equals(value, HtmlReportFormatValue, StringComparison.OrdinalIgnoreCase))
        {
            reportFormat = ReportFormat.Html;
        }
        else if (string.Equals(value, MarkdownReportFormatValue, StringComparison.OrdinalIgnoreCase))
        {
            reportFormat = ReportFormat.Markdown;
        }
        else if (string.Equals(value, GitHubSummaryReportFormatValue, StringComparison.OrdinalIgnoreCase))
        {
            reportFormat = ReportFormat.GitHubSummary;
        }
        else
        {
            reportFormat = default;
            error =
                $"'{value}' is not a valid report format. Expected '{ConsoleReportFormatValue}', "
                + $"'{HtmlReportFormatValue}', '{MarkdownReportFormatValue}' or '{GitHubSummaryReportFormatValue}'.";

            return false;
        }

        error = null;

        return true;
    }

    private static bool TryValidate(RawOptions raw, [NotNullWhen(false)] out string? error)
    {
        if (string.IsNullOrEmpty(raw.TestOutputDirectory))
        {
            error = $"Missing required argument '{TestOutputFlag}'.";

            return false;
        }

        if (string.IsNullOrEmpty(raw.ProductionAssemblyFileName))
        {
            error = $"Missing required argument '{ProductionAssemblyFlag}'.";

            return false;
        }

        if (string.IsNullOrEmpty(raw.TestAssemblyFileName))
        {
            error = $"Missing required argument '{TestAssemblyFlag}'.";

            return false;
        }

        if (raw.SourceFilePaths.Count == 0)
        {
            error = $"At least one '{SourceFlag}' is required.";

            return false;
        }

        if (!Directory.Exists(raw.TestOutputDirectory))
        {
            error = $"The test output directory '{raw.TestOutputDirectory}' does not exist.";

            return false;
        }

        var missingSourceFilePath = raw.SourceFilePaths.FirstOrDefault(path => !File.Exists(path));

        if (missingSourceFilePath is not null)
        {
            error = $"The source file '{missingSourceFilePath}' does not exist.";

            return false;
        }

        if (raw.ReportFormat == ReportFormat.Html && string.IsNullOrEmpty(raw.ReportPath))
        {
            error = $"'{ReportPathFlag}' is required when '{ReportFormatFlag}' is '{HtmlReportFormatValue}'.";

            return false;
        }

        error = null;

        return true;
    }

    /// <summary>
    /// The unvalidated values collected while scanning the command line, before they are checked and
    /// turned into an <see cref="ExecutionCliOptions" />.
    /// </summary>
    private sealed class RawOptions
    {
        public string? TestOutputDirectory { get; set; }

        public string? ProductionAssemblyFileName { get; set; }

        public string? TestAssemblyFileName { get; set; }

        public required ImmutableArray<string>.Builder SourceFilePaths { get; init; }

        public int TimeoutSeconds { get; set; }

        public ReportFormat ReportFormat { get; set; }

        public string? ReportPath { get; set; }
    }

    private static bool TryReadValue(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            index++;

            return false;
        }

        value = args[index + 1];
        index += 2;

        return true;
    }
}
