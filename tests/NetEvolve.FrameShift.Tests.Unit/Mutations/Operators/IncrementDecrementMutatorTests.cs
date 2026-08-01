namespace NetEvolve.FrameShift.Tests.Unit.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Mutations;
using NetEvolve.FrameShift.Mutations.Operators;
using NetEvolve.FrameShift.Tests.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the increment and decrement swap, which keeps the fixity of the original expression, and
/// the guard for user defined operators that only provide one direction.
/// </summary>
public class IncrementDecrementMutatorTests
{
    private const string ExpressionPlaceholder = "EXPRESSION";

    private const string KeywordPlaceholder = "KEYWORD";

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

    private const string BorrowedOperatorNameSource = """
        namespace Fixtures;

        internal readonly struct Ticks
        {
            internal Ticks(int count) => Count = count;

            internal int Count { get; }

            public static Ticks operator ++(Ticks value) => new Ticks(value.Count + 1);

            internal static Ticks op_Decrement(Ticks value) => value;
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

    private const string MemberTemplate = """
        namespace Fixtures;

        internal sealed class Counter
        {
            private readonly int[] _values = new int[4];

            internal int Field = 0;

            internal int Property { get; set; }

            internal int this[int index]
            {
                get => _values[index];
                set => _values[index] = value;
            }

            internal int Advance(int index)
            {
                _ = /*!*/EXPRESSION;
                return Field + Property + this[index];
            }
        }
        """;

    private const string CheckedTemplate = """
        namespace Fixtures;

        internal static class Counter
        {
            internal static int Advance(int value)
            {
                _ = KEYWORD(/*!*/value++);
                return value;
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

        using (Assert.Multiple())
        {
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

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
                .IsEquivalentTo(new[] { expectedId });
            _ = await Assert
                .That(Sorted(result.Mutations.Select(mutation => mutation.DisplayName)))
                .IsEquivalentTo(new[] { expectedDisplayName });
            _ = await Assert.That(result.Mutations[0].Replacement.ToString()).IsEqualTo(expectedReplacement);
            _ = await Assert.That(result.Mutations[0].Original).IsEqualTo(result.Node);
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.Node.Kind()).IsEqualTo(kind);
            _ = await Assert.That(mutator.SupportedSyntaxKinds).Contains(kind);
            _ = await Assert.That(result.Mutations).Count().IsEqualTo(1);
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(mutated)
                .IsEqualTo(TriviaSource.Replace("++ /* after */", "-- /* after */", StringComparison.Ordinal));
            _ = await Assert.That(mutated).Contains("// Advances a value.");
            _ = await Assert.That(mutated).Contains("/* leading */");
            _ = await Assert.That(mutated).Contains("value /* inner */ -- /* after */; // tail");
        }
    }

