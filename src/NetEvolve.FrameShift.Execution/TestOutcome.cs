namespace NetEvolve.FrameShift.Execution;

/// <summary>
/// Whether a single invocation of a test method, inside an isolated mutant assembly, completed or threw.
/// </summary>
internal enum TestOutcome
{
    /// <summary>
    /// The test method returned without throwing.
    /// </summary>
    Passed,

    /// <summary>
    /// The test method threw, or an awaited <see cref="Task" /> it returned faulted.
    /// </summary>
    Failed,
}
