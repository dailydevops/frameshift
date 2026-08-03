namespace NetEvolve.FrameShift.Execution;

using System.Collections.Immutable;
using System.Linq;

/// <summary>
/// Aggregates the execution-based results of a batch of mutants into the one number mutation testing is
/// usually reported as: the proportion of real mutants a test suite actually kills.
/// </summary>
internal sealed class MutationScore
{
    private MutationScore(ImmutableArray<MutantExecutionResult> results) => Results = results;

    /// <summary>
    /// Gets every individual result the score was computed from.
    /// </summary>
    public ImmutableArray<MutantExecutionResult> Results { get; }

    /// <summary>
    /// Gets the number of mutants a test failed on.
    /// </summary>
    public int Killed => Results.Count(result => result.Verdict == MutantVerdict.Killed);

    /// <summary>
    /// Gets the number of mutants every test passed against.
    /// </summary>
    public int Survived => Results.Count(result => result.Verdict == MutantVerdict.Survived);

    /// <summary>
    /// Gets the number of mutants that never became a real program at all.
    /// </summary>
    public int BuildFailed => Results.Count(result => result.Verdict == MutantVerdict.BuildFailed);

    /// <summary>
    /// Gets the number of mutants whose test host had to be killed for exceeding its timeout.
    /// </summary>
    public int TimedOut => Results.Count(result => result.Verdict == MutantVerdict.Timeout);

    /// <summary>
    /// Gets the mutation score: <see cref="Killed" /> divided by the number of mutants that were real
    /// programs at all (<see cref="Killed" /> plus <see cref="Survived" />), excluding
    /// <see cref="BuildFailed" /> mutants the same way the analyzer never classifies a mutant that does
    /// not compile as covered or as a gap. <c>0</c> when there is not a single real mutant to score.
    /// </summary>
    public double Score
    {
        get
        {
            var realMutants = Killed + Survived;

            return realMutants == 0 ? 0d : (double)Killed / realMutants;
        }
    }

    /// <summary>
    /// Builds a score from a batch of individual results.
    /// </summary>
    /// <param name="results">The results to aggregate.</param>
    /// <returns>The aggregated score.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="results" /> is <see langword="null" />.</exception>
    public static MutationScore FromResults(IEnumerable<MutantExecutionResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        return new MutationScore([.. results]);
    }
}
