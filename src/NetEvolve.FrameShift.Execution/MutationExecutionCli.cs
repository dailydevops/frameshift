namespace NetEvolve.FrameShift.Execution;

using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Execution.Reports;
using NetEvolve.FrameShift.Mutations;

/// <summary>
/// The runnable core of the execution CLI: recompile the given production source files, generate every
/// candidate mutation of them, and run each one through
/// <see cref="MutationExecutionEngine.ExecuteViaTestHostAsync" /> against the given, already-built test
/// project.
/// </summary>
/// <remarks>
/// The production compilation is deliberately built fresh from source here, referencing every other
/// assembly already sitting in the test output directory, instead of loading the project file through
/// MSBuild. That keeps this CLI independent of the SDK resolution logic a full project evaluation would
/// need, at the cost of a real limitation: a production project with source generators, conditional
/// compilation symbols, or references that are not simply "every DLL already in the test output
/// directory" is not reproduced faithfully. Closing that gap by evaluating the real project file is left
/// as follow-up work.
/// </remarks>
internal static class MutationExecutionCli
{
    private const string DllSearchPattern = "*.dll";

    /// <summary>
    /// Runs a full mutation pass and writes progress and the final score to <paramref name="output" />.
    /// </summary>
    /// <param name="options">The parsed command-line options.</param>
    /// <param name="output">Where progress and the final score are written.</param>
    /// <param name="cancellationToken">A token observed between mutants.</param>
    /// <returns>The aggregated score of the run.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options" /> or <paramref name="output" /> is <see langword="null" />.
    /// </exception>
    public static async Task<MutationScore> RunAsync(
        ExecutionCliOptions options,
        TextWriter output,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        var (compilation, mutations) = CreateMutations(options);

        await output
            .WriteLineAsync(
                $"Found {mutations.Length} candidate mutation(s) across {options.SourceFilePaths.Length} file(s)."
            )
            .ConfigureAwait(false);

        var results = ImmutableArray.CreateBuilder<MutantExecutionResult>(mutations.Length);

        for (var index = 0; index < mutations.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (mutation, tree) = mutations[index];
            var result = await MutationExecutionEngine
                .ExecuteViaTestHostAsync(
                    compilation,
                    mutation,
                    tree,
                    options.TestOutputDirectory,
                    options.ProductionAssemblyFileName,
                    options.TestAssemblyFileName,
                    options.Timeout,
                    cancellationToken
                )
                .ConfigureAwait(false);

            results.Add(result);

            await ReportResultAsync(output, index, mutations.Length, mutation, tree, result).ConfigureAwait(false);
        }

        var score = MutationScore.FromResults(results.ToImmutable());
        await ReportScoreAsync(output, score).ConfigureAwait(false);
        await WriteReportAsync(options, output, ExecutionReport.FromScore(score)).ConfigureAwait(false);

        return score;
    }

    /// <summary>
    /// The environment variable a GitHub Actions job exposes the path of its step summary file under; see
    /// https://docs.github.com/actions/using-workflows/workflow-commands-for-github-actions#adding-a-job-summary.
    /// </summary>
    private const string GitHubStepSummaryEnvironmentVariable = "GITHUB_STEP_SUMMARY";

    /// <summary>
    /// Writes the end-of-run report in the format <paramref name="options" /> selected.
    /// </summary>
    private static async Task WriteReportAsync(ExecutionCliOptions options, TextWriter output, ExecutionReport report)
    {
        if (options.ReportFormat == ReportFormat.Html)
        {
            var html = HtmlExecutionReportWriter.Write(report);
            await File.WriteAllTextAsync(options.ReportPath!, html).ConfigureAwait(false);

            return;
        }

        if (options.ReportFormat == ReportFormat.GitHubSummary)
        {
            var summaryPath = Environment.GetEnvironmentVariable(GitHubStepSummaryEnvironmentVariable);

            if (string.IsNullOrEmpty(summaryPath))
            {
                throw new InvalidOperationException(
                    $"The '{GitHubStepSummaryEnvironmentVariable}' environment variable is not set; "
                        + "'github-summary' is only usable inside a GitHub Actions job."
                );
            }

            var summaryMarkdown = MarkdownExecutionReportWriter.Write(report);
            await File.AppendAllTextAsync(summaryPath, summaryMarkdown + Environment.NewLine).ConfigureAwait(false);

            return;
        }

        if (options.ReportFormat == ReportFormat.Markdown)
        {
            var markdown = MarkdownExecutionReportWriter.Write(report);

            if (options.ReportPath is { Length: > 0 })
            {
                await File.WriteAllTextAsync(options.ReportPath, markdown).ConfigureAwait(false);
            }
            else
            {
                await output.WriteLineAsync(markdown).ConfigureAwait(false);
            }

            return;
        }

        if (options.ReportPath is { Length: > 0 })
        {
            var reportWriter = new StreamWriter(options.ReportPath);

            try
            {
                await ConsoleExecutionReportWriter.WriteAsync(reportWriter, report).ConfigureAwait(false);
            }
            finally
            {
                await reportWriter.DisposeAsync().ConfigureAwait(false);
            }

            return;
        }

        await ConsoleExecutionReportWriter.WriteAsync(output, report).ConfigureAwait(false);
    }

