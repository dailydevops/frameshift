namespace NetEvolve.FrameShift.Configuration;

using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis.Diagnostics;
using NetEvolve.FrameShift.Mutations;

/// <summary>
/// The strongly typed configuration of FrameShift, as read from the MSBuild properties the build assets
/// of the package expose to the analyzers.
/// </summary>
internal sealed class FrameShiftOptions
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

    /// <summary>
    /// The default of <see cref="EnableRegexPatternMutations" />.
    /// </summary>
    public const bool DefaultEnableRegexPatternMutations = true;

    /// <summary>
    /// The token of <see cref="TestAnalyzers" /> that stands for every test-surface analyzer running
    /// exactly as it would without this option, i.e. awake purely by its own probe's detection.
    /// </summary>
    public const string DiscoveryToken = "Discovery";

    /// <summary>
    /// The default of <see cref="TestAnalyzers" />, as a raw MSBuild property value.
    /// </summary>
    public const string DefaultTestAnalyzers = DiscoveryToken;

    private static readonly ImmutableHashSet<string> _defaultTestAnalyzers = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        DiscoveryToken
    );

    private FrameShiftOptions(
        bool isEnabled,
        bool verifyMutantCompilation,
        int maxMutantsPerMember,
        bool reportTrivialMutants,
        bool enableRegexPatternMutations,
        ImmutableHashSet<string> testAnalyzers
    )
    {
        IsEnabled = isEnabled;
        VerifyMutantCompilation = verifyMutantCompilation;
        MaxMutantsPerMember =
            maxMutantsPerMember < MinimumMaxMutantsPerMember ? MinimumMaxMutantsPerMember : maxMutantsPerMember;
        ReportTrivialMutants = reportTrivialMutants;
        EnableRegexPatternMutations = enableRegexPatternMutations;
        TestAnalyzers = testAnalyzers;
    }

    /// <summary>
    /// Gets the options every analyzer falls back to when nothing is configured.
    /// </summary>
    public static FrameShiftOptions Default { get; } =
        new FrameShiftOptions(
            DefaultIsEnabled,
            DefaultVerifyMutantCompilation,
            DefaultMaxMutantsPerMember,
            DefaultReportTrivialMutants,
            DefaultEnableRegexPatternMutations,
            _defaultTestAnalyzers
        );

    /// <summary>
    /// Gets a value indicating whether FrameShift analyses the current compilation at all.
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
    /// Gets a value indicating whether the operators of the regular expression pattern family produce
    /// mutations.
    /// </summary>
    /// <remarks>
    /// The family has a switch of its own because it multiplies the mutation points of a single member:
    /// one forty character pattern can carry dozens of anchors, quantifiers, groups and branches, and
    /// every one of them is a mutation point. Turning the family off has to be possible without losing
    /// the other operators, and it has to happen before <see cref="MaxMutantsPerMember" /> is consulted -
    /// otherwise the budget of a member would be spent on pattern mutants and would hide the mutation
    /// points of the surrounding code.
    /// </remarks>
    public bool EnableRegexPatternMutations { get; }

    /// <summary>
    /// Gets the configured tokens of <c>FrameShiftTestAnalyzers</c>, compared ordinally and ignoring
    /// case. Never empty: an absent, blank or entirely unparseable property falls back to
    /// <see cref="DiscoveryToken" />, exactly like every other option here falls back to its default.
    /// </summary>
    public ImmutableHashSet<string> TestAnalyzers { get; }

    /// <summary>
    /// Determines whether the test-surface analyzer identified by <paramref name="configurationToken" />
    /// is allowed to run at all under this configuration.
    /// </summary>
    /// <param name="configurationToken">
    /// The <see cref="TestSurface.ITestFrameworkProbe.ConfigurationToken" /> of the analyzer asking.
    /// </param>
    /// <returns>
    /// <see langword="true" /> when <see cref="TestAnalyzers" /> contains <see cref="DiscoveryToken" /> —
    /// which is both the default and an explicit "run everything" — or names
    /// <paramref name="configurationToken" /> itself; otherwise <see langword="false" />.
    /// </returns>
    public bool IsTestAnalyzerEnabled(string configurationToken) =>
        TestAnalyzers.Contains(DiscoveryToken) || TestAnalyzers.Contains(configurationToken);

    /// <summary>
    /// Reads the options from the analyzer configuration of the current compilation.
    /// </summary>
    /// <param name="options">The analyzer configuration holding the visible MSBuild properties.</param>
    /// <returns>
    /// The configured options; every value that is absent or unparseable falls back to its documented
    /// default.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="options" /> is <see langword="null" />.</exception>
    public static FrameShiftOptions Read(AnalyzerConfigOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        return new FrameShiftOptions(
            ReadBoolean(options, FrameShiftOptionKeys.Enabled, DefaultIsEnabled),
            ReadBoolean(options, FrameShiftOptionKeys.VerifyMutantCompilation, DefaultVerifyMutantCompilation),
            ReadInt32(options, FrameShiftOptionKeys.MaxMutantsPerMember, DefaultMaxMutantsPerMember),
            ReadBoolean(options, FrameShiftOptionKeys.ReportTrivialMutants, DefaultReportTrivialMutants),
            ReadBoolean(options, FrameShiftOptionKeys.EnableRegexPatternMutations, DefaultEnableRegexPatternMutations),
            ReadTestAnalyzers(options, FrameShiftOptionKeys.TestAnalyzers)
        );
    }

    /// <summary>
    /// Determines whether the operators of <paramref name="kind" /> may produce mutations under this
    /// configuration.
    /// </summary>
    /// <param name="kind">The operator family to ask about.</param>
    /// <returns>
    /// <see langword="false" /> only for a family the configuration switched off; every family without a
    /// switch of its own is always enabled.
    /// </returns>
    /// <remarks>
    /// The mapping lives here rather than on <see cref="MutationKind" />, because it answers a
    /// configuration question and not a question about the operator. <c>MutationKind.RegexOptions</c> is
    /// deliberately not part of it: that operator mutates the option flags a call passes and belongs to
    /// the culture sensitivity family, not to the pattern family this switch governs.
    /// </remarks>
    public bool IsKindEnabled(MutationKind kind) =>
        kind switch
        {
            MutationKind.RegexAnchor
            or MutationKind.RegexQuantifier
            or MutationKind.RegexGroup
            or MutationKind.RegexAlternation
            or MutationKind.RegexCharacterClass
            or MutationKind.RegexEscape
            or MutationKind.RegexLookaround
            or MutationKind.RegexBackreference => EnableRegexPatternMutations,
            _ => true,
        };

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

    /// <summary>
    /// Reads the semicolon-separated <c>FrameShiftTestAnalyzers</c> option, trimming and discarding any
    /// blank entry. Falls back to <see cref="DiscoveryToken" /> - the same outcome as every other option
    /// here on an absent or unparseable value - whenever nothing usable remains, which covers the
    /// property being entirely absent, empty, made only of separators, or made only of whitespace.
    /// </summary>
    /// <param name="options">The analyzer configuration holding the visible MSBuild properties.</param>
    /// <param name="key">The key of the option.</param>
    /// <returns>The configured tokens, compared ordinally and ignoring case; never empty.</returns>
    private static ImmutableHashSet<string> ReadTestAnalyzers(AnalyzerConfigOptions options, string key)
    {
        if (!options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return _defaultTestAnalyzers;
        }

        var tokens = value
            .Split(';')
            .Select(token => token.Trim())
            .Where(token => token.Length > 0)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        return tokens.IsEmpty ? _defaultTestAnalyzers : tokens;
    }
}
