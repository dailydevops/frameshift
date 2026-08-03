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
/// Drives <see cref="MutationCoverageAnalyzer" /> end to end over call graphs shaped specifically to
/// exercise the reachability closure's dispatch approximation and related-member propagation, and over
/// member shapes shaped to exercise the equivalence classifier's member-level triviality checks.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CultureMutationTests" /> and <see cref="MutationAnalysisRoundTripTests" /> already prove
/// the closure's plain call-graph walk (direct calls, transitive helpers, local functions, lambdas) and
/// the constant-folding and regex-shorthand halves of the equivalence classifier. What neither of them
/// drives through the real analyzer is virtual and interface dispatch, the accessors a property or event
/// shares its reachability with, or the member-level triviality checks (a throw-only body, code the
/// compiler already proves unreachable, a value assigned to a discard, and a member excluded by name or
/// by attribute). This file fills exactly that gap.
/// </para>
/// <para>
/// Every fixture pairs the member under inspection with <c>Fixture.Reached.Identity</c>, whose body
/// carries no mutation point at all. Naming that member in the manifest is what gives the analyzer a
/// non-empty reachable set - without one it reports an unusable manifest and stays silent about the
/// code - while contributing not a single diagnostic of its own.
/// </para>
/// </remarks>
public class ReachabilityAndEquivalenceMutationTests
{
    private const string ProductionAssemblyName = "ProductionAssembly";

    /// <summary>
    /// The member every fixture declares to make the manifest resolvable, and whose <c>return value;</c>
    /// carries no mutation point.
    /// </summary>
    private const string AnchorMemberId = "M:Fixture.Reached.Identity(System.Int32)~System.Int32";

    /// <summary>
    /// The test method id every manifest of this fixture attributes its references to. No test asserts
    /// on it, because these tests state what is reachable or trivial, not which test reached what.
    /// </summary>
    private const string AnonymousTestId = "M:Fixture.Tests.AnonymousTests.Reaches";

    /// <summary>
    /// The case count recorded for <see cref="AnonymousTestId" />: a lower bound, because nothing here
    /// establishes how many input combinations the reaching test carries.
    /// </summary>
    private const string LowerBoundCount = "1+";

    private const string NoGaps = "<no gaps>";
    private const string LineFeed = "\n";

    private const string RendererDescribeMemberId =
        "M:Fixture.Renderer.Describe(Fixture.IShape,System.Int32)~System.Int32";
    private const string ShapeAreaMemberId = "M:Fixture.IShape.Area(System.Int32)~System.Int32";
    private const string ZooCountLegsMemberId = "M:Fixture.Zoo.CountLegs(Fixture.Animal,System.Int32)~System.Int32";
    private const string ReaderReadMemberId = "M:Fixture.Reader.Read(Fixture.Counter)~System.Int32";

    private const int SquareAreaLine = 20;
    private const int CircleAreaLine = 28;
    private const int InterfaceLonerLine = 44;

    private const int AnimalLegsLine = 15;
    private const int DogLegsLine = 23;
    private const int PuppyLegsLine = 31;
    private const int OverrideLonerLine = 47;

    private const int PropertyGetLine = 17;
    private const int PropertySetLine = 18;
    private const int PropertyLonerLine = 34;

    private const int ThrowLine = 17;
    private const int UnreachableStatementLine = 17;
    private const int DiscardLine = 15;

    private const int ToStringLine = 26;
    private const int ExcludeFromCoverageLine = 35;
    private const int GeneratedCodeLine = 44;
    private const int ObsoleteLine = 53;

    private const string ThrowOnlyBodyReason = "the containing member does nothing but throw";
    private const string UnreachableStatementReason = "the mutated statement is already unreachable";
    private const string DiscardAssignmentReason = "the mutated value is assigned to a discard";
    private const string WellKnownMemberReason = "the containing member is a well known infrastructure member";
    private const string ObsoleteMemberReason = "the containing member is marked obsolete";
    private const string ConstantFoldingReason = "the mutated expression folds to the same constant";
    private const string ConstantDeclarationReason = "the mutation only changes a compile-time constant";
    private const string DefaultParameterReason = "the mutation only changes a default parameter value";
    private const string CaseLabelReason = "the mutation only changes a compile-time case label";

