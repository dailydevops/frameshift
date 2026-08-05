namespace NetEvolve.FrameShift.Tests.Integration.Analyzers;

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using NetEvolve.FrameShift.Analyzers;
using NetEvolve.FrameShift.Diagnostics;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Drives <see cref="NetEvolve.FrameShift.Mutations.Operators.ConditionalExpressionMutator" />,
/// <see cref="NetEvolve.FrameShift.Mutations.Operators.EqualityOperatorMutator" />,
/// <see cref="NetEvolve.FrameShift.Mutations.Operators.LogicalNegationMutator" />,
/// <see cref="NetEvolve.FrameShift.Mutations.Operators.LogicalOperatorMutator" />,
/// <see cref="NetEvolve.FrameShift.Mutations.Operators.NullableLiteralMutator" />,
/// <see cref="NetEvolve.FrameShift.Mutations.Operators.NullCoalescingMutator" />,
/// <see cref="NetEvolve.FrameShift.Mutations.Operators.OptionalArgumentRemovalMutator" /> and
/// <see cref="NetEvolve.FrameShift.Mutations.Operators.NumericLiteralMutator" /> through
/// <see cref="MutationCoverageAnalyzer" /> end to end, the way <c>CultureMutationTests</c> drives the
/// culture-sensitivity family: a real production fixture, a manifest naming which members are reachable,
/// and the exact set of <c>FSH0001</c> diagnostics the analyzer reports for it, stated as identifier,
/// 1-based line and full message.
/// </summary>
/// <remarks>
/// <para>
/// Several fixtures deliberately let two operators share one construct instead of avoiding the overlap:
/// a ternary condition that is a plain boolean identifier is offered to
/// <see cref="NetEvolve.FrameShift.Mutations.Operators.ConditionalExpressionMutator" /> for the branches
/// and to <see cref="NetEvolve.FrameShift.Mutations.Operators.LogicalNegationMutator" /> for the condition
/// itself, and a comparer argument dropped by
/// <see cref="NetEvolve.FrameShift.Mutations.Operators.OptionalArgumentRemovalMutator" /> is exactly the
/// kind of position a real call site would also drop. Stating every diagnostic such a construct produces,
/// instead of contriving a fixture that avoids the second operator, is what proves the two operators
/// coexist correctly on the very same node.
/// </para>
/// <para>
/// Every other fixture keeps helper members - a custom <c>IComparer&lt;int&gt;</c>, a constant fallback
/// value - free of mutation points of their own, either because their body has no syntax an operator
/// recognises or because the position is a compile-time constant context, so that the exact gap sets below
/// stay a statement about the operator under test alone.
/// </para>
/// </remarks>
public class ConditionalAndLogicalMutationTests
{
    private const string ProductionAssemblyName = "ProductionAssembly";

    /// <summary>
    /// The member every fixture declares to make the manifest resolvable, and whose <c>return value;</c>
    /// carries no mutation point.
    /// </summary>
    private const string AnchorMemberId = "M:Fixture.Reached.Identity(System.Int32)~System.Int32";

    private const string TernaryPickMemberId =
        "M:Fixture.Ternary.Pick(System.Boolean,System.Int32,System.Int32)~System.Int32";
    private const string AreEqualMemberId = "M:Fixture.Numbers.AreEqual(System.Int32,System.Int32)~System.Boolean";
    private const string EnsureLoadedMemberId = "M:Fixture.Cache.EnsureLoaded(System.String)~System.String";

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

    private const int TernaryLine = 15;
    private const int EqualsLine = 15;
    private const int NotEqualsLine = 20;
    private const int LogicalAndLine = 28;
    private const int LogicalOrLine = 33;
    private const int NegationRemovalLine = 15;
    private const int NegationWrapLine = 25;
    private const int NullableNumericLine = 15;
    private const int CoalesceLine = 15;
    private const int CoalesceAssignLine = 25;
    private const int OptionalArgumentLine = 27;

    private const int OperatorEqualsLine = 22;
    private const int OperatorNotEqualsLine = 27;
    private const int WalletSameLine = 45;

