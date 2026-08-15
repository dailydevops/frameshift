namespace NetEvolve.FrameShift.Tests.Unit.Infrastructure;

using System.Diagnostics.CodeAnalysis;

/// <summary>
/// Cancellation helpers that behave the same on every target framework of the test matrix.
/// </summary>
/// <remarks>
/// <c>CancellationTokenSource.CancelAsync()</c> was introduced with .NET 8, but the tests also run on
/// net6.0, net7.0 and the classic .NET Framework targets, where only the synchronous
/// <see cref="CancellationTokenSource.Cancel()" /> exists. Keeping the difference in this one place
/// means no test file needs a conditional block of its own. The asynchronous member is named in prose
/// rather than referenced, because a <c>cref</c> to it does not resolve on the frameworks that lack it.
/// </remarks>
internal static class CancellationTokenSourceExtensions
{
    /// <summary>
    /// Cancels <paramref name="source" />, asynchronously where the framework supports it.
    /// </summary>
    /// <param name="source">The source to cancel.</param>
    /// <returns>
    /// A task that completes once the registered callbacks have run. On frameworks without
    /// <c>CancelAsync()</c> the callbacks run synchronously and the returned task is already completed.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is <see langword="null" />.</exception>
#if !NET8_0_OR_GREATER
    [SuppressMessage(
        "Reliability",
        "CA1849:Call async methods when in an async method",
        Justification = "The asynchronous overload does not exist before .NET 8, which is the reason this helper exists."
    )]
    [SuppressMessage(
        "Usage",
        "VSTHRD103:Call async methods when in an async method",
        Justification = "The asynchronous overload does not exist before .NET 8, which is the reason this helper exists."
    )]
#endif
    public static Task CancelAsyncCompat(this CancellationTokenSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

#if NET8_0_OR_GREATER
        return source.CancelAsync();
#else
        source.Cancel();
        return Task.CompletedTask;
#endif
    }
}
