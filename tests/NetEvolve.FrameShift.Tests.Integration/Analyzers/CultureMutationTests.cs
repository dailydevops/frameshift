namespace NetEvolve.FrameShift.Tests.Integration.Analyzers;

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using NetEvolve.FrameShift.Analyzers;
using NetEvolve.FrameShift.Diagnostics;
using NetEvolve.FrameShift.Tests.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Drives the six operators of the culture-sensitivity family through <see cref="MutationCoverageAnalyzer" />
/// end to end, so that the family is proven to produce the diagnostics a consumer sees in its build log
/// instead of merely to construct mutations.
/// </summary>
/// <remarks>
/// <para>
/// Every test states the exact set of reported gaps as one text block, built from the identifier, the
/// 1-based line and the full message of each diagnostic. That is stricter than a set of line numbers,
/// because a culture operator produces several mutants at one and the same location, and it is what makes
/// a test fail when an operator stops firing, starts firing twice or renames a mutation.
/// </para>
/// <para>
/// Each fixture pairs the member under inspection with <c>Fixture.Reached.Identity</c>, whose body carries
/// no mutation point at all. Naming that member in the manifest is what gives the analyzer a non-empty
/// reachable set — without one it reports an unusable manifest and stays silent about the code — while
/// contributing not a single diagnostic of its own.
/// </para>
/// </remarks>
public class CultureMutationTests
{
    private const string ProductionAssemblyName = "ProductionAssembly";

    /// <summary>
    /// The member every fixture declares to make the manifest resolvable, and whose <c>return value;</c>
    /// carries no mutation point.
    /// </summary>
    private const string AnchorMemberId = "M:Fixture.Reached.Identity(System.Int32)~System.Int32";

    /// <summary>
    /// The test method id every manifest of this fixture attributes its references to. No test asserts on
    /// it, because these tests state what the culture operators report, not which test reached what.
    /// </summary>
    private const string AnonymousTestId = "M:Fixture.Tests.AnonymousTests.Reaches";

    /// <summary>
    /// The case count recorded for <see cref="AnonymousTestId" />: a lower bound, because nothing here
    /// establishes how many input combinations the reaching test carries.
    /// </summary>
    private const string LowerBoundCount = "1+";

    /// <summary>
    /// The comparing member of <see cref="ComparisonSource" />, used to cover it instead of the anchor.
    /// </summary>
    private const string ComparisonMemberId = "M:Fixture.Names.AreSame(System.String,System.String)~System.Boolean";

    /// <summary>
    /// The text the assertions use for "not a single gap was reported".
    /// </summary>
    private const string NoGaps = "<no gaps>";

    /// <summary>
    /// The line feed the expectations are joined with, instead of <see cref="Environment.NewLine" />, so
    /// that the very same text is produced on Windows and on Linux.
    /// </summary>
    private const string LineFeed = "\n";

    private const int ComparisonLine = 17;
    private const int FormatLine = 17;
    private const int RemovableProviderLine = 23;
    private const int RequiredProviderLine = 28;
    private const int CaseConversionLine = 23;
    private const int RegexOptionsLine = 17;
    private const int DenseLine = 17;

    /// <summary>
    /// <c>Names.AreSame</c> on line 15 compares with <c>StringComparison.Ordinal</c> on line 17. Neither
    /// operand is a literal, so the comparison is the only mutation point of the whole file.
    /// </summary>
    private const string ComparisonSource = """
        namespace Fixture;

        using System;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Names
        {
            public static bool AreSame(string left, string right)
            {
                return string.Equals(left, right, StringComparison.Ordinal);
            }
        }
        """;

    /// <summary>
    /// <c>Amount.Format</c> formats with an explicit culture on line 17, which is a mutation point of two
    /// operators at once: the culture can be swapped, and the provider argument can be dropped.
    /// </summary>
    private const string CultureSource = """
        namespace Fixture;

        using System.Globalization;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Amount
        {
            public static string Format(int value)
            {
                return value.ToString(CultureInfo.InvariantCulture);
            }
        }
        """;

