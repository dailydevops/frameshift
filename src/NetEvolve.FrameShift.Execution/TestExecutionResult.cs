namespace NetEvolve.FrameShift.Execution;

/// <summary>
/// The outcome of one test invocation, together with the exception that caused a failure, if any.
/// </summary>
internal sealed class TestExecutionResult
{
    private TestExecutionResult(TestOutcome outcome, Exception? failure)
    {
        Outcome = outcome;
        Failure = failure;
    }

    /// <summary>
    /// Gets whether the invocation passed or failed.
    /// </summary>
    public TestOutcome Outcome { get; }

    /// <summary>
    /// Gets the exception that made the invocation fail, or <see langword="null" /> when it passed.
    /// </summary>
    public Exception? Failure { get; }

    /// <summary>
    /// Creates a passing result.
    /// </summary>
    public static TestExecutionResult Passed() => new TestExecutionResult(TestOutcome.Passed, failure: null);

    /// <summary>
    /// Creates a failing result.
    /// </summary>
    /// <param name="failure">The exception the invocation threw or faulted with.</param>
    public static TestExecutionResult Failed(Exception failure) => new TestExecutionResult(TestOutcome.Failed, failure);
}
