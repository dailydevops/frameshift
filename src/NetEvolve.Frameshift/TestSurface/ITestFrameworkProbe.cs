namespace NetEvolve.Frameshift.TestSurface;

using Microsoft.CodeAnalysis;

/// <summary>
/// Detects whether a compilation uses a particular test framework and, if it does, supplies the
/// recogniser for that framework's test methods.
/// </summary>
/// <remarks>
/// <para>
/// This is the single seam through which a test framework is plugged into Frameshift. Supporting a
/// further framework means adding one probe, one <see cref="ITestMethodRecognizer" /> and one thin
/// analyzer that hands the probe to the shared analysis; nothing framework-neutral has to change.
/// </para>
/// <para>
/// A probe deliberately reports absence rather than guessing. When
/// <see cref="TryCreateRecognizer(Compilation)" /> returns <see langword="null" />, the analysis using
/// it shuts down completely and reports nothing at all, so a compilation that does not belong to this
/// framework can never be judged by it.
/// </para>
/// </remarks>
internal interface ITestFrameworkProbe
{
    /// <summary>
    /// Gets the display name of the probed test framework, used in diagnostic messages.
    /// </summary>
    string FrameworkName { get; }

    /// <summary>
    /// Creates the recogniser for <paramref name="compilation" /> if the probed framework is present.
    /// </summary>
    /// <param name="compilation">The compilation to probe.</param>
    /// <returns>
    /// The recogniser for the compilation, or <see langword="null" /> if the compilation does not use
    /// the probed framework.
    /// </returns>
    ITestMethodRecognizer? TryCreateRecognizer(Compilation compilation);
}
