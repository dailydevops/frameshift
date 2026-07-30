namespace NetEvolve.Frameshift.Configuration;

/// <summary>
/// The <see cref="Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions" /> keys under which the
/// MSBuild configuration of Frameshift becomes visible to the analyzers.
/// </summary>
/// <remarks>
/// Every key is the MSBuild property name prefixed with <c>build_property.</c>, which is the form the
/// compiler generates for each <c>CompilerVisibleProperty</c>. The matching properties are declared in
/// the <c>NetEvolve.Frameshift.props</c> build asset shipped inside the package; both sides must be kept
/// in sync, therefore the names exist exactly once, here.
/// </remarks>
internal static class FrameshiftOptionKeys
{
    /// <summary>
    /// The prefix the compiler adds to every MSBuild property it exposes to analyzers.
    /// </summary>
    public const string BuildPropertyPrefix = "build_property.";

    /// <summary>
    /// MSBuild property <c>FrameshiftEnabled</c>: a <see cref="bool" /> switching the whole analysis on
    /// or off. Defaults to <see langword="true" />.
    /// </summary>
    public const string Enabled = BuildPropertyPrefix + "FrameshiftEnabled";

    /// <summary>
    /// MSBuild property <c>FrameshiftVerifyMutantCompilation</c>: a <see cref="bool" /> deciding whether
    /// every generated mutant is compiled before it is reported. Defaults to <see langword="true" />.
    /// </summary>
    public const string VerifyMutantCompilation = BuildPropertyPrefix + "FrameshiftVerifyMutantCompilation";

    /// <summary>
    /// MSBuild property <c>FrameshiftMaxMutantsPerMember</c>: an <see cref="int" /> capping the number of
    /// mutants generated for a single member. Defaults to <c>64</c> and is clamped to at least <c>1</c>.
    /// </summary>
    public const string MaxMutantsPerMember = BuildPropertyPrefix + "FrameshiftMaxMutantsPerMember";

    /// <summary>
    /// MSBuild property <c>FrameshiftReportTrivialMutants</c>: a <see cref="bool" /> deciding whether
    /// mutants without observable effect are reported. Defaults to <see langword="true" />.
    /// </summary>
    public const string ReportTrivialMutants = BuildPropertyPrefix + "FrameshiftReportTrivialMutants";
}
