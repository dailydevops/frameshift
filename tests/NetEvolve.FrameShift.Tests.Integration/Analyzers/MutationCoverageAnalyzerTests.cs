namespace NetEvolve.FrameShift.Tests.Integration.Analyzers;

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Analyzers;
using NetEvolve.FrameShift.Diagnostics;
using NetEvolve.FrameShift.Tests.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Drives <see cref="MutationCoverageAnalyzer" /> end to end: a real production compilation, a real
/// test-surface manifest and the real mutation engine, so that every test states what a consumer of
/// the package would actually see in its build log.
/// </summary>
public class MutationCoverageAnalyzerTests
{
    private const string ProductionAssemblyName = "ProductionAssembly";
    private const string TestAssemblyName = "TestAssembly";
    private const string TestFilePath = "ScaleTests.cs";
    private const string RobustFilePath = "Robust.cs";
    private const string RobustAssemblyName = "RobustAssembly";

    private const string CoveredMemberId = "M:Fixture.Covered.Scale(System.Int32)~System.Int32";
    private const string ReachedMemberId = "M:Fixture.Reached.Identity(System.Int32)";
    private const string EntryMemberId = "M:Fixture.Pipeline.Run(System.Int32)";
    private const string AlphaMemberId = "M:Fixture.First.Alpha(System.Int32)";
    private const string BetaMemberId = "M:Fixture.Second.Beta(System.Int32)";
    private const string DescribeMemberId = "M:Toolbox.Describe(System.Int32)";
    private const string GhostMemberId = "M:Fixture.Ghost.Vanished(System.Int32)~System.Int32";

    private const string BrokenManifestPath = "Broken.frameshift-tests";
    private const string UnrelatedAdditionalFilePath = "Notes.txt";

    private const string DiscardedTrivialMessage =
        "Mutation '+ => -' cannot change observable behaviour (the mutated value is assigned to a discard)";

    private const int CoveredMemberLine = 7;
    private const int UncoveredMemberLine = 15;
    private const int TrivialMutationLine = 15;
    private const int TrivialFixtureGapLine = 20;
    private const int ElapsedMemberLine = 17;
    private const int BetaMemberLine = 15;
    private const int GammaMemberLine = 23;

    /// <summary>
    /// The line feed the snapshots are built with, instead of <see cref="Environment.NewLine" />.
    /// </summary>
    private const string LineFeed = "\n";

    /// <summary>
    /// The separator between the parts of the key <see cref="SortKey(Diagnostic)" /> builds. It never
    /// occurs in an identifier or a path, and a message containing it still sorts deterministically.
    /// </summary>
    private const string KeySeparator = "|";

    /// <summary>
    /// The width every number of an ordering key is padded to, so that an ordinal comparison of the keys
    /// puts line 9 before line 10 instead of after it.
    /// </summary>
    private const string KeyNumberFormat = "D6";

    private const string EveryMutantHeading = "=== every mutant of the member ===";
    private const string OneMutantHeading = "=== at most one mutant per member ===";
    private const string FirstManifestHeading = "=== the first manifest alone ===";
    private const string BothManifestsHeading = "=== both manifests ===";

    /// <summary>
    /// Two members with identical shape, of which only <c>Covered.Scale</c> is exercised by the test
    /// compilation below. <c>Covered.Scale</c> sits on line 7, <c>Uncovered.Shrink</c> on line 15.
    /// </summary>
    private const string CoverageSource = """
        namespace Fixture;

        public static class Covered
        {
            public static int Scale(int value)
            {
                return value * 2;
            }
        }

        public static class Uncovered
        {
            public static int Shrink(int value)
            {
                return value / 2;
            }
        }
        """;

    /// <summary>
    /// A real TUnit test compilation, used to produce the manifest with the real collector instead of
    /// a hand-written id.
    /// </summary>
    private const string TestSource = """
        namespace Fixture.Tests;

        using TUnit.Core;

        public class ScaleTests
        {
            [Test]
            public void Scale_DoublesTheValue()
            {
                _ = Covered.Scale(2);
            }
        }
        """;