    private const string IntWrapperUseIntMemberId = "M:Fixture.IntWrapper.UseInt(System.Int32)~System.Int32";
    private const string SubscriberSubscribeMemberId =
        "M:Fixture.Subscriber.Subscribe(Fixture.Bell,System.EventHandler)";

    private const int GenericMethodLine = 15;
    private const int GenericLonerLine = 39;

    private const int EventAddLine = 22;
    private const int EventRemoveLine = 27;
    private const int EventLonerLine = 45;

    private const int ConstantFoldingLine = 17;
    private const int ConstantDeclarationLine = 13;
    private const int DefaultParameterLine = 13;
    private const int CaseLabelLine = 17;

    /// <summary>
    /// The five mutants an untested <c>value + 1</c> carries: the four counterparts the arithmetic
    /// operator offers, plus the one-to-zero mutant the numeric literal operator offers for the literal
    /// <c>1</c>. Every equivalence fixture below reuses this exact shape, so that one array describes the
    /// mutants of every member under inspection.
    /// </summary>
    private static readonly string[] _additionMutants = ["+ => -", "+ => *", "+ => /", "+ => %", "1 => 0"];

    /// <summary>
    /// <c>Square</c> and <c>Circle</c> both implement <c>IShape</c> and are declared nowhere else in the
    /// compilation than here, so <c>Renderer.Describe</c> calling through the interface variable is the
    /// only route a test can take to either of them. <c>Loner.Unrelated</c> implements nothing and is
    /// called by nobody, so it stays a gap in every test of this fixture.
    /// </summary>
    private const string InterfaceDispatchSource = """
        namespace Fixture;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public interface IShape
        {
            int Area(int side);
        }

        public sealed class Square : IShape
        {
            public int Area(int side)
            {
                return side * side;
            }
        }

        public sealed class Circle : IShape
        {
            public int Area(int side)
            {
                return side * side;
            }
        }

        public static class Renderer
        {
            public static int Describe(IShape shape, int side)
            {
                return shape.Area(side);
            }
        }

        public static class Loner
        {
            public static int Unrelated(int value)
            {
                return value + 5;
            }
        }
        """;

    /// <summary>
    /// <c>Puppy</c> overrides <c>Dog</c>, which overrides the virtual <c>Animal.Legs</c>: a call through
    /// the base class reference has to walk two links of the override chain to reach both. <c>Loner</c>
    /// carries no relation to <c>Animal</c> at all.
    /// </summary>
    private const string VirtualOverrideSource = """
        namespace Fixture;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public class Animal
        {
            public virtual int Legs(int extra)
            {
                return 4 + extra;
            }
        }

        public class Dog : Animal
        {
            public override int Legs(int extra)
            {
                return 4 + extra;
            }
        }

        public class Puppy : Dog
        {
            public override int Legs(int extra)
            {
                return 4 + extra;
            }
        }

        public static class Zoo
        {
            public static int CountLegs(Animal animal, int extra)
            {
                return animal.Legs(extra);
            }
        }

        public static class Loner
        {
            public static int Unrelated(int value)
            {
                return value + 5;
            }
        }
        """;

    /// <summary>
    /// <c>Reader.Read</c> only ever reads <c>Counter.Value</c>, never assigns it, yet both accessors of
    /// the property are separate declarations of their own and share the reachability of the property
    /// they belong to.
    /// </summary>
    private const string PropertyAccessorSource = """
        namespace Fixture;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public sealed class Counter
        {
            private int _value;

            public int Value
            {
                get { return _value + 1; }
                set { _value = value + 2; }
            }
        }

        public static class Reader
        {
            public static int Read(Counter counter)
            {
                return counter.Value;
            }
        }

        public static class Loner
        {
            public static int Unrelated(int value)
            {
                return value + 5;
            }
        }
        """;