    /// <summary>
    /// The two halves of the provider removal. <c>int.ToString</c> on line 23 has a provider-less overload,
    /// so dropping the provider yields a mutant that binds. <c>IRenderer.Render</c> on line 28 is declared
    /// exactly once and takes the provider, so the same removal yields a mutant that does not compile.
    /// </summary>
    /// <remarks>
    /// The provider parameter of the interface method is what makes the second call unmutatable, and the
    /// interface declares no body at all, so it contributes no mutation point of its own.
    /// </remarks>
    private const string OverloadSource = """
        namespace Fixture;

        using System;
        using System.Globalization;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public interface IRenderer
        {
            string Render(int value, IFormatProvider provider);
        }

        public static class Renderer
        {
            public static string WithProviderLessOverload(int value)
            {
                return value.ToString(CultureInfo.InvariantCulture);
            }

            public static string WithoutProviderLessOverload(IRenderer renderer, int value)
            {
                return renderer.Render(value, CultureInfo.InvariantCulture);
            }
        }
        """;

    /// <summary>
    /// <c>Normalizer.Shout</c> converts the case of a <see cref="string" /> on line 23, while
    /// <c>Normalizer.ShoutLabel</c> calls a <c>ToUpper</c> declared by <c>Label</c> on line 28. Only the
    /// first one is a case conversion of <see cref="string" />, and <c>Label.ToUpper</c> returns
    /// <c>string.Empty</c>, which is a field access and therefore no mutation point either.
    /// </summary>
    private const string CaseSource = """
        namespace Fixture;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public sealed class Label
        {
            public string ToUpper()
            {
                return string.Empty;
            }
        }

        public static class Normalizer
        {
            public static string Shout(string value)
            {
                return value.ToUpperInvariant();
            }

            public static string ShoutLabel(Label label)
            {
                return label.ToUpper();
            }
        }
        """;

    /// <summary>
    /// <c>Matcher.Create</c> builds a matcher with one option flag on line 17. The pattern is a parameter
    /// rather than a literal, so the flag expression is the only mutation point of the member.
    /// </summary>
    private const string RegexSource = """
        namespace Fixture;

        using System.Text.RegularExpressions;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Matcher
        {
            public static Regex Create(string pattern)
            {
                return new Regex(pattern, RegexOptions.IgnoreCase);
            }
        }
        """;

    /// <summary>
    /// The three positions a culture value can take that only accept a compile-time constant: an attribute
    /// argument, a <see langword="const" /> initializer and a default parameter value. None of them is
    /// behaviour a test could observe, so none of them is a mutation point.
    /// </summary>
    private const string ConstantContextSource = """
        namespace Fixture;

        using System;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public sealed class ComparisonAttribute : Attribute
        {
            public ComparisonAttribute(StringComparison comparison)
            {
                Comparison = comparison;
            }

            public StringComparison Comparison { get; }
        }

        public static class Annotated
        {
            private const StringComparison Default = StringComparison.Ordinal;

            [Comparison(StringComparison.Ordinal)]
            public static bool Matches(string left, string right)
            {
                return string.Equals(left, right, Default);
            }

            public static bool With(
                string left,
                string right,
                StringComparison comparison = StringComparison.Ordinal
            )
            {
                return string.Equals(left, right, comparison);
            }
        }
        """;

    /// <summary>
    /// One member whose single expression on line 17 carries nine culture mutation points: two case
    /// conversions with two mutants each and one comparison with five. It is the shape the budget has to
    /// bound, because the family multiplies the mutation points of a member instead of adding one.
    /// </summary>
    private const string DenseSource = """
        namespace Fixture;

        using System;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Dense
        {
            public static bool Compare(string left, string right)
            {
                return string.Equals(left.ToUpperInvariant(), right.ToLowerInvariant(), StringComparison.Ordinal);
            }
        }
        """;

