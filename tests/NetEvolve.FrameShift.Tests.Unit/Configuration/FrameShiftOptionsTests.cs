namespace NetEvolve.FrameShift.Tests.Unit.Configuration;

using System.Globalization;
using NetEvolve.FrameShift.Configuration;
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
    private static string DocumentedDefaults =>
        Describe(
            FrameShiftOptions.DefaultIsEnabled,
            FrameShiftOptions.DefaultVerifyMutantCompilation,
            FrameShiftOptions.DefaultMaxMutantsPerMember,
            FrameShiftOptions.DefaultReportTrivialMutants
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

            _ = await Assert
                .That(invariantSign.MaxMutantsPerMember)
                .IsEqualTo(FrameShiftOptions.MinimumMaxMutantsPerMember);
            _ = await Assert
                .That(cultureSign.MaxMutantsPerMember)
                .IsEqualTo(FrameShiftOptions.DefaultMaxMutantsPerMember);
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
                }
            )
        );

        _ = await Assert.That(options.IsEnabled).IsTrue();
        _ = await Assert.That(options.VerifyMutantCompilation).IsFalse();
        _ = await Assert.That(options.MaxMutantsPerMember).IsEqualTo(9);
        _ = await Assert.That(options.ReportTrivialMutants).IsFalse();
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
            options.ReportTrivialMutants
        );

    private static string Describe(bool isEnabled, bool verifyCompilation, int maxMutants, bool reportTrivial) =>
        string.Join(
            "|",
            isEnabled.ToString(CultureInfo.InvariantCulture),
            verifyCompilation.ToString(CultureInfo.InvariantCulture),
            maxMutants.ToString(CultureInfo.InvariantCulture),
            reportTrivial.ToString(CultureInfo.InvariantCulture)
        );
}