    /// <summary>
    /// <c>Thrower.AlwaysFails</c> never returns, so no test could ever observe the difference between
    /// the mutated argument and the original one.
    /// </summary>
    private const string ThrowOnlySource = """
        namespace Fixture;

        using System;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Thrower
        {
            public static int AlwaysFails(int value)
            {
                throw new InvalidOperationException((value + 1).ToString());
            }
        }
        """;

    /// <summary>
    /// The unconditional <c>return value;</c> always leaves the method, so the compiler proves the
    /// statement after it - the one carrying the mutation point - can never execute, and reports
    /// <c>CS0162</c> for it.
    /// </summary>
    private const string UnreachableStatementSource = """
        namespace Fixture;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class DeadCode
        {
            public static int Dead(int value)
            {
                return value;

                return value + 1;
            }
        }
        """;

    /// <summary>
    /// <c>Discarder.Discard</c> assigns the mutated value to <c>_</c>, so no test could ever observe it
    /// regardless of what the value becomes.
    /// </summary>
    private const string DiscardAssignmentSource = """
        namespace Fixture;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Discarder
        {
            public static void Discard(int value)
            {
                _ = value + 1;
            }
        }
        """;

    /// <summary>
    /// Four members, two different reasons a mutant of them never turns into a gap. <c>Widget.ToString</c>
    /// and <c>Deprecated.Old</c> still get mutated - the mutant generator has no rule excluding a well
    /// known infrastructure member or an obsolete one - so it is the equivalence classifier that has to
    /// recognise both and report every mutant as trivial. <c>Excluded.Ignored</c> and <c>Generated.Machine</c>
    /// are excluded one step earlier: the mutant generator itself never descends into a declaration
    /// carrying <c>[ExcludeFromCodeCoverage]</c> or <c>[GeneratedCode]</c>, so neither line produces a
    /// mutation point - and therefore no diagnostic at all - in the first place.
    /// </summary>
    private const string ExcludedMemberSource = """
        namespace Fixture;

        using System;
        using System.CodeDom.Compiler;
        using System.Diagnostics.CodeAnalysis;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public sealed class Widget
        {
            private readonly int _value;

            public Widget(int value)
            {
                _value = value;
            }

            public override string ToString()
            {
                return (_value + 1).ToString();
            }
        }

        public static class Excluded
        {
            [ExcludeFromCodeCoverage]
            public static int Ignored(int value)
            {
                return value + 1;
            }
        }

        public static class Generated
        {
            [GeneratedCode("tool", "1.0")]
            public static int Machine(int value)
            {
                return value + 1;
            }
        }

        public static class Deprecated
        {
            [Obsolete]
            public static int Old(int value)
            {
                return value + 1;
            }
        }
        """;

    /// <summary>
    /// <c>Ops.Mark&lt;T&gt;</c> ignores its type parameter entirely, yet <c>IntWrapper.UseInt</c> only ever
    /// calls the <c>&lt;int&gt;</c> instantiation while <c>StringWrapper.UseString</c> calls the
    /// <c>&lt;string&gt;</c> one. Both constructed methods normalize to the very same original definition,
    /// so a test naming only the first instantiation still covers the shared body.
    /// </summary>
    private const string GenericMethodSource = """
        namespace Fixture;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Ops
        {
            public static int Mark<T>(int marker)
            {
                return marker + 1;
            }
        }

        public static class IntWrapper
        {
            public static int UseInt(int value)
            {
                return Ops.Mark<int>(value);
            }
        }

        public static class StringWrapper
        {
            public static string UseString(int value)
            {
                return Ops.Mark<string>(value).ToString();
            }
        }

        public static class Loner
        {
            public static int Unrelated(int value)
            {
                return value + 5;
            }
        }
        """;

    /// <summary>
    /// <c>Subscriber.Subscribe</c> only ever adds a handler to <c>Bell.Rung</c>, never removes one, yet the
    /// <c>remove</c> accessor is a separate declaration that shares the reachability of the event it
    /// belongs to, exactly like a property's setter shares the reachability of a getter-only caller.
    /// </summary>
    private const string EventAccessorSource = """
        namespace Fixture;

        using System;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public sealed class Bell
        {
            private int _count;
            private EventHandler? _rung;

            public event EventHandler Rung
            {
                add
                {
                    _count = _count + 1;
                    _rung += value;
                }
                remove
                {
                    _count = _count - 1;
                    _rung -= value;
                }
            }
        }

        public static class Subscriber
        {
            public static void Subscribe(Bell bell, EventHandler handler)
            {
                bell.Rung += handler;
            }
        }

        public static class Loner
        {
            public static int Unrelated(int value)
            {
                return value + 5;
            }
        }
        """;

