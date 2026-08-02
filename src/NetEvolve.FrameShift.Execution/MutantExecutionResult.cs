namespace NetEvolve.FrameShift.Execution;

using NetEvolve.FrameShift.Mutations;

/// <summary>
/// The execution-based outcome of a single mutant.
/// </summary>
internal sealed class MutantExecutionResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MutantExecutionResult" /> class.
    /// </summary>
    /// <param name="mutation">The mutation that was executed.</param>
    /// <param name="verdict">The verdict the execution reached.</param>
    /// <param name="failure">
    /// The exception the test method threw, when <paramref name="verdict" /> is
    /// <see cref="MutantVerdict.Killed" />; otherwise <see langword="null" />.
    /// </param>
    public MutantExecutionResult(Mutation mutation, MutantVerdict verdict, Exception? failure)
    {
        Mutation = mutation ?? throw new ArgumentNullException(nameof(mutation));
        Verdict = verdict;
        Failure = failure;
    }

    /// <summary>
    /// Gets the mutation that was executed.
    /// </summary>
    public Mutation Mutation { get; }

    /// <summary>
    /// Gets the verdict the execution reached.
    /// </summary>
    public MutantVerdict Verdict { get; }

    /// <summary>
    /// Gets the exception the test method threw when the mutant was killed, or <see langword="null" />
    /// otherwise.
    /// </summary>
    public Exception? Failure { get; }
}