    private const int WrittenTrueLine = 17;
    private const int WrittenFalseLine = 22;
    private const int UnsetLine = 27;
    private const int InitialLine = 35;
    private const int MissingLine = 43;
    private const int BelowZeroLine = 51;

    private const int BigCountLine = 15;
    private const int TicketLine = 20;
    private const int RatioLine = 28;
    private const int ZeroLine = 33;

    /// <summary>
    /// <c>Money</c> overloads <c>==</c> and <c>!=</c> for the same parameter shape, which is the branch of
    /// <c>EqualityOperatorMutator</c> the built-in-operator fixtures above never reach:
    /// <c>HasUsableCounterpart</c> has to resolve the bound method as a user-defined operator, find its
    /// declared counterpart on the containing type, and match their parameter types before allowing the
    /// swap. The two comparisons inside the operator bodies themselves (line 22, line 27) are plain
    /// <see langword="int" /> comparisons - the built-in operator, not the user-defined one - so they are
    /// two more, unrelated mutation points of the very same fixture. <c>Equals</c>'s own <c>this == other</c>
    /// (line 32) is a well known member override <c>EquivalenceClassifier</c> already excludes elsewhere,
    /// so it contributes nothing here.
    /// </summary>
    private const string UserDefinedEqualitySource = """
        namespace Fixture;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public readonly struct Money
        {
            public readonly int Amount;

            public Money(int amount)
            {
                Amount = amount;
            }

            public static bool operator ==(Money left, Money right)
            {
                return left.Amount == right.Amount;
            }

            public static bool operator !=(Money left, Money right)
            {
                return left.Amount != right.Amount;
            }

            public override bool Equals(object obj)
            {
                return obj is Money other && this == other;
            }

            public override int GetHashCode()
            {
                return Amount;
            }
        }

        public static class Wallet
        {
            public static bool Same(Money left, Money right)
            {
                return left == right;
            }
        }
        """;

    /// <summary>
    /// Every supported underlying type of <c>NullableLiteralMutator</c> the flagship fixture does not
    /// already cover: <see langword="bool" />? on both its written values and both directions out of
    /// <see langword="null" />, <see langword="char" />? on a non-default written value,
    /// <c>System.Guid</c>? whose only literal-shaped mutation point is <see langword="null" />, and a
    /// negative <see langword="int" />? literal, which arrives as its own unary-minus node instead of a
    /// bare literal.
    /// </summary>
    private const string NullableLiteralFamilySource = """
        namespace Fixture;

        using System;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Flags
        {
            public static bool? WrittenTrue()
            {
                return true;
            }

            public static bool? WrittenFalse()
            {
                return false;
            }

            public static bool? Unset()
            {
                return null;
            }
        }

        public static class Letters
        {
            public static char? Initial()
            {
                return 'A';
            }
        }

        public static class Identifiers
        {
            public static Guid? Missing()
            {
                return null;
            }
        }

        public static class Temperatures
        {
            public static int? BelowZero()
            {
                return -5;
            }
        }
        """;

    /// <summary>
    /// Every constant kind of <c>NumericLiteralMutator</c> the flagship fixture does not already cover: an
    /// integral type with its own boundary type (<see langword="long" />, <see langword="ulong" />) and
    /// both floating-point directions - the zero-to-one mutant and the negation mutant a non-zero value
    /// gets instead.
    /// </summary>
    private const string NumericLiteralFamilySource = """
        namespace Fixture;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Counters
        {
            public static long BigCount()
            {
                return 10L;
            }

            public static ulong Ticket()
            {
                return 3UL;
            }
        }

        public static class Measurements
        {
            public static double Ratio()
            {
                return 2.5;
            }

            public static float Zero()
            {
                return 0.0f;
            }
        }
        """;

    /// <summary>
    /// <c>Ternary.Pick</c> returns a conditional expression on line 15 whose condition is a plain boolean
    /// parameter: it is a mutation point of <c>ConditionalExpressionMutator</c> for the branches and of
    /// <c>LogicalNegationMutator</c> for the condition itself, since the latter also watches every
    /// conditional expression.
    /// </summary>
    private const string ConditionalSource = """
        namespace Fixture;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Ternary
        {
            public static int Pick(bool flag, int a, int b)
            {
                return flag ? a : b;
            }
        }
        """;

