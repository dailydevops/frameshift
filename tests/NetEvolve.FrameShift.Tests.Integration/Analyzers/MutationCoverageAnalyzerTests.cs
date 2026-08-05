namespace NetEvolve.FrameShift.Tests.Integration.Analyzers;

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Analyzers;
using NetEvolve.FrameShift.Diagnostics;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
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
    private const string AddMemberId = "M:Fixture.Calculator.Add(System.Int32,System.Int32)~System.Int32";
    private const string IgnoreMemberId = "M:Fixture.Gap.Ignore(System.Int32)";
    private const string CombineMemberId = "M:Fixture.Gap.Combine(System.Int32)~System.Int32";
    private const string CoveredLogMemberId = "M:Fixture.Covered.Log(System.String)";

    /// <summary>
    /// The test method id the manifests of the reachability tests are attributed to. Those tests state
    /// nothing about test data, so the id is never asserted on.
    /// </summary>
    private const string AnonymousTestId = "M:Fixture.Tests.AnonymousTests.Reaches";

    private const string FirstAddTestId = "M:Fixture.Tests.CalculatorTests.Add_AddsTheValues";
    private const string SecondAddTestId = "M:Fixture.Tests.CalculatorTests.Add_AddsNegativeValues";

    private const string FirstAddTestName = "Fixture.Tests.CalculatorTests.Add_AddsTheValues";
    private const string IgnoreTestId = "M:Fixture.Tests.GapTests.Ignore_DiscardsTheValue";
    private const string CombineTestId = "M:Fixture.Tests.GapTests.Combine_CombinesTheValue";

    /// <summary>
    /// The case count of a test declaring exactly one test case.
    /// </summary>
    private const string SingleCaseCount = "1";

    /// <summary>
    /// The case count of a test declaring exactly three test cases.
    /// </summary>
    private const string ThreeCasesCount = "3";

    /// <summary>
    /// The case count of a test whose exact number of cases cannot be determined statically.
    /// </summary>
    private const string LowerBoundCount = "1+";

    /// <summary>
    /// The part of the <c>FSH0006</c> message the single test method name follows.
    /// </summary>
    private const string TestMethodMarker = "the one of test method '";

    private const string BrokenManifestPath = "Broken.frameshift";
    private const string FirstManifestPath = "First.frameshift";
    private const string SecondManifestPath = "Second.frameshift";
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
    private const int AddMemberLine = 7;
    private const int NormalizeMemberLine = 12;
    private const int MultiplyMemberLine = 20;
    private const int CombineMemberLine = 15;
    private const int CoveredLogLine = 9;
    private const int UncoveredLogLine = 17;

    /// <summary>
    /// The number of meaningful mutants <c>Gap.Combine</c> of <see cref="BudgetSource" /> carries, and the
    /// number of diagnostics it therefore produces without a budget.
    /// </summary>
    private const int CombineMutantCount = 17;

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
    /// <c>Calculator.Add</c> (line 7) calls the private <c>Calculator.Normalize</c> (line 12), while
    /// <c>Ignored.Multiply</c> (line 20) is called by nobody. Every one of the three expressions carries
    /// meaningful mutants, so a manifest naming <c>Calculator.Add</c> alone separates the three verdicts:
    /// directly reached, transitively reached and not reached at all.
    /// </summary>
    private const string CalculatorSource = """
        namespace Fixture;

        public static class Calculator
        {
            public static int Add(int left, int right)
            {
                return Normalize(left) + right;
            }

            private static int Normalize(int value)
            {
                return value * 2;
            }
        }

        public static class Ignored
        {
            public static int Multiply(int left, int right)
            {
                return left * right;
            }
        }
        """;

    /// <summary>
    /// Two members with identical shape, each a standalone <see langword="void"/> invocation statement - the
    /// statement removal operator's invocation construct. <c>Covered.Log</c> sits on line 9,
    /// <c>Uncovered.Log</c> on line 17.
    /// </summary>
    private const string StatementRemovalSource = """
        namespace Fixture;

        using System;

        public static class Covered
        {
            public static void Log(string message)
            {
                Console.WriteLine(message);
            }
        }

        public static class Uncovered
        {
            public static void Log(string message)
            {
                Console.WriteLine(message);
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
    /// matching, a <see langword="switch"/> expression and a record, all in one compilation. The body of
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(DiagnosticAssertions.Describe(diagnostics))
                .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    [Test]
    public async Task Analyze_ManifestHeaderIsMalformed_ReportsTheParseProblemOnce()
    {
        var compilation = CompilationFactory.Create(CoverageSource, ProductionAssemblyName);
        var manifest = new InMemoryAdditionalText(BrokenManifestPath, "not-a-manifest\nR " + CoveredMemberId + "\n");

        var diagnostics = await RunAsync(compilation, [manifest]).ConfigureAwait(false);
        var problems = AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest);

        using (Assert.Multiple())
        {
            _ = await Assert.That(problems).Count().IsEqualTo(1);
            _ = await Assert.That(DiagnosticAssertions.Describe(problems)).Contains("Line 1");
            _ = await Assert.That(DiagnosticAssertions.Describe(problems)).Contains("not-a-manifest");
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.UnreachableMutationPoint)).IsEmpty();
        }
    }

    [Test]
    public async Task Analyze_ManifestWithoutReferencedMembers_ExplainsTheManifestInsteadOfBlamingTheCode()
    {
        var compilation = CompilationFactory.Create(CoverageSource, ProductionAssemblyName);
        var manifest = new InMemoryAdditionalText(
            "Empty.frameshift",
            TestSurfaceManifestFormat.Header + "\nT M:Fixture.Tests.ScaleTests.Scale_DoublesTheValue 1\n"
        );

        var diagnostics = await RunAsync(compilation, [manifest]).ConfigureAwait(false);
        var problems = AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest);

        using (Assert.Multiple())
        {
            _ = await Assert.That(problems).Count().IsEqualTo(1);
            _ = await Assert.That(DiagnosticAssertions.Describe(problems)).Contains("empty or stale");
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.UnreachableMutationPoint)).IsEmpty();
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.TrivialMutant)).IsEmpty();
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(manifestText).Contains(CoveredMemberId);
            _ = await Assert.That(manifestText.Contains("Shrink", StringComparison.Ordinal)).IsFalse();
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
            _ = await Assert.That(lines.Distinct()).IsEquivalentTo([UncoveredMemberLine]);
            _ = await Assert.That(lines.Where(line => line == CoveredMemberLine)).IsEmpty();
            _ = await Assert.That(DiagnosticAssertions.Describe(gaps)).Contains("Mutation '/ => +'");
        }
    }

    /// <summary>
    /// This is the acceptance criterion of the reachability-only diagnostic at the analyzer's own
    /// boundary: a manifest that records a member as reachable but never as behaviorally referenced -
    /// exactly what a real manifest looks like for a test that only takes a method reference and asserts
    /// <c>IsNotNull</c> on it - must not be silently treated as covered. FSH0001 has to stay silent,
    /// because the member really is reachable, but FSH0007 has to fire in its place.
    /// </summary>
    [Test]
    public async Task Analyze_MemberIsReachableButNeverBehaviorallyReferenced_ReportsFSH0007InsteadOfSilence()
    {
        var compilation = CompilationFactory.Create(CoverageSource, ProductionAssemblyName);
        var manifest = CreateTestManifestAt(
            InMemoryAdditionalText.DefaultPath,
            behavioral: false,
            (AnonymousTestId, LowerBoundCount, [CoveredMemberId])
        );

        var diagnostics = await RunAsync(compilation, [manifest]).ConfigureAwait(false);
        var gaps = AnalyzerRunner.OfId(diagnostics, DiagnosticIds.UnreachableMutationPoint);
        var reachabilityOnly = AnalyzerRunner.OfId(diagnostics, DiagnosticIds.ReachabilityOnlyMutationPoint);
        var reachabilityOnlyLines = DiagnosticAssertions.Summarise(reachabilityOnly).Select(summary => summary.Line);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(gaps.Where(gap => gap.Location.GetLineSpan().StartLinePosition.Line + 1 == CoveredMemberLine))
                .IsEmpty();
            _ = await Assert.That(reachabilityOnlyLines.Distinct()).IsEquivalentTo([CoveredMemberLine]);
        }
    }

    /// <summary>
    /// FSH0007 takes precedence over FSH0006: a member reached by exactly one test case is reported as
    /// FSH0006 only when it is also behaviorally reachable. This is the same manifest shape as
    /// <see cref="Analyze_MemberIsReachableButNeverBehaviorallyReferenced_ReportsFSH0007InsteadOfSilence" />,
    /// with the one difference that matters: <c>behavioral: true</c>.
    /// </summary>
    [Test]
    public async Task Analyze_MemberIsBehaviorallyReferencedByASingleTestCase_ReportsSingleTestCaseHintNotFSH0007()
    {
        var compilation = CompilationFactory.Create(CoverageSource, ProductionAssemblyName);
        var manifest = CreateTestManifestAt(
            InMemoryAdditionalText.DefaultPath,
            behavioral: true,
            (AnonymousTestId, SingleCaseCount, [CoveredMemberId])
        );

        var diagnostics = await RunAsync(compilation, [manifest]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.ReachabilityOnlyMutationPoint))
                .IsEmpty();
            _ = await Assert.That(SingleTestCaseDiagnostics(diagnostics)).IsNotEmpty();
        }
    }

    /// <summary>
    /// Behavioral reachability is aggregated per member, over every test that reaches it, not per test.
    /// A member reached by one test that only captures a bare reference and by a second test that
    /// actually invokes it and asserts on the result is behaviorally reachable overall: FSH0007 asks
    /// "is there a credible basis at all", and the second test alone already answers that.
    /// </summary>
    [Test]
    public async Task Analyze_MemberReachedByOneReachabilityOnlyTestAndOneBehavioralTest_IsNotReachabilityOnly()
    {
        var compilation = CompilationFactory.Create(CoverageSource, ProductionAssemblyName);
        var builder = new StringBuilder();
        _ = builder.Append(TestSurfaceManifestFormat.Header).Append('\n');
        _ = builder
            .Append(TestSurfaceManifestFormat.TestPrefix)
            .Append(' ')
            .Append(AnonymousTestId)
            .Append(' ')
            .Append(LowerBoundCount)
            .Append('\n')
            .Append(TestSurfaceManifestFormat.ReferencePrefix)
            .Append(' ')
            .Append(CoveredMemberId)
            .Append('\n')
            .Append(TestSurfaceManifestFormat.TestPrefix)
            .Append(' ')
            .Append(FirstAddTestId)
            .Append(' ')
            .Append(LowerBoundCount)
            .Append('\n')
            .Append(TestSurfaceManifestFormat.ReferencePrefix)
            .Append(' ')
            .Append(CoveredMemberId)
            .Append('\n')
            .Append(TestSurfaceManifestFormat.BehavioralReferencePrefix)
            .Append(' ')
            .Append(CoveredMemberId)
            .Append('\n');
        var manifest = new InMemoryAdditionalText(builder.ToString());

        var diagnostics = await RunAsync(compilation, [manifest]).ConfigureAwait(false);

        _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.ReachabilityOnlyMutationPoint)).IsEmpty();
    }

    /// <summary>
    /// The counter-example to the previous test: behavioral reachability recorded for one member never
    /// leaks into a different member, even one declared right next to it. <c>Uncovered.Shrink</c> is
    /// reached, but only ever as a bare reference, while <c>Covered.Scale</c> is the behaviorally
    /// verified one - each member keeps its own verdict.
    /// </summary>
    [Test]
    public async Task Analyze_BehavioralReferenceOfOneMember_DoesNotCoverAnUnrelatedMember()
    {
        const string uncoveredMemberId = "M:Fixture.Uncovered.Shrink(System.Int32)~System.Int32";

        var compilation = CompilationFactory.Create(CoverageSource, ProductionAssemblyName);
        var builder = new StringBuilder();
        _ = builder.Append(TestSurfaceManifestFormat.Header).Append('\n');
        _ = builder
            .Append(TestSurfaceManifestFormat.TestPrefix)
            .Append(' ')
            .Append(AnonymousTestId)
            .Append(' ')
            .Append(LowerBoundCount)
            .Append('\n')
            .Append(TestSurfaceManifestFormat.ReferencePrefix)
            .Append(' ')
            .Append(CoveredMemberId)
            .Append('\n')
            .Append(TestSurfaceManifestFormat.BehavioralReferencePrefix)
            .Append(' ')
            .Append(CoveredMemberId)
            .Append('\n')
            .Append(TestSurfaceManifestFormat.ReferencePrefix)
            .Append(' ')
            .Append(uncoveredMemberId)
            .Append('\n');
        var manifest = new InMemoryAdditionalText(builder.ToString());

        var diagnostics = await RunAsync(compilation, [manifest]).ConfigureAwait(false);
        var reachabilityOnlyLines = DiagnosticAssertions
            .Summarise(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.ReachabilityOnlyMutationPoint))
            .Select(summary => summary.Line);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachabilityOnlyLines.Distinct()).IsEquivalentTo([UncoveredMemberLine]);
            _ = await Assert.That(reachabilityOnlyLines.Where(line => line == CoveredMemberLine)).IsEmpty();
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(trivial.Select(summary => summary.Line).Distinct())
                .IsEquivalentTo([TrivialMutationLine]);
            _ = await Assert
                .That(gaps.Select(summary => summary.Line).Distinct())
                .IsEquivalentTo([TrivialFixtureGapLine]);
            _ = await Assert.That(trivial.Select(summary => summary.Message)).Contains(DiscardedTrivialMessage);
        }
    }

    /// <summary>
    /// Drives the statement removal operator's invocation construct end to end: <c>Covered.Log</c> is
    /// reached by the manifest and reports nothing, while the identically shaped <c>Uncovered.Log</c>
    /// is not reached and reports the removal of its standalone <c>Console.WriteLine</c> call as a gap.
    /// </summary>
    [Test]
    public async Task Analyze_StatementRemovalInvocationConstruct_ReportsOnlyTheUncoveredMember()
    {
        var compilation = CompilationFactory.Create(StatementRemovalSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(CoveredLogMemberId)]).ConfigureAwait(false);
        var gaps = AnalyzerRunner.OfId(diagnostics, DiagnosticIds.UnreachableMutationPoint);
        var lines = DiagnosticAssertions.Summarise(gaps).Select(summary => summary.Line);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
            _ = await Assert.That(lines.Distinct()).IsEquivalentTo([UncoveredLogLine]);
            _ = await Assert.That(lines.Where(line => line == CoveredLogLine)).IsEmpty();
            _ = await Assert
                .That(DiagnosticAssertions.Describe(gaps))
                .Contains("Mutation 'Console.WriteLine(message) => (removed)'");
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(AnalyzerRunner.OfId(reported, DiagnosticIds.TrivialMutant)).Count().IsEqualTo(5);
            _ = await Assert.That(AnalyzerRunner.OfId(suppressed, DiagnosticIds.TrivialMutant)).IsEmpty();
            _ = await Assert
                .That(Describe(suppressed, DiagnosticIds.UnreachableMutationPoint))
                .IsEqualTo(Describe(reported, DiagnosticIds.UnreachableMutationPoint));
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(AnalyzerRunner.OfId(verified, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
            _ = await Assert.That(AnalyzerRunner.OfId(verified, DiagnosticIds.UnreachableMutationPoint)).IsEmpty();
            _ = await Assert.That(reported.Select(summary => summary.Line)).IsEquivalentTo([ElapsedMemberLine]);
            _ = await Assert.That(DiagnosticAssertions.Describe(unverified)).Contains("Mutation '- => +'");
        }
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
        var first = CreateManifestAt("First.frameshift", AlphaMemberId);
        var second = CreateManifestAt("Second.frameshift", BetaMemberId);
        var single = await RunAsync(compilation, [first]).ConfigureAwait(false);

        var merged = await RunAsync(compilation, [first, second]).ConfigureAwait(false);

        _ = await Assert.That(AnalyzerRunner.OfId(merged, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
        _ = await Verify(Combine((FirstManifestHeading, single), (BothManifestsHeading, merged))).ConfigureAwait(false);
    }

    /// <summary>
    /// Top-level statements, generics, a local function, lambdas, an expression-bodied member, pattern
    /// matching, a <see langword="switch"/> expression and a record, all analysed at once. The snapshot is the whole
    /// diagnostic set, so it states the two things an enumeration of line numbers could only hint at: the
    /// body of <c>Toolbox.Describe</c> on the lines 10 to 25 produces nothing, because the manifest covers
    /// it, and every other construct is walked and reported instead of silently skipped or crashed on.
    /// </summary>
    [Test]
    public async Task Analyze_CompilationUsesEveryLanguageFeature_ReportsWithoutCrashing()
    {
        var compilation = CreateRobustCompilation();

        var diagnostics = await RunAsync(compilation, [CreateManifest(DescribeMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(DiagnosticAssertions.Ids(diagnostics).Where(IsAnalyzerFailure)).IsEmpty();
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
        }
        _ = await Verify(Snapshot(diagnostics)).ConfigureAwait(false);
    }

    /// <summary>
    /// The whole point of <c>FSH0006</c>: <c>Calculator.Add</c> is covered, but by one test with one test
    /// case, so a mutant that only differs for other inputs would survive. The transitively reached
    /// <c>Calculator.Normalize</c> is attributed to the very same test case, while the member no test
    /// reaches keeps reporting <c>FSH0001</c> and is never named as thinly covered.
    /// </summary>
    [Test]
    public async Task Analyze_MemberIsReachedByOneSingleCaseTest_ReportsTheSingleTestCaseHint()
    {
        var compilation = CompilationFactory.Create(CalculatorSource, ProductionAssemblyName);
        var manifest = CreateTestManifest((FirstAddTestId, SingleCaseCount, [AddMemberId]));

        var diagnostics = await RunAsync(compilation, [manifest]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(DiagnosticAssertions.Ids(diagnostics).Where(IsAnalyzerFailure)).IsEmpty();
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
            _ = await Assert
                .That(SingleTestCaseLines(diagnostics).Distinct())
                .IsEquivalentTo([AddMemberLine, NormalizeMemberLine]);
            _ = await Assert.That(GapLines(diagnostics).Distinct()).IsEquivalentTo([MultiplyMemberLine]);
            _ = await Assert
                .That(SingleTestCaseMethods(diagnostics).Distinct(StringComparer.Ordinal))
                .IsEquivalentTo([FirstAddTestName]);
        }
    }

    /// <summary>
    /// Two tests with one case each sum to two input combinations, which is not a single case any more.
    /// The <c>FSH0001</c> set proves the run analysed the very same compilation.
    /// </summary>
    [Test]
    public async Task Analyze_MemberIsReachedByTwoSingleCaseTests_DoesNotReportTheSingleTestCaseHint()
    {
        var compilation = CompilationFactory.Create(CalculatorSource, ProductionAssemblyName);
        var manifest = CreateTestManifest(
            (FirstAddTestId, SingleCaseCount, [AddMemberId]),
            (SecondAddTestId, SingleCaseCount, [AddMemberId])
        );

        var diagnostics = await RunAsync(compilation, [manifest]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(SingleTestCaseDiagnostics(diagnostics)).IsEmpty();
            _ = await Assert.That(GapLines(diagnostics).Distinct()).IsEquivalentTo([MultiplyMemberLine]);
        }
    }

    /// <summary>
    /// One test with three inline data rows exercises three input combinations, so its coverage is not
    /// narrow in the sense of <c>FSH0006</c>.
    /// </summary>
    [Test]
    public async Task Analyze_MemberIsReachedByOneTestWithThreeCases_DoesNotReportTheSingleTestCaseHint()
    {
        var compilation = CompilationFactory.Create(CalculatorSource, ProductionAssemblyName);
        var manifest = CreateTestManifest((FirstAddTestId, ThreeCasesCount, [AddMemberId]));

        var diagnostics = await RunAsync(compilation, [manifest]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(SingleTestCaseDiagnostics(diagnostics)).IsEmpty();
            _ = await Assert.That(GapLines(diagnostics).Distinct()).IsEquivalentTo([MultiplyMemberLine]);
        }
    }

    /// <summary>
    /// A lower bound says "at least one case, the exact number is unknown", for example a data source whose
    /// sequence only exists at run time. The true total could be far higher than one, so the finding is
    /// suppressed instead of guessed.
    /// </summary>
    [Test]
    public async Task Analyze_MemberIsReachedByOneTestWithALowerBound_DoesNotReportTheSingleTestCaseHint()
    {
        var compilation = CompilationFactory.Create(CalculatorSource, ProductionAssemblyName);
        var manifest = CreateTestManifest((FirstAddTestId, LowerBoundCount, [AddMemberId]));

        var diagnostics = await RunAsync(compilation, [manifest]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(SingleTestCaseDiagnostics(diagnostics)).IsEmpty();
            _ = await Assert.That(GapLines(diagnostics).Distinct()).IsEquivalentTo([MultiplyMemberLine]);
        }
    }

    /// <summary>
    /// One single-case test and one test with a lower bound reach the same member. The exact part alone
    /// would sum to one, but the bound could contribute any number, so the sum is not exact and the finding
    /// is suppressed for the directly and the transitively reached member alike.
    /// </summary>
    [Test]
    public async Task Analyze_MemberIsReachedByASingleCaseTestAndALowerBound_DoesNotReportTheSingleTestCaseHint()
    {
        var compilation = CompilationFactory.Create(CalculatorSource, ProductionAssemblyName);
        var manifest = CreateTestManifest(
            (FirstAddTestId, SingleCaseCount, [AddMemberId]),
            (SecondAddTestId, LowerBoundCount, [AddMemberId])
        );

        var diagnostics = await RunAsync(compilation, [manifest]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(SingleTestCaseDiagnostics(diagnostics)).IsEmpty();
            _ = await Assert.That(GapLines(diagnostics).Distinct()).IsEquivalentTo([MultiplyMemberLine]);
        }
    }

    /// <summary>
    /// Two manifests, each naming one single-case test for the same member, must aggregate to two cases
    /// instead of being judged one file at a time.
    /// </summary>
    [Test]
    public async Task Analyze_TwoManifestsEachNameOneSingleCaseTest_AggregatesThemToTwoCases()
    {
        var compilation = CompilationFactory.Create(CalculatorSource, ProductionAssemblyName);
        var first = CreateTestManifestAt(FirstManifestPath, (FirstAddTestId, SingleCaseCount, [AddMemberId]));
        var second = CreateTestManifestAt(SecondManifestPath, (SecondAddTestId, SingleCaseCount, [AddMemberId]));
        var alone = await RunAsync(compilation, [first]).ConfigureAwait(false);

        var merged = await RunAsync(compilation, [first, second]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(SingleTestCaseLines(alone).Distinct())
                .IsEquivalentTo([AddMemberLine, NormalizeMemberLine]);
            _ = await Assert.That(SingleTestCaseDiagnostics(merged)).IsEmpty();
        }
    }

    /// <summary>
    /// The same test method recorded by two manifests, as a multi-targeted test project produces, is one
    /// test method with one test case and must not be summed into two.
    /// </summary>
    [Test]
    public async Task Analyze_TwoManifestsNameTheSameSingleCaseTest_StillCountsOneCase()
    {
        var compilation = CompilationFactory.Create(CalculatorSource, ProductionAssemblyName);
        var first = CreateTestManifestAt(FirstManifestPath, (FirstAddTestId, SingleCaseCount, [AddMemberId]));
        var second = CreateTestManifestAt(SecondManifestPath, (FirstAddTestId, SingleCaseCount, [AddMemberId]));

        var diagnostics = await RunAsync(compilation, [first, second]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
            _ = await Assert
                .That(SingleTestCaseLines(diagnostics).Distinct())
                .IsEquivalentTo([AddMemberLine, NormalizeMemberLine]);
            _ = await Assert
                .That(SingleTestCaseMethods(diagnostics).Distinct(StringComparer.Ordinal))
                .IsEquivalentTo([FirstAddTestName]);
        }
    }

    /// <summary>
    /// A mutant that cannot change observable behaviour is not made interesting by weak test data, so the
    /// trivial verdict wins and <c>FSH0006</c> stays silent even though <c>Gap.Ignore</c> is reached by
    /// exactly one test case.
    /// </summary>
    [Test]
    public async Task Analyze_TrivialMutantIsReachedByOneSingleCaseTest_ReportsOnlyTheTrivialMutant()
    {
        var compilation = CompilationFactory.Create(TrivialSource, ProductionAssemblyName);
        var manifest = CreateTestManifest((IgnoreTestId, SingleCaseCount, [IgnoreMemberId]));

        var diagnostics = await RunAsync(compilation, [manifest]).ConfigureAwait(false);
        var trivial = DiagnosticAssertions.Summarise(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.TrivialMutant));

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(trivial.Select(summary => summary.Line).Distinct())
                .IsEquivalentTo([TrivialMutationLine]);
            _ = await Assert.That(SingleTestCaseDiagnostics(diagnostics)).IsEmpty();
            _ = await Assert.That(GapLines(diagnostics).Distinct()).IsEquivalentTo([TrivialFixtureGapLine]);
        }
    }

    /// <summary>
    /// The mutant budget bounds the informational output exactly like it bounds the gaps: without a budget
    /// <c>Gap.Combine</c> reports every one of its meaningful mutants as thinly covered, with a budget of
    /// one it reports a single one.
    /// </summary>
    [Test]
    public async Task Analyze_MutantBudgetIsOneAndOneTestCaseReaches_BoundsTheSingleTestCaseHints()
    {
        var compilation = CompilationFactory.Create(BudgetSource, ProductionAssemblyName);
        var manifest = new[] { CreateTestManifest((CombineTestId, SingleCaseCount, [CombineMemberId])) };
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(SingleTestCaseDiagnostics(unlimited)).Count().IsEqualTo(CombineMutantCount);
            _ = await Assert.That(SingleTestCaseDiagnostics(limited)).Count().IsEqualTo(1);
            _ = await Assert.That(SingleTestCaseLines(limited)).IsEquivalentTo([CombineMemberLine]);
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(problems).Count().IsEqualTo(1);
            _ = await Assert
                .That(DiagnosticAssertions.Describe(problems))
                .Contains("the content of the additional file is not available to the analyzer");
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.UnreachableMutationPoint)).IsEmpty();
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.TrivialMutant)).IsEmpty();
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(problems).Count().IsEqualTo(1);
            _ = await Assert
                .That(DiagnosticAssertions.Describe(problems))
                .Contains("belongs to a different project or is stale");
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.UnreachableMutationPoint)).IsEmpty();
        }
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
        var usable = CreateManifestAt("Usable.frameshift", AlphaMemberId);

        var diagnostics = await RunAsync(compilation, [broken, usable]).ConfigureAwait(false);
        var problems = AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest);

        using (Assert.Multiple())
        {
            _ = await Assert.That(problems).Count().IsEqualTo(1);
            _ = await Assert.That(DiagnosticAssertions.Describe(problems)).Contains(BrokenManifestPath);
            _ = await Assert.That(GapLines(diagnostics).Distinct()).IsEquivalentTo([BetaMemberLine, GammaMemberLine]);
        }
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

    /// <summary>
    /// Builds a manifest that attributes <paramref name="referencedMemberIds" /> to one anonymous test
    /// whose case count is the lower bound <see cref="LowerBoundCount" />.
    /// </summary>
    /// <param name="referencedMemberIds">The production members the manifest records as referenced.</param>
    /// <returns>The manifest as an additional file.</returns>
    /// <remarks>
    /// Every <c>R</c> line of the format belongs to the <c>T</c> line above it, so a manifest that records
    /// a reference always names a test as well. A lower bound is the right default for the tests that are
    /// about reachability alone: it says "at least one case, the exact number is unknown", which is exactly
    /// what a hand-written member id expresses, and it keeps the single-test-case heuristic of
    /// <c>FSH0006</c> out of a run that does not state anything about test data.
    /// </remarks>
    private static InMemoryAdditionalText CreateManifest(params string[] referencedMemberIds) =>
        CreateManifestAt(InMemoryAdditionalText.DefaultPath, referencedMemberIds);

    private static InMemoryAdditionalText CreateManifestAt(string path, params string[] referencedMemberIds) =>
        CreateTestManifestAt(path, (AnonymousTestId, LowerBoundCount, referencedMemberIds));

    /// <summary>
    /// Builds a manifest from complete test entries, so that a test can state which test reached what and
    /// with how many cases.
    /// </summary>
    /// <param name="tests">The test entries, each a test method id, a case count and the referenced members.</param>
    /// <returns>The manifest as an additional file.</returns>
    private static InMemoryAdditionalText CreateTestManifest(
        params (string TestMethodId, string Count, string[] ReferencedMemberIds)[] tests
    ) => CreateTestManifestAt(InMemoryAdditionalText.DefaultPath, tests);

    /// <summary>
    /// Builds a hand-written manifest exactly like the three-argument overload of this method, but every
    /// reference is also written as a <c>B</c> line: this is the harness used by every test of this file
    /// that is not itself about the behavioral classification, so that those tests keep asserting on
    /// reachability and on <c>FSH0006</c> without also having to state a behavioral assertion. The
    /// FSH0007 tests build their manifest without this helper, deliberately.
    /// </summary>
    private static InMemoryAdditionalText CreateTestManifestAt(
        string path,
        params (string TestMethodId, string Count, string[] ReferencedMemberIds)[] tests
    ) => CreateTestManifestAt(path, behavioral: true, tests);

    private static InMemoryAdditionalText CreateTestManifestAt(
        string path,
        bool behavioral,
        params (string TestMethodId, string Count, string[] ReferencedMemberIds)[] tests
    )
    {
        var builder = new StringBuilder();
        _ = builder.Append(TestSurfaceManifestFormat.Header).Append('\n');

        foreach (var (testMethodId, count, referencedMemberIds) in tests)
        {
            _ = builder
                .Append(TestSurfaceManifestFormat.TestPrefix)
                .Append(' ')
                .Append(testMethodId)
                .Append(' ')
                .Append(count)
                .Append('\n');

            foreach (var referencedMemberId in referencedMemberIds)
            {
                _ = builder
                    .Append(TestSurfaceManifestFormat.ReferencePrefix)
                    .Append(' ')
                    .Append(referencedMemberId)
                    .Append('\n');
            }

            if (!behavioral)
            {
                continue;
            }

            foreach (var referencedMemberId in referencedMemberIds)
            {
                _ = builder
                    .Append(TestSurfaceManifestFormat.BehavioralReferencePrefix)
                    .Append(' ')
                    .Append(referencedMemberId)
                    .Append('\n');
            }
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

    private static ImmutableArray<Diagnostic> SingleTestCaseDiagnostics(ImmutableArray<Diagnostic> diagnostics) =>
        AnalyzerRunner.OfId(diagnostics, DiagnosticIds.SingleTestCaseMutationPoint);

    private static IEnumerable<int> SingleTestCaseLines(ImmutableArray<Diagnostic> diagnostics) =>
        DiagnosticAssertions.Summarise(SingleTestCaseDiagnostics(diagnostics)).Select(summary => summary.Line);

    /// <summary>
    /// Reads the single test method every <c>FSH0006</c> message names, so that an assertion states the
    /// attribution without also pinning the name of the mutation the message starts with.
    /// </summary>
    /// <param name="diagnostics">The diagnostics of a run.</param>
    /// <returns>One test method name per <c>FSH0006</c> diagnostic.</returns>
    private static IEnumerable<string> SingleTestCaseMethods(ImmutableArray<Diagnostic> diagnostics) =>
        DiagnosticAssertions
            .Summarise(SingleTestCaseDiagnostics(diagnostics))
            .Select(summary => ExtractTestMethod(summary.Message));

    /// <summary>
    /// Extracts the quoted test method name that follows <see cref="TestMethodMarker" />.
    /// </summary>
    /// <param name="message">The message of an <c>FSH0006</c> diagnostic.</param>
    /// <returns>The name, or the whole message when the marker is missing, which fails the assertion.</returns>
    private static string ExtractTestMethod(string message)
    {
        var marker = message.IndexOf(TestMethodMarker, StringComparison.Ordinal);

        if (marker < 0)
        {
            return message;
        }

        var start = marker + TestMethodMarker.Length;
        var end = message.IndexOf('\'', start);

        return end < 0 ? message.Substring(start) : message.Substring(start, end - start);
    }

    private static string Describe(ImmutableArray<Diagnostic> diagnostics, string id) =>
        DiagnosticAssertions.Describe(AnalyzerRunner.OfId(diagnostics, id));

    private static bool IsAnalyzerFailure(string id) =>
        string.Equals(id, AnalyzerRunner.AnalyzerFailureId, StringComparison.Ordinal);

    private static ImmutableArray<Diagnostic> Errors(Compilation compilation) =>
        CompilationFactory.GetCompileErrors(compilation);
}
