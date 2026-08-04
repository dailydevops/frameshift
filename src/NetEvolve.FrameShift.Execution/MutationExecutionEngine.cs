namespace NetEvolve.FrameShift.Execution;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using NetEvolve.FrameShift.Mutations;

/// <summary>
/// Orchestrates execution-based mutation verification: build the mutant, run tests against it, and
/// classify the mutant as killed, survived, or never a real program at all.
/// </summary>
/// <remarks>
/// Two orchestrations exist, at two different depths.
/// <list type="bullet">
/// <item>
/// <description>
/// <see cref="Execute" />/<see cref="Run" /> invoke exactly one named test method in-process, through
/// <see cref="IsolatedAssemblyRunner" />. This is the narrowest possible slice that is still genuinely
/// execution-based, and it is what proved the core mechanism - compile a real mutant, load it in
/// isolation, run real code against it - actually works.
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="ExecuteViaTestHostAsync" />/<see cref="RunViaTestHostAsync" /> instead run a real,
/// already-built test project's own test host as a subprocess, against a copy of its build output with
/// only the production assembly swapped for the mutant. This is test-framework agnostic - it reads
/// nothing but the process exit code - and is the shape a real build-time or CI gate would actually use,
/// because it exercises the whole test suite exactly the way the project's own test runner already does,
/// instead of one method picked out by hand.
/// </description>
/// </item>
/// </list>
/// Neither orchestration discovers which tests to run on its own, and neither is wired into a CLI or an
/// MSBuild target yet; both remain the caller's responsibility for now.
/// </remarks>
internal static class MutationExecutionEngine
{
    /// <summary>
    /// Builds and executes a single mutant.
    /// </summary>
    /// <param name="compilation">The unmutated compilation the mutation is applied to.</param>
    /// <param name="mutation">The candidate mutation to execute.</param>
    /// <param name="originalTree">The unmutated syntax tree containing <see cref="Mutation.Original" />.</param>
    /// <param name="testTypeFullName">The full name of the type declaring the test method to run.</param>
    /// <param name="testMethodName">The name of the parameterless test method to run.</param>
    /// <param name="cancellationToken">A token observed while building the mutant.</param>
    /// <returns>The execution-based result of the mutant.</returns>
    public static MutantExecutionResult Execute(
        Compilation compilation,
        Mutation mutation,
        SyntaxTree originalTree,
        string testTypeFullName,
        string testMethodName,
        CancellationToken cancellationToken = default
    )
    {
        var emitResult = MutantAssemblyBuilder.TryEmit(compilation, mutation, originalTree, cancellationToken);

        if (!emitResult.Success)
        {
            return new MutantExecutionResult(
                mutation,
                MutantVerdict.BuildFailed,
                failure: null,
                DescribeDiagnostics(emitResult.Diagnostics)
            );
        }

        var executionResult = IsolatedAssemblyRunner.InvokeParameterlessTest(
            emitResult.AssemblyBytes!,
            testTypeFullName,
            testMethodName
        );

        return executionResult.Outcome == TestOutcome.Failed
            ? new MutantExecutionResult(mutation, MutantVerdict.Killed, executionResult.Failure)
            : new MutantExecutionResult(mutation, MutantVerdict.Survived, failure: null);
    }

    /// <summary>
    /// Builds and executes a batch of mutants against the same test method, aggregating the results into
    /// a <see cref="MutationScore" />.
    /// </summary>
    /// <param name="compilation">The unmutated compilation every mutation is applied to.</param>
    /// <param name="mutations">The candidate mutations to execute.</param>
    /// <param name="originalTree">The unmutated syntax tree containing every mutation's original node.</param>
    /// <param name="testTypeFullName">The full name of the type declaring the test method to run.</param>
    /// <param name="testMethodName">The name of the parameterless test method to run.</param>
    /// <param name="cancellationToken">A token observed between mutants.</param>
    /// <returns>The aggregated score.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mutations" /> is <see langword="null" />.</exception>
    public static MutationScore Run(
        Compilation compilation,
        IEnumerable<Mutation> mutations,
        SyntaxTree originalTree,
        string testTypeFullName,
        string testMethodName,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mutations);

        var results = new List<MutantExecutionResult>();

        foreach (var mutation in mutations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            results.Add(
                Execute(compilation, mutation, originalTree, testTypeFullName, testMethodName, cancellationToken)
            );
        }

