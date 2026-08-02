namespace NetEvolve.FrameShift.Execution;

/// <summary>
/// The outcome of running a test host assembly as a real subprocess.
/// </summary>
internal sealed class TestHostRunResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TestHostRunResult" /> class.
    /// </summary>
    /// <param name="exitCode">The process exit code, or <see langword="null" /> when it timed out.</param>
    /// <param name="timedOut">Whether the process had to be killed because it exceeded its timeout.</param>
    /// <param name="standardOutput">Everything the process wrote to standard output.</param>
    /// <param name="standardError">Everything the process wrote to standard error.</param>
    public TestHostRunResult(int? exitCode, bool timedOut, string standardOutput, string standardError)
    {
        ExitCode = exitCode;
        TimedOut = timedOut;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    /// <summary>
    /// Gets the process exit code, or <see langword="null" /> when <see cref="TimedOut" /> is
    /// <see langword="true" />: a killed process never reports one.
    /// </summary>
    public int? ExitCode { get; }

    /// <summary>
    /// Gets a value indicating whether the process exceeded its timeout and had to be killed.
    /// </summary>
    public bool TimedOut { get; }

    /// <summary>
    /// Gets everything the process wrote to standard output, for diagnosing an unexpected exit code.
    /// </summary>
    public string StandardOutput { get; }

    /// <summary>
    /// Gets everything the process wrote to standard error, for diagnosing an unexpected exit code.
    /// </summary>
    public string StandardError { get; }
}
