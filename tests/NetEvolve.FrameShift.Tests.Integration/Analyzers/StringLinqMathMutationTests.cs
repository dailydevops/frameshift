namespace NetEvolve.FrameShift.Tests.Integration.Analyzers;

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using NetEvolve.FrameShift.Analyzers;
using NetEvolve.FrameShift.Diagnostics;
using NetEvolve.FrameShift.Mutations.Operators;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Drives <see cref="StringLiteralMutator" />, <see cref="StringMethodMutator" />,
/// <see cref="LinqMethodMutator" />, <see cref="MathMethodMutator" />, <see cref="StatementRemovalMutator" />
/// and <see cref="CollectionInitializerMutator" /> through <see cref="MutationCoverageAnalyzer" /> end to
/// end, exactly like <see cref="CultureMutationTests" /> does for the culture-sensitivity family, so that
/// each operator is proven to produce the diagnostics a consumer sees in its build log instead of merely
/// to construct mutations in isolation.
/// </summary>
/// <remarks>
/// <para>
/// Every test states the exact set of reported gaps as one text block, built from the identifier, the
/// 1-based line and the full message of each diagnostic. Every fixture is written so that the operator
/// under test is the only one with a mutation point on the lines that matter: arguments are parameters or
/// field/property reads instead of literals, comparisons are avoided where they are not the point of the
/// test, so the diagnostic set stays a statement about the one operator instead of about every operator
/// that happens to also see the fixture.
/// </para>
/// <para>
/// Each fixture pairs the member under inspection with <c>Fixture.Reached.Identity</c>, whose body carries
/// no mutation point at all. Naming that member in the manifest is what gives the analyzer a non-empty
/// reachable set - without one it reports an unusable manifest and stays silent about the code - while
/// contributing not a single diagnostic of its own.
/// </para>
/// </remarks>
public class StringLinqMathMutationTests
{
    private const string ProductionAssemblyName = "ProductionAssembly";

    /// <summary>
    /// The member every fixture declares to make the manifest resolvable, and whose <c>return value;</c>
    /// carries no mutation point.
    /// </summary>
    private const string AnchorMemberId = "M:Fixture.Reached.Identity(System.Int32)~System.Int32";

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

    private const int GreetingLine = 15;
    private const int BlankLine = 20;

    private const int StartsWithLine = 20;
#if !NETFRAMEWORK
    private const int TrimLine = 25;
#endif
    private const int IsBlankLine = 30;

    private const int AnyLine = 26;
    private const int FirstLine = 31;
    private const int MinLine = 36;
    private const int SkipLine = 41;

    private const int SineLine = 22;
    private const int MinMaxLine = 27;
    private const int FloorLine = 32;
    private const int AbsLine = 37;

    private const int EarlyExitConditionLine = 17;
    private const int NestedReturnLine = 19;
    private const int EarlyExitAnnounceLine = 22;
    private const int AnnounceBodyLine = 27;
    private const int LastReturnAnnounceLine = 32;
    private const int LoopConditionLine = 40;
    private const int LoopBreakLine = 42;
    private const int LoopAnnounceLine = 45;
    private const int SkipConditionLine = 53;
    private const int SkipContinueLine = 55;
    private const int SkipAnnounceLine = 58;
    private const int ValidateConditionLine = 64;
    private const int ValidateThrowLine = 66;
    private const int FailAnnounceLine = 72;

    private const int EmptyArrayLine = 22;
    private const int CollectionInitializerLine = 30;
    private const int EmptyIntsLine = 35;
    private const int EmptyObjectsLine = 40;
    private const int EmptyNullableStringsLine = 45;

    /// <summary>
    /// <c>Literals.Greeting</c> (line 15) returns a non-empty literal, <c>Literals.Blank</c> (line 20)
    /// returns the empty one, and <c>ConstantLabel</c> reads a <see langword="const" /> field whose
    /// initializer literal sits in a position that only accepts a compile-time constant - the one literal
    /// of the whole file that is not a mutation point at all.
    /// </summary>
    private const string StringLiteralSource = """
        namespace Fixture;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Literals
        {
            public static string Greeting()
            {
                return "hello";
            }

            public static string Blank()
            {
                return "";
            }
        }

        public static class ConstantLabel
        {
            private const string Marker = "fixed";

            public static string Describe()
            {
                return Marker;
            }
        }
        """;