        return MutationScore.FromResults(results);
    }

    /// <summary>
    /// Builds a single mutant and runs a real, already-built test project's test host against a copy of
    /// its build output with the production assembly swapped for the mutant.
    /// </summary>
    /// <param name="compilation">The unmutated compilation the mutation is applied to.</param>
    /// <param name="mutation">The candidate mutation to execute.</param>
    /// <param name="originalTree">The unmutated syntax tree containing <see cref="Mutation.Original" />.</param>
    /// <param name="testOutputDirectory">
    /// The build output directory of the test project, containing the already-compiled test assembly,
    /// its <c>*.runtimeconfig.json</c>, and every assembly it depends on, including the unmutated
    /// production assembly.
    /// </param>
    /// <param name="productionAssemblyFileName">
    /// The file name (not a path) of the production assembly inside <paramref name="testOutputDirectory" />.
    /// </param>
    /// <param name="testAssemblyFileName">
    /// The file name (not a path) of the test assembly inside <paramref name="testOutputDirectory" />.
    /// </param>
    /// <param name="timeout">
    /// The time to wait for the test host before it is killed and the mutant is reported as timed out.
    /// </param>
    /// <param name="cancellationToken">A token observed while building the mutant and running the host.</param>
    /// <returns>The execution-based result of the mutant.</returns>
    public static async Task<MutantExecutionResult> ExecuteViaTestHostAsync(
        Compilation compilation,
        Mutation mutation,
        SyntaxTree originalTree,
        string testOutputDirectory,
        string productionAssemblyFileName,
        string testAssemblyFileName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    )
    {
        var emitResult = MutantAssemblyBuilder.TryEmit(compilation, mutation, originalTree, cancellationToken);

        if (!emitResult.Success)
        {
            return new MutantExecutionResult(
                mutation,
                MutantVerdict.BuildFailed,
                failure: null,
                DescribeDiagnostics(emitResult.Diagnostics)
            );
        }

        using var workspace = MutantSwapWorkspace.Prepare(
            testOutputDirectory,
            productionAssemblyFileName,
            emitResult.AssemblyBytes!
        );

        var testAssemblyPath = Path.Combine(workspace.Directory, testAssemblyFileName);
        var runResult = await ProcessTestHostRunner
            .RunAsync(testAssemblyPath, timeout, cancellationToken)
            .ConfigureAwait(false);

        return Classify(mutation, runResult);
    }

    /// <summary>
    /// Builds and executes a batch of mutants against the same real test host, aggregating the results
    /// into a <see cref="MutationScore" />.
    /// </summary>
    /// <param name="compilation">The unmutated compilation every mutation is applied to.</param>
    /// <param name="mutations">The candidate mutations to execute.</param>
    /// <param name="originalTree">The unmutated syntax tree containing every mutation's original node.</param>
    /// <param name="testOutputDirectory">
    /// The build output directory of the test project, see <see cref="ExecuteViaTestHostAsync" />.
    /// </param>
    /// <param name="productionAssemblyFileName">
    /// The file name (not a path) of the production assembly inside <paramref name="testOutputDirectory" />.
    /// </param>
    /// <param name="testAssemblyFileName">
    /// The file name (not a path) of the test assembly inside <paramref name="testOutputDirectory" />.
    /// </param>
    /// <param name="timeout">The time to wait for the test host of every mutant before it is killed.</param>
    /// <param name="cancellationToken">A token observed between mutants.</param>
    /// <returns>The aggregated score.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mutations" /> is <see langword="null" />.</exception>
    public static async Task<MutationScore> RunViaTestHostAsync(
        Compilation compilation,
        IEnumerable<Mutation> mutations,
        SyntaxTree originalTree,
        string testOutputDirectory,
        string productionAssemblyFileName,
        string testAssemblyFileName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mutations);

        var results = new List<MutantExecutionResult>();

        foreach (var mutation in mutations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            results.Add(
                await ExecuteViaTestHostAsync(
                        compilation,
                        mutation,
                        originalTree,
                        testOutputDirectory,
                        productionAssemblyFileName,
                        testAssemblyFileName,
                        timeout,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
            );
        }

        return MutationScore.FromResults(results);
    }

    /// <summary>
    /// Turns a test host's process run into a verdict: <c>0</c> means every test passed, so the mutant
    /// survived; anything else means a test noticed it, so it was killed; and a timeout is its own
    /// verdict, because a hung host answers neither question.
    /// </summary>
    /// <summary>
    /// Renders the diagnostics <c>Compilation.Emit</c> reported for a mutant that failed to build, so a
    /// <c>BuildFailed</c> verdict carries the reason instead of only the fact.
    /// </summary>
    /// <param name="diagnostics">The diagnostics reported for the mutant, possibly empty.</param>
    /// <returns>
    /// One line per diagnostic, or <see langword="null" /> when there is nothing to show - either the
    /// mutant did not even reach emission, or emission reported no diagnostics at all.
    /// </returns>
    private static string? DescribeDiagnostics(ImmutableArray<Diagnostic> diagnostics) =>
        diagnostics.IsDefaultOrEmpty
            ? null
            : string.Join('\n', diagnostics.Select(diagnostic => diagnostic.ToString()));

    private static MutantExecutionResult Classify(Mutation mutation, TestHostRunResult runResult)
    {
        if (runResult.TimedOut)
        {
            return new MutantExecutionResult(mutation, MutantVerdict.Timeout, failure: null);
        }

        var diagnostics = runResult.StandardOutput + runResult.StandardError;

        return runResult.ExitCode == 0
            ? new MutantExecutionResult(mutation, MutantVerdict.Survived, failure: null)
            : new MutantExecutionResult(mutation, MutantVerdict.Killed, failure: null, diagnostics);
    }
}
