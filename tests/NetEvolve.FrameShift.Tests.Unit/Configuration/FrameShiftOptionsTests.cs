namespace NetEvolve.FrameShift.Tests.Unit.Configuration;

using System.Globalization;
using NetEvolve.FrameShift.Configuration;
using NetEvolve.FrameShift.Mutations;
using NetEvolve.FrameShift.Tests.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Tests <see cref="FrameShiftOptions" />. The configuration is read from a build, therefore a value a
/// user mistyped must never fail the build and must never silently turn the analysis into something
/// else than the documented default.
/// </summary>
public class FrameShiftOptionsTests
{
    /// <summary>
    /// The mutation kinds the regular expression pattern switch governs, ordered ordinally.
    /// <c>MutationKind.RegexOptions</c> is deliberately not among them: it belongs to the culture
    /// sensitivity family.
    /// </summary>
    private const string PatternKindNames =
        "RegexAlternation, RegexAnchor, RegexBackreference, RegexCharacterClass, RegexEscape, RegexGroup, RegexLookaround, RegexQuantifier";

    private static string DocumentedDefaults =>
        Describe(
            FrameShiftOptions.DefaultIsEnabled,
            FrameShiftOptions.DefaultVerifyMutantCompilation,
            FrameShiftOptions.DefaultMaxMutantsPerMember,
            FrameShiftOptions.DefaultReportTrivialMutants,
            FrameShiftOptions.DefaultEnableRegexPatternMutations
        );

    [Test]
    public async Task Default_WithoutConfiguration_MatchesTheDocumentedDefaults()
    {
        var options = FrameShiftOptions.Default;

        _ = await Assert.That(Describe(options)).IsEqualTo(DocumentedDefaults);
    }

    [Test]
    public async Task Read_WithoutAnyOption_ReturnsTheDefaults()
    {
        var options = FrameShiftOptions.Read(TestAnalyzerConfigOptions.Empty);

        _ = await Assert.That(Describe(options)).IsEqualTo(DocumentedDefaults);
    }

    [Test]
    [Arguments("false", false)]
    [Arguments("true", true)]
    [Arguments("TRUE", true)]
    [Arguments("False", false)]
    [Arguments("  false  ", false)]
    [Arguments("yes", FrameShiftOptions.DefaultIsEnabled)]
    [Arguments("0", FrameShiftOptions.DefaultIsEnabled)]
    [Arguments("", FrameShiftOptions.DefaultIsEnabled)]
    public async Task Read_Enabled_IsParsedOrFallsBack(string value, bool expected)
    {
        var options = Read(FrameShiftOptionKeys.Enabled, value);

        _ = await Assert.That(options.IsEnabled).IsEqualTo(expected);
    }

    [Test]
    [Arguments("false", false)]
    [Arguments("FALSE", false)]
    [Arguments("true", true)]
    [Arguments("nonsense", FrameShiftOptions.DefaultVerifyMutantCompilation)]
    public async Task Read_VerifyMutantCompilation_IsParsedOrFallsBack(string value, bool expected)
    {
        var options = Read(FrameShiftOptionKeys.VerifyMutantCompilation, value);

        _ = await Assert.That(options.VerifyMutantCompilation).IsEqualTo(expected);
    }

    [Test]
    [Arguments("false", false)]
    [Arguments("False", false)]
    [Arguments("true", true)]
    [Arguments("nonsense", FrameShiftOptions.DefaultReportTrivialMutants)]
    public async Task Read_ReportTrivialMutants_IsParsedOrFallsBack(string value, bool expected)
    {
        var options = Read(FrameShiftOptionKeys.ReportTrivialMutants, value);

        _ = await Assert.That(options.ReportTrivialMutants).IsEqualTo(expected);
    }

    [Test]
    [Arguments("false", false)]
    [Arguments("FALSE", false)]
    [Arguments("True", true)]
    [Arguments("  false  ", false)]
    [Arguments("off", FrameShiftOptions.DefaultEnableRegexPatternMutations)]
    [Arguments("", FrameShiftOptions.DefaultEnableRegexPatternMutations)]
    public async Task Read_EnableRegexPatternMutations_IsParsedOrFallsBack(string value, bool expected)
    {
        var options = Read(FrameShiftOptionKeys.EnableRegexPatternMutations, value);

        _ = await Assert.That(options.EnableRegexPatternMutations).IsEqualTo(expected);
    }