    /// <summary>
    /// <c>Text.StartsWith</c> (line 20), <c>Text.TrimAll</c> (line 25) and <c>Text.IsBlank</c> (line 30)
    /// each call a well known <see cref="string" /> method with a matching counterpart overload, while
    /// <c>Text.CustomTrim</c> calls a same-named, same-shaped method declared on <c>Custom</c> instead of
    /// on <see cref="string" /> itself, which the operator leaves untouched.
    /// </summary>
    private const string StringMethodSource = """
        namespace Fixture;

        public sealed class Custom
        {
            public string Trim() => string.Empty;
        }

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Text
        {
            public static bool StartsWith(string value, string prefix)
            {
                return value.StartsWith(prefix);
            }

            public static string TrimAll(string value)
            {
                return value.Trim();
            }

            public static bool IsBlank(string value)
            {
                return string.IsNullOrEmpty(value);
            }

            public static string CustomTrim(Custom custom)
            {
                return custom.Trim();
            }
        }
        """;

    /// <summary>
    /// <c>Queries.AnyMatch</c> (line 26), <c>Queries.FirstMatch</c> (line 31), <c>Queries.MinOf</c> (line
    /// 36) and <c>Queries.SkipSome</c> (line 41) each call a well known <c>System.Linq.Enumerable</c>
    /// method, while <c>Queries.CustomAny</c> calls a same-named, same-shaped method declared on
    /// <c>Custom</c> instead of on <c>Enumerable</c> itself. Every predicate reads the property
    /// <c>Flag</c> instead of comparing anything, so the lambda itself carries no mutation point of its
    /// own.
    /// </summary>
    private const string LinqMethodSource = """
        namespace Fixture;

        using System;
        using System.Collections.Generic;
        using System.Linq;

        public sealed class Custom
        {
            public bool Any() => Environment.Is64BitProcess;
        }

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Queries
        {
            private static bool Flag => Environment.Is64BitProcess;

            public static bool AnyMatch(IEnumerable<int> values)
            {
                return values.Any(_ => Flag);
            }

            public static int FirstMatch(IEnumerable<int> values)
            {
                return values.First(_ => Flag);
            }

            public static int MinOf(IEnumerable<int> values)
            {
                return values.Min();
            }

            public static IEnumerable<int> SkipSome(IEnumerable<int> values, int count)
            {
                return values.Skip(count);
            }

            public static bool CustomAny(Custom custom)
            {
                return custom.Any();
            }
        }
        """;

    /// <summary>
    /// <c>Trig.SineOf</c> (line 22), <c>Trig.SmallerOf</c> (line 27) and <c>Trig.FloorOf</c> (line 32)
    /// each call a well known <see cref="System.Math" /> method with a matching counterpart overload,
    /// <c>Trig.AbsoluteOf</c> (line 37) calls <c>Math.Abs</c>, whose only mutation drops the call entirely,
    /// and <c>Trig.CustomAbs</c> calls a same-named, same-shaped method declared on <c>Custom</c> instead
    /// of on <see cref="System.Math" /> itself.
    /// </summary>
    private const string MathMethodSource = """
        namespace Fixture;

        using System;

        public sealed class Custom
        {
            public static double Abs(double value) => value;
        }

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Trig
        {
            public static double SineOf(double angle)
            {
                return Math.Sin(angle);
            }

            public static double SmallerOf(double left, double right)
            {
                return Math.Min(left, right);
            }

            public static double FloorOf(double value)
            {
                return Math.Floor(value);
            }

            public static double AbsoluteOf(double value)
            {
                return Math.Abs(value);
            }

            public static double CustomAbs(double value)
            {
                return Custom.Abs(value);
            }
        }
        """;

