namespace NetEvolve.FrameShift.Execution;

/// <summary>
/// The format the end-of-run <see cref="ExecutionReport" /> is written in.
/// </summary>
internal enum ReportFormat
{
    /// <summary>
    /// Plain text, written through a <see cref="TextWriter" />.
    /// </summary>
    Console,

    /// <summary>
    /// A single self-contained HTML document.
    /// </summary>
    Html,

    /// <summary>
    /// GitHub-flavored Markdown.
    /// </summary>
    Markdown,

    /// <summary>
    /// GitHub-flavored Markdown, appended to the file named by the <c>GITHUB_STEP_SUMMARY</c> environment
    /// variable instead of any file <see cref="ExecutionCliOptions.ReportPath" /> names, so a run inside a
    /// GitHub Actions job shows up as that job's summary without a caller having to know that file's path.
    /// </summary>
    GitHubSummary,
}
