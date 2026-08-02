namespace NetEvolve.FrameShift.Execution;

/// <summary>
/// The entry point of the execution CLI: parses the command line, delegates the actual run to
/// <see cref="MutationExecutionCli" />, and reports a process exit code.
/// </summary>
/// <remarks>
/// The exit code answers "did the run complete", not "did the code pass mutation testing": <c>0</c> means
/// a score was produced, whatever it is, and a non-zero code means the invocation itself was wrong or the
/// run was interrupted. Gating a build on a minimum mutation score is a policy decision this CLI
/// deliberately does not make on a caller's behalf, and is left as follow-up work.
/// </remarks>
internal static class Program
{
    private const int SuccessExitCode = 0;
    private const int UsageErrorExitCode = 2;
    private const int CancelledExitCode = 130;

    /// <summary>
    /// Runs the execution CLI.
    /// </summary>
    /// <param name="args">The raw command-line arguments.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            await Console.Out.WriteLineAsync(ExecutionCliOptions.Usage).ConfigureAwait(false);

            return SuccessExitCode;
        }

        if (!ExecutionCliOptions.TryParse(args, out var options, out var error))
        {
            await Console.Error.WriteLineAsync(error).ConfigureAwait(false);
            await Console.Error.WriteLineAsync().ConfigureAwait(false);
            await Console.Error.WriteLineAsync(ExecutionCliOptions.Usage).ConfigureAwait(false);

            return UsageErrorExitCode;
        }

        try
        {
            _ = await MutationExecutionCli.RunAsync(options!, Console.Out).ConfigureAwait(false);

            return SuccessExitCode;
        }
        catch (OperationCanceledException)
        {
            return CancelledExitCode;
        }
    }
}