    /// <summary>
    /// Every construct <see cref="StatementRemovalMutator" /> recognises and every guard that keeps it
    /// from firing, in one file: a nested <c>return;</c> (line 19, removed - it is not the trailing
    /// statement of the method body), a trailing <c>return;</c> in <c>LastReturn</c> (kept - removing it
    /// would change nothing), a <c>break</c> and a <c>continue</c> inside a loop (removed), a nested
    /// <c>throw</c> in a <see langword="void" /> method (removed), a trailing <c>throw</c> in a
    /// non-<see langword="void" /> method (kept - removing it would leave a code path without a required
    /// return) and a standalone invocation with a <c>ref</c> argument (kept - removing it would change
    /// what the caller observes through the reference). Every condition is a bare identifier, so no other
    /// operator has a mutation point on any of these lines.
    /// </summary>
    private const string StatementRemovalSource = """
        namespace Fixture;

        using System;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Guard
        {
            public static void EarlyExit(bool skip)
            {
                if (skip)
                {
                    return;
                }

                Announce(skip);
            }

            public static void Announce(bool flag)
            {
                Console.WriteLine(flag);
            }

            public static void LastReturn(bool skip)
            {
                Announce(skip);
                return;
            }

            public static void Loop(bool[] flags)
            {
                foreach (var flag in flags)
                {
                    if (flag)
                    {
                        break;
                    }

                    Announce(flag);
                }
            }

            public static void SkipEvens(bool[] flags)
            {
                foreach (var flag in flags)
                {
                    if (flag)
                    {
                        continue;
                    }

                    Announce(flag);
                }
            }

            public static void Validate(bool skip)
            {
                if (skip)
                {
                    throw new InvalidOperationException();
                }
            }

            public static int Fail(bool skip)
            {
                Announce(skip);
                throw new InvalidOperationException();
            }

            public static void Adjust(ref int value)
            {
                Increment(ref value);
            }

            public static void Increment(ref int value) { }
        }
        """;

    /// <summary>
    /// <c>Arrays.Pair</c> (line 17) empties a non-empty array initializer, <c>Arrays.EmptyArray</c> (line
    /// 22) fills an empty collection expression converted to an array, <c>Collections.Values</c> (line 30)
    /// empties a non-empty collection expression, and the remaining members of <c>Collections</c> probe
    /// every answer <c>AllowsDefault</c> can give for an empty one: <c>int</c> is a value type (line 35,
    /// filled), <c>object</c> is <see cref="Microsoft.CodeAnalysis.SpecialType.System_Object" /> (line 40, filled), an
    /// annotated <c>string?</c> is a nullable reference type (line 45, filled), a plain <c>string</c> is
    /// not annotated under this compilation's enabled nullable context (not filled), and an unconstrained
    /// type parameter resolves to neither (not filled).
    /// </summary>
    private const string CollectionInitializerSource = """
        namespace Fixture;

        using System.Collections.Generic;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Arrays
        {
            public static int[] Pair(int first, int second)
            {
                return new[] { first, second };
            }

            public static int[] EmptyArray()
            {
                return [];
            }
        }

        public static class Collections
        {
            public static List<int> Values(int first, int second)
            {
                return [first, second];
            }

            public static List<int> EmptyInts()
            {
                return [];
            }

            public static List<object> EmptyObjects()
            {
                return [];
            }

            public static List<string?> EmptyNullableStrings()
            {
                return [];
            }

            public static List<string> EmptyStrings()
            {
                return [];
            }

            public static List<T> EmptyGeneric<T>()
            {
                return [];
            }
        }
        """;

    /// <summary>
    /// Every fixture of this class, so that one test can prove that all of them compile and that none of
    /// them makes the analyzer crash.
    /// </summary>
    /// <returns>One factory per fixture.</returns>
    public static IEnumerable<Func<string>> Fixtures() =>
        new[]
        {
            StringLiteralSource,
            StringMethodSource,
            LinqMethodSource,
            MathMethodSource,
            StatementRemovalSource,
            CollectionInitializerSource,
        }.Select(source => (Func<string>)(() => source));

    /// <summary>
    /// A non-empty literal reports the collapse to an empty one, an empty literal reports the fill with a
    /// non-empty placeholder, and the literal sitting in a <see langword="const" /> field initializer -
    /// a position that only accepts a compile-time constant - reports nothing at all.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedStringLiterals_ReportsTheEmptyAndNonEmptySwap()
    {
        var compilation = CompilationFactory.Create(StringLiteralSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(Expect((GreetingLine, "\"...\" => \"\""), (BlankLine, "\"\" => \"FrameShift\"")));
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
        }
    }

