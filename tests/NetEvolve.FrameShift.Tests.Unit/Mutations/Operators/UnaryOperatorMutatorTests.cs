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
/// Covers the unary sign mutations, the constant folding guard for literal operands and the user
/// defined operator handling, which can suppress the sign swap but never the operator removal.
/// </summary>
public class UnaryOperatorMutatorTests
{
    private const string NegateOnlyOperatorSource = """
        namespace Fixtures;

        internal readonly struct Money
        {
            internal Money(int amount) => Amount = amount;

            internal int Amount { get; }

            public static Money operator -(Money value) => new Money(0 - value.Amount);
        }

        internal static class Wallet
        {
            internal static Money Invert(Money value) => /*!*/-value;
        }
        """;

    private const string NegateAndPlusOperatorSource = """
        namespace Fixtures;

        internal readonly struct Money
        {
            internal Money(int amount) => Amount = amount;

            internal int Amount { get; }

            public static Money operator -(Money value) => new Money(0 - value.Amount);

            public static Money operator +(Money value) => value;
        }

        internal static class Wallet
        {
            internal static Money Invert(Money value) => /*!*/-value;
        }
        """;

    private const string PlusOnlyOperatorSource = """
        namespace Fixtures;

        internal readonly struct Money
        {
            internal Money(int amount) => Amount = amount;

            internal int Amount { get; }

            public static Money operator +(Money value) => value;
        }

        internal static class Wallet
        {
            internal static Money Identity(Money value) => /*!*/+value;
        }
        """;

    private const string GenericOperatorSource = """
        namespace Fixtures;

        internal readonly struct Box<TValue>
        {
            internal Box(TValue value) => Value = value;

            internal TValue Value { get; }

            public static Box<TValue> operator -(Box<TValue> value) => value;

            public static Box<TValue> operator +(Box<TValue> value) => value;
        }

        internal static class Boxes
        {
            internal static Box<int> Invert(Box<int> value) => /*!*/-value;
        }
        """;

    private const string NullableLiftedNegateOnlySource = """
        namespace Fixtures;

        internal readonly struct Money
        {
            internal Money(int amount) => Amount = amount;

            internal int Amount { get; }

            public static Money operator -(Money value) => new Money(0 - value.Amount);
        }

        internal static class Wallet
        {
            internal static Money? Invert(Money? value) => /*!*/-value;
        }
        """;

    private const string BorrowedOperatorNameSource = """
        namespace Fixtures;

        internal readonly struct Money
        {
            internal Money(int amount) => Amount = amount;

            internal int Amount { get; }

            public static Money operator -(Money value) => new Money(0 - value.Amount);

            internal static Money op_UnaryPlus(Money value) => value;
        }

        internal static class Wallet
        {
            internal static Money Invert(Money value) => /*!*/-value;
        }
        """;

    private const string NonConstantLiteralSource = """
        namespace Fixtures;

        internal static class Calculator
        {
            internal static object Invert() => /*!*/-"text";
        }
        """;

    private const string TriviaSource = """
        namespace Fixtures;

        internal static class Calculator
        {
            // Inverts a value.
            internal static int Invert(int value)
            {
                /* leading */
                return /*!*/- /* sign */ value; // tail
            }
        }
        """;