    /// <summary>
    /// <c>4 - Two</c> carries five mutation points: four arithmetic swaps of <c>-</c> and two increments
    /// and decrements of the literal <c>4</c>, which the numeric literal operator still offers because a
    /// normal method body is not a constant-only context. Exactly one of the arithmetic swaps, the
    /// division, folds to the very same value the original subtraction does, which is what the equivalence
    /// classifier's constant folding check has to recognise among the other four candidates that do not.
    /// </summary>
    private const string ConstantFoldingSource = """
        namespace Fixture;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Constants
        {
            private const int Two = 2;

            public static int Ratio()
            {
                return 4 - Two;
            }
        }
        """;

    /// <summary>
    /// <c>1 + 2</c> sits inside a <see langword="const" /> field initializer, a position the numeric
    /// literal operator already refuses to touch, but the arithmetic operator carries no such check of its
    /// own. The equivalence classifier is what has to recognise every one of its four mutants as trivial,
    /// regardless of the value each one folds to.
    /// </summary>
    private const string ConstantDeclarationSource = """
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
            public const int Sum = 1 + 2;
        }
        """;

    /// <summary>
    /// <c>1 + 2</c> sits inside a default parameter value, the same shape as
    /// <see cref="ConstantDeclarationSource" /> one syntax position over.
    /// </summary>
    private const string DefaultParameterSource = """
        namespace Fixture;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Options
        {
            public static int WithDefault(int amount = 1 + 2)
            {
                return amount;
            }
        }
        """;