    private static async Task ReportResultAsync(
        TextWriter output,
        int index,
        int total,
        Mutation mutation,
        SyntaxTree tree,
        MutantExecutionResult result
    )
    {
        await output
            .WriteLineAsync(
                $"[{index + 1}/{total}] {DescribeLocation(mutation, tree)} '{mutation.DisplayName}' -> {result.Verdict}"
            )
            .ConfigureAwait(false);

        if (result.Diagnostics is { Length: > 0 })
        {
            await output
                .WriteLineAsync("    " + result.Diagnostics.Replace("\n", "\n    ", StringComparison.Ordinal))
                .ConfigureAwait(false);
        }
    }

    private static async Task ReportScoreAsync(TextWriter output, MutationScore score)
    {
        await output.WriteLineAsync().ConfigureAwait(false);
        await output
            .WriteLineAsync(
                $"Killed: {score.Killed}, Survived: {score.Survived}, "
                    + $"Build failed: {score.BuildFailed}, Timed out: {score.TimedOut}"
            )
            .ConfigureAwait(false);
        await output
            .WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"Mutation score: {score.Score:P1}"))
            .ConfigureAwait(false);
    }

    private static (
        CSharpCompilation Compilation,
        ImmutableArray<(Mutation Mutation, SyntaxTree Tree)> Mutations
    ) CreateMutations(ExecutionCliOptions options)
    {
        var sourceTrees = options
            .SourceFilePaths.Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path))
            .ToImmutableArray();

        // The test assembly's own *.deps.json pins the production assembly's identity, including its
        // assembly version, and rejects a same-named file whose version does not match - which a
        // recompile from source always would, since Roslyn defaults an unversioned compilation to
        // 0.0.0.0 while the original SDK build defaulted to 1.0.0.0. Recording the real version as an
        // assembly-level attribute of the recompiled mutant keeps its identity indistinguishable from
        // the assembly *.deps.json already expects.
        var originalAssemblyPath = Path.Combine(options.TestOutputDirectory, options.ProductionAssemblyFileName);
        var originalVersion = AssemblyName.GetAssemblyName(originalAssemblyPath).Version ?? new Version(0, 0, 0, 0);
        var versionTree = CSharpSyntaxTree.ParseText(
            $"""[assembly: System.Reflection.AssemblyVersion("{originalVersion}")]""",
            path: "AssemblyVersion.g.cs"
        );

        var trees = sourceTrees.Add(versionTree);
        var references = CollectReferences(options.TestOutputDirectory, options.ProductionAssemblyFileName);
        var assemblyName = Path.GetFileNameWithoutExtension(options.ProductionAssemblyFileName);
        var compilation = CSharpCompilation.Create(
            assemblyName,
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var mutations = ImmutableArray.CreateBuilder<(Mutation, SyntaxTree)>();

        foreach (var tree in sourceTrees)
        {
            var semanticModel = compilation.GetSemanticModel(tree);

            foreach (
                var mutation in MutantGenerator.CreateMutations(tree.GetRoot(), semanticModel, CancellationToken.None)
            )
            {
                mutations.Add((mutation, tree));
            }
        }

        return (compilation, mutations.ToImmutable());
    }

    /// <summary>
    /// References every assembly already sitting in the test output directory except the production
    /// assembly itself, which is recompiled fresh from <see cref="ExecutionCliOptions.SourceFilePaths" />
    /// instead.
    /// </summary>
    private static ImmutableArray<MetadataReference> CollectReferences(
        string testOutputDirectory,
        string productionAssemblyFileName
    )
    {
        // The shared framework assemblies first: a framework-dependent build output directory never
        // contains them itself, see RuntimeAssemblyReferences. Everything actually sitting in the test
        // output directory - NuGet package dependencies, the test assembly itself - comes after, and a
        // same-named file there never overrides the shared one.
        var references = ImmutableArray.CreateBuilder<MetadataReference>();
        references.AddRange(RuntimeAssemblyReferences.Shared);

        var sharedFileNames = RuntimeAssemblyReferences.Shared.Select(reference =>
            Path.GetFileName(reference.Display ?? string.Empty)
        );
        var excludedFileNames = sharedFileNames
            .Append(productionAssemblyFileName)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var dllPath in Directory.EnumerateFiles(testOutputDirectory, DllSearchPattern))
        {
            if (excludedFileNames.Contains(Path.GetFileName(dllPath)))
            {
                continue;
            }

            references.Add(MetadataReference.CreateFromFile(dllPath));
        }

        return references.ToImmutable();
    }

    private static string DescribeLocation(Mutation mutation, SyntaxTree tree)
    {
        var lineSpan = mutation.Location.GetLineSpan();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Path.GetFileName(tree.FilePath)}:{lineSpan.StartLinePosition.Line + 1}"
        );
    }
}
