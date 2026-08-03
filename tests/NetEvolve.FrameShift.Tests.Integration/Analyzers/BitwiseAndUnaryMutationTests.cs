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
/// Drives the bitwise, boolean-literal, increment/decrement and unary-sign operators through
/// <see cref="MutationCoverageAnalyzer" /> end to end, so that the family is proven to produce the
/// diagnostics a consumer sees in its build log instead of merely to construct mutations.
/// </summary>
/// <remarks>
/// <para>
/// One fixture carries every mutation point of the family, each on its own member and its own line, so
/// that a single manifest can leave the whole family unreached and one test can state the exact,
/// complete set of gaps as one text block - the same discipline
/// <see cref="CultureMutationTests" /> uses for the culture-sensitivity family.
/// </para>
/// <para>
/// <c>EnumBitwise.Combine</c> and <c>NullableBitwise.AndNullable</c> exist to drive
/// <see cref="NetEvolve.FrameShift.Mutations.Operators.IntegralTypeCheck" /> through its enum and its
/// nullable branch, the two decisions a plain <see langword="int" /> operand never exercises.
/// </para>
/// </remarks>
public class BitwiseAndUnaryMutationTests
{
    private const string ProductionAssemblyName = "ProductionAssembly";
    private const string TestAssemblyName = "TestAssembly";
    private const string TestFilePath = "FamilyTests.cs";

    /// <summary>
    /// The member every fixture declares to make the manifest resolvable, and whose <c>return value;</c>
    /// carries no mutation point.
    /// </summary>
    private const string AnchorMemberId = "M:Fixture.Reached.Identity(System.Int32)~System.Int32";

    /// <summary>
    /// The compound bitwise assignment covered in isolation by
    /// <see cref="Analyze_CoveredBitwiseAssignment_LeavesOnlyThatMemberSilent" />.
    /// </summary>
    private const string AndAssignMemberId = "M:Fixture.Bitwise.AndAssign(System.Int32,System.Int32)~System.Int32";

    /// <summary>
    /// The test method id every manifest of this fixture attributes its references to. No test asserts on
    /// it, because these tests state what the operators report, not which test reached what.
    /// </summary>
    private const string AnonymousTestId = "M:Fixture.Tests.AnonymousTests.Reaches";

    /// <summary>
    /// The case count recorded for <see cref="AnonymousTestId" />: a lower bound, because nothing here
    /// establishes how many input combinations the reaching test carries.
    /// </summary>
    private const string LowerBoundCount = "1+";

    /// <summary>
    /// The text the assertions use for "not a single gap was reported".
    /// </summary>
    private const string NoGaps = "<no gaps>";

    /// <summary>
    /// The line feed the expectations are joined with, instead of <see cref="Environment.NewLine" />, so
    /// that the very same text is produced on Windows and on Linux.
    /// </summary>
    private const string LineFeed = "\n";

    private const int AddAssignLine = 15;
    private const int BitwiseAndLine = 24;
    private const int LeftShiftLine = 29;
    private const int AndAssignLine = 34;
    private const int LeftShiftAssignLine = 40;
    private const int EnumOrLine = 56;
    private const int NullableAndLine = 64;
    private const int TrueLiteralLine = 72;
    private const int FalseLiteralLine = 77;
    private const int PreIncrementLine = 85;
    private const int PostDecrementLine = 90;
    private const int UnaryMinusLine = 98;
    private const int UnaryPlusLine = 103;

    private const int CheckedExpressionLine = 15;
    private const int UncheckedExpressionLine = 20;
    private const int CheckedStatementLine = 25;
    private const int UncheckedStatementLine = 33;