    /// <summary>
    /// <c>Pipeline.Run</c> (line 7) calls the private <c>Pipeline.Normalize</c> (line 12), while
    /// <c>Orphan.Ignore</c> (line 20) is called by nobody.
    /// </summary>
    private const string PipelineSource = """
        namespace Fixture;

        public static class Pipeline
        {
            public static int Run(int value)
            {
                return Normalize(value) + 1;
            }

            private static int Normalize(int value)
            {
                return value * 3;
            }
        }

        public static class Orphan
        {
            public static int Ignore(int value)
            {
                return value - 4;
            }
        }
        """;

    /// <summary>
    /// The mutations of the discarded expression on line 15 cannot be observed by any test, the ones
    /// of the returned expression on line 20 can.
    /// </summary>
    private const string TrivialSource = """
        namespace Fixture;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Gap
        {
            public static void Ignore(int value)
            {
                _ = value + 1;
            }

            public static int Shrink(int value)
            {
                return value / 2;
            }
        }
        """;

    /// <summary>
    /// <c>DateTime</c> declares <c>op_Subtraction</c> and <c>op_Addition</c>, so the arithmetic
    /// operator is offered the <c>-</c> to <c>+</c> mutation on line 17, but <c>end + start</c> does
    /// not bind.
    /// </summary>
    private const string ElapsedSource = """
        namespace Fixture;

        using System;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Elapsed
        {
            public static TimeSpan Between(DateTime start, DateTime end)
            {
                return end - start;
            }
        }
        """;

    /// <summary>
    /// A single member on line 15 carrying far more than one mutation point.
    /// </summary>
    private const string BudgetSource = """
        namespace Fixture;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Gap
        {
            public static int Combine(int value)
            {
                return (value + 1) * (value - 2);
            }
        }
        """;

    /// <summary>
    /// Three interchangeable members on lines 7, 15 and 23, so that each manifest can cover exactly
    /// one of them.
    /// </summary>
    private const string MergeSource = """
        namespace Fixture;

        public static class First
        {
            public static int Alpha(int value)
            {
                return value * 2;
            }
        }

        public static class Second
        {
            public static int Beta(int value)
            {
                return value * 3;
            }
        }

        public static class Third
        {
            public static int Gamma(int value)
            {
                return value * 4;
            }
        }
        """;

    /// <summary>
    /// Top-level statements, generics, local functions, lambdas, expression-bodied members, pattern
    /// matching, a <c>switch</c> expression and a record, all in one compilation. The body of
    /// <c>Toolbox.Describe</c> spans the lines 10 to 25.
    /// </summary>
    private const string RobustSource = """
        using System;
        using System.Collections.Generic;
        using System.Linq;

        var toolbox = new Toolbox();
        Console.WriteLine(toolbox.Describe(3));

        public sealed class Toolbox
        {
            public string Describe(int value)
            {
                var doubled = Scale(value, 2);

                int Local(int inner) => inner % 3;

                Func<int, int> lambda = candidate => candidate + 1;

                return value switch
                {
                    < 0 => "negative",
                    0 => "zero",
                    _ when doubled > 10 => Format(Local(lambda(value))),
                    _ => Format(doubled),
                };
            }

            public int Sum(IEnumerable<int> values) => values.Select(value => value * 2).Sum();

            private static T Scale<T>(T value, int factor)
                where T : struct
            {
                return value;
            }

            private static string Format(int value) =>
                value is > 0 and < 100 ? $"small {value}" : $"large {value}";
        }

        public record Measurement(int Value)
        {
            public int Doubled => Value * 2;

            public bool IsLarge => Value >= 50 && Value != 0;
        }
        """;

