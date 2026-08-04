namespace NetEvolve.FrameShift.Execution.Tests.Unit;

using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Drives <see cref="MutationExecutionCli.RunAsync" /> end to end, but without spawning a real
/// <c>dotnet build</c>: the production and test host assemblies are emitted directly through Roslyn, the
/// same way every other fixture of this project is, which is enough to exercise every step
/// <see cref="MutationExecutionCli" /> itself performs - recompiling the source, generating mutations,
/// running each through the real test host subprocess, and reporting progress and the final score.
/// </summary>
public class MutationExecutionCliTests
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

    private const string ProductionAssemblyFileName = "Production.dll";
    private const string TestHostAssemblyFileName = "TestHost.dll";
    private static readonly TimeSpan MutantTimeout = TimeSpan.FromSeconds(30);

    [Test]
    public async Task RunAsync_HandEmittedProductionAndTestHost_ReportsAScoreWithoutBuildFailures()
    {
        var directory = Directory.CreateTempSubdirectory("frameshift-cli-unit-");

        try
        {
            var sourcePath = Path.Combine(directory.FullName, "Calculator.cs");
            await File.WriteAllTextAsync(sourcePath, ProductionSource).ConfigureAwait(false);

            await PrepareTestOutputAsync(directory.FullName).ConfigureAwait(false);

            var parsed = ExecutionCliOptions.TryParse(
                [
                    "--test-output",
                    directory.FullName,
                    "--production-dll",
                    ProductionAssemblyFileName,
                    "--test-dll",
                    TestHostAssemblyFileName,
                    "--source",
                    sourcePath,
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
                _ = await Assert.That(score.Killed + score.Survived).IsGreaterThan(0);
                _ = await Assert.That(output.ToString()).Contains("Mutation score:");
            }
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task RunAsync_CancelledBeforeAnyMutant_ThrowsOperationCanceled()
    {
        var directory = Directory.CreateTempSubdirectory("frameshift-cli-unit-cancel-");

        try
        {
            var sourcePath = Path.Combine(directory.FullName, "Calculator.cs");
            await File.WriteAllTextAsync(sourcePath, ProductionSource).ConfigureAwait(false);

            await PrepareTestOutputAsync(directory.FullName).ConfigureAwait(false);

            var parsed = ExecutionCliOptions.TryParse(
                [
                    "--test-output",
                    directory.FullName,
                    "--production-dll",
                    ProductionAssemblyFileName,
                    "--test-dll",
                    TestHostAssemblyFileName,
                    "--source",
                    sourcePath,
                ],
                out var options,
                out var error
            );

            _ = await Assert.That(parsed).IsTrue();
            _ = await Assert.That(error).IsNull();

            using var output = new StringWriter();
            using var cancellationSource = new CancellationTokenSource();
            await cancellationSource.CancelAsync().ConfigureAwait(false);

            _ = await Assert
                .That(async () =>
                    await MutationExecutionCli
                        .RunAsync(options!, output, cancellationSource.Token)
                        .ConfigureAwait(false)
                )
                .Throws<OperationCanceledException>();
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task RunAsync_HtmlReportFormat_WritesHtmlFileToReportPath()
    {
        var directory = Directory.CreateTempSubdirectory("frameshift-cli-report-html-");

        try
        {
            var sourcePath = Path.Combine(directory.FullName, "Calculator.cs");
            await File.WriteAllTextAsync(sourcePath, ProductionSource).ConfigureAwait(false);
            await PrepareTestOutputAsync(directory.FullName).ConfigureAwait(false);

            var reportPath = Path.Combine(directory.FullName, "report.html");
            _ = await RunWithReportOptionsAsync(
                    directory.FullName,
                    sourcePath,
                    "--report-format",
                    "html",
                    "--report-path",
                    reportPath
                )
                .ConfigureAwait(false);

            var html = await File.ReadAllTextAsync(reportPath).ConfigureAwait(false);

            _ = await Assert.That(html).StartsWith("<!doctype html>");
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task RunAsync_MarkdownReportFormatWithPath_WritesMarkdownFileToReportPath()
    {
        var directory = Directory.CreateTempSubdirectory("frameshift-cli-report-markdown-path-");

        try
        {
            var sourcePath = Path.Combine(directory.FullName, "Calculator.cs");
            await File.WriteAllTextAsync(sourcePath, ProductionSource).ConfigureAwait(false);
            await PrepareTestOutputAsync(directory.FullName).ConfigureAwait(false);

            var reportPath = Path.Combine(directory.FullName, "report.md");
            _ = await RunWithReportOptionsAsync(
                    directory.FullName,
                    sourcePath,
                    "--report-format",
                    "markdown",
                    "--report-path",
                    reportPath
                )
                .ConfigureAwait(false);

            var markdown = await File.ReadAllTextAsync(reportPath).ConfigureAwait(false);

            _ = await Assert.That(markdown).Contains("Mutation execution report");
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task RunAsync_MarkdownReportFormatWithoutPath_WritesMarkdownToOutput()
    {
        var directory = Directory.CreateTempSubdirectory("frameshift-cli-report-markdown-console-");

        try
        {
            var sourcePath = Path.Combine(directory.FullName, "Calculator.cs");
            await File.WriteAllTextAsync(sourcePath, ProductionSource).ConfigureAwait(false);
            await PrepareTestOutputAsync(directory.FullName).ConfigureAwait(false);

            var (_, output) = await RunWithReportOptionsAsync(
                    directory.FullName,
                    sourcePath,
                    "--report-format",
                    "markdown"
                )
                .ConfigureAwait(false);

            _ = await Assert.That(output).Contains("Mutation execution report");
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task RunAsync_ConsoleReportFormatWithPath_WritesConsoleReportToFile()
    {
        var directory = Directory.CreateTempSubdirectory("frameshift-cli-report-console-path-");

        try
        {
            var sourcePath = Path.Combine(directory.FullName, "Calculator.cs");
            await File.WriteAllTextAsync(sourcePath, ProductionSource).ConfigureAwait(false);
            await PrepareTestOutputAsync(directory.FullName).ConfigureAwait(false);

            var reportPath = Path.Combine(directory.FullName, "report.txt");
            var (_, output) = await RunWithReportOptionsAsync(
                    directory.FullName,
                    sourcePath,
                    "--report-path",
                    reportPath
                )
                .ConfigureAwait(false);

            var console = await File.ReadAllTextAsync(reportPath).ConfigureAwait(false);

            using (Assert.Multiple())
            {
                _ = await Assert.That(console).Contains("Next steps:");
                _ = await Assert.That(output).DoesNotContain("Next steps:");
            }
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task RunAsync_GitHubSummaryFormatWithoutEnvironmentVariable_ThrowsInvalidOperationException()
    {
        var directory = Directory.CreateTempSubdirectory("frameshift-cli-report-github-summary-missing-");
        var previousSummaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");

        try
        {
            Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", null);

            var sourcePath = Path.Combine(directory.FullName, "Calculator.cs");
            await File.WriteAllTextAsync(sourcePath, ProductionSource).ConfigureAwait(false);
            await PrepareTestOutputAsync(directory.FullName).ConfigureAwait(false);

            _ = await Assert
                .That(async () =>
                    await RunWithReportOptionsAsync(directory.FullName, sourcePath, "--report-format", "github-summary")
                        .ConfigureAwait(false)
                )
                .Throws<InvalidOperationException>();
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", previousSummaryPath);
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task RunAsync_GitHubSummaryFormatWithEnvironmentVariable_AppendsMarkdownToSummaryFile()
    {
        var directory = Directory.CreateTempSubdirectory("frameshift-cli-report-github-summary-");
        var previousSummaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        var summaryPath = Path.Combine(directory.FullName, "step-summary.md");

        try
        {
            await File.WriteAllTextAsync(summaryPath, "# Existing summary\n").ConfigureAwait(false);
            Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", summaryPath);

            var sourcePath = Path.Combine(directory.FullName, "Calculator.cs");
            await File.WriteAllTextAsync(sourcePath, ProductionSource).ConfigureAwait(false);
            await PrepareTestOutputAsync(directory.FullName).ConfigureAwait(false);

            _ = await RunWithReportOptionsAsync(directory.FullName, sourcePath, "--report-format", "github-summary")
                .ConfigureAwait(false);

            var summary = await File.ReadAllTextAsync(summaryPath).ConfigureAwait(false);

            using (Assert.Multiple())
            {
                _ = await Assert.That(summary).StartsWith("# Existing summary");
                _ = await Assert.That(summary).Contains("Mutation execution report");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", previousSummaryPath);
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    private static async Task<(MutationScore Score, string Output)> RunWithReportOptionsAsync(
        string testOutputDirectory,
        string sourcePath,
        params string[] extraArgs
    )
    {
        var parsed = ExecutionCliOptions.TryParse(
            [
                "--test-output",
                testOutputDirectory,
                "--production-dll",
                ProductionAssemblyFileName,
                "--test-dll",
                TestHostAssemblyFileName,
                "--source",
                sourcePath,
                "--timeout-seconds",
                ((int)MutantTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture),
                .. extraArgs,
            ],
            out var options,
            out var error
        );

        if (!parsed)
        {
            throw new InvalidOperationException($"Fixture options failed to parse: {error}");
        }

        using var output = new StringWriter();
        var score = await MutationExecutionCli.RunAsync(options!, output).ConfigureAwait(false);

        return (score, output.ToString());
    }

    private static async Task PrepareTestOutputAsync(string testOutputDirectory)
    {
        var productionTree = CSharpSyntaxTree.ParseText(ProductionSource, path: "Production.cs");
        var productionCompilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(ProductionAssemblyFileName),
            [productionTree],
            RuntimeReferences.Default,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        var productionBytes = Emit(productionCompilation);

        await File.WriteAllBytesAsync(Path.Combine(testOutputDirectory, ProductionAssemblyFileName), productionBytes)
            .ConfigureAwait(false);

        var testHostTree = CSharpSyntaxTree.ParseText(TestHostSource, path: "Program.cs");
        var testHostReferences = RuntimeReferences.Default.Add(MetadataReference.CreateFromImage(productionBytes));
        var testHostCompilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(TestHostAssemblyFileName),
            [testHostTree],
            testHostReferences,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication)
        );
        var testHostBytes = Emit(testHostCompilation);

        await File.WriteAllBytesAsync(Path.Combine(testOutputDirectory, TestHostAssemblyFileName), testHostBytes)
            .ConfigureAwait(false);

        var ownRuntimeConfigPath = Path.ChangeExtension(
            typeof(MutationExecutionCliTests).Assembly.Location,
            ".runtimeconfig.json"
        );
        var testHostRuntimeConfigPath = Path.Combine(
            testOutputDirectory,
            Path.ChangeExtension(TestHostAssemblyFileName, ".runtimeconfig.json")
        );
        File.Copy(ownRuntimeConfigPath, testHostRuntimeConfigPath, overwrite: true);
    }

    private static byte[] Emit(Compilation compilation)
    {
        using var stream = new MemoryStream();
        var emitResult = compilation.Emit(stream);

        return emitResult.Success
            ? stream.ToArray()
            : throw new InvalidOperationException(
                "Fixture failed to compile: " + string.Join("; ", emitResult.Diagnostics)
            );
    }
}