    /// <summary>
    /// The regular expression pattern family is the only one with a switch of its own, so with the switch
    /// on nothing at all is disabled - including every kind a future release adds, which is why the kinds
    /// are taken from the enumeration instead of being listed here.
    /// </summary>
    [Test]
    public async Task IsKindEnabled_PatternFamilyEnabled_EnablesEveryKind()
    {
        var configured = Read(FrameShiftOptionKeys.EnableRegexPatternMutations, "true");

        _ = await Assert.That(DisabledKinds(FrameShiftOptions.Default)).IsEqualTo(string.Empty);
        _ = await Assert.That(DisabledKinds(configured)).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// Switching the pattern family off must cost exactly the four pattern kinds. Above all it must not
    /// touch <c>MutationKind.RegexOptions</c>, which mutates the option flags a call passes and belongs to
    /// the culture sensitivity family.
    /// </summary>
    [Test]
    public async Task IsKindEnabled_PatternFamilyDisabled_DisablesExactlyThePatternKinds()
    {
        var options = Read(FrameShiftOptionKeys.EnableRegexPatternMutations, "false");

        _ = await Assert.That(options.EnableRegexPatternMutations).IsFalse();
        _ = await Assert.That(DisabledKinds(options)).IsEqualTo(PatternKindNames);
        _ = await Assert.That(options.IsKindEnabled(MutationKind.RegexOptions)).IsTrue();
        _ = await Assert.That(options.IsKindEnabled(MutationKind.StringLiteral)).IsTrue();
    }

    [Test]
    [Arguments("7", 7)]
    [Arguments("  12  ", 12)]
    [Arguments("+7", 7)]
    [Arguments("1", FrameShiftOptions.MinimumMaxMutantsPerMember)]
    [Arguments("abc", FrameShiftOptions.DefaultMaxMutantsPerMember)]
    [Arguments("", FrameShiftOptions.DefaultMaxMutantsPerMember)]
    [Arguments("1.5", FrameShiftOptions.DefaultMaxMutantsPerMember)]
    [Arguments("1,000", FrameShiftOptions.DefaultMaxMutantsPerMember)]
    [Arguments("99999999999999999999", FrameShiftOptions.DefaultMaxMutantsPerMember)]
    public async Task Read_MaxMutantsPerMember_IsParsedOrFallsBack(string value, int expected)
    {
        var options = Read(FrameShiftOptionKeys.MaxMutantsPerMember, value);

        _ = await Assert.That(options.MaxMutantsPerMember).IsEqualTo(expected);
    }

    [Test]
    [Arguments("0")]
    [Arguments("-1")]
    [Arguments("-64")]
    public async Task Read_MaxMutantsPerMemberIsNotPositive_IsClampedToTheMinimum(string value)
    {
        var options = Read(FrameShiftOptionKeys.MaxMutantsPerMember, value);

        _ = await Assert.That(options.MaxMutantsPerMember).IsEqualTo(FrameShiftOptions.MinimumMaxMutantsPerMember);
    }

    [Test]
    public async Task Read_MaxMutantsPerMember_UsesTheInvariantCulture()
    {
        // Swedish formats a negative number with U+2212, so a culture sensitive parse would accept
        // that sign and reject the ASCII one. Reading the configuration must not depend on the
        // culture of the build machine.
        var minusSign = new string((char)0x2212, count: 1);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");

            var invariantSign = Read(FrameShiftOptionKeys.MaxMutantsPerMember, "-5");
            var cultureSign = Read(FrameShiftOptionKeys.MaxMutantsPerMember, minusSign + "5");

            using (Assert.Multiple())
            {
                _ = await Assert
                    .That(invariantSign.MaxMutantsPerMember)
                    .IsEqualTo(FrameShiftOptions.MinimumMaxMutantsPerMember);
                _ = await Assert
                    .That(cultureSign.MaxMutantsPerMember)
                    .IsEqualTo(FrameShiftOptions.DefaultMaxMutantsPerMember);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    public async Task Read_DifferentlyCasedKey_IsFoundCaseInsensitively()
    {
        var options = Read(FrameShiftOptionKeys.Enabled.ToUpperInvariant(), "false");

        _ = await Assert.That(options.IsEnabled).IsFalse();
    }

    [Test]
    public async Task Read_EveryOptionConfigured_ReadsEachOneIndependently()
    {
        var options = FrameShiftOptions.Read(
            new TestAnalyzerConfigOptions(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [FrameShiftOptionKeys.Enabled] = "true",
                    [FrameShiftOptionKeys.VerifyMutantCompilation] = "false",
                    [FrameShiftOptionKeys.MaxMutantsPerMember] = "9",
                    [FrameShiftOptionKeys.ReportTrivialMutants] = "false",
                    [FrameShiftOptionKeys.EnableRegexPatternMutations] = "false",
                }
            )
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(options.IsEnabled).IsTrue();
            _ = await Assert.That(options.VerifyMutantCompilation).IsFalse();
            _ = await Assert.That(options.MaxMutantsPerMember).IsEqualTo(9);
            _ = await Assert.That(options.ReportTrivialMutants).IsFalse();
            _ = await Assert.That(options.EnableRegexPatternMutations).IsFalse();
        }
    }

    [Test]
    public async Task Read_OptionsAreNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => _ = FrameShiftOptions.Read(null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("options");
    }

    /// <summary>
    /// An MSBuild property that is declared but never given a value reaches the analyzer as a key that is
    /// present and a value that is not there. Reading it must land on the documented default, exactly like
    /// a value nobody could parse, and it must never make the build fail.
    /// </summary>
    [Test]
    public async Task Read_EveryOptionPresentWithoutAValue_ReturnsTheDefaults()
    {
        var options = FrameShiftOptions.Read(
            new TestAnalyzerConfigOptions(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [FrameShiftOptionKeys.Enabled] = null!,
                    [FrameShiftOptionKeys.VerifyMutantCompilation] = null!,
                    [FrameShiftOptionKeys.MaxMutantsPerMember] = null!,
                    [FrameShiftOptionKeys.ReportTrivialMutants] = null!,
                    [FrameShiftOptionKeys.EnableRegexPatternMutations] = null!,
                }
            )
        );

        _ = await Assert.That(Describe(options)).IsEqualTo(DocumentedDefaults);
    }

    private static FrameShiftOptions Read(string key, string value) =>
        FrameShiftOptions.Read(
            new TestAnalyzerConfigOptions(new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value })
        );