    /// <summary>
    /// All four shapes <c>CheckedContextMutator</c> recognises: the expression form and the statement
    /// form, each once as <see langword="checked" /> and once as <see langword="unchecked" />.
    /// </summary>
    private const string CheckedContextSource = """
        namespace Fixture;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Overflow
        {
            public static int CheckedExpression(int value)
            {
                return checked(value);
            }

            public static int UncheckedExpression(int value)
            {
                return unchecked(value);
            }

            public static int CheckedStatement(int value)
            {
                checked
                {
                    return value;
                }
            }

            public static int UncheckedStatement(int value)
            {
                unchecked
                {
                    return value;
                }
            }
        }
        """;

    /// <summary>
    /// One member per mutation point of the whole family. Every member takes a variable operand, never a
    /// literal, so that neither the arithmetic-assignment operand check nor the unary
    /// constant-preservation check ever discards a mutation this fixture means to exercise.
    /// </summary>
    private const string FamilySource = """
        namespace Fixture;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Arithmetic
        {
            public static int AddAssign(int value)
            {
                value += 1;
                return value;
            }
        }

        public static class Bitwise
        {
            public static int AndOp(int left, int right)
            {
                return left & right;
            }

            public static int ShiftOp(int value)
            {
                return value << 1;
            }

            public static int AndAssign(int value, int mask)
            {
                value &= mask;
                return value;
            }

            public static int ShiftAssign(int value)
            {
                value <<= 1;
                return value;
            }
        }

        public enum Options
        {
            None = 0,
            A = 1,
            B = 2,
        }

        public static class EnumBitwise
        {
            public static Options Combine(Options left, Options right)
            {
                return left | right;
            }
        }

        public static class NullableBitwise
        {
            public static int? AndNullable(int? left, int? right)
            {
                return left & right;
            }
        }

        public static class Flag
        {
            public static bool AlwaysTrue()
            {
                return true;
            }

            public static bool AlwaysFalse()
            {
                return false;
            }
        }

        public static class Counter
        {
            public static int PreIncrement(int value)
            {
                return ++value;
            }

            public static int PostDecrement(int value)
            {
                return value--;
            }
        }

        public static class Sign
        {
            public static int Negate(int value)
            {
                return -value;
            }

            public static int Plus(int value)
            {
                return +value;
            }
        }
        """;

    /// <summary>
    /// A real TUnit test that calls every member of <see cref="FamilySource" /> once, used to build the
    /// manifest of <see cref="Analyze_EveryMemberCovered_ReportsNothing" /> with the real collector
    /// instead of hand-written member ids.
    /// </summary>
    private const string TestSource = """
        namespace Fixture.Tests;

        using Fixture;
        using TUnit.Core;

        public class FamilyTests
        {
            [Test]
            public void ReachesEveryMember()
            {
                _ = Arithmetic.AddAssign(1);
                _ = Bitwise.AndOp(1, 2);
                _ = Bitwise.ShiftOp(1);
                _ = Bitwise.AndAssign(1, 2);
                _ = Bitwise.ShiftAssign(1);
                _ = EnumBitwise.Combine(Options.A, Options.B);
                _ = NullableBitwise.AndNullable(1, 2);
                _ = Flag.AlwaysTrue();
                _ = Flag.AlwaysFalse();
                _ = Counter.PreIncrement(1);
                _ = Counter.PostDecrement(1);
                _ = Sign.Negate(1);
                _ = Sign.Plus(1);
            }
        }
        """;