    /// <summary>
    /// Two equality comparisons on lines 15 and 20 and two logical combinations on lines 28 and 33, all
    /// over plain parameters so that no other operator has anything to mutate in the same expressions.
    /// </summary>
    private const string EqualityAndLogicalSource = """
        namespace Fixture;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Numbers
        {
            public static bool AreEqual(int left, int right)
            {
                return left == right;
            }

            public static bool AreDifferent(int left, int right)
            {
                return left != right;
            }
        }

        public static class Gate
        {
            public static bool Both(bool left, bool right)
            {
                return left && right;
            }

            public static bool Either(bool left, bool right)
            {
                return left || right;
            }
        }
        """;

    /// <summary>
    /// <c>Toggle.Invert</c> removes an existing negation on line 15, while <c>Guard.ClampNonNegative</c>
    /// wraps the condition of an <c>if</c> statement on line 25. <c>Guard.Fallback</c> is a
    /// <see langword="const" /> field, so its literal never becomes a mutation point of its own and the
    /// method returning it carries none either.
    /// </summary>
    private const string NegationSource = """
        namespace Fixture;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Toggle
        {
            public static bool Invert(bool flag)
            {
                return !flag;
            }
        }

        public static class Guard
        {
            private const int Fallback = 7;

            public static int ClampNonNegative(bool isValid, int value)
            {
                if (isValid)
                {
                    return value;
                }

                return Fallback;
            }
        }
        """;

    /// <summary>
    /// <c>Quantity.Value</c> returns the literal <c>5</c> converted to <c>int?</c> on line 15: a mutation
    /// point of two operators at once, since <c>NullableLiteralMutator</c> only cares about the nullable
    /// conversion and <c>NumericLiteralMutator</c> only cares about the literal's own (unwrapped) type.
    /// </summary>
    private const string NullableAndNumericSource = """
        namespace Fixture;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Quantity
        {
            public static int? Value()
            {
                return 5;
            }
        }
        """;

    /// <summary>
    /// <c>Config.Resolve</c> coalesces two <see cref="string" /> parameters on line 15, and
    /// <c>Cache.EnsureLoaded</c> uses the coalescing assignment on line 25. Neither operand carries a
    /// literal or any other construct another operator recognises.
    /// </summary>
    private const string NullCoalescingSource = """
        namespace Fixture;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Config
        {
            public static string Resolve(string primary, string secondary)
            {
                return primary ?? secondary;
            }
        }

        public static class Cache
        {
            private static string _value = string.Empty;

            public static string EnsureLoaded(string fallback)
            {
                _value ??= fallback;
                return _value;
            }
        }
        """;

