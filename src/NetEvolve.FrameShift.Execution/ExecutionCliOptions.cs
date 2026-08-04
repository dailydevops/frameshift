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

    private const int DefaultTimeoutSeconds = 60;

    private ExecutionCliOptions(
        string testOutputDirectory,
        string productionAssemblyFileName,
        string testAssemblyFileName,
        ImmutableArray<string> sourceFilePaths,
        TimeSpan timeout
    )
    {
        TestOutputDirectory = testOutputDirectory;
        ProductionAssemblyFileName = productionAssemblyFileName;
        TestAssemblyFileName = testAssemblyFileName;
        SourceFilePaths = sourceFilePaths;
        Timeout = timeout;
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
    /// The usage text printed on a parse failure or an explicit <c>--help</c>.
    /// </summary>
    public static string Usage =>
        $"""
            Usage: frameshift {TestOutputFlag} <dir> {ProductionAssemblyFlag} <file.dll> {TestAssemblyFlag} <file.dll> {SourceFlag} <file.cs> [{SourceFlag} <file.cs> ...] [{TimeoutFlag} <seconds>]

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
            TimeSpan.FromSeconds(raw.TimeoutSeconds)
        );

        return true;
    }

    private static bool TryParseFlags(string[] args, out RawOptions raw, [NotNullWhen(false)] out string? error)
    {
        raw = new RawOptions
        {
            SourceFilePaths = ImmutableArray.CreateBuilder<string>(),
            TimeoutSeconds = DefaultTimeoutSeconds,
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
                default:
                    error = $"Unrecognised argument '{flag}'.";

                    return false;
            }
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