    [Test]
    public async Task Analyze_WithoutAnyManifest_ReportsNothing()
    {
        var compilation = CompilationFactory.Create(CoverageSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation).ConfigureAwait(false);

        _ = await Assert.That(Errors(compilation)).IsEmpty();
        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    public async Task Analyze_ManifestHeaderIsMalformed_ReportsTheParseProblemOnce()
    {
        var compilation = CompilationFactory.Create(CoverageSource, ProductionAssemblyName);
        var manifest = new InMemoryAdditionalText(BrokenManifestPath, "not-a-manifest\nR " + CoveredMemberId + "\n");

        var diagnostics = await RunAsync(compilation, [manifest]).ConfigureAwait(false);
        var problems = AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest);

        _ = await Assert.That(problems).Count().IsEqualTo(1);
        _ = await Assert.That(DiagnosticAssertions.Describe(problems)).Contains("Line 1");
        _ = await Assert.That(DiagnosticAssertions.Describe(problems)).Contains("not-a-manifest");
        _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.UnreachableMutationPoint)).IsEmpty();
    }

    [Test]
    public async Task Analyze_ManifestWithoutReferencedMembers_ExplainsTheManifestInsteadOfBlamingTheCode()
    {
        var compilation = CompilationFactory.Create(CoverageSource, ProductionAssemblyName);
        var manifest = new InMemoryAdditionalText(
            "Empty.frameshift-tests",
            TestSurfaceManifestFormat.Header + "\nT M:Fixture.Tests.ScaleTests.Scale_DoublesTheValue\n"
        );

        var diagnostics = await RunAsync(compilation, [manifest]).ConfigureAwait(false);
        var problems = AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest);

        _ = await Assert.That(problems).Count().IsEqualTo(1);
        _ = await Assert.That(DiagnosticAssertions.Describe(problems)).Contains("empty or stale");
        _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.UnreachableMutationPoint)).IsEmpty();
        _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.TrivialMutant)).IsEmpty();
    }

    [Test]
    public async Task Analyze_ManifestCollectedFromTheTestCompilation_ReportsOnlyTheUncoveredMember()
    {
        var production = CompilationFactory.Create(CoverageSource, ProductionAssemblyName);
        var manifestText = CollectManifest(production);
        var manifest = new InMemoryAdditionalText(manifestText);

        var diagnostics = await RunAsync(production, [manifest]).ConfigureAwait(false);
        var gaps = AnalyzerRunner.OfId(diagnostics, DiagnosticIds.UnreachableMutationPoint);
        var lines = DiagnosticAssertions.Summarise(gaps).Select(summary => summary.Line);

        _ = await Assert.That(manifestText).Contains(CoveredMemberId);
        _ = await Assert.That(manifestText.Contains("Shrink", StringComparison.Ordinal)).IsFalse();
        _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
        _ = await Assert.That(lines.Distinct()).IsEquivalentTo(new[] { UncoveredMemberLine });
        _ = await Assert.That(lines.Where(line => line == CoveredMemberLine)).IsEmpty();
        _ = await Assert.That(DiagnosticAssertions.Describe(gaps)).Contains("Mutation '/ => +'");
    }

    /// <summary>
    /// The manifest names <c>Pipeline.Run</c> and nothing else, and that member calls the private
    /// <c>Pipeline.Normalize</c> on line 12. The snapshot states both halves of "transitively reached
    /// counts as covered" at once: not one line of the helper is reported, and <c>Orphan.Ignore</c> on
    /// line 20, which nobody calls, is reported with every mutation it carries.
    /// </summary>
    [Test]
    public async Task Analyze_ManifestNamesOnlyTheEntryMethod_TreatsTheCalledHelperAsCovered()
    {
        var compilation = CompilationFactory.Create(PipelineSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(EntryMemberId)]).ConfigureAwait(false);

        _ = await Assert.That(Errors(compilation)).IsEmpty();
        _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
        _ = await Verify(Snapshot(diagnostics)).ConfigureAwait(false);
    }

    [Test]
    public async Task Analyze_MutationCannotBeObserved_ReportsItAsTrivialInsteadOfAsAGap()
    {
        var compilation = CompilationFactory.Create(TrivialSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(ReachedMemberId)]).ConfigureAwait(false);
        var trivial = DiagnosticAssertions.Summarise(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.TrivialMutant));
        var gaps = DiagnosticAssertions.Summarise(
            AnalyzerRunner.OfId(diagnostics, DiagnosticIds.UnreachableMutationPoint)
        );

        _ = await Assert
            .That(trivial.Select(summary => summary.Line).Distinct())
            .IsEquivalentTo(new[] { TrivialMutationLine });
        _ = await Assert
            .That(gaps.Select(summary => summary.Line).Distinct())
            .IsEquivalentTo(new[] { TrivialFixtureGapLine });
        _ = await Assert.That(trivial.Select(summary => summary.Message)).Contains(DiscardedTrivialMessage);
    }

    [Test]
    public async Task Analyze_TrivialMutantsAreNotReported_KeepsTheGapsUntouched()
    {
        var compilation = CompilationFactory.Create(TrivialSource, ProductionAssemblyName);
        var manifest = new[] { CreateManifest(ReachedMemberId) };
        var reported = await RunAsync(compilation, manifest).ConfigureAwait(false);

        var suppressed = await RunAsync(
                compilation,
                manifest,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["build_property.FrameShiftReportTrivialMutants"] = "false",
                }
            )
            .ConfigureAwait(false);

        _ = await Assert.That(AnalyzerRunner.OfId(reported, DiagnosticIds.TrivialMutant)).Count().IsEqualTo(5);
        _ = await Assert.That(AnalyzerRunner.OfId(suppressed, DiagnosticIds.TrivialMutant)).IsEmpty();
        _ = await Assert
            .That(Describe(suppressed, DiagnosticIds.UnreachableMutationPoint))
            .IsEqualTo(Describe(reported, DiagnosticIds.UnreachableMutationPoint));
    }

    [Test]
    public async Task Analyze_FrameShiftIsDisabled_ReportsNothing()
    {
        var compilation = CompilationFactory.Create(CoverageSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(
                compilation,
                [CreateManifest(CoveredMemberId)],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["build_property.FrameShiftEnabled"] = "false",
                }
            )
            .ConfigureAwait(false);

        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    public async Task Analyze_MutantDoesNotCompile_IsOnlyReportedWithoutVerification()
    {
        var compilation = CompilationFactory.Create(ElapsedSource, ProductionAssemblyName);
        var manifest = new[] { CreateManifest(ReachedMemberId) };

        var verified = await RunAsync(compilation, manifest, CreateVerificationOptions(verify: true))
            .ConfigureAwait(false);
        var unverified = await RunAsync(compilation, manifest, CreateVerificationOptions(verify: false))
            .ConfigureAwait(false);
        var reported = DiagnosticAssertions.Summarise(
            AnalyzerRunner.OfId(unverified, DiagnosticIds.UnreachableMutationPoint)
        );

        _ = await Assert.That(Errors(compilation)).IsEmpty();
        _ = await Assert.That(AnalyzerRunner.OfId(verified, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
        _ = await Assert.That(AnalyzerRunner.OfId(verified, DiagnosticIds.UnreachableMutationPoint)).IsEmpty();
        _ = await Assert.That(reported.Select(summary => summary.Line)).IsEquivalentTo(new[] { ElapsedMemberLine });
        _ = await Assert.That(DiagnosticAssertions.Describe(unverified)).Contains("Mutation '- => +'");
    }

    /// <summary>
    /// Both halves of the mutant budget in one snapshot, run over the same compilation and the same
    /// manifest: without a budget <c>Gap.Combine</c> reports every mutation point of its expression, with
    /// a budget of one it reports exactly one of them, and the member is still named.
    /// </summary>
    [Test]
    public async Task Analyze_MutantBudgetIsOne_ReportsOneDiagnosticForTheMember()
    {
        var compilation = CompilationFactory.Create(BudgetSource, ProductionAssemblyName);
        var manifest = new[] { CreateManifest(ReachedMemberId) };
        var unlimited = await RunAsync(compilation, manifest).ConfigureAwait(false);

        var limited = await RunAsync(
                compilation,
                manifest,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["build_property.FrameShiftMaxMutantsPerMember"] = "1",
                }
            )
            .ConfigureAwait(false);

        _ = await Verify(Combine((EveryMutantHeading, unlimited), (OneMutantHeading, limited))).ConfigureAwait(false);
    }

    /// <summary>
    /// Two manifests, each recording one of three interchangeable members, over the same compilation. The
    /// snapshot shows the first manifest alone leaving <c>Second.Beta</c> and <c>Third.Gamma</c> reported
    /// and both manifests together leaving only <c>Third.Gamma</c>, which is what uniting instead of
    /// replacing means.
    /// </summary>
    [Test]
    public async Task Analyze_TwoManifests_UnitesTheirRecordedMembers()
    {
        var compilation = CompilationFactory.Create(MergeSource, ProductionAssemblyName);
        var first = CreateManifestAt("First.frameshift-tests", AlphaMemberId);
        var second = CreateManifestAt("Second.frameshift-tests", BetaMemberId);
        var single = await RunAsync(compilation, [first]).ConfigureAwait(false);

        var merged = await RunAsync(compilation, [first, second]).ConfigureAwait(false);

        _ = await Assert.That(AnalyzerRunner.OfId(merged, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
        _ = await Verify(Combine((FirstManifestHeading, single), (BothManifestsHeading, merged))).ConfigureAwait(false);
    }

    /// <summary>
    /// Top-level statements, generics, a local function, lambdas, an expression-bodied member, pattern
    /// matching, a <c>switch</c> expression and a record, all analysed at once. The snapshot is the whole
    /// diagnostic set, so it states the two things an enumeration of line numbers could only hint at: the
    /// body of <c>Toolbox.Describe</c> on the lines 10 to 25 produces nothing, because the manifest covers
    /// it, and every other construct is walked and reported instead of silently skipped or crashed on.
    /// </summary>
    [Test]
    public async Task Analyze_CompilationUsesEveryLanguageFeature_ReportsWithoutCrashing()
    {
        var compilation = CreateRobustCompilation();

        var diagnostics = await RunAsync(compilation, [CreateManifest(DescribeMemberId)]).ConfigureAwait(false);

        _ = await Assert.That(Errors(compilation)).IsEmpty();
        _ = await Assert.That(DiagnosticAssertions.Ids(diagnostics).Where(IsAnalyzerFailure)).IsEmpty();
        _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
        _ = await Verify(Snapshot(diagnostics)).ConfigureAwait(false);
    }

    [Test]
    public async Task Initialize_ContextIsNull_ThrowsArgumentNullException()
    {
        var analyzer = new MutationCoverageAnalyzer();

        var exception = Assert.Throws<ArgumentNullException>(() => analyzer.Initialize(null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("context");
    }

    /// <summary>
    /// Pins the unreadable side of reading a manifest file: the file is recognised as a manifest, but its
    /// content is not available at all. Staying silent would let the whole compilation be judged against
    /// an empty surface.
    /// </summary>
    [Test]
    public async Task Analyze_ManifestContentIsNotAvailable_ReportsTheUnreadableFileInsteadOfGaps()
    {
        var compilation = CompilationFactory.Create(CoverageSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [InMemoryAdditionalText.WithoutContent()]).ConfigureAwait(false);
        var problems = AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest);

        _ = await Assert.That(problems).Count().IsEqualTo(1);
        _ = await Assert
            .That(DiagnosticAssertions.Describe(problems))
            .Contains("the content of the additional file is not available to the analyzer");
        _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.UnreachableMutationPoint)).IsEmpty();
        _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.TrivialMutant)).IsEmpty();
    }

    /// <summary>
    /// Pins the second reason of an unusable manifest, the one an empty manifest never produces: the
    /// manifest parses and does record members, but not one of them exists in this compilation.
    /// </summary>
    [Test]
    public async Task Analyze_RecordedMembersResolveToNothing_ExplainsTheForeignManifestInsteadOfBlamingTheCode()
    {
        var compilation = CompilationFactory.Create(CoverageSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(GhostMemberId)]).ConfigureAwait(false);
        var problems = AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest);

        _ = await Assert.That(problems).Count().IsEqualTo(1);
        _ = await Assert
            .That(DiagnosticAssertions.Describe(problems))
            .Contains("belongs to a different project or is stale");
        _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.UnreachableMutationPoint)).IsEmpty();
    }

    /// <summary>
    /// The one combination the other manifest tests never produce: a problem to report <em>and</em> a
    /// usable reachable set. The broken file must be named without the good one losing its effect.
    /// </summary>
    [Test]
    public async Task Analyze_OneManifestIsMalformedAndOneIsUsable_ReportsTheBrokenFileAndStillUsesTheGoodOne()
    {
        var compilation = CompilationFactory.Create(MergeSource, ProductionAssemblyName);
        var broken = new InMemoryAdditionalText(BrokenManifestPath, "not-a-manifest\n");
        var usable = CreateManifestAt("Usable.frameshift-tests", AlphaMemberId);

        var diagnostics = await RunAsync(compilation, [broken, usable]).ConfigureAwait(false);
        var problems = AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest);

        _ = await Assert.That(problems).Count().IsEqualTo(1);
        _ = await Assert.That(DiagnosticAssertions.Describe(problems)).Contains(BrokenManifestPath);
        _ = await Assert
            .That(GapLines(diagnostics).Distinct())
            .IsEquivalentTo(new[] { BetaMemberLine, GammaMemberLine });
    }

    /// <summary>
    /// Additional files are shared by everything the build hands to the analyzers, so a file that is not
    /// a manifest must not opt the project in: the analysis stays as silent as it is without any file.
    /// </summary>
    [Test]
    public async Task Analyze_AdditionalFileIsNotAManifest_ReportsNothing()
    {
        var compilation = CompilationFactory.Create(CoverageSource, ProductionAssemblyName);
        var unrelated = new InMemoryAdditionalText(
            UnrelatedAdditionalFilePath,
            TestSurfaceManifestFormat.Header + "\n"
        );

        var diagnostics = await RunAsync(compilation, [unrelated]).ConfigureAwait(false);

        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    public async Task Analyze_CancelledToken_ThrowsOperationCanceledException()
    {
        var compilation = CompilationFactory.Create(CoverageSource, ProductionAssemblyName);
        using var source = new CancellationTokenSource();
        await source.CancelAsyncCompat().ConfigureAwait(false);

        OperationCanceledException? caught = null;
        try
        {
            _ = await AnalyzerRunner
                .RunAsync(
                    new MutationCoverageAnalyzer(),
                    compilation,
                    additionalFiles: [CreateManifest(CoveredMemberId)],
                    globalOptions: null,
                    cancellationToken: source.Token
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            caught = exception;
        }

        _ = await Assert.That(caught).IsNotNull();
    }

    private static Task<ImmutableArray<Diagnostic>> RunAsync(
        Compilation compilation,
        IEnumerable<AdditionalText>? additionalFiles = null,
        IReadOnlyDictionary<string, string>? globalOptions = null
    ) => AnalyzerRunner.RunAsync(new MutationCoverageAnalyzer(), compilation, additionalFiles, globalOptions);

    /// <summary>
    /// Builds the manifest of <paramref name="production" /> the way the first pass does: from a real
    /// TUnit test compilation that sees the production code as a metadata reference only.
    /// </summary>
    /// <param name="production">The production compilation the tests are written against.</param>
    /// <returns>The serialized manifest.</returns>
    private static string CollectManifest(Compilation production)
    {
        var test = CompilationFactory.Create(
            [(TestFilePath, TestSource)],
            TestAssemblyName,
            includeTUnit: true,
            additionalReferences: [production.ToMetadataReference()]
        );

        return TestSurfaceManifestWriter.Write(
            TestSurfaceCollector.Collect(
                test,
                new TUnitTestMethodRecognizer(TUnitTestFrameworkProbe.GetTestAttributeType(test)),
                CancellationToken.None
            )
        );
    }

    private static CSharpCompilation CreateRobustCompilation()
    {
        var options = new CSharpCompilationOptions(
            OutputKind.ConsoleApplication,
            nullableContextOptions: NullableContextOptions.Enable
        );

        return CSharpCompilation.Create(
            RobustAssemblyName,
            [CompilationFactory.ParseTree(RobustSource, RobustFilePath)],
            ReferenceAssemblies.Default,
            options
        );
    }

    private static InMemoryAdditionalText CreateManifest(params string[] referencedMemberIds) =>
        CreateManifestAt(InMemoryAdditionalText.DefaultPath, referencedMemberIds);

    private static InMemoryAdditionalText CreateManifestAt(string path, params string[] referencedMemberIds)
    {
        var builder = new StringBuilder();
        _ = builder.Append(TestSurfaceManifestFormat.Header).Append('\n');

        foreach (var referencedMemberId in referencedMemberIds)
        {
            _ = builder
                .Append(TestSurfaceManifestFormat.ReferencePrefix)
                .Append(' ')
                .Append(referencedMemberId)
                .Append('\n');
        }

        return new InMemoryAdditionalText(path, builder.ToString());
    }

    private static Dictionary<string, string> CreateVerificationOptions(bool verify) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["build_property.FrameShiftVerifyMutantCompilation"] = verify ? "true" : "false",
        };

    /// <summary>
    /// Builds the snapshot of several diagnostic sets of one test, each one under its own heading.
    /// </summary>
    /// <param name="sections">The sections, each a heading and the diagnostics reported under it.</param>
    /// <returns>The snapshot content.</returns>
    private static string Combine(params (string Heading, ImmutableArray<Diagnostic> Diagnostics)[] sections) =>
        string.Join(LineFeed, sections.Select(section => section.Heading + LineFeed + Snapshot(section.Diagnostics)));

    /// <summary>
    /// Describes every diagnostic the way a build log does — identifier, file, line, column and message —
    /// in an order that does not depend on when the analyzer happened to report it.
    /// </summary>
    /// <param name="diagnostics">The diagnostics to describe.</param>
    /// <returns>
    /// One line per diagnostic, or <see cref="DiagnosticAssertions.NoDiagnostics" /> when there is none.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The order <see cref="DiagnosticAssertions" /> fixes — file, position, identifier — is not enough for
    /// a snapshot. One mutation point produces several diagnostics at the very same position under the very
    /// same identifier, the sort behind that order is not stable, and the analyzer callbacks run
    /// concurrently, so those diagnostics would change places between two runs and between two target
    /// frameworks. The message is therefore the last part of the key, which makes the order total.
    /// </para>
    /// <para>
    /// The lines are joined with a line feed instead of with <see cref="Environment.NewLine" />, so that
    /// the same snapshot is produced on Windows and on Linux.
    /// </para>
    /// </remarks>
    private static string Snapshot(ImmutableArray<Diagnostic> diagnostics)
    {
        if (diagnostics.IsDefaultOrEmpty)
        {
            return DiagnosticAssertions.NoDiagnostics;
        }

        var described = diagnostics
            .Select(diagnostic => (Key: SortKey(diagnostic), Text: DiagnosticAssertions.Describe(diagnostic)))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => entry.Text);

        return string.Join(LineFeed, described);
    }

    /// <summary>
    /// Builds the ordering key of one diagnostic: its file, its line and column as fixed width numbers, its
    /// identifier and its message.
    /// </summary>
    /// <param name="diagnostic">The diagnostic to build the key of.</param>
    /// <returns>The key, to be compared ordinally.</returns>
    private static string SortKey(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        var position = span.StartLinePosition;

        return string.Join(
            KeySeparator,
            span.Path,
            ToKeyNumber(position.Line),
            ToKeyNumber(position.Character),
            diagnostic.Id,
            diagnostic.GetMessage(CultureInfo.InvariantCulture)
        );
    }

    private static string ToKeyNumber(int value) => value.ToString(KeyNumberFormat, CultureInfo.InvariantCulture);

    private static IEnumerable<int> GapLines(ImmutableArray<Diagnostic> diagnostics) =>
        DiagnosticAssertions
            .Summarise(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.UnreachableMutationPoint))
            .Select(summary => summary.Line);

    private static string Describe(ImmutableArray<Diagnostic> diagnostics, string id) =>
        DiagnosticAssertions.Describe(AnalyzerRunner.OfId(diagnostics, id));

    private static bool IsAnalyzerFailure(string id) =>
        string.Equals(id, AnalyzerRunner.AnalyzerFailureId, StringComparison.Ordinal);

    private static ImmutableArray<Diagnostic> Errors(Compilation compilation) =>
        CompilationFactory.GetCompileErrors(compilation);
}
