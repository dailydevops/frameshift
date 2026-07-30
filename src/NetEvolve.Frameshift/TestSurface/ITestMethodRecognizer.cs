namespace NetEvolve.Frameshift.TestSurface;

using Microsoft.CodeAnalysis;

/// <summary>
/// Recognises the test methods of a single compilation for one test framework. An instance is created
/// per compilation by an <see cref="ITestFrameworkProbe" />, so that the symbols a framework is
/// identified by are resolved exactly once.
/// </summary>
/// <remarks>
/// Implementations must be immutable and thread-safe, because analyzer callbacks run concurrently.
/// </remarks>
internal interface ITestMethodRecognizer
{
    /// <summary>
    /// Gets the display name of the recognised test framework, used in diagnostic messages.
    /// </summary>
    string FrameworkName { get; }

    /// <summary>
    /// Determines whether <paramref name="method" /> is a test method of the recognised framework.
    /// </summary>
    /// <param name="method">The method to inspect.</param>
    /// <returns>
    /// <see langword="true" /> if the method is a test method; otherwise <see langword="false" />.
    /// </returns>
    bool IsTestMethod(IMethodSymbol method);
}
