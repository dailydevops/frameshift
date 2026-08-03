namespace NetEvolve.FrameShift.Execution;

using System.Diagnostics;
using System.Text;

/// <summary>
/// Runs an already-built, framework-dependent test assembly as a real subprocess through
/// <c>dotnet exec</c>, the same mechanism every mainstream .NET test runner ultimately uses to isolate a
/// test run from the process driving it.
/// </summary>
/// <remarks>
/// The verdict this runner reports is nothing but the process exit code: <c>0</c> for every test
/// framework in this repository's support matrix (TUnit, xUnit, NUnit, MSTest, whether hosted by
/// <c>VSTest</c> or by <c>Microsoft.Testing.Platform</c>) means every test passed, and anything else
/// means at least one did not. That convention is what lets this runner stay completely test-framework
/// agnostic: it never has to parse a TRX file or any other framework-specific result format to answer
/// "did a test notice this mutant".
/// </remarks>
internal static class ProcessTestHostRunner
{
    private const string DotnetExecutable = "dotnet";
    private const string ExecCommand = "exec";

    /// <summary>
    /// Runs <paramref name="assemblyPath" /> with <c>dotnet exec</c>.
    /// </summary>
    /// <param name="assemblyPath">
    /// The path of the test assembly to run. Its directory must also contain a matching
    /// <c>*.runtimeconfig.json</c>, exactly as a normal build output directory does.
    /// </param>
    /// <param name="timeout">
    /// The time to wait before the process is killed and the run is reported as timed out.
    /// </param>
    /// <param name="cancellationToken">A token observed while waiting for the process to exit.</param>
    /// <returns>The outcome of the run.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="assemblyPath" /> is <see langword="null" />.</exception>
    public static async Task<TestHostRunResult> RunAsync(
        string assemblyPath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(assemblyPath);

        // Resolving "dotnet" through PATH, rather than an absolute path, is deliberate: it is the exact
        // same executable this process itself was very likely started with, and there is no single
        // correct absolute path for it across the machines a build or CI runner executes on.
#pragma warning disable S4036 // Use an absolute path for this command
        var startInfo = new ProcessStartInfo(DotnetExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(assemblyPath),
        };
#pragma warning restore S4036
        startInfo.ArgumentList.Add(ExecCommand);
        startInfo.ArgumentList.Add(assemblyPath);

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) => AppendLine(standardOutput, args.Data);
        process.ErrorDataReceived += (_, args) => AppendLine(standardError, args.Data);

        _ = process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token
        );

        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);

            return new TestHostRunResult(
                process.ExitCode,
                timedOut: false,
                standardOutput.ToString(),
                standardError.ToString()
            );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);

            return new TestHostRunResult(
                exitCode: null,
                timedOut: true,
                standardOutput.ToString(),
                standardError.ToString()
            );
        }
    }

    private static void AppendLine(StringBuilder builder, string? line)
    {
        if (line is not null)
        {
            _ = builder.AppendLine(line);
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process had already exited between the timeout firing and the kill request, which is
            // exactly the outcome being asked for; nothing left to do.
        }
    }
}