    /// <summary>
    /// The complete set of gaps <see cref="FamilySource" /> produces when nothing but the anchor is
    /// reached: one entry per mutation point, sorted by line and then by message the same way the
    /// analyzer itself reports them. The literal <c>1</c> operand of <c>AddAssign</c>, <c>ShiftOp</c> and
    /// <c>ShiftAssign</c> also gets its own <c>1 =&gt; 0</c> gap from <c>NumericLiteralMutator</c>, on top
    /// of the operator under test at that line - a second, independent operator firing on the same
    /// location, not a mistake.
    /// </summary>
    private static readonly (int Line, string DisplayName)[] _everyGap =
    [
        (AddAssignLine, "+= => %="),
        (AddAssignLine, "+= => *="),
        (AddAssignLine, "+= => -="),
        (AddAssignLine, "+= => /="),
        (AddAssignLine, "1 => 0"),
        (BitwiseAndLine, "& => ^"),
        (BitwiseAndLine, "& => |"),
        (LeftShiftLine, "1 => 0"),
        (LeftShiftLine, "<< => >>"),
        (AndAssignLine, "&= => ^="),
        (AndAssignLine, "&= => |="),
        (LeftShiftAssignLine, "1 => 0"),
        (LeftShiftAssignLine, "<<= => >>="),
        (EnumOrLine, "| => &"),
        (EnumOrLine, "| => ^"),
        (NullableAndLine, "& => ^"),
        (NullableAndLine, "& => |"),
        (TrueLiteralLine, "true => false"),
        (FalseLiteralLine, "false => true"),
        (PreIncrementLine, "++x => --x"),
        (PostDecrementLine, "x-- => x++"),
        (UnaryMinusLine, "-x => +x"),
        (UnaryMinusLine, "-x => x"),
        (UnaryPlusLine, "+x => -x"),
        (UnaryPlusLine, "+x => x"),
    ];

