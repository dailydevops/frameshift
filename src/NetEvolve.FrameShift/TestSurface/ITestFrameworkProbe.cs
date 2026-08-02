namespace NetEvolve.FrameShift.TestSurface;

using Microsoft.CodeAnalysis;

/// <summary>
/// Detects whether a compilation uses one particular version of one particular test framework and, if it
/// does, supplies the recogniser for that version's test methods.
/// </summary>
/// <remarks>
/// <para>
/// This is the single seam through which a test framework is plugged into FrameShift. Supporting a
/// further framework means adding one probe, one <see cref="ITestMethodRecognizer" /> and one thin
/// analyzer that hands the probe to the shared analysis; nothing framework-neutral has to change.
/// </para>
/// <para>
/// <em>A probe stands for a framework version, not for a framework.</em> Where two major versions of one
/// framework declare their test attributes in different assemblies, each version gets its own probe, its
/// own recogniser, its own analyzer and its own <see cref="FrameworkName" />, and the versions are listed
/// as separate entries of <see cref="TestFrameworkProbeRegistry.All" />. That is what allows a probe to
/// resolve its well-known type through <see cref="IAssemblySymbol.GetTypeByMetadataName(string)" /> on the
/// one assembly that is supposed to declare it, which is exact even when a compilation references both
/// versions and <see cref="Compilation.GetTypeByMetadataName(string)" /> would answer
/// <see langword="null" /> for the ambiguous name. A probe therefore never has to guess a version from a
/// type it cannot resolve.
/// </para>
/// <para>
/// <em>Several probes may match the same compilation, and the same methods in it.</em> A project is free
/// to reference two frameworks, or two versions of one framework, at the same time, so
/// <see cref="TryCreateRecognizer(Compilation)" /> answering non-<see langword="null" /> is never an
/// exclusive claim. Two probes may even recognise the very same test method — the two versions of one
/// framework describe the same tests by design, and a single method can carry the test attributes of two
/// frameworks at once. A probe must therefore judge nothing but its own framework version and must not
/// assume it is alone; coordinating what the developer finally reads about a compilation is the business
/// of the shared analysis, which elects one framework to report the test-surface manifest and reports a
/// test method that several frameworks recognise only once.
/// </para>
/// <para>
/// The two decisions a framework adapter makes fail in opposite directions on purpose, and a further
/// framework has to follow the same rule to behave like the existing ones.
/// </para>
/// <para>
/// <em>Detecting the framework fails open.</em> A probe hands out a recogniser as soon as
/// <em>either</em> the framework's well-known test attribute type resolves in the framework assembly
/// <em>or</em> the compilation references one of the framework's assemblies — never only when both hold.
/// A probe that reports absence produces no diagnostic whatsoever, so reading a framework that is
/// unmistakably in use as absent would switch the whole analysis off silently: precisely the failure
/// nobody notices. A repackaged, renamed or trimmed build of the framework, or one whose well-known type
/// has moved, must not have that effect. A recogniser created on the assembly rule alone is harmless by
/// construction, because it has no resolved type to compare against and therefore finds no tests, which
/// is the same outcome as an empty test project.
/// </para>
/// <para>
/// <em>Judging a method fails closed.</em> The <see cref="ITestMethodRecognizer" /> a probe returns
/// accepts a method only on positive evidence: an attribute that is, or derives from, the well-known type
/// the probe resolved out of the framework's own assembly. A simple type name is never evidence on its
/// own — anyone may declare a type called <c>FactAttribute</c>, <c>TestAttribute</c> or
/// <c>TestMethodAttribute</c> — and neither is an attribute of the other version of the same framework.
/// This one decision is what every downstream judgement about mutation coverage rests on, and it is why
/// the generous detection above costs nothing.
/// </para>
/// <para>
/// When <see cref="TryCreateRecognizer(Compilation)" /> returns <see langword="null" />, the analysis
/// using it shuts down completely and reports nothing at all, so a compilation that shows no trace of
/// the framework can never be judged by it.
/// </para>
/// <para>
/// Every comparison of an assembly name uses <see cref="StringComparison.OrdinalIgnoreCase" />, and a new
/// probe must do the same. Assembly identities are not case-sensitive, and a false negative here means
/// silently analysing nothing, so the casing a reference hint, facade assembly or repackaged build happens
/// to use must never decide whether FrameShift runs.
/// </para>
/// </remarks>
internal interface ITestFrameworkProbe
{
    /// <summary>
    /// Gets the display name of the probed test framework, used in diagnostic messages. It names the
    /// framework version the probe stands for, so that two probes of one framework are told apart wherever
    /// the name is read.
    /// </summary>
    string FrameworkName { get; }

    /// <summary>
    /// Gets the identifier this probe's framework version is selected by in the
    /// <c>FrameShiftTestAnalyzers</c> MSBuild property, e.g. <c>"XunitV2"</c> for the framework whose
    /// <see cref="FrameworkName" /> is <c>"xUnit v2"</c>. Unlike <see cref="FrameworkName" />, which is
    /// meant to be read by a person in a diagnostic message, this is meant to be typed into a
    /// semicolon-separated MSBuild property value and therefore carries no space.
    /// </summary>
    string ConfigurationToken { get; }

    /// <summary>
    /// Creates the recogniser for <paramref name="compilation" /> if the probed framework version is
    /// present.
    /// </summary>
    /// <param name="compilation">The compilation to probe.</param>
    /// <returns>
    /// The recogniser for the compilation, or <see langword="null" /> if the compilation does not use
    /// the probed framework version.
    /// </returns>
    /// <remarks>
    /// Answering non-<see langword="null" /> claims nothing about the other probes: further probes may
    /// recognise the same compilation, and a compilation referencing two versions of one framework is
    /// recognised by both of that framework's probes.
    /// </remarks>
    ITestMethodRecognizer? TryCreateRecognizer(Compilation compilation);
}