    /// <summary>
    /// The five mutants of <c>StringComparison.Ordinal</c>, in the declaration order of the enumeration and
    /// each one naming the axes it moves along.
    /// </summary>
    private static readonly string[] _ordinalMutants =
    [
        "StringComparison.Ordinal => StringComparison.CurrentCulture (ordinal => culture)",
        "StringComparison.Ordinal => StringComparison.CurrentCultureIgnoreCase "
            + "(ordinal => culture, case-sensitive => case-insensitive)",
        "StringComparison.Ordinal => StringComparison.InvariantCulture (ordinal => culture)",
        "StringComparison.Ordinal => StringComparison.InvariantCultureIgnoreCase "
            + "(ordinal => culture, case-sensitive => case-insensitive)",
        "StringComparison.Ordinal => StringComparison.OrdinalIgnoreCase (case-sensitive => case-insensitive)",
    ];

    /// <summary>
    /// Every fixture of this class, so that one test can prove that all of them compile and that none of
    /// them makes the analyzer crash.
    /// </summary>
    /// <returns>One factory per fixture.</returns>
    public static IEnumerable<Func<string>> Fixtures() =>
        new[]
        {
            ComparisonSource,
            CultureSource,
            OverloadSource,
            CaseSource,
            RegexSource,
            ConstantContextSource,
            DenseSource,
        }.Select(source => (Func<string>)(() => source));

