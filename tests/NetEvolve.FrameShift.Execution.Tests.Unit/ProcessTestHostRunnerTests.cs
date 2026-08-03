namespace NetEvolve.FrameShift.Execution.Tests.Unit;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Runs a real, on-disk console application through <see cref="ProcessTestHostRunner" />: a genuinely
/// spawned subprocess, not a simulated one, whose exit code, standard output and standard error this
/// runner reads back exactly the way <see cref="MutationExecutionEngine.ExecuteViaTestHostAsync" /> does.
/// </summary>
public class ProcessTestHostRunnerTests
{
    private const string HostAssemblyFileName = "TestHost.dll";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(500);

    [Test]
    public async Task RunAsync_ProcessExitsZero_ReportsSuccessWithoutTimeout()
    {
        using var directory = await PrepareHostAsync("return 0;").ConfigureAwait(false);

        var result = await ProcessTestHostRunner
            .RunAsync(Path.Combine(directory.Path, HostAssemblyFileName), DefaultTimeout)
            .ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.ExitCode).IsEqualTo(0);
            _ = await Assert.That(result.TimedOut).IsFalse();
        }
    }

    [Test]
    public async Task RunAsync_ProcessExitsNonZero_ReportsThatExitCode()
    {
        using var directory = await PrepareHostAsync("return 1;").ConfigureAwait(false);

        var result = await ProcessTestHostRunner
            .RunAsync(Path.Combine(directory.Path, HostAssemblyFileName), DefaultTimeout)
            .ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.ExitCode).IsEqualTo(1);
            _ = await Assert.That(result.TimedOut).IsFalse();
        }
    }

    [Test]
    public async Task RunAsync_ProcessWritesToStandardOutputAndError_CapturesBoth()
    {
        using var directory = await PrepareHostAsync(
                """
                System.Console.Out.WriteLine("hello-stdout");
                System.Console.Error.WriteLine("hello-stderr");
                return 0;
                """
            )
            .ConfigureAwait(false);

        var result = await ProcessTestHostRunner
            .RunAsync(Path.Combine(directory.Path, HostAssemblyFileName), DefaultTimeout)
            .ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.StandardOutput).Contains("hello-stdout");
            _ = await Assert.That(result.StandardError).Contains("hello-stderr");
        }
    }

    [Test]
    public async Task RunAsync_ProcessExceedsTheTimeout_IsKilledAndReportedAsTimedOut()
    {
        using var directory = await PrepareHostAsync(
                "System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);"
            )
            .ConfigureAwait(false);

        var result = await ProcessTestHostRunner
            .RunAsync(Path.Combine(directory.Path, HostAssemblyFileName), ShortTimeout)
            .ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.TimedOut).IsTrue();
            _ = await Assert.That(result.ExitCode).IsNull();
        }
    }

    /// <summary>
    /// Builds a real, on-disk console application from <paramref name="programBody" />, including a
    /// <c>*.runtimeconfig.json</c> the installed runtime can actually load. The config is copied from
    /// this very test assembly's own build output, which is guaranteed to already match the installed
    /// runtime, instead of being hand-written and risking a version mismatch <c>dotnet exec</c> would
    /// reject.
    /// </summary>
    private static async Task<TempDirectory> PrepareHostAsync(string programBody)
    {
        var tree = CSharpSyntaxTree.ParseText(programBody, path: "Program.cs");
        var compilation = CSharpCompilation.Create(
            "TestHost",
            [tree],
            RuntimeReferences.Default,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication)
        );

        using var stream = new MemoryStream();
        var emitResult = compilation.Emit(stream);

        if (!emitResult.Success)
        {
            throw new InvalidOperationException(
                "Fixture failed to compile: " + string.Join("; ", emitResult.Diagnostics)
            );
        }

        var directory = Directory.CreateTempSubdirectory("frameshift-testhost-unit-");

        await File.WriteAllBytesAsync(Path.Combine(directory.FullName, HostAssemblyFileName), stream.ToArray())
            .ConfigureAwait(false);

        var ownRuntimeConfigPath = Path.ChangeExtension(
            typeof(ProcessTestHostRunnerTests).Assembly.Location,
            ".runtimeconfig.json"
        );
        var hostRuntimeConfigPath = Path.Combine(
            directory.FullName,
            Path.ChangeExtension(HostAssemblyFileName, ".runtimeconfig.json")
        );
        File.Copy(ownRuntimeConfigPath, hostRuntimeConfigPath, overwrite: true);

        return new TempDirectory(directory.FullName);
    }

    private sealed class TempDirectory : IDisposable
    {
        private const int MaxDeleteAttempts = 5;
        private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(100);

        public TempDirectory(string path) => Path = path;

        public string Path { get; }

        /// <summary>
        /// A killed process's runtime does not always release its file handles by the moment
        /// <see cref="ProcessTestHostRunner.RunAsync" /> returns, so the very first delete attempt of a
        /// timed-out host's directory can genuinely race an OS handle that has not closed yet. Retrying
        /// briefly absorbs that race instead of making the test flaky. Windows reports that race as
        /// either an <see cref="IOException" /> or an <see cref="UnauthorizedAccessException" /> depending
        /// on exactly when the handle closes, so both are treated the same way.
        /// </summary>
        public void Dispose()
        {
            for (var attempt = 1; attempt <= MaxDeleteAttempts; attempt++)
            {
                try
                {
                    Directory.Delete(Path, recursive: true);

                    return;
                }
                catch (Exception exception)
                    when (attempt < MaxDeleteAttempts && exception is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(DeleteRetryDelay);
                }
            }
        }
    }
}