    /// <summary>
    /// <c>1 + 2</c> sits inside a classic <c>case</c> label, the third and last constant-only position
    /// exercised in this file.
    /// </summary>
    private const string CaseLabelSource = """
        namespace Fixture;

        public static class Reached
        {
            public static int Identity(int value)
            {
                return value;
            }
        }

        public static class Classifier
        {
            public static int Describe(int value)
            {
                switch (value)
                {
                    case 1 + 2:
                        return value;
                    default:
                        return value;
                }
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
            InterfaceDispatchSource,
            VirtualOverrideSource,
            PropertyAccessorSource,
            ThrowOnlySource,
            UnreachableStatementSource,
            DiscardAssignmentSource,
            ExcludedMemberSource,
            GenericMethodSource,
            EventAccessorSource,
            ConstantFoldingSource,
            ConstantDeclarationSource,
            DefaultParameterSource,
            CaseLabelSource,
        }.Select(source => (Func<string>)(() => source));

    /// <summary>
    /// A test calling through an <c>IShape</c> variable reaches every implementation declared in the
    /// compilation, not only the one a concrete call site happens to construct: the dispatch
    /// approximation adds every implementation of the interface member it resolved to.
    /// </summary>
    [Test]
    public async Task Analyze_TestCallsThroughInterfaceVariable_ReachesEveryImplementation()
    {
        var compilation = CompilationFactory.Create(InterfaceDispatchSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(RendererDescribeMemberId)]).ConfigureAwait(false);
        var lines = GapLines(diagnostics);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(lines).IsEquivalentTo([InterfaceLonerLine]);
            _ = await Assert.That(lines.Contains(SquareAreaLine)).IsFalse();
            _ = await Assert.That(lines.Contains(CircleAreaLine)).IsFalse();
            _ = await Assert.That(Trivial(diagnostics)).IsEqualTo(NoGaps);
        }
    }

    /// <summary>
    /// The dispatch approximation also runs when the manifest names the interface member itself, without
    /// any caller in the manifest at all: a test that calls an interface member directly is the most
    /// common shape there is, and the seed has to be treated exactly like a member reached transitively.
    /// </summary>
    [Test]
    public async Task Analyze_ManifestNamesTheInterfaceMemberDirectly_StillReachesEveryImplementation()
    {
        var compilation = CompilationFactory.Create(InterfaceDispatchSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(ShapeAreaMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(GapLines(diagnostics)).IsEquivalentTo([InterfaceLonerLine]);
        }
    }

    /// <summary>
    /// A call through a base class reference reaches every override in the chain, not only the one
    /// closest to the declared type: <c>Puppy.Legs</c> overrides <c>Dog.Legs</c>, which overrides the
    /// virtual <c>Animal.Legs</c>, so walking the override chain twice is what makes it reachable at all.
    /// </summary>
    [Test]
    public async Task Analyze_TestCallsThroughBaseClassVariable_ReachesEveryOverrideInTheChain()
    {
        var compilation = CompilationFactory.Create(VirtualOverrideSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(ZooCountLegsMemberId)]).ConfigureAwait(false);
        var lines = GapLines(diagnostics);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(lines).IsEquivalentTo([OverrideLonerLine]);
            _ = await Assert.That(lines.Contains(AnimalLegsLine)).IsFalse();
            _ = await Assert.That(lines.Contains(DogLegsLine)).IsFalse();
            _ = await Assert.That(lines.Contains(PuppyLegsLine)).IsFalse();
            _ = await Assert.That(Trivial(diagnostics)).IsEqualTo(NoGaps);
        }
    }

    /// <summary>
    /// A test reading a property never assigns it, yet the setter becomes reachable as well: the getter
    /// and the setter are separate declarations, and both share the reachability of the property they
    /// belong to once the property itself is reached.
    /// </summary>
    [Test]
    public async Task Analyze_TestReadsAProperty_MakesTheSetterReachableAsWell()
    {
        var compilation = CompilationFactory.Create(PropertyAccessorSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(ReaderReadMemberId)]).ConfigureAwait(false);
        var lines = GapLines(diagnostics);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(lines).IsEquivalentTo([PropertyLonerLine]);
            _ = await Assert.That(lines.Contains(PropertyGetLine)).IsFalse();
            _ = await Assert.That(lines.Contains(PropertySetLine)).IsFalse();
        }
    }

    /// <summary>
    /// A member whose body does nothing but throw can never let a test observe a mutated value, so every
    /// mutant of it is reported as trivial instead of as a gap - even though nothing in the manifest
    /// reaches the member at all.
    /// </summary>
    [Test]
    public async Task Analyze_MemberBodyThrowsUnconditionally_ReportsEveryMutantAsTrivial()
    {
        var compilation = CompilationFactory.Create(ThrowOnlySource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(Gaps(diagnostics)).IsEqualTo(NoGaps);
            _ = await Assert
                .That(Trivial(diagnostics))
                .IsEqualTo(ExpectTrivial((ThrowLine, ThrowOnlyBodyReason, _additionMutants)));
        }
    }

    /// <summary>
    /// A statement the compiler already proves unreachable, here because the statement ahead of it
    /// always returns, can never run at all, so every mutant inside it is trivial rather than a gap.
    /// </summary>
    [Test]
    public async Task Analyze_StatementAfterAnUnconditionalReturn_ReportsEveryMutantAsTrivial()
    {
        var compilation = CompilationFactory.Create(UnreachableStatementSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(Gaps(diagnostics)).IsEqualTo(NoGaps);
            _ = await Assert
                .That(Trivial(diagnostics))
                .IsEqualTo(ExpectTrivial((UnreachableStatementLine, UnreachableStatementReason, _additionMutants)));
        }
    }

    /// <summary>
    /// A value assigned to the discard <c>_</c> is thrown away in the very same statement, so no test
    /// could ever tell the mutant from the original code.
    /// </summary>
    [Test]
    public async Task Analyze_MutatedValueAssignedToADiscard_ReportsEveryMutantAsTrivial()
    {
        var compilation = CompilationFactory.Create(DiscardAssignmentSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(Gaps(diagnostics)).IsEqualTo(NoGaps);
            _ = await Assert
                .That(Trivial(diagnostics))
                .IsEqualTo(ExpectTrivial((DiscardLine, DiscardAssignmentReason, _additionMutants)));
        }
    }

    /// <summary>
    /// A well known infrastructure member and an obsolete one are still mutated, and the equivalence
    /// classifier is what turns every one of their mutants into a trivial diagnostic instead of a gap. A
    /// member excluded by <c>[ExcludeFromCodeCoverage]</c> or by <c>[GeneratedCode]</c> never reaches that
    /// point: the mutant generator excludes the declaration outright, so neither line produces any
    /// diagnostic, trivial or otherwise.
    /// </summary>
    [Test]
    public async Task Analyze_WellKnownAndObsoleteMembers_ReportEveryMutantAsTrivial()
    {
        var compilation = CompilationFactory.Create(ExcludedMemberSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);
        var lines = AllLines(diagnostics);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(Gaps(diagnostics)).IsEqualTo(NoGaps);
            _ = await Assert
                .That(Trivial(diagnostics))
                .IsEqualTo(
                    ExpectTrivial(
                        (ToStringLine, WellKnownMemberReason, _additionMutants),
                        (ObsoleteLine, ObsoleteMemberReason, _additionMutants)
                    )
                );
            _ = await Assert.That(lines.Contains(ExcludeFromCoverageLine)).IsFalse();
            _ = await Assert.That(lines.Contains(GeneratedCodeLine)).IsFalse();
        }
    }

    /// <summary>
    /// <c>Ops.Mark&lt;T&gt;</c> ignores <c>T</c> entirely, and a test naming only the caller of its
    /// <c>&lt;int&gt;</c> instantiation still covers the member's own mutation point: the reachable set
    /// normalizes a constructed generic method back to its original definition, so it does not matter which
    /// instantiation a test happens to go through.
    /// </summary>
    [Test]
    public async Task Analyze_TestCallsOneInstantiationOfAGenericMethod_ReachesTheSharedDefinition()
    {
        var compilation = CompilationFactory.Create(GenericMethodSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(IntWrapperUseIntMemberId)]).ConfigureAwait(false);
        var lines = GapLines(diagnostics);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(lines).IsEquivalentTo([GenericLonerLine]);
            _ = await Assert.That(lines.Contains(GenericMethodLine)).IsFalse();
            _ = await Assert.That(Trivial(diagnostics)).IsEqualTo(NoGaps);
        }
    }

    /// <summary>
    /// A test that only ever adds a handler to an event never removes one, yet the <c>remove</c> accessor
    /// becomes reachable as well: like a property's getter and setter, both accessors are separate
    /// declarations that share the reachability of the event they belong to.
    /// </summary>
    [Test]
    public async Task Analyze_TestAddsAnEventHandler_MakesTheRemoveAccessorReachableAsWell()
    {
        var compilation = CompilationFactory.Create(EventAccessorSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(SubscriberSubscribeMemberId)])
            .ConfigureAwait(false);
        var lines = GapLines(diagnostics);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(lines).IsEquivalentTo([EventLonerLine]);
            _ = await Assert.That(lines.Contains(EventAddLine)).IsFalse();
            _ = await Assert.That(lines.Contains(EventRemoveLine)).IsFalse();
            _ = await Assert.That(Trivial(diagnostics)).IsEqualTo(NoGaps);
        }
    }

    /// <summary>
    /// Of the four arithmetic mutants of an untested subtraction, one - the division - folds to the very
    /// same value the original does, and the equivalence classifier has to single it out as trivial while
    /// leaving the other three, and the two literal increments and decrements of the numeric literal
    /// operator, as genuine gaps.
    /// </summary>
    [Test]
    public async Task Analyze_ArithmeticMutantFoldsToTheSameConstant_IsTrivialConstantFolding()
    {
        var compilation = CompilationFactory.Create(ConstantFoldingSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert
                .That(Gaps(diagnostics))
                .IsEqualTo(ExpectGaps(ConstantFoldingLine, "- => +", "- => *", "- => %", "4 => 5", "4 => 3"));
            _ = await Assert
                .That(Trivial(diagnostics))
                .IsEqualTo(ExpectTrivial((ConstantFoldingLine, ConstantFoldingReason, ["- => /"])));
        }
    }

    /// <summary>
    /// The arithmetic operator carries no check of its own for a <see langword="const" /> field
    /// initializer, unlike the numeric literal operator, so all four of its mutants are created; the
    /// equivalence classifier is what has to prove every one of them trivial regardless of the value it
    /// folds to.
    /// </summary>
    [Test]
    public async Task Analyze_ArithmeticMutationInsideAConstFieldInitializer_ReportsEveryMutantAsTrivial()
    {
        var compilation = CompilationFactory.Create(ConstantDeclarationSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(Gaps(diagnostics)).IsEqualTo(NoGaps);
            _ = await Assert
                .That(Trivial(diagnostics))
                .IsEqualTo(
                    ExpectTrivial(
                        (ConstantDeclarationLine, ConstantDeclarationReason, ["+ => -", "+ => *", "+ => /", "+ => %"])
                    )
                );
        }
    }

    /// <summary>
    /// The same shape as a <see langword="const" /> field initializer, one syntax position over: a default
    /// parameter value can only ever be a compile-time constant, so no test could ever observe a mutant of
    /// it either.
    /// </summary>
    [Test]
    public async Task Analyze_ArithmeticMutationInsideADefaultParameterValue_ReportsEveryMutantAsTrivial()
    {
        var compilation = CompilationFactory.Create(DefaultParameterSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(Gaps(diagnostics)).IsEqualTo(NoGaps);
            _ = await Assert
                .That(Trivial(diagnostics))
                .IsEqualTo(
                    ExpectTrivial(
                        (DefaultParameterLine, DefaultParameterReason, ["+ => -", "+ => *", "+ => /", "+ => %"])
                    )
                );
        }
    }

    /// <summary>
    /// The third and last constant-only position exercised in this file: a classic <c>case</c> label can
    /// only ever be a compile-time constant as well.
    /// </summary>
    [Test]
    public async Task Analyze_ArithmeticMutationInsideACaseLabel_ReportsEveryMutantAsTrivial()
    {
        var compilation = CompilationFactory.Create(CaseLabelSource, ProductionAssemblyName);

        var diagnostics = await RunAsync(compilation, [CreateManifest(AnchorMemberId)]).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Errors(compilation)).IsEmpty();
            _ = await Assert.That(Gaps(diagnostics)).IsEqualTo(NoGaps);
            _ = await Assert
                .That(Trivial(diagnostics))
                .IsEqualTo(ExpectTrivial((CaseLabelLine, CaseLabelReason, ["+ => -", "+ => *", "+ => /", "+ => %"])));
        }
    }

    /// <summary>
    /// Every fixture of this class compiles and is analysed without the analyzer throwing. Roslyn turns
    /// an analyzer exception into <c>AD0001</c> and carries on, so a crash would otherwise look like a
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
        }
    }

    private static Task<ImmutableArray<Diagnostic>> RunAsync(
        Compilation compilation,
        IEnumerable<AdditionalText>? additionalFiles = null,
        IReadOnlyDictionary<string, string>? globalOptions = null
    ) => AnalyzerRunner.RunAsync(new MutationCoverageAnalyzer(), compilation, additionalFiles, globalOptions);

    /// <summary>
    /// Builds a manifest recording <paramref name="referencedMemberIds" /> as the production members the
    /// tests of the first pass touched, and as behaviorally verified as well, so that <c>FSH0007</c> never
    /// shows up in a diagnostic set that is meant to be a statement about reachability or triviality alone.
    /// </summary>
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
    /// Reduces the reported gaps to their distinct 1-based lines, so that a reachability assertion states
    /// which lines are gaps without depending on how many mutants a single arithmetic expression carries.
    /// </summary>
    /// <param name="diagnostics">All diagnostics of a run.</param>
    /// <returns>The distinct lines carrying an <c>FSH0001</c> diagnostic, possibly empty.</returns>
    private static ImmutableArray<int> GapLines(ImmutableArray<Diagnostic> diagnostics) =>
        [
            .. DiagnosticAssertions
                .Summarise(AnalyzerRunner.OfId(diagnostics, DiagnosticIds.UnreachableMutationPoint))
                .Select(summary => summary.Line)
                .Distinct()
                .OrderBy(line => line),
        ];

    /// <summary>
    /// Collects the distinct 1-based lines of every diagnostic of any kind, used to prove that a line
    /// produces no diagnostic at all rather than merely no gap.
    /// </summary>
    /// <param name="diagnostics">All diagnostics of a run.</param>
    /// <returns>The distinct lines carrying any diagnostic, possibly empty.</returns>
    private static ImmutableArray<int> AllLines(ImmutableArray<Diagnostic> diagnostics) =>
        [
            .. DiagnosticAssertions
                .Summarise(diagnostics)
                .Select(summary => summary.Line)
                .Distinct()
                .OrderBy(line => line),
        ];

    /// <summary>
    /// Describes every reported gap as one text block, one line per diagnostic, ordered ordinally so that
    /// the result does not depend on the order the concurrently running analyzer callbacks reported them in.
    /// </summary>
    /// <param name="diagnostics">All diagnostics of a run.</param>
    /// <returns>The described gaps, or <see cref="NoGaps" /> when there is none.</returns>
    private static string Gaps(ImmutableArray<Diagnostic> diagnostics)
    {
        var gaps = AnalyzerRunner.OfId(diagnostics, DiagnosticIds.UnreachableMutationPoint);

        return gaps.IsEmpty
            ? NoGaps
            : Join(
                DiagnosticAssertions.Summarise(gaps).Select(summary => Entry(summary.Id, summary.Line, summary.Message))
            );
    }

    /// <summary>
    /// Describes every reported trivial mutant as one text block, exactly like <see cref="Gaps" /> does
    /// for the coverage gaps.
    /// </summary>
    /// <param name="diagnostics">All diagnostics of a run.</param>
    /// <returns>The described trivial mutants, or <see cref="NoGaps" /> when there is none.</returns>
    private static string Trivial(ImmutableArray<Diagnostic> diagnostics)
    {
        var trivial = AnalyzerRunner.OfId(diagnostics, DiagnosticIds.TrivialMutant);

        return trivial.IsEmpty
            ? NoGaps
            : Join(
                DiagnosticAssertions
                    .Summarise(trivial)
                    .Select(summary => Entry(summary.Id, summary.Line, summary.Message))
            );
    }

    /// <summary>
    /// Builds the expectation of a set of trivial mutants, each group sharing one line and one reason and
    /// contributing one entry per display name in <see cref="_additionMutants" /> or an equivalent array.
    /// </summary>
    /// <param name="groups">The expected groups.</param>
    /// <returns>The expected text block.</returns>
    private static string ExpectTrivial(params (int Line, string Reason, string[] DisplayNames)[] groups) =>
        Join(
            groups.SelectMany(group =>
                group.DisplayNames.Select(displayName => TrivialEntry(group.Line, displayName, group.Reason))
            )
        );

    /// <summary>
    /// Builds the described trivial mutant, spelling out the message
    /// <see cref="Descriptors.TrivialMutant" /> formats.
    /// </summary>
    /// <param name="line">The 1-based line the mutant is reported on.</param>
    /// <param name="displayName">The display name of the mutation.</param>
    /// <param name="reason">The reason clause the classifier attached.</param>
    /// <returns>The described trivial mutant.</returns>
    private static string TrivialEntry(int line, string displayName, string reason) =>
        Entry(
            DiagnosticIds.TrivialMutant,
            line,
            "Mutation '" + displayName + "' cannot change observable behaviour (" + reason + ")"
        );

    /// <summary>
    /// Builds the expectation of a set of gaps that all sit on <paramref name="line" />, spelling out the
    /// message <see cref="Descriptors.UnreachableMutationPoint" /> formats.
    /// </summary>
    /// <param name="line">The 1-based line every gap is reported on.</param>
    /// <param name="displayNames">The display names of the expected mutations.</param>
    /// <returns>The expected text block.</returns>
    private static string ExpectGaps(int line, params string[] displayNames) =>
        Join(displayNames.Select(displayName => GapEntry(line, displayName)));

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