    /// <summary>
    /// <c>StartsWith</c> and <c>IsNullOrEmpty</c> each report the rename to their counterpart(s), while
    /// the same-named, same-shaped method of <c>Custom</c> reports nothing: the operator resolves the
    /// called method instead of matching its name.
    /// </summary>
    /// <remarks>
    /// Whether <c>Trim()</c> renames to <c>TrimStart</c>/<c>TrimEnd</c> depends on
    /// <see cref="System.String" /> actually declaring a genuinely zero-parameter overload of those two
    /// methods, which the modern .NET reference assemblies do and the .NET Framework ones this repository
    /// also targets do not - there, only the <c>params char[]</c> overload exists, so
    /// <c>HasSameParameters</c> never matches and the operator stays silent on that member.
    /// <see cref="Analyze_CoveredStringMethods_ReportsNothing" /> still covers <c>TrimAll</c> in its
    /// manifest regardless, so it is unaffected either way.
    /// </remarks>
    [Test]
    public async Task Analyze_UntestedStringMethods_ReportsRenamesAndSkipsAForeignType()
    {
        var compilation = CompilationFactory.Create(StringMethodSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(
                    Expect(
                        (StartsWithLine, "StartsWith => EndsWith"),
#if !NETFRAMEWORK
                        (TrimLine, "Trim => TrimStart"),
                        (TrimLine, "Trim => TrimEnd"),
#endif
                        (IsBlankLine, "IsNullOrEmpty => IsNullOrWhiteSpace")
                    )
                );
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
        }
    }

    /// <summary>
    /// The counterpart of the previous test: naming every member that calls a well known <see cref="string" />
    /// method silences the analysis completely, which is what makes the four gaps above a statement about
    /// coverage rather than about the operator.
    /// </summary>
    [Test]
    public async Task Analyze_CoveredStringMethods_ReportsNothing()
    {
        const string startsWithMemberId = "M:Fixture.Text.StartsWith(System.String,System.String)~System.Boolean";
        const string trimAllMemberId = "M:Fixture.Text.TrimAll(System.String)~System.String";
        const string isBlankMemberId = "M:Fixture.Text.IsBlank(System.String)~System.Boolean";

        var compilation = CompilationFactory.Create(StringMethodSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(
                compilation,
                [CreateManifest(AnchorMemberId, startsWithMemberId, trimAllMemberId, isBlankMemberId)]
            )
            .ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(Gaps(diagnostics)).IsEqualTo(NoGaps);
        }
    }

    /// <summary>
    /// <c>Any</c>, <c>First</c>, <c>Min</c> and <c>Skip</c> each report the rename to their counterpart(s),
    /// while the same-named, same-shaped method of <c>Custom</c> reports nothing: the invoked method has
    /// to be bound to <c>System.Linq.Enumerable</c> itself.
    /// </summary>
    /// <remarks>
    /// <c>Skip =&gt; SkipLast</c> is deliberately not asserted here: <c>Enumerable.SkipLast</c> was added
    /// to modern .NET and was never backported to .NET Framework, which this repository also targets, so
    /// that particular rename candidate exists on some target frameworks and not on others. <c>Skip =&gt;
    /// Take</c> is unaffected and asserted on every framework.
    /// </remarks>
    [Test]
    public async Task Analyze_UntestedLinqMethods_ReportsRenamesAndSkipsAForeignType()
    {
        var compilation = CompilationFactory.Create(LinqMethodSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(
                    Expect(
                        (AnyLine, "Any => All"),
                        (FirstLine, "First => FirstOrDefault"),
                        (MinLine, "Min => Max"),
                        (SkipLine, "Skip => Take")
#if !NETFRAMEWORK
                        ,
                        (SkipLine, "Skip => SkipLast")
#endif
                    )
                );
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
        }
    }