    private static string Describe(FrameShiftOptions options) =>
        Describe(
            options.IsEnabled,
            options.VerifyMutantCompilation,
            options.MaxMutantsPerMember,
            options.ReportTrivialMutants,
            options.EnableRegexPatternMutations
        );

    private static string Describe(
        bool isEnabled,
        bool verifyCompilation,
        int maxMutants,
        bool reportTrivial,
        bool enableRegexPatterns
    ) =>
        string.Join(
            "|",
            isEnabled.ToString(CultureInfo.InvariantCulture),
            verifyCompilation.ToString(CultureInfo.InvariantCulture),
            maxMutants.ToString(CultureInfo.InvariantCulture),
            reportTrivial.ToString(CultureInfo.InvariantCulture),
            enableRegexPatterns.ToString(CultureInfo.InvariantCulture)
        );

    /// <summary>
    /// Names the mutation kinds <paramref name="options" /> switched off, ordered ordinally so that the
    /// result is a single string a test can compare in one go.
    /// </summary>
    /// <param name="options">The configuration to ask.</param>
    /// <returns>The disabled kinds, comma separated, or an empty string when none is disabled.</returns>
    private static string DisabledKinds(FrameShiftOptions options) =>
        string.Join(
            ", ",
            Enum.GetValues<MutationKind>()
                .Where(kind => !options.IsKindEnabled(kind))
                .Select(kind => kind.ToString())
                .OrderBy(name => name, StringComparer.Ordinal)
        );
}
