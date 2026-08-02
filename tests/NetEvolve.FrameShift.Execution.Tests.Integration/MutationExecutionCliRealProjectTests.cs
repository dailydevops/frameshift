namespace NetEvolve.FrameShift.Execution.Tests.Integration;

using System.Diagnostics;
using NetEvolve.FrameShift.Execution;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Drives the execution CLI against a real, SDK-built project pair - not a hand-emitted fixture - because
/// that is the one thing the other tests in this project cannot exercise: a real <c>*.deps.json</c>.
/// </summary>
/// <remarks>
/// This is the regression test for a real defect the CLI hit the first time it ran against an actual
/// `dotnet build` output: Roslyn defaults a compilation without an explicit
/// <c>[assembly: AssemblyVersion]</c> to <c>0.0.0.0</c>, while the .NET SDK's own build defaults to
/// <c>1.0.0.0</c>. A recompiled mutant assembly at the wrong version does not fail to compile or to emit
/// - it fails to <em>load</em>, because the test host's own <c>*.deps.json</c> pins the production
/// assembly's expected version and rejects a same-named file that does not match, which one of Roslyn's
/// unversioned compilations always would. Every mutant in the fixture below is real, and every one of
/// them would have reported a false "Killed" verdict - masked as an unhandled
/// <see cref="System.IO.FileNotFoundException" /> inside the test host - if that version were not
/// restored on the recompiled assembly.
/// </remarks>
public class MutationExecutionCliRealProjectTests
{
    private const string ProductionSource = """
        namespace Fixture;

        public sealed class Calculator
        {
            public int Add(int left, int right) => left + right;

            public int AlwaysZero() => 0;
        }
        """;

    private const string TestHostSource = """
        var calculator = new Fixture.Calculator();

        return calculator.Add(2, 3) == 5 ? 0 : 1;
        """;

    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MutantTimeout = TimeSpan.FromSeconds(30);

    [Test]
    public async Task RunAsync_RealSdkBuiltProject_MatchesTheExpectedScore()
    {
        var projectDirectory = Directory.CreateTempSubdirectory("frameshift-cli-real-project-").FullName;

        try
        {
            var (productionSourcePath, testOutputDirectory) = await CreateAndBuildProjectAsync(projectDirectory)
                .ConfigureAwait(false);

            var parsed = ExecutionCliOptions.TryParse(
                [
                    "--test-output",
                    testOutputDirectory,
                    "--production-dll",
                    "Production.dll",
                    "--test-dll",
                    "TestHost.dll",
                    "--source",
                    productionSourcePath,
                    "--timeout-seconds",
                    ((int)MutantTimeout.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ],
                out var options,
                out var error
            );

            _ = await Assert.That(parsed).IsTrue();
            _ = await Assert.That(error).IsNull();

            using var output = new StringWriter();
            var score = await MutationExecutionCli.RunAsync(options!, output).ConfigureAwait(false);

            using (Assert.Multiple())
            {
                _ = await Assert.That(score.BuildFailed).IsEqualTo(0);
                _ = await Assert.That(score.Killed).IsEqualTo(4);
                _ = await Assert.That(score.Survived).IsEqualTo(1);
            }
        }
        finally
        {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    private static async Task<(string ProductionSourcePath, string TestOutputDirectory)> CreateAndBuildProjectAsync(
        string projectDirectory
    )
    {
        var productionDirectory = Path.Combine(projectDirectory, "Production");
        var testHostDirectory = Path.Combine(projectDirectory, "TestHost");
        _ = Directory.CreateDirectory(productionDirectory);
        _ = Directory.CreateDirectory(testHostDirectory);

        var productionSourcePath = Path.Combine(productionDirectory, "Calculator.cs");
        await File.WriteAllTextAsync(productionSourcePath, ProductionSource).ConfigureAwait(false);
        await File.WriteAllTextAsync(
                Path.Combine(productionDirectory, "Production.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """
            )
            .ConfigureAwait(false);

        await File.WriteAllTextAsync(Path.Combine(testHostDirectory, "Program.cs"), TestHostSource)
            .ConfigureAwait(false);
        var testHostProjectPath = Path.Combine(testHostDirectory, "TestHost.csproj");
        await File.WriteAllTextAsync(
                testHostProjectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Production/Production.csproj" />
                  </ItemGroup>
                </Project>
                """
            )
            .ConfigureAwait(false);

        await RunDotnetBuildAsync(testHostProjectPath).ConfigureAwait(false);

        var testOutputDirectory = Path.Combine(testHostDirectory, "bin", "Debug", "net10.0");

        return (productionSourcePath, testOutputDirectory);
    }

    private static async Task RunDotnetBuildAsync(string projectPath)
    {
        // Resolved through PATH deliberately, see ProcessTestHostRunner's identical justification.
#pragma warning disable S4036 // Use an absolute path for this command
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
#pragma warning restore S4036
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("quiet");

        using var process = new Process { StartInfo = startInfo };
        _ = process.Start();

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();

        using var timeoutSource = new CancellationTokenSource(BuildTimeout);
        await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var standardOutput = await standardOutputTask.ConfigureAwait(false);
            var standardError = await standardErrorTask.ConfigureAwait(false);

            throw new InvalidOperationException(
                $"'dotnet build {projectPath}' failed with exit code {process.ExitCode}.{Environment.NewLine}"
                    + $"{standardOutput}{Environment.NewLine}{standardError}"
            );
        }
    }
}