    /// <summary>
    /// The co-function, the extreme and the rounding direction each report their swap, <c>Math.Abs</c>
    /// reports the removal of the call itself, and the same-named, same-shaped method of <c>Custom</c>
    /// reports nothing: the called method has to be declared on <see cref="System.Math" /> itself.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedMathMethods_ReportsRenamesAndTheAbsRemovalAndSkipsAForeignType()
    {
        var compilation = CompilationFactory.Create(MathMethodSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(
                    Expect(
                        (SineLine, "Sin => Cos"),
                        (MinMaxLine, "Min => Max"),
                        (FloorLine, "Floor => Ceiling"),
                        (AbsLine, "Math.Abs(value) => value")
                    )
                );
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
        }
    }

    /// <summary>
    /// The flagship case of the family: every removable construct reports its removal, and every guard -
    /// the trailing <c>return;</c>, the trailing <c>throw</c> of a non-<see langword="void" /> member and
    /// the invocation with a <c>ref</c> argument - keeps the analysis silent instead. Every <c>if</c>
    /// condition guarding a removable statement is itself a <c>bool</c> parameter, which
    /// <see cref="LogicalNegationMutator" /> also reports a gap for at its own line - a second,
    /// independent operator firing on the same fixture, not a mistake.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedStatementRemovalConstructs_ReportsRemovalsAndRespectsEveryGuard()
    {
        var compilation = CompilationFactory.Create(StatementRemovalSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(
                    Expect(
                        (EarlyExitConditionLine, "x => !(x)"),
                        (NestedReturnLine, "return; => (removed)"),
                        (EarlyExitAnnounceLine, "Announce(skip) => (removed)"),
                        (AnnounceBodyLine, "Console.WriteLine(flag) => (removed)"),
                        (LastReturnAnnounceLine, "Announce(skip) => (removed)"),
                        (LoopConditionLine, "x => !(x)"),
                        (LoopBreakLine, "break; => (removed)"),
                        (LoopAnnounceLine, "Announce(flag) => (removed)"),
                        (SkipConditionLine, "x => !(x)"),
                        (SkipContinueLine, "continue; => (removed)"),
                        (SkipAnnounceLine, "Announce(flag) => (removed)"),
                        (ValidateConditionLine, "x => !(x)"),
                        (ValidateThrowLine, "throw ...; => (removed)"),
                        (FailAnnounceLine, "Announce(skip) => (removed)")
                    )
                );
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
        }
    }

    /// <summary>
    /// A non-empty collection expression reports its collapse to an empty one, an empty collection
    /// expression reports the fill with <see langword="default" /> for every element type
    /// <c>AllowsDefault</c> accepts - a value type, <c>object</c>, and an annotated nullable reference
    /// type - and reports nothing for the two it rejects: a non-annotated reference type and an
    /// unconstrained type parameter. The old-style array initializer of <c>Arrays.Pair</c>
    /// (<c>new[] { first, second }</c>) is deliberately included and reports nothing at all:
    /// <see cref="CollectionInitializerMutator" /> only recognises collection-expression syntax
    /// (<c>[ ... ]</c>), not the classic <c>new[] { ... }</c> form.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedCollectionInitializers_ReportsEmptyingAndDefaultFillsWhereSafe()
    {
        var compilation = CompilationFactory.Create(CollectionInitializerSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(
                    Expect(
                        (EmptyArrayLine, "[] => [default]"),
                        (CollectionInitializerLine, "[ ... ] => []"),
                        (EmptyIntsLine, "[] => [default]"),
                        (EmptyObjectsLine, "[] => [default]"),
                        (EmptyNullableStringsLine, "[] => [default]")
                    )
                );
            _ = await Assert.That(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.InvalidTestSurfaceManifest)).IsEmpty();
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
    /// nothing about test data, so a lower bound is the honest count - and it keeps <c>FSH0006</c> silent,
    /// which is what lets every exact diagnostic set above stay a statement about the six operators alone.
    /// Every reference is also written as behaviorally verified, for the same reason, to keep
    /// <c>FSH0007</c> silent as well.
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

    private static string ToText(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static ImmutableArray<Diagnostic> Errors(Compilation compilation) =>
        CompilationFactory.GetCompileErrors(compilation);
}