    [Test]
    public async Task Metadata_Operator_ExposesIdKindAndSupportedKinds()
    {
        var mutator = new UnaryOperatorMutator();

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutator.Id).IsEqualTo("unary");
            _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.UnaryOperator);
            _ = await Assert
                .That(mutator.SupportedSyntaxKinds)
                .IsEquivalentTo(new[] { SyntaxKind.UnaryMinusExpression, SyntaxKind.UnaryPlusExpression });
        }
    }

    [Test]
    public async Task Fixture_UnaryExpression_Compiles()
    {
        var (compilation, _, _) = CompilationFactory.CreateWithModel(Fixture("-value"));

        _ = await Assert.That(CompilationFactory.GetCompileErrors(compilation)).IsEmpty();
    }

    [Test]
    [Arguments("-value", "unary.negate-to-plus,unary.remove-negate", "-x => +x,-x => x")]
    [Arguments("+value", "unary.plus-to-negate,unary.remove-plus", "+x => -x,+x => x")]
    public async Task CreateMutations_UnaryExpression_ProducesSwapAndRemoval(
        string expression,
        string expectedIds,
        string expectedDisplayNames
    )
    {
        ArgumentNullException.ThrowIfNull(expectedIds);
        ArgumentNullException.ThrowIfNull(expectedDisplayNames);

        var result = Mutate(Fixture(expression));

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
                .IsEquivalentTo(Sorted(SplitValues(expectedIds)));
            _ = await Assert
                .That(Sorted(result.Mutations.Select(mutation => mutation.DisplayName)))
                .IsEquivalentTo(Sorted(SplitValues(expectedDisplayNames)));
            _ = await Assert
                .That(result.Mutations.Select(mutation => mutation.Kind).Distinct())
                .IsEquivalentTo(new[] { MutationKind.UnaryOperator });
        }
    }

    [Test]
    [Arguments("-value", SyntaxKind.UnaryMinusExpression)]
    [Arguments("+value", SyntaxKind.UnaryPlusExpression)]
    public async Task SupportedSyntaxKinds_EveryKind_IsHandledByCreateMutations(string expression, SyntaxKind kind)
    {
        var mutator = new UnaryOperatorMutator();
        var result = Mutate(Fixture(expression));

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.Node.Kind()).IsEqualTo(kind);
            _ = await Assert.That(mutator.SupportedSyntaxKinds).Contains(kind);
            _ = await Assert.That(result.Mutations).Count().IsEqualTo(2);
        }
    }

    [Test]
    [Arguments("!flag")]
    [Arguments("~value")]
    [Arguments("value++")]
    [Arguments("++value")]
    public async Task CreateMutations_UnsupportedSyntaxKind_ReturnsEmpty(string expression)
    {
        var result = Mutate(Fixture(expression));

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    [Arguments("+5")]
    [Arguments("+0")]
    [Arguments("-0")]
    public async Task CreateMutations_LiteralFoldingToTheSameConstant_ReturnsEmpty(string expression)
    {
        var result = Mutate(Fixture(expression));

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_NegatedLiteralWithDifferentConstant_ProducesBothMutations()
    {
        string[] expectedIds = ["unary.negate-to-plus", "unary.remove-negate"];
        var result = Mutate(Fixture("-5"));

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
    }

    [Test]
    public async Task CreateMutations_Replacements_SwapTheSignAndDropTheOperator()
    {
        var result = Mutate(Fixture("-value"));
        var swap = Single(result.Mutations, "unary.negate-to-plus");
        var removal = Single(result.Mutations, "unary.remove-negate");

        using (Assert.Multiple())
        {
            _ = await Assert.That(swap.Replacement.ToString()).IsEqualTo("+value");
            _ = await Assert.That(removal.Replacement.ToString()).IsEqualTo("value");
            _ = await Assert.That(removal.Original).IsEqualTo(result.Node);
        }
    }

    [Test]
    public async Task ApplyTo_NegateToPlus_RewritesOperatorAndKeepsTrivia()
    {
        var result = Mutate(TriviaSource);
        var mutation = Single(result.Mutations, "unary.negate-to-plus");

        var mutated = mutation.ApplyTo(result.Tree).ToString();

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(mutated)
                .IsEqualTo(TriviaSource.Replace("- /* sign */", "+ /* sign */", StringComparison.Ordinal));
            _ = await Assert.That(mutated).Contains("// Inverts a value.");
            _ = await Assert.That(mutated).Contains("/* leading */");
            _ = await Assert.That(mutated).Contains("return /*!*/+ /* sign */ value; // tail");
        }
    }

    [Test]
    public async Task ApplyTo_RemoveNegate_DropsOperatorAndKeepsSurroundingComments()
    {
        var result = Mutate(TriviaSource);
        var mutation = Single(result.Mutations, "unary.remove-negate");

        var mutated = mutation.ApplyTo(result.Tree).ToString();

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(mutated)
                .IsEqualTo(TriviaSource.Replace("- /* sign */ value", "value", StringComparison.Ordinal));
            _ = await Assert.That(mutated).Contains("// Inverts a value.");
            _ = await Assert.That(mutated).Contains("/* leading */");
            _ = await Assert.That(mutated).Contains("return /*!*/value; // tail");
        }
    }

    [Test]
    public async Task CreateMutations_UserDefinedNegationWithoutUnaryPlus_ProducesOnlyTheRemoval()
    {
        string[] expectedIds = ["unary.remove-negate"];
        string[] expectedDisplayNames = ["-x => x"];
        var result = Mutate(NegateOnlyOperatorSource);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
                .IsEquivalentTo(expectedIds);
            _ = await Assert
                .That(Sorted(result.Mutations.Select(mutation => mutation.DisplayName)))
                .IsEquivalentTo(expectedDisplayNames);
        }
    }

    [Test]
    public async Task CreateMutations_UserDefinedNegationWithUnaryPlus_ProducesBothMutations()
    {
        string[] expectedIds = ["unary.negate-to-plus", "unary.remove-negate"];
        var result = Mutate(NegateAndPlusOperatorSource);

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
    }

    [Test]
    public async Task CreateMutations_UserDefinedUnaryPlusWithoutNegation_ProducesOnlyTheRemoval()
    {
        string[] expectedIds = ["unary.remove-plus"];
        string[] expectedDisplayNames = ["+x => x"];
        var result = Mutate(PlusOnlyOperatorSource);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
                .IsEquivalentTo(expectedIds);
            _ = await Assert
                .That(Sorted(result.Mutations.Select(mutation => mutation.DisplayName)))
                .IsEquivalentTo(expectedDisplayNames);
        }
    }

    [Test]
    public async Task CreateMutations_UserDefinedOperatorsOnAGenericType_ProducesBothMutations()
    {
        string[] expectedIds = ["unary.negate-to-plus", "unary.remove-negate"];
        var result = Mutate(GenericOperatorSource);

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
    }

    /// <summary>
    /// The lifted negation of a nullable value type is bound to the operator declared on the underlying
    /// type, which declares no unary plus, so only the removal survives.
    /// </summary>
    [Test]
    public async Task CreateMutations_LiftedUserDefinedNegation_ProducesOnlyTheRemoval()
    {
        string[] expectedIds = ["unary.remove-negate"];
        var result = Mutate(NullableLiftedNegateOnlySource);
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(NullableLiftedNegateOnlySource);
        var unary = SyntaxNodeLocator.FindMarked<PrefixUnaryExpressionSyntax>(tree);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(semanticModel.GetTypeInfo(unary.Operand).Type?.ToDisplayString())
                .IsEqualTo("Fixtures.Money?");
            _ = await Assert
                .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
                .IsEquivalentTo(expectedIds);
        }
    }

    /// <summary>
    /// A member that only borrows the metadata name of the unary plus operator is no counterpart: it is an
    /// ordinary method, not a user defined operator, so the sign swap stays out of the mutation set.
    /// </summary>
    [Test]
    public async Task CreateMutations_MemberWithTheCounterpartNameThatIsNoOperator_ProducesOnlyTheRemoval()
    {
        string[] expectedIds = ["unary.remove-negate"];
        var result = Mutate(BorrowedOperatorNameSource);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
                .IsEquivalentTo(expectedIds);
            _ = await Assert.That(result.Mutations[0].DisplayName).IsEqualTo("-x => x");
        }
    }

    /// <summary>
    /// Pins the counterpart lookup directly, ahead of it moving into a shared helper: a member that only
    /// borrows the metadata name of the unary plus operator is no counterpart, because it is an ordinary
    /// method rather than a user defined operator.
    /// </summary>
    [Test]
    public async Task HasCounterpart_MemberIsNoOperator_ReturnsFalse()
    {
        var (compilation, _, _) = CompilationFactory.CreateWithModel(BorrowedOperatorNameSource);
        var money =
            compilation.GetTypeByMetadataName("Fixtures.Money")
            ?? throw new InvalidOperationException("The fixture does not declare 'Fixtures.Money'.");
        var negate = money.GetMembers("op_UnaryNegation").OfType<IMethodSymbol>().Single();

        using (Assert.Multiple())
        {
            _ = await Assert.That(CompilationFactory.GetCompileErrors(compilation)).IsEmpty();
            _ = await Assert.That(InvokeHasCounterpart(negate, "op_UnaryPlus")).IsFalse();
        }
    }

    /// <summary>
    /// The constant folding guard only skips a literal operand whose constant value survives the operator.
    /// A literal the operator cannot be applied to has no constant value at all, so both mutations are
    /// offered. C# rejects the fixture, which is the only way to negate a string literal.
    /// </summary>
    [Test]
    public async Task CreateMutations_LiteralOperandWithoutAConstantResult_ProducesBothMutations()
    {
        string[] expectedIds = ["unary.negate-to-plus", "unary.remove-negate"];
        var (mutations, _, node, model) = MutateAllowingErrors(NonConstantLiteralSource);
        var unary = (PrefixUnaryExpressionSyntax)node;

        using (Assert.Multiple())
        {
            _ = await Assert.That(model.GetConstantValue(unary).HasValue).IsFalse();
            _ = await Assert.That(model.GetConstantValue(unary.Operand).HasValue).IsTrue();
            _ = await Assert
                .That(Sorted(mutations.Select(mutation => mutation.OperatorId)))
                .IsEquivalentTo(expectedIds);
        }
    }

    [Test]
    public async Task CreateMutations_CancelledToken_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(Fixture("-value"));
        var node = SyntaxNodeLocator.FindMarked<ExpressionSyntax>(tree);
        var mutator = new UnaryOperatorMutator();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = mutator.CreateMutations(node, semanticModel, cancellation.Token).ToList()
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    private static string Fixture(string expression) =>
        $$"""
            namespace Fixtures;

            internal static class Calculator
            {
                internal static void Apply(int value, bool flag)
                {
                    _ = /*!*/{{expression}};
                    _ = flag;
                }
            }
            """;

    /// <summary>
    /// Splits a comma separated expectation of an inline data case. Reading the parameter here instead
    /// of in the public test method keeps the null contract of the test signature simple.
    /// </summary>
    /// <param name="values">The comma separated expectation.</param>
    /// <returns>The single expectations.</returns>
    private static string[] SplitValues(string values) => values.Split(',');

    private static ImmutableArray<string> Sorted(IEnumerable<string> values) =>
        [.. values.OrderBy(value => value, StringComparer.Ordinal)];

    private static Mutation Single(ImmutableArray<Mutation> mutations, string operatorId) =>
        mutations.Single(mutation => string.Equals(mutation.OperatorId, operatorId, StringComparison.Ordinal));

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
        var mutator = new UnaryOperatorMutator();

        return ([.. mutator.CreateMutations(node, semanticModel, CancellationToken.None)], tree, node);
    }

    /// <summary>
    /// Mutates a fixture that deliberately does not compile, which is the only way to bind a literal
    /// operand the unary operator cannot fold. The test using this overload pins the shape of the fixture
    /// through the semantic model instead of through its compile errors.
    /// </summary>
    /// <param name="source">The fixture source.</param>
    /// <returns>The created mutations, the tree, the marked node and the semantic model.</returns>
    private static (
        ImmutableArray<Mutation> Mutations,
        SyntaxTree Tree,
        ExpressionSyntax Node,
        SemanticModel Model
    ) MutateAllowingErrors(string source)
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var node = SyntaxNodeLocator.FindMarked<ExpressionSyntax>(tree);
        var mutator = new UnaryOperatorMutator();

        return ([.. mutator.CreateMutations(node, semanticModel, CancellationToken.None)], tree, node, semanticModel);
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
