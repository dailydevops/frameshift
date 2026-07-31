namespace NetEvolve.Frameshift.Tests.Unit.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.Frameshift.Mutations;
using NetEvolve.Frameshift.Mutations.Operators;
using NetEvolve.Frameshift.Tests.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the increment and decrement swap, which keeps the fixity of the original expression, and
/// the guard for user defined operators that only provide one direction.
/// </summary>
public class IncrementDecrementMutatorTests
{
    private const string IncrementOnlyOperatorSource = """
        namespace Fixtures;

        internal readonly struct Ticks
        {
            internal Ticks(int count) => Count = count;

            internal int Count { get; }

            public static Ticks operator ++(Ticks value) => new Ticks(value.Count + 1);
        }

        internal static class Clock
        {
            internal static Ticks Advance(Ticks value)
            {
                var current = value;
                _ = /*!*/current++;
                return current;
            }
        }
        """;

    private const string DecrementOnlyOperatorSource = """
        namespace Fixtures;

        internal readonly struct Ticks
        {
            internal Ticks(int count) => Count = count;

            internal int Count { get; }

            public static Ticks operator --(Ticks value) => new Ticks(value.Count - 1);
        }

        internal static class Clock
        {
            internal static Ticks Rewind(Ticks value)
            {
                var current = value;
                _ = /*!*/current--;
                return current;
            }
        }
        """;

    private const string BothOperatorsSource = """
        namespace Fixtures;

        internal readonly struct Ticks
        {
            internal Ticks(int count) => Count = count;

            internal int Count { get; }

            public static Ticks operator ++(Ticks value) => new Ticks(value.Count + 1);

            public static Ticks operator --(Ticks value) => new Ticks(value.Count - 1);
        }

        internal static class Clock
        {
            internal static Ticks Advance(Ticks value)
            {
                var current = value;
                _ = /*!*/current++;
                return current;
            }
        }
        """;

    private const string GenericBothOperatorsSource = """
        namespace Fixtures;

        internal readonly struct Box<TValue>
        {
            internal Box(TValue value) => Value = value;

            internal TValue Value { get; }

            public static Box<TValue> operator ++(Box<TValue> value) => value;

            public static Box<TValue> operator --(Box<TValue> value) => value;
        }

        internal static class Boxes
        {
            internal static Box<int> Advance(Box<int> value)
            {
                var current = value;
                _ = /*!*/++current;
                return current;
            }
        }
        """;

    private const string GenericIncrementOnlySource = """
        namespace Fixtures;

        internal readonly struct Box<TValue>
        {
            internal Box(TValue value) => Value = value;

            internal TValue Value { get; }

            public static Box<TValue> operator ++(Box<TValue> value) => value;
        }

        internal static class Boxes
        {
            internal static Box<int> Advance(Box<int> value)
            {
                var current = value;
                _ = /*!*/++current;
                return current;
            }
        }
        """;

    private const string NullableLiftedBothOperatorsSource = """
        namespace Fixtures;

        internal readonly struct Ticks
        {
            internal Ticks(int count) => Count = count;

            internal int Count { get; }

            public static Ticks operator ++(Ticks value) => new Ticks(value.Count + 1);

            public static Ticks operator --(Ticks value) => new Ticks(value.Count - 1);
        }

        internal static class Clock
        {
            internal static Ticks? Advance(Ticks? value)
            {
                var current = value;
                _ = /*!*/current++;
                return current;
            }
        }
        """;

    private const string NullableLiftedIncrementOnlySource = """
        namespace Fixtures;

        internal readonly struct Ticks
        {
            internal Ticks(int count) => Count = count;

            internal int Count { get; }

            public static Ticks operator ++(Ticks value) => new Ticks(value.Count + 1);
        }

        internal static class Clock
        {
            internal static Ticks? Advance(Ticks? value)
            {
                var current = value;
                _ = /*!*/current++;
                return current;
            }
        }
        """;