    /// <summary>
    /// The flagship case of the family: an untested comparison reports one gap per remaining member of
    /// <c>StringComparison</c>, all of them at the comparison itself.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedOrdinalComparison_ReportsAGapPerRemainingComparison()
    {
        var compilation = CompilationFactory.Create(ComparisonSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(Gaps(diagnostics)).IsEqualTo(ExpectAt(ComparisonLine, _ordinalMutants));
            _ = await Assert.That(Trivial(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
        }
    }

    /// <summary>
    /// The same compilation and the same operator, with the comparing member itself recorded in the
    /// manifest: the analysis goes completely silent, which is what makes the five gaps above a statement
    /// about coverage rather than about the operator.
    /// </summary>
    [Test]
    public async Task Analyze_CoveredOrdinalComparison_ReportsNothing()
    {
        var compilation = CompilationFactory.Create(ComparisonSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(ComparisonMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(DiagnosticAssertions.Describe(diagnostics))
                .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    /// <summary>
    /// Formatting with an explicit culture is a mutation point of two operators at once, and both of them
    /// report: the culture can become the ambient one, and the provider can vanish entirely.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedInvariantCultureFormatting_ReportsTheCultureSwapAndTheProviderRemoval()
    {
        var compilation = CompilationFactory.Create(CultureSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(
                    Expect(
                        (FormatLine, "InvariantCulture => CurrentCulture"),
                        (FormatLine, "CultureInfo.InvariantCulture => (removed)")
                    )
                );
            _ = await Assert.That(Trivial(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    /// <summary>
    /// The interaction with the mutant compiler the unit tests can only document: the operator offers the
    /// removal for every call that passes a provider, and the analyzer reports it only where the remaining
    /// arguments still bind to an overload. Line 23 has a provider-less overload and is reported, line 28
    /// has none and is not, while the culture swap of the very same argument is reported on both lines —
    /// so the difference cannot be explained by the operator having stayed silent.
    /// </summary>
    [Test]
    public async Task Analyze_ProviderRemovalWithoutAMatchingOverload_IsDroppedAsANonCompilingMutant()
    {
        var compilation = CompilationFactory.Create(OverloadSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(
                    Expect(
                        (RemovableProviderLine, "CultureInfo.InvariantCulture => (removed)"),
                        (RemovableProviderLine, "InvariantCulture => CurrentCulture"),
                        (RequiredProviderLine, "InvariantCulture => CurrentCulture")
                    )
                );
            _ = await Assert.That(Trivial(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    /// <summary>
    /// The other half of the same claim: with the verification switched off, the removal on line 28 is
    /// reported as well. The mutant is therefore genuinely produced by the operator and genuinely dropped
    /// by the mutant compiler, which is the only reason it is missing above.
    /// </summary>
    [Test]
    public async Task Analyze_ProviderRemovalWithoutVerification_ReportsTheNonCompilingMutantAsWell()
    {
        var compilation = CompilationFactory.Create(OverloadSource, ProductionAssemblyName);
        var manifest = new[] { CreateManifest(AnchorMemberId) };

        var diagnostics = await RunAsync(compilation, manifest, CreateVerificationOptions(verify: false))
            .ConfigureAwait(false);

        _ = await Assert
            .That(Gaps(diagnostics))
            .IsEqualTo(
                Expect(
                    (RemovableProviderLine, "CultureInfo.InvariantCulture => (removed)"),
                    (RemovableProviderLine, "InvariantCulture => CurrentCulture"),
                    (RequiredProviderLine, "CultureInfo.InvariantCulture => (removed)"),
                    (RequiredProviderLine, "InvariantCulture => CurrentCulture")
                )
            );
    }

    /// <summary>
    /// A case conversion of <see cref="string" /> reports the culture counterpart and the direction
    /// counterpart, while an equally named method of another type reports nothing at all — the operator
    /// resolves the called method instead of matching its name.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedInvariantCaseConversion_ReportsOnlyTheConversionsOfString()
    {
        var compilation = CompilationFactory.Create(CaseSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);
        var lines = DiagnosticAssertions
            .Summarise(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.UnreachableMutationPoint))
            .Select(summary => summary.Line);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(
                    Expect(
                        (CaseConversionLine, "ToUpperInvariant => ToUpper"),
                        (CaseConversionLine, "ToUpperInvariant => ToLowerInvariant")
                    )
                );
            _ = await Assert.That(lines.Distinct()).IsEquivalentTo([CaseConversionLine]);
            _ = await Assert.That(Trivial(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    /// <summary>
    /// The option flags of a matcher are reported as one gap per offered flag: the removal of the flag that
    /// is present and the addition of each of the six that are absent.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedRegexOptions_ReportsAGapPerOfferedFlag()
    {
        var compilation = CompilationFactory.Create(RegexSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(
                    ExpectAt(
                        RegexOptionsLine,
                        [
                            "RegexOptions - IgnoreCase",
                            "RegexOptions + CultureInvariant",
                            "RegexOptions + Multiline",
                            "RegexOptions + Singleline",
                            "RegexOptions + ExplicitCapture",
                            "RegexOptions + IgnorePatternWhitespace",
                            "RegexOptions + RightToLeft",
                        ]
                    )
                );
            _ = await Assert.That(Trivial(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    /// <summary>
    /// A culture value in a position that only accepts a compile-time constant is not behaviour a test
    /// could ever observe, so the analysis stays completely silent — not even an informational diagnostic
    /// is produced, because no mutation is created in the first place.
    /// </summary>
    [Test]
    public async Task Analyze_CultureValueInAConstantContext_ReportsNothing()
    {
        var compilation = CompilationFactory.Create(ConstantContextSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(DiagnosticAssertions.Describe(diagnostics))
                .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    /// <summary>
    /// The budget bounds a member of the culture family exactly as it bounds any other member. One
    /// expression carrying nine culture mutation points reports all nine without a budget, four of them
    /// with a budget of four and one with a budget of one, always in the order the mutation points are
    /// walked: the leftmost case conversion first, the comparison last.
    /// </summary>
    [Test]
    public async Task Analyze_MemberDenseWithCultureMutations_IsBoundedByTheMutantBudget()
    {
        var compilation = CompilationFactory.Create(DenseSource, ProductionAssemblyName);
        var manifest = new[] { CreateManifest(AnchorMemberId) };
        var caseConversions = new[]
        {
            "ToUpperInvariant => ToUpper",
            "ToUpperInvariant => ToLowerInvariant",
            "ToLowerInvariant => ToLower",
            "ToLowerInvariant => ToUpperInvariant",
        };

        var unlimited = await RunAsync(compilation, manifest).ConfigureAwait(false);
        var four = await RunAsync(compilation, manifest, CreateBudget(4)).ConfigureAwait(false);
        var one = await RunAsync(compilation, manifest, CreateBudget(1)).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(unlimited))
                .IsEqualTo(ExpectAt(DenseLine, [.. caseConversions, .. _ordinalMutants]));
            _ = await Assert.That(Gaps(four)).IsEqualTo(ExpectAt(DenseLine, caseConversions));
            _ = await Assert.That(Gaps(one)).IsEqualTo(ExpectAt(DenseLine, [caseConversions[0]]));
        }
    }

    /// <summary>
    /// Every fixture of this class compiles and is analysed without the analyzer throwing. Roslyn turns an
    /// analyzer exception into <c>AD0001</c> and carries on, so a crash would otherwise look like a
    /// diagnostic the tests above simply did not expect.
    /// </summary>
    /// <param name="source">The fixture to analyse.</param>
    /// <returns>A task that completes when the fixture was analysed.</returns>
    [Test]
    [MethodDataSource(nameof(Fixtures))]
    public async Task Analyze_EveryFixture_CompilesAndReportsNoAnalyzerFailure(string source)
    {
        var compilation = CompilationFactory.Create(source, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(string.Join("; ", Errors(compilation))).IsEqualTo(string.Empty);
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, AnalyzerRunner.AnalyzerFailureId)).IsEmpty();
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
        }
    }

    private static Task<ImmutableArray<Diagnostic>> RunAsync(
        Compilation compilation,
        IEnumerable<AdditionalText>? additionalFiles = null,
        IReadOnlyDictionary<string, string>? globalOptions = null
    ) => AnalyzerRunner.RunAsync(new MutationCoverageAnalyzer(), compilation, additionalFiles, globalOptions);

    /// <summary>
    /// Builds a manifest recording <paramref name="referencedMemberIds" /> as the production members the
    /// tests of the first pass touched.
    /// </summary>
    /// <remarks>
    /// Every reference is attributed to one anonymous test whose case count is the lower bound
    /// <see cref="LowerBoundCount" />. These tests are about which mutation points are reachable and state
    /// nothing about test data, so a lower bound is the honest count — and it keeps <c>FSH0006</c> silent,
    /// which is what lets every exact diagnostic set below stay a statement about the culture operators
    /// alone.
    /// </remarks>
    /// <param name="referencedMemberIds">The declaration ids of the covered members.</param>
    /// <returns>The manifest as an additional file.</returns>
    private static InMemoryAdditionalText CreateManifest(params string[] referencedMemberIds)
    {
        var builder = new StringBuilder();
        _ = builder.Append(TestSurfaceManifestFormat.Header).Append('\n');
        _ = builder
            .Append(TestSurfaceManifestFormat.TestPrefix)
            .Append(TestSurfaceManifestFormat.FieldSeparator)
            .Append(AnonymousTestId)
            .Append(TestSurfaceManifestFormat.FieldSeparator)
            .Append(LowerBoundCount)
            .Append('\n');

        foreach (var referencedMemberId in referencedMemberIds)
        {
            _ = builder
                .Append(TestSurfaceManifestFormat.ReferencePrefix)
                .Append(TestSurfaceManifestFormat.FieldSeparator)
                .Append(referencedMemberId)
                .Append('\n');
        }

        // These tests are about which mutation points are reachable, not about the behavioral
        // classification, so every reference is also written as behaviorally verified to keep FSH0007
        // out of a diagnostic set that is meant to be a statement about the culture operators alone.
        foreach (var referencedMemberId in referencedMemberIds)
        {
            _ = builder
                .Append(TestSurfaceManifestFormat.BehavioralReferencePrefix)
                .Append(TestSurfaceManifestFormat.FieldSeparator)
                .Append(referencedMemberId)
                .Append('\n');
        }

        return new InMemoryAdditionalText(builder.ToString());
    }

    private static Dictionary<string, string> CreateVerificationOptions(bool verify) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["build_property.FrameShiftVerifyMutantCompilation"] = verify ? "true" : "false",
        };

    private static Dictionary<string, string> CreateBudget(int maximum) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["build_property.FrameShiftMaxMutantsPerMember"] = ToText(maximum),
        };

    /// <summary>
    /// Describes the reported gaps as one text block, one line per diagnostic, ordered ordinally so that
    /// the result does not depend on the order the concurrently running analyzer callbacks reported them
    /// in. Several gaps share one location, which is exactly the case a positional order cannot separate.
    /// </summary>
    /// <param name="diagnostics">All diagnostics of a run.</param>
    /// <returns>The described gaps, or <see cref="NoGaps" /> when there is none.</returns>
    private static string Gaps(ImmutableArray<Diagnostic> diagnostics)
    {
        var gaps = AnalyzerRunner.OfId(diagnostics, DiagnosticIds.UnreachableMutationPoint);

        if (gaps.IsEmpty)
        {
            return NoGaps;
        }

        return Join(
            DiagnosticAssertions.Summarise(gaps).Select(summary => Entry(summary.Id, summary.Line, summary.Message))
        );
    }

    /// <summary>
    /// Builds the expectation of a set of gaps that all sit on <paramref name="line" />.
    /// </summary>
    /// <param name="line">The 1-based line every gap is reported on.</param>
    /// <param name="displayNames">The display names of the expected mutations.</param>
    /// <returns>The expected text block.</returns>
    private static string ExpectAt(int line, IEnumerable<string> displayNames) =>
        Expect([.. displayNames.Select(displayName => (Line: line, DisplayName: displayName))]);

    /// <summary>
    /// Builds the expectation of a set of gaps, each one a line and the display name of its mutation.
    /// </summary>
    /// <param name="gaps">The expected gaps.</param>
    /// <returns>The expected text block, or <see cref="NoGaps" /> when nothing is expected.</returns>
    private static string Expect(params (int Line, string DisplayName)[] gaps) =>
        gaps.Length == 0 ? NoGaps : Join(gaps.Select(gap => GapEntry(gap.Line, gap.DisplayName)));

    /// <summary>
    /// Builds the described gap of one mutation, spelling out the message
    /// <see cref="Descriptors.UnreachableMutationPoint" /> formats.
    /// </summary>
    /// <param name="line">The 1-based line the gap is reported on.</param>
    /// <param name="displayName">The display name of the mutation.</param>
    /// <returns>The described gap.</returns>
    private static string GapEntry(int line, string displayName) =>
        Entry(
            DiagnosticIds.UnreachableMutationPoint,
            line,
            "Mutation '"
                + displayName
                + "' at this location is not reachable from any test; a surviving mutant here would go unnoticed"
        );

    private static string Entry(string id, int line, string message) => $"{id} line {ToText(line)}: {message}";

    private static string Join(IEnumerable<string> entries) =>
        string.Join(LineFeed, entries.OrderBy(entry => entry, StringComparer.Ordinal));

    private static string Trivial(ImmutableArray<Diagnostic> diagnostics) =>
        DiagnosticAssertions.Describe(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.TrivialMutant));

    private static string ToText(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static ImmutableArray<Diagnostic> Errors(Compilation compilation) =>
        CompilationFactory.GetCompileErrors(compilation);
}
