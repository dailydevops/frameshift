namespace NetEvolve.FrameShift.TestSurface;

using Microsoft.CodeAnalysis;

/// <summary>
/// Recognises the test methods of a single compilation for one version of one test framework. An instance
/// is created per compilation by an <see cref="ITestFrameworkProbe" />, so that the symbols the framework
/// is identified by are resolved exactly once.
/// </summary>
/// <remarks>
/// <para>
/// <em>Recognising a method fails closed</em>, which is the opposite direction from the fail-open
/// detection of <see cref="ITestFrameworkProbe.TryCreateRecognizer(Compilation)" />. Nothing but positive
/// evidence makes a method a test: an attribute that is, or derives from, the well-known test attribute
/// type the probe resolved out of the framework's own assembly. A matching simple type name is not
/// evidence, because any assembly may declare a type of that name, and an attribute belonging to another
/// version of the same framework is not evidence either. A recogniser created by a probe that only saw a
/// reference to the framework, without resolving its well-known type, consequently recognises nothing at
/// all — the same, harmless outcome as an empty test project. Every judgement FrameShift makes about
/// mutation coverage rests on this decision, so a false positive here is far more expensive than a false
/// negative.
/// </para>
/// <para>
/// A recogniser answers only for its own framework version and never asks whether another one would answer
/// the same. Two recognisers may well accept the same method: the two versions of one framework describe
/// the same tests by design, and a single method can carry the test attributes of two frameworks at once.
/// Keeping the developer from hearing about such a method twice is the business of the shared analysis,
/// not of a recogniser.
/// </para>
/// <para>
/// Implementations must be immutable and thread-safe, because analyzer callbacks run concurrently.
/// </para>
/// </remarks>
internal interface ITestMethodRecognizer
{
    /// <summary>
    /// Gets the display name of the recognised test framework, used in diagnostic messages. It names the
    /// framework version, matching the <see cref="ITestFrameworkProbe.FrameworkName" /> of the probe that
    /// created the recogniser.
    /// </summary>
    string FrameworkName { get; }

    /// <summary>
    /// Determines whether <paramref name="method" /> is a test method of the recognised framework version.
    /// </summary>
    /// <param name="method">The method to inspect.</param>
    /// <returns>
    /// <see langword="true" /> if the method is a test method; otherwise <see langword="false" />.
    /// </returns>
    /// <remarks>
    /// Answering <see langword="true" /> claims nothing exclusively: another framework's recogniser may
    /// accept the same method.
    /// </remarks>
    bool IsTestMethod(IMethodSymbol method);
}
