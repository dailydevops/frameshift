namespace NetEvolve.FrameShift.TestSurface;

using Microsoft.CodeAnalysis;

/// <summary>
/// Detects whether a compilation uses a particular test framework and, if it does, supplies the
/// recogniser for that framework's test methods.
/// </summary>
/// <remarks>
/// <para>
/// This is the single seam through which a test framework is plugged into FrameShift. Supporting a
/// further framework means adding one probe, one <see cref="ITestMethodRecognizer" /> and one thin
/// analyzer that hands the probe to the shared analysis; nothing framework-neutral has to change.
/// </para>
/// <para>
/// The two decisions a framework adapter makes fail in opposite directions on purpose, and a fifth
/// framework has to follow the same rule to behave like the four that exist.
/// </para>
/// <para>
/// <em>Detecting the framework fails open.</em> A probe hands out a recogniser as soon as
/// <em>either</em> the framework's well-known test attribute type resolves <em>or</em> the compilation
/// references one of the framework's assemblies — never only when both hold.
/// <see cref="Compilation.GetTypeByMetadataName(string)" /> answers <see langword="null" /> not just for
/// a type that is absent but also for one that is ambiguous, which is exactly what happens when a
/// compilation references two major versions of the same framework declaring identical type names.
/// Demanding the resolved type as well would then read that ambiguity as absence and switch the entire
/// analysis off for a project that unmistakably uses the framework — silently, because a probe reporting
/// absence produces no diagnostic at all. A recogniser created on the assembly rule alone is harmless by
/// construction: it falls back to matching an attribute by its simple name plus its declaring assembly,
/// and where no rule matches it simply finds no tests, which is the same outcome as an empty test
/// project.
/// </para>
/// <para>
/// <em>Judging a method fails closed.</em> The <see cref="ITestMethodRecognizer" /> a probe returns
/// accepts a method only on positive evidence: an attribute that is, or derives from, the resolved
/// well-known type, or one whose simple name matches <em>and</em> whose declaring assembly belongs to the
/// framework. The assembly half of the second rule is what carries the fail-closed weight — anyone may
/// declare a type called <c>TestAttribute</c> or <c>TestMethodAttribute</c>, so a name alone must never
/// make a method a test — and it is why the generous detection above costs nothing: every downstream
/// judgement about mutation coverage rests on this one decision.
/// </para>
/// <para>
/// When <see cref="TryCreateRecognizer(Compilation)" /> returns <see langword="null" />, the analysis
/// using it shuts down completely and reports nothing at all, so a compilation that shows no trace of
/// the framework can never be judged by it.
/// </para>
/// <para>
/// Both halves compare an assembly name with <see cref="StringComparison.OrdinalIgnoreCase" />, and a new
/// probe must do the same. Assembly identities are not case-sensitive, and a false negative here means
/// silently analysing nothing, so the casing a reference hint, facade assembly or repackaged build happens
/// to use must never decide whether FrameShift runs.
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