    /// <summary>
    /// The flagship case of the family: with only the anchor reached, every member reports every mutation
    /// it carries, which is what makes the members above a statement about the operators themselves.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedFamily_ReportsEveryMutationOfEveryMember()
    {
        var compilation = CompilationFactory.Create(FamilySource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(Gaps(diagnostics)).IsEqualTo(Expect(_everyGap));
            _ = await Assert.That(Trivial(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
        }
    }

    /// <summary>
    /// The same compilation, with <c>Bitwise.AndAssign</c> itself recorded in the manifest: its two gaps
    /// vanish and every other member keeps reporting exactly as before, which is what proves the silence
    /// is a statement about coverage of that one member rather than about the operator going quiet.
    /// </summary>
    [Test]
    public async Task Analyze_CoveredBitwiseAssignment_LeavesOnlyThatMemberSilent()
    {
        var compilation = CompilationFactory.Create(FamilySource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AndAssignMemberId)]).ConfigureAwait(false);
        var expected = _everyGap.Where(gap => gap.Line != AndAssignLine);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(Gaps(diagnostics)).IsEqualTo(Expect([.. expected]));
            _ = await Assert.That(Trivial(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    /// <summary>
    /// With every member of the fixture collected as a real manifest built from a real test compilation
    /// that calls each of them, the whole family goes silent at once - the counter-example that shows the
    /// gaps above come from the manifest missing the members, not from the fixture itself being
    /// unreachable code. Collecting the manifest instead of hand-writing the member ids also proves the
    /// enum and the nullable member ids resolve the way <see cref="TestSurfaceCollector" /> writes them,
    /// which a hand-written id could easily get subtly wrong.
    /// </summary>
    [Test]
    public async Task Analyze_EveryMemberCovered_ReportsNothing()
    {
        var compilation = CompilationFactory.Create(FamilySource, ProductionAssemblyName);
        var manifest = new InMemoryAdditionalText(CollectManifest(compilation));

        var diagnostics = await RunAsync(compilation, [manifest]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
            _ = await Assert
                .That(
                    DiagnosticAssertions.Describe(
                        AnalyzerRunner.OfId(diagnostics, DiagnosticIds.UnreachableMutationPoint)
                    )
                )
                .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    /// <summary>
    /// <see cref="FamilySource" /> only ever uses <c>+=</c> as the assignment kind under test, which
    /// leaves the <c>-=</c>/<c>*=</c>/<c>/=</c>/<c>%=</c> arms of <c>ArithmeticAssignmentMutator</c>'s own
    /// name/symbol/token/metadata-name lookups untouched as a <em>source</em> kind - they are only ever
    /// exercised as a mutation <em>target</em>. Using <c>*=</c> here as the source exercises every one of
    /// those lookups for a different starting point, producing mutations to <c>+=</c>, <c>-=</c>, <c>/=</c>
    /// and <c>%=</c> instead.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedMultiplyAssignment_ReportsEveryOtherArithmeticAssignment()
    {
        const string source = """
            namespace Fixture;

            public static class Reached
            {
                public static int Identity(int value)
                {
                    return value;
                }
            }

            public static class Scaling
            {
                public static int MultiplyAssign(int value, int factor)
                {
                    value *= factor;
                    return value;
                }
            }
            """;
        const int multiplyAssignLine = 15;

        var compilation = CompilationFactory.Create(source, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(
                    Expect(
                        (multiplyAssignLine, "*= => +="),
                        (multiplyAssignLine, "*= => -="),
                        (multiplyAssignLine, "*= => /="),
                        (multiplyAssignLine, "*= => %=")
                    )
                );
        }
    }

    /// <summary>
    /// All four shapes <c>CheckedContextMutator</c> recognises in one fixture: the expression form and the
    /// statement form, each swapped in both directions.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedCheckedContexts_ReportsAllFourSwaps()
    {
        var compilation = CompilationFactory.Create(CheckedContextSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(
                    Expect(
                        (CheckedExpressionLine, "checked(...) => unchecked(...)"),
                        (UncheckedExpressionLine, "unchecked(...) => checked(...)"),
                        (CheckedStatementLine, "checked { } => unchecked { }"),
                        (UncheckedStatementLine, "unchecked { } => checked { }")
                    )
                );
        }
    }

    /// <summary>
    /// The fixture compiles and is analysed without the analyzer throwing. Roslyn turns an analyzer
    /// exception into <c>AD0001</c> and carries on, so a crash would otherwise look like a diagnostic the
    /// tests above simply did not expect.
    /// </summary>
    [Test]
    public async Task Analyze_Family_CompilesAndReportsNoAnalyzerFailure()
    {
        var compilation = CompilationFactory.Create(FamilySource, ProductionAssemblyName);

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

    /// <summary>
    /// Builds a manifest recording <paramref name="referencedMemberIds" /> as the production members the
    /// tests of the first pass touched.
    /// </summary>
    /// <remarks>
    /// Every reference is attributed to one anonymous test whose case count is the lower bound
    /// <see cref="LowerBoundCount" />. These tests are about which mutation points are reachable and state
    /// nothing about test data, so a lower bound is the honest count - and it keeps <c>FSH0006</c> silent,
    /// which is what lets every exact diagnostic set below stay a statement about the family alone. Every
    /// reference is also written as behaviorally verified, for the same reason: these tests are not about
    /// the behavioral classification, so <c>FSH0007</c> has to stay out of the way as well.
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
            DiagnosticAssertions
                .Summarise(gaps)
                // Several operators can each produce their own mutant at the exact same location (for
                // example the two candidates of a rename family), and the order the analyzer enumerates
                // those ties in is not guaranteed to be the same across runtimes - .NET Framework's string
                // hashing differs from modern .NET, which can reorder anything keyed by a Dictionary
                // internally. Sorting ties by message text makes the expectation below deterministic.
                .OrderBy(summary => summary.Line)
                .ThenBy(summary => summary.Message, StringComparer.Ordinal)
                .Select(summary => Entry(summary.Id, summary.Line, summary.Message))
        );
    }

    /// <summary>
    /// Builds the expectation of a set of gaps, each one a line and the display name of its mutation.
    /// </summary>
    /// <param name="gaps">The expected gaps.</param>
    /// <returns>The expected text block, or <see cref="NoGaps" /> when nothing is expected.</returns>
    private static string Expect(params (int Line, string DisplayName)[] gaps) =>
        gaps.Length == 0
            ? NoGaps
            : Join(
                gaps.OrderBy(gap => gap.Line)
                    .ThenBy(gap => gap.DisplayName, StringComparer.Ordinal)
                    .Select(gap => GapEntry(gap.Line, gap.DisplayName))
            );

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
