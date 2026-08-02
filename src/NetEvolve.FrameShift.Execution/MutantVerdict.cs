namespace NetEvolve.FrameShift.Execution;

/// <summary>
/// The execution-based verdict of a single mutant, as opposed to the static verdicts the analyzer
/// reports (<c>FSH0001</c>, <c>FSH0006</c>, <c>FSH0007</c>): this one comes from actually running code.
/// </summary>
internal enum MutantVerdict
{
    /// <summary>
    /// The mutant assembly ran and the test method failed: the mutation is a real gap the static
    /// heuristics missed if the analyzer had reported this member as covered.
    /// </summary>
    Killed,

    /// <summary>
    /// The mutant assembly ran and the test method passed, exactly as it does against the original
    /// program: the test does not actually notice this mutation.
    /// </summary>
    Survived,

    /// <summary>
    /// The mutant did not compile at all, so it was never a real mutation of the program and is excluded
    /// from the mutation score the same way the analyzer excludes a mutant
    /// <see cref="Mutations.MutantViability.DoesNotCompile" /> ever reaches classification for.
    /// </summary>
    BuildFailed,
}