    [Test]
    public async Task CreateMutations_UserDefinedIncrementWithoutDecrement_ReturnsEmpty()
    {
        var result = Mutate(IncrementOnlyOperatorSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.Node.Kind()).IsEqualTo(SyntaxKind.PostIncrementExpression);
            _ = await Assert.That(result.Mutations).IsEmpty();
        }
    }

    [Test]
    public async Task CreateMutations_UserDefinedDecrementWithoutIncrement_ReturnsEmpty()
    {
        var result = Mutate(DecrementOnlyOperatorSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.Node.Kind()).IsEqualTo(SyntaxKind.PostDecrementExpression);
            _ = await Assert.That(result.Mutations).IsEmpty();
        }
    }

    [Test]
    public async Task CreateMutations_UserDefinedIncrementAndDecrement_ProducesTheSwap()
    {
        string[] expectedIds = ["increment-decrement.postfix-increment-to-decrement"];
        var result = Mutate(BothOperatorsSource);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
                .IsEquivalentTo(expectedIds);
            _ = await Assert.That(result.Mutations[0].Replacement.ToString()).IsEqualTo("current--");
        }
    }

    [Test]
    public async Task CreateMutations_UserDefinedOperatorsOnAGenericType_ProducesTheSwap()
    {
        string[] expectedIds = ["increment-decrement.prefix-increment-to-decrement"];
        var result = Mutate(GenericBothOperatorsSource);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
                .IsEquivalentTo(expectedIds);
            _ = await Assert.That(result.Mutations[0].Replacement.ToString()).IsEqualTo("--current");
        }
    }

    [Test]
    public async Task CreateMutations_UserDefinedIncrementOnAGenericTypeWithoutDecrement_ReturnsEmpty()
    {
        var result = Mutate(GenericIncrementOnlySource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.Node.Kind()).IsEqualTo(SyntaxKind.PreIncrementExpression);
            _ = await Assert.That(result.Mutations).IsEmpty();
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.Node.Kind()).IsEqualTo(SyntaxKind.PostIncrementExpression);
            _ = await Assert.That(result.Mutations).IsEmpty();
        }
    }

    /// <summary>
    /// The operand of an increment is not restricted to a local: a field, an auto property and an indexer
    /// element are mutated exactly the same way, in both fixities.
    /// </summary>
    [Test]
    [Arguments("Field++", "increment-decrement.postfix-increment-to-decrement", "Field--")]
    [Arguments("++Field", "increment-decrement.prefix-increment-to-decrement", "--Field")]
    [Arguments("Property++", "increment-decrement.postfix-increment-to-decrement", "Property--")]
    [Arguments("++Property", "increment-decrement.prefix-increment-to-decrement", "--Property")]
    [Arguments("Property--", "increment-decrement.postfix-decrement-to-increment", "Property++")]
    [Arguments("this[index]++", "increment-decrement.postfix-increment-to-decrement", "this[index]--")]
    [Arguments("++this[index]", "increment-decrement.prefix-increment-to-decrement", "--this[index]")]
    public async Task CreateMutations_FieldPropertyOrIndexerOperand_SwapsTheOperator(
        string expression,
        string expectedId,
        string expectedReplacement
    )
    {
        var result = Mutate(CreateSource(MemberTemplate, expression, ExpressionPlaceholder));

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
                .IsEquivalentTo(new[] { expectedId });
            _ = await Assert.That(result.Mutations[0].Replacement.ToString()).IsEqualTo(expectedReplacement);
            _ = await Assert.That(result.Mutations[0].Original).IsEqualTo(result.Node);
        }
    }

    /// <summary>
    /// A <c>checked</c> or <c>unchecked</c> context changes what the mutant does at run time, but not
    /// whether it is created: the swap is offered in both contexts.
    /// </summary>
    [Test]
    [Arguments("checked")]
    [Arguments("unchecked")]
    public async Task CreateMutations_CheckedOrUncheckedContext_SwapsTheOperator(string keyword)
    {
        string[] expectedIds = ["increment-decrement.postfix-increment-to-decrement"];
        var result = Mutate(CreateSource(CheckedTemplate, keyword, KeywordPlaceholder));

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.Node.Kind()).IsEqualTo(SyntaxKind.PostIncrementExpression);
            _ = await Assert
                .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
                .IsEquivalentTo(expectedIds);
            _ = await Assert.That(result.Mutations[0].Replacement.ToString()).IsEqualTo("value--");
        }
    }

    /// <summary>
    /// A member that only borrows the metadata name of the decrement operator is no counterpart: it is an
    /// ordinary method, not a user defined operator, so the swap is suppressed.
    /// </summary>
    [Test]
    public async Task CreateMutations_MemberWithTheCounterpartNameThatIsNoOperator_ReturnsEmpty()
    {
        var result = Mutate(BorrowedOperatorNameSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.Node.Kind()).IsEqualTo(SyntaxKind.PostIncrementExpression);
            _ = await Assert.That(result.Mutations).IsEmpty();
        }
    }

    /// <summary>
    /// Pins the counterpart lookup directly, ahead of it moving into a shared helper: a member that only
    /// borrows the metadata name of the decrement operator is no counterpart, because it is an ordinary
    /// method rather than a user defined operator.
    /// </summary>
    [Test]
    public async Task HasCounterpart_MemberIsNoOperator_ReturnsFalse()
    {
        var (compilation, _, _) = CompilationFactory.CreateWithModel(BorrowedOperatorNameSource);
        var ticks =
            compilation.GetTypeByMetadataName("Fixtures.Ticks")
            ?? throw new InvalidOperationException("The fixture does not declare 'Fixtures.Ticks'.");
        var increment = ticks.GetMembers("op_Increment").OfType<IMethodSymbol>().Single();

        using (Assert.Multiple())
        {
            _ = await Assert.That(CompilationFactory.GetCompileErrors(compilation)).IsEmpty();
            _ = await Assert.That(InvokeHasCounterpart(increment, "op_Decrement")).IsFalse();
        }
    }

    [Test]
    public async Task CreateMutations_CancelledToken_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(Fixture("value++"));
        var node = SyntaxNodeLocator.FindMarked<ExpressionSyntax>(tree);
        var mutator = new IncrementDecrementMutator();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = mutator.CreateMutations(node, semanticModel, cancellation.Token).ToList()
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    private static string CreateSource(string template, string value, string placeholder) =>
        template.Replace(placeholder, value, StringComparison.Ordinal);

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

    /// <summary>
    /// Reaches the shared counterpart lookup directly.
    /// </summary>
    /// <param name="userDefinedOperator">The operator to find a counterpart for.</param>
    /// <param name="metadataName">The metadata name of the wanted counterpart.</param>
    /// <returns>Whether the declaring type provides such a counterpart.</returns>
    private static bool InvokeHasCounterpart(IMethodSymbol userDefinedOperator, string metadataName) =>
        OperatorCounterpart.HasCounterpart(userDefinedOperator, metadataName);
}
