namespace NetEvolve.Frameshift.Configuration;

using System.Globalization;
using Microsoft.CodeAnalysis.Diagnostics;

/// <summary>
/// The strongly typed configuration of Frameshift, as read from the MSBuild properties the build assets
/// of the package expose to the analyzers.
/// </summary>
internal sealed class FrameshiftOptions
{
    /// <summary>
    /// The default of <see cref="IsEnabled" />.
    /// </summary>
    public const bool DefaultIsEnabled = true;

    /// <summary>
    /// The default of <see cref="VerifyMutantCompilation" />.
    /// </summary>
    public const bool DefaultVerifyMutantCompilation = true;

    /// <summary>
    /// The default of <see cref="MaxMutantsPerMember" />.
    /// </summary>
    public const int DefaultMaxMutantsPerMember = 64;

    /// <summary>
    /// The smallest value <see cref="MaxMutantsPerMember" /> can take.
    /// </summary>
    public const int MinimumMaxMutantsPerMember = 1;

    /// <summary>
    /// The default of <see cref="ReportTrivialMutants" />.
    /// </summary>
    public const bool DefaultReportTrivialMutants = true;

    private FrameshiftOptions(
        bool isEnabled,
        bool verifyMutantCompilation,
        int maxMutantsPerMember,
        bool reportTrivialMutants
    )
    {
        IsEnabled = isEnabled;
        VerifyMutantCompilation = verifyMutantCompilation;
        MaxMutantsPerMember =
            maxMutantsPerMember < MinimumMaxMutantsPerMember ? MinimumMaxMutantsPerMember : maxMutantsPerMember;
        ReportTrivialMutants = reportTrivialMutants;
    }

    /// <summary>
    /// Gets the options every analyzer falls back to when nothing is configured.
    /// </summary>
    public static FrameshiftOptions Default { get; } =
        new FrameshiftOptions(
            DefaultIsEnabled,
            DefaultVerifyMutantCompilation,
            DefaultMaxMutantsPerMember,
            DefaultReportTrivialMutants
        );

    /// <summary>
    /// Gets a value indicating whether Frameshift analyses the current compilation at all.
    /// </summary>
    public bool IsEnabled { get; }

    /// <summary>
    /// Gets a value indicating whether every generated mutant is compiled before it is reported.
    /// </summary>
    public bool VerifyMutantCompilation { get; }

    /// <summary>
    /// Gets the maximum number of mutants generated for a single member, at least
    /// <see cref="MinimumMaxMutantsPerMember" />.
    /// </summary>
    public int MaxMutantsPerMember { get; }

    /// <summary>
    /// Gets a value indicating whether mutants without observable effect are reported.
    /// </summary>
    public bool ReportTrivialMutants { get; }

    /// <summary>
    /// Reads the options from the analyzer configuration of the current compilation.
    /// </summary>
    /// <param name="options">The analyzer configuration holding the visible MSBuild properties.</param>
    /// <returns>
    /// The configured options; every value that is absent or unparseable falls back to its documented
    /// default.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="options" /> is <see langword="null" />.</exception>
    public static FrameshiftOptions Read(AnalyzerConfigOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        return new FrameshiftOptions(
            ReadBoolean(options, FrameshiftOptionKeys.Enabled, DefaultIsEnabled),
            ReadBoolean(options, FrameshiftOptionKeys.VerifyMutantCompilation, DefaultVerifyMutantCompilation),
            ReadInt32(options, FrameshiftOptionKeys.MaxMutantsPerMember, DefaultMaxMutantsPerMember),
            ReadBoolean(options, FrameshiftOptionKeys.ReportTrivialMutants, DefaultReportTrivialMutants)
        );
    }

    /// <summary>
    /// Reads a single boolean option, accepting the values of <see cref="bool.TryParse" /> in any casing.
    /// </summary>
    /// <param name="options">The analyzer configuration holding the visible MSBuild properties.</param>
    /// <param name="key">The key of the option.</param>
    /// <param name="fallback">The value used when the option is absent or unparseable.</param>
    /// <returns>The configured value, or <paramref name="fallback" />.</returns>
    private static bool ReadBoolean(AnalyzerConfigOptions options, string key, bool fallback)
    {
        if (!options.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return bool.TryParse(value?.Trim(), out var parsed) ? parsed : fallback;
    }

    /// <summary>
    /// Reads a single integer option, using <see cref="CultureInfo.InvariantCulture" /> so that the result
    /// never depends on the culture of the build machine.
    /// </summary>
    /// <param name="options">The analyzer configuration holding the visible MSBuild properties.</param>
    /// <param name="key">The key of the option.</param>
    /// <param name="fallback">The value used when the option is absent or unparseable.</param>
    /// <returns>The configured value, or <paramref name="fallback" />.</returns>
    private static int ReadInt32(AnalyzerConfigOptions options, string key, int fallback)
    {
        if (!options.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }
}