    /// <summary>
    /// <c>Passthrough</c> is a minimal <c>IComparer&lt;int&gt;</c> whose members carry no mutation point of
    /// their own - <c>x.CompareTo(y)</c> is not a construct any operator recognises, and its parameterless
    /// object creation has no argument to remove - so that <c>Sorted.Build</c> on line 27 is the only
    /// source of a gap: dropping the trailing comparer argument still binds to <c>SortedSet&lt;int&gt;</c>'s
    /// parameterless constructor.
    /// </summary>
    private const string OptionalArgumentRemovalSource = """
        namespace Fixture;

        using System.Collections.Generic;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public sealed class Passthrough : IComparer<int>
        {
            public static readonly Passthrough Instance = new Passthrough();

            public int Compare(int x, int y)
            {
                return x.CompareTo(y);
            }
        }

        public static class Sorted
        {
            public static SortedSet<int> Build()
            {
                return new SortedSet<int>(Passthrough.Instance);
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
            ConditionalSource,
            EqualityAndLogicalSource,
            NegationSource,
            NullableAndNumericSource,
            NullCoalescingSource,
            OptionalArgumentRemovalSource,
            UserDefinedEqualitySource,
            NullableLiteralFamilySource,
            NumericLiteralFamilySource,
        }.Select(source => (Func<string>)(() => source));

    /// <summary>
    /// The flagship case of the conditional family: an untested ternary reports all four branch mutations
    /// of <c>ConditionalExpressionMutator</c> plus the condition wrap of <c>LogicalNegationMutator</c>,
    /// which also watches every conditional expression.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedTernaryConditional_ReportsAllFourBranchMutationsAndTheNegationWrap()
    {
        var compilation = CompilationFactory.Create(ConditionalSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(
                    Expect(
                        (TernaryLine, "c ? a : b => c ? b : a"),
                        (TernaryLine, "c ? a : b => !c ? a : b"),
                        (TernaryLine, "c ? a : b => true ? a : b"),
                        (TernaryLine, "c ? a : b => false ? a : b"),
                        (TernaryLine, "x => !(x)")
                    )
                );
            _ = await Assert.That(Trivial(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    /// <summary>
    /// The same compilation, with <c>Ternary.Pick</c> itself recorded in the manifest: the analysis goes
    /// completely silent, which is what makes the five gaps above a statement about the two operators
    /// rather than about the fixture.
    /// </summary>
    [Test]
    public async Task Analyze_CoveredTernaryConditional_ReportsNothing()
    {
        var compilation = CompilationFactory.Create(ConditionalSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(TernaryPickMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(DiagnosticAssertions.Describe(diagnostics))
                .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    /// <summary>
    /// Every one of the four comparisons is offered its single counterpart mutation, with nothing left
    /// covered: <c>EqualityOperatorMutator</c> on lines 15 and 20, <c>LogicalOperatorMutator</c> on lines
    /// 28 and 33.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedEqualityAndLogicalOperators_ReportsEachOperatorSwap()
    {
        var compilation = CompilationFactory.Create(EqualityAndLogicalSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(
                    Expect(
                        (EqualsLine, "== => !="),
                        (NotEqualsLine, "!= => =="),
                        (LogicalAndLine, "&& => ||"),
                        (LogicalOrLine, "|| => &&")
                    )
                );
            _ = await Assert.That(Trivial(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    /// <summary>
    /// The counter-example that proves the four gaps above are about coverage, not about the fixture:
    /// covering <c>Numbers.AreEqual</c> alone silences only its own line, while the sibling comparison and
    /// both logical combinations keep reporting.
    /// </summary>
    [Test]
    public async Task Analyze_CoveredEqualityComparison_ReportsOnlyTheRemainingOperators()
    {
        var compilation = CompilationFactory.Create(EqualityAndLogicalSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AreEqualMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(
                    Expect((NotEqualsLine, "!= => =="), (LogicalAndLine, "&& => ||"), (LogicalOrLine, "|| => &&"))
                );
        }
    }

    /// <summary>
    /// Both directions of <c>LogicalNegationMutator</c> in one fixture: removing an existing negation on
    /// line 15, and wrapping the condition of an <c>if</c> statement that has none on line 25.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedNegationRemovalAndWrap_ReportsBothDirections()
    {
        var compilation = CompilationFactory.Create(NegationSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(Expect((NegationRemovalLine, "!x => x"), (NegationWrapLine, "x => !(x)")));
            _ = await Assert.That(Trivial(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    /// <summary>
    /// The literal <c>5</c> converted to <c>int?</c> is a mutation point of two operators at once:
    /// <c>NullableLiteralMutator</c> offers moving it to <see langword="null" /> and to the underlying
    /// type's default, <c>NumericLiteralMutator</c> offers incrementing and decrementing it - four gaps at
    /// the very same location, from two operators that ask different questions about it.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedNullableLiteralAndNumericLiteral_ReportsAllFourMutants()
    {
        var compilation = CompilationFactory.Create(NullableAndNumericSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(ExpectAt(NullableNumericLine, ["5 => null", "5 => 0", "5 => 6", "5 => 4"]));
            _ = await Assert.That(Trivial(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    /// <summary>
    /// The two halves of <c>NullCoalescingMutator</c>: a plain <c>??</c> expression offers keeping either
    /// operand, and a <c>??=</c> assignment offers becoming a plain assignment.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedNullCoalescingAndCoalesceAssignment_ReportsAllThreeMutants()
    {
        var compilation = CompilationFactory.Create(NullCoalescingSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(
                    Expect(
                        (CoalesceLine, "a ?? b => a"),
                        (CoalesceLine, "a ?? b => b"),
                        (CoalesceAssignLine, "a ??= b => a = b")
                    )
                );
            _ = await Assert.That(Trivial(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    /// <summary>
    /// Covering <c>Cache.EnsureLoaded</c> alone silences only the coalescing assignment, leaving the plain
    /// <c>??</c> expression of the unrelated, uncovered member reported - the same "covering one member
    /// never covers another" claim <c>CultureMutationTests</c> makes for the culture family.
    /// </summary>
    [Test]
    public async Task Analyze_CoveredCoalesceAssignment_ReportsOnlyTheCoalesceExpressionGaps()
    {
        var compilation = CompilationFactory.Create(NullCoalescingSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(EnsureLoadedMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(Expect((CoalesceLine, "a ?? b => a"), (CoalesceLine, "a ?? b => b")));
        }
    }

    /// <summary>
    /// The comparer argument of <c>Sorted.Build</c> is dropped because the remaining argument list still
    /// binds to <c>SortedSet&lt;int&gt;</c>'s own parameterless constructor - the acceptance criterion
    /// <c>OptionalArgumentRemovalMutator</c> exists for.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedOptionalArgumentRemoval_ReportsTheComparerRemoval()
    {
        var compilation = CompilationFactory.Create(OptionalArgumentRemovalSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(Expect((OptionalArgumentLine, "Passthrough.Instance => (removed)")));
            _ = await Assert.That(Trivial(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    /// <summary>
    /// Exercises <see cref="UserDefinedEqualitySource" />, see its doc comment for what each gap means.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedUserDefinedEquality_ReportsTheSwap()
    {
        var compilation = CompilationFactory.Create(UserDefinedEqualitySource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(
                    Expect(
                        (OperatorEqualsLine, "== => !="),
                        (OperatorNotEqualsLine, "!= => =="),
                        (WalletSameLine, "== => !=")
                    )
                );
        }
    }

    /// <summary>
    /// Exercises <see cref="NullableLiteralFamilySource" />, see its doc comment for what each gap means.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedNullableLiteralFamily_ReportsEveryUnderlyingKind()
    {
        var compilation = CompilationFactory.Create(NullableLiteralFamilySource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(
                    Expect(
                        (WrittenTrueLine, "true => null"),
                        (WrittenTrueLine, "true => false"),
                        (WrittenTrueLine, "true => false"),
                        (WrittenFalseLine, "false => null"),
                        (WrittenFalseLine, "false => true"),
                        (UnsetLine, "null => false"),
                        (UnsetLine, "null => true"),
                        (InitialLine, "'A' => null"),
                        (InitialLine, "'A' => '\\0'"),
                        (MissingLine, "null => Guid.Empty"),
                        (BelowZeroLine, "-5 => null"),
                        (BelowZeroLine, "-5 => 0"),
                        (BelowZeroLine, "5 => 6"),
                        (BelowZeroLine, "5 => 4"),
                        (BelowZeroLine, "-x => +x"),
                        (BelowZeroLine, "-x => x")
                    )
                );
        }
    }

    /// <summary>
    /// Exercises <see cref="NumericLiteralFamilySource" />, see its doc comment for what each gap means.
    /// </summary>
    [Test]
    public async Task Analyze_UntestedNumericLiteralFamily_ReportsEveryNumericKind()
    {
        var compilation = CompilationFactory.Create(NumericLiteralFamilySource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(
                    Expect(
                        (BigCountLine, "10L => 11L"),
                        (BigCountLine, "10L => 9L"),
                        (TicketLine, "3UL => 4UL"),
                        (TicketLine, "3UL => 2UL"),
                        (RatioLine, "2.5 => -2.5"),
                        (ZeroLine, "0 => 1")
                    )
                );
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
    /// <see cref="LowerBoundCount" />, and every reference is also written as behaviorally verified, so
    /// that these coverage-only tests never trip <c>FSH0006</c> or <c>FSH0007</c> by accident.
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
