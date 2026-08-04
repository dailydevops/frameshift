namespace NetEvolve.FrameShift.Execution;

using System.Collections.Immutable;
using System.Linq;

/// <summary>
/// The actionable subset of a <see cref="MutationScore" />: every mutant a caller actually needs to look
/// at before the next run, grouped by what needs doing about it.
/// </summary>
internal sealed class ExecutionReport
{
    private ExecutionReport(
        MutationScore score,
        ImmutableArray<MutantExecutionResult> survivedMutants,
        ImmutableArray<MutantExecutionResult> buildFailedMutants,
        ImmutableArray<MutantExecutionResult> timedOutMutants
    )
    {
        Score = score;
        SurvivedMutants = survivedMutants;
        BuildFailedMutants = buildFailedMutants;
        TimedOutMutants = timedOutMutants;
    }

    /// <summary>
    /// Gets the aggregated score the report was built from.
    /// </summary>
    public MutationScore Score { get; }

    /// <summary>
    /// Gets the mutants no test failed against - each one is a gap in test coverage to close.
    /// </summary>
    public ImmutableArray<MutantExecutionResult> SurvivedMutants { get; }

    /// <summary>
    /// Gets the mutants that never became a real program at all.
    /// </summary>
    public ImmutableArray<MutantExecutionResult> BuildFailedMutants { get; }

    /// <summary>
    /// Gets the mutants whose test host had to be killed for exceeding its timeout.
    /// </summary>
    public ImmutableArray<MutantExecutionResult> TimedOutMutants { get; }

    /// <summary>
    /// Gets a value indicating whether there is nothing left for a caller to act on.
    /// </summary>
    public bool IsClean => SurvivedMutants.IsEmpty && BuildFailedMutants.IsEmpty && TimedOutMutants.IsEmpty;

    /// <summary>
    /// Builds a report from a score, grouping its results by what a caller needs to do next.
    /// </summary>
    /// <param name="score">The score to build the report from.</param>
    /// <returns>The report.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="score" /> is <see langword="null" />.</exception>
    public static ExecutionReport FromScore(MutationScore score)
    {
        ArgumentNullException.ThrowIfNull(score);

        return new ExecutionReport(
            score,
            score.Results.Where(result => result.Verdict == MutantVerdict.Survived).ToImmutableArray(),
            score.Results.Where(result => result.Verdict == MutantVerdict.BuildFailed).ToImmutableArray(),
            score.Results.Where(result => result.Verdict == MutantVerdict.Timeout).ToImmutableArray()
        );
    }
}
