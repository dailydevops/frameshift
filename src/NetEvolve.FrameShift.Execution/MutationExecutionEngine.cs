namespace NetEvolve.FrameShift.Execution;

using Microsoft.CodeAnalysis;
using NetEvolve.FrameShift.Mutations;

/// <summary>
/// Orchestrates execution-based mutation verification: build the mutant, run one test method against
/// it, and classify the mutant as killed, survived, or never a real program at all.
/// </summary>
/// <remarks>
/// This is deliberately the minimal orchestration that is still genuinely execution-based, not the full
/// pipeline a build-time gate would need. In particular it runs exactly one named test method against
/// each mutant, resolved by the caller, instead of discovering and running a whole test suite through a
/// real test host; wiring that up - copying a build output directory, swapping only the production
/// assembly, and shelling out to the appropriate test runner per framework - is deliberately left as
/// follow-up work, because it is mostly mechanical integration effort once this core mechanism, the part
/// that had to be proven to actually work, is in place.
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
            return new MutantExecutionResult(mutation, MutantVerdict.BuildFailed, failure: null);
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
}