    private const string TriviaSource = """
        namespace Fixtures;

        internal static class Counter
        {
            // Advances a value.
            internal static int Advance(int value)
            {
                /* leading */
                _ = /*!*/value /* inner */ ++ /* after */; // tail
                return value;
            }
        }
        """;

    [Test]
    public async Task Metadata_Operator_ExposesIdKindAndSupportedKinds()
    {
        var mutator = new IncrementDecrementMutator();

        _ = await Assert.That(mutator.Id).IsEqualTo("increment-decrement");
        _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.Increment);
        _ = await Assert
            .That(mutator.SupportedSyntaxKinds)
            .IsEquivalentTo(
                new[]
                {
                    SyntaxKind.PreIncrementExpression,
                    SyntaxKind.PreDecrementExpression,
                    SyntaxKind.PostIncrementExpression,
                    SyntaxKind.PostDecrementExpression,
                }
            );
    }

    [Test]
    public async Task Fixture_IncrementExpression_Compiles()
    {
        var (compilation, _, _) = CompilationFactory.CreateWithModel(Fixture("value++"));

        _ = await Assert.That(CompilationFactory.GetCompileErrors(compilation)).IsEmpty();
    }

    [Test]
    [Arguments("value++", "increment-decrement.postfix-increment-to-decrement", "x++ => x--", "value--")]
    [Arguments("value--", "increment-decrement.postfix-decrement-to-increment", "x-- => x++", "value++")]
    [Arguments("++value", "increment-decrement.prefix-increment-to-decrement", "++x => --x", "--value")]
    [Arguments("--value", "increment-decrement.prefix-decrement-to-increment", "--x => ++x", "++value")]
    public async Task CreateMutations_IncrementOrDecrement_SwapsTheOperatorAndKeepsTheFixity(
        string expression,
        string expectedId,
        string expectedDisplayName,
        string expectedReplacement
    )
    {
        var result = Mutate(Fixture(expression));

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(new[] { expectedId });
        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.DisplayName)))
            .IsEquivalentTo(new[] { expectedDisplayName });
        _ = await Assert.That(result.Mutations[0].Replacement.ToString()).IsEqualTo(expectedReplacement);
        _ = await Assert.That(result.Mutations[0].Original).IsEqualTo(result.Node);
    }

    [Test]
    [Arguments("value++", "Increment")]
    [Arguments("++value", "Increment")]
    [Arguments("value--", "Decrement")]
    [Arguments("--value", "Decrement")]
    public async Task CreateMutations_MutationKind_FollowsTheOriginalOperator(string expression, string expectedKind)
    {
        var result = Mutate(Fixture(expression));

        _ = await Assert.That(result.Mutations[0].Kind.ToString()).IsEqualTo(expectedKind);
    }

    [Test]
    [Arguments("value++", SyntaxKind.PostIncrementExpression)]
    [Arguments("value--", SyntaxKind.PostDecrementExpression)]
    [Arguments("++value", SyntaxKind.PreIncrementExpression)]
    [Arguments("--value", SyntaxKind.PreDecrementExpression)]
    public async Task SupportedSyntaxKinds_EveryKind_IsHandledByCreateMutations(string expression, SyntaxKind kind)
    {
        var mutator = new IncrementDecrementMutator();
        var result = Mutate(Fixture(expression));

        _ = await Assert.That(result.Node.Kind()).IsEqualTo(kind);
        _ = await Assert.That(mutator.SupportedSyntaxKinds).Contains(kind);
        _ = await Assert.That(result.Mutations).Count().IsEqualTo(1);
    }

    [Test]
    [Arguments("-value")]
    [Arguments("+value")]
    [Arguments("~value")]
    [Arguments("value + 1")]
    public async Task CreateMutations_UnsupportedSyntaxKind_ReturnsEmpty(string expression)
    {
        var result = Mutate(Fixture(expression));

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task ApplyTo_PostfixIncrementToDecrement_RewritesOperatorAndKeepsTrivia()
    {
        var result = Mutate(TriviaSource);
        var mutation = result.Mutations[0];

        var mutated = mutation.ApplyTo(result.Tree).ToString();

        _ = await Assert
            .That(mutated)
            .IsEqualTo(TriviaSource.Replace("++ /* after */", "-- /* after */", StringComparison.Ordinal));
        _ = await Assert.That(mutated).Contains("// Advances a value.");
        _ = await Assert.That(mutated).Contains("/* leading */");
        _ = await Assert.That(mutated).Contains("value /* inner */ -- /* after */; // tail");
    }

    [Test]
    public async Task CreateMutations_UserDefinedIncrementWithoutDecrement_ReturnsEmpty()
    {
        var result = Mutate(IncrementOnlyOperatorSource);

        _ = await Assert.That(result.Node.Kind()).IsEqualTo(SyntaxKind.PostIncrementExpression);
        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_UserDefinedDecrementWithoutIncrement_ReturnsEmpty()
    {
        var result = Mutate(DecrementOnlyOperatorSource);

        _ = await Assert.That(result.Node.Kind()).IsEqualTo(SyntaxKind.PostDecrementExpression);
        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_UserDefinedIncrementAndDecrement_ProducesTheSwap()
    {
        string[] expectedIds = ["increment-decrement.postfix-increment-to-decrement"];
        var result = Mutate(BothOperatorsSource);

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
        _ = await Assert.That(result.Mutations[0].Replacement.ToString()).IsEqualTo("current--");
    }

    [Test]
    public async Task CreateMutations_UserDefinedOperatorsOnAGenericType_ProducesTheSwap()
    {
        string[] expectedIds = ["increment-decrement.prefix-increment-to-decrement"];
        var result = Mutate(GenericBothOperatorsSource);

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
        _ = await Assert.That(result.Mutations[0].Replacement.ToString()).IsEqualTo("--current");
    }

    [Test]
    public async Task CreateMutations_UserDefinedIncrementOnAGenericTypeWithoutDecrement_ReturnsEmpty()
    {
        var result = Mutate(GenericIncrementOnlySource);

        _ = await Assert.That(result.Node.Kind()).IsEqualTo(SyntaxKind.PreIncrementExpression);
        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    /// <summary>
    /// A lifted increment on a nullable value type is bound to the operator declared on the underlying
    /// type, so the counterpart lookup has to succeed on that underlying type.
    /// </summary>
    [Test]
    public async Task CreateMutations_LiftedUserDefinedOperators_ProducesTheSwap()
    {
        string[] expectedIds = ["increment-decrement.postfix-increment-to-decrement"];
        var result = Mutate(NullableLiftedBothOperatorsSource);

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
    }

    [Test]
    public async Task CreateMutations_LiftedUserDefinedIncrementWithoutDecrement_ReturnsEmpty()
    {
        var result = Mutate(NullableLiftedIncrementOnlySource);

        _ = await Assert.That(result.Node.Kind()).IsEqualTo(SyntaxKind.PostIncrementExpression);
        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    private static string Fixture(string expression) =>
        $$"""
            namespace Fixtures;

            internal static class Counter
            {
                internal static int Advance(int value)
                {
                    _ = /*!*/{{expression}};
                    return value;
                }
            }
            """;

    private static ImmutableArray<string> Sorted(IEnumerable<string> values) =>
        [.. values.OrderBy(value => value, StringComparer.Ordinal)];

    private static (ImmutableArray<Mutation> Mutations, SyntaxTree Tree, ExpressionSyntax Node) Mutate(string source)
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var errors = CompilationFactory.GetCompileErrors(compilation);
        if (!errors.IsEmpty)
        {
            throw new InvalidOperationException(
                $"The fixture does not compile: {DiagnosticAssertions.Describe(errors)}"
            );
        }

        var node = SyntaxNodeLocator.FindMarked<ExpressionSyntax>(tree);
        var mutator = new IncrementDecrementMutator();

        return ([.. mutator.CreateMutations(node, semanticModel, CancellationToken.None)], tree, node);
    }
}
