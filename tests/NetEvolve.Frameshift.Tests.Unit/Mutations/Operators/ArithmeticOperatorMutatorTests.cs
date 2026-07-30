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
/// Covers the binary arithmetic operator mutations, the operand guards that keep string
/// concatenations and delegate combinations out, and the user defined operator handling.
/// </summary>
public class ArithmeticOperatorMutatorTests
{
    private const string StringOperandSource = """
        namespace Fixtures;

        internal static class Text
        {
            internal static string Combine(string left, string right) => /*!*/left + right;
        }
        """;

    private const string StringLiteralSource = """
        namespace Fixtures;

        internal static class Text
        {
            internal static string Combine() => /*!*/"a" + "b";
        }
        """;

    private const string DelegateOperandSource = """
        namespace Fixtures;

        internal static class Handlers
        {
            internal static System.Action Combine(System.Action left, System.Action right) => /*!*/left + right;
        }
        """;

    private const string AddOnlyOperatorSource = """
        namespace Fixtures;

        internal readonly struct Money
        {
            internal Money(int amount) => Amount = amount;

            internal int Amount { get; }

            public static Money operator +(Money left, Money right) => new Money(left.Amount + right.Amount);
        }

        internal static class Wallet
        {
            internal static Money Combine(Money left, Money right) => /*!*/left + right;
        }
        """;

    private const string AddAndSubtractOperatorSource = """
        namespace Fixtures;

        internal readonly struct Money
        {
            internal Money(int amount) => Amount = amount;

            internal int Amount { get; }

            public static Money operator +(Money left, Money right) => new Money(left.Amount + right.Amount);

            public static Money operator -(Money left, Money right) => new Money(left.Amount - right.Amount);
        }

        internal static class Wallet
        {
            internal static Money Combine(Money left, Money right) => /*!*/left + right;
        }
        """;

    private const string TriviaSource = """
        namespace Fixtures;

        internal static class Calculator
        {
            // Combines two numbers.
            internal static int Combine(int left, int right)
            {
                /* leading */
                return /*!*/left /* inner */ + /* after */ right; // tail
            }
        }
        """;

    [Test]
    public async Task Metadata_Operator_ExposesIdKindAndSupportedKinds()
    {
        var mutator = new ArithmeticOperatorMutator();

        _ = await Assert.That(mutator.Id).IsEqualTo("arithmetic");
        _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.ArithmeticOperator);
        _ = await Assert
            .That(mutator.SupportedSyntaxKinds)
            .IsEquivalentTo(
                new[]
                {
                    SyntaxKind.AddExpression,
                    SyntaxKind.SubtractExpression,
                    SyntaxKind.MultiplyExpression,
                    SyntaxKind.DivideExpression,
                    SyntaxKind.ModuloExpression,
                }
            );
    }

    [Test]
    public async Task Fixture_ArithmeticExpression_Compiles()
    {
        var (compilation, _, _) = CompilationFactory.CreateWithModel(BinaryFixture("+"));

        _ = await Assert.That(CompilationFactory.GetCompileErrors(compilation)).IsEmpty();
    }

    [Test]
    [Arguments("+", "add", "subtract,multiply,divide,modulo")]
    [Arguments("-", "subtract", "add,multiply,divide,modulo")]
    [Arguments("*", "multiply", "add,subtract,divide,modulo")]
    [Arguments("/", "divide", "add,subtract,multiply,modulo")]
    [Arguments("%", "modulo", "add,subtract,multiply,divide")]
    public async Task CreateMutations_ArithmeticExpression_ProducesEveryCounterpart(
        string symbol,
        string originalName,
        string targetNames
    )
    {
        ArgumentNullException.ThrowIfNull(targetNames);

        var targets = SplitValues(targetNames);
        var result = Mutate(BinaryFixture(symbol));

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(Sorted(targets.Select(target => $"arithmetic.{originalName}-to-{target}")));
        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.DisplayName)))
            .IsEquivalentTo(Sorted(targets.Select(target => $"{symbol} => {SymbolOf(target)}")));
        _ = await Assert
            .That(result.Mutations.Select(mutation => mutation.Kind).Distinct())
            .IsEquivalentTo(new[] { MutationKind.ArithmeticOperator });
    }

    [Test]
    [Arguments("+", SyntaxKind.AddExpression)]
    [Arguments("-", SyntaxKind.SubtractExpression)]
    [Arguments("*", SyntaxKind.MultiplyExpression)]
    [Arguments("/", SyntaxKind.DivideExpression)]
    [Arguments("%", SyntaxKind.ModuloExpression)]
    public async Task SupportedSyntaxKinds_EveryKind_IsHandledByCreateMutations(string symbol, SyntaxKind kind)
    {
        var mutator = new ArithmeticOperatorMutator();
        var result = Mutate(BinaryFixture(symbol));

        _ = await Assert.That(result.Node.Kind()).IsEqualTo(kind);
        _ = await Assert.That(mutator.SupportedSyntaxKinds).Contains(kind);
        _ = await Assert.That(result.Mutations).Count().IsEqualTo(4);
    }

    [Test]
    [Arguments("left < right")]
    [Arguments("left == right")]
    [Arguments("left & right")]
    [Arguments("left << right")]
    public async Task CreateMutations_UnsupportedSyntaxKind_ReturnsEmpty(string expression)
    {
        var result = Mutate(ExpressionFixture(expression));

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_MutatedNodeIsTheOriginal_KeepsLocation()
    {
        var result = Mutate(BinaryFixture("+"));
        var mutation = Single(result.Mutations, "arithmetic.add-to-subtract");

        _ = await Assert.That(mutation.Original).IsEqualTo(result.Node);
        _ = await Assert.That(mutation.Replacement.ToString()).IsEqualTo("left - right");
        _ = await Assert.That(mutation.Location).IsEqualTo(result.Node.GetLocation());
    }

    [Test]
    public async Task ApplyTo_AddToMultiply_RewritesOperatorAndKeepsTrivia()
    {
        var result = Mutate(TriviaSource);
        var mutation = Single(result.Mutations, "arithmetic.add-to-multiply");

        var mutated = mutation.ApplyTo(result.Tree).ToString();

        _ = await Assert
            .That(mutated)
            .IsEqualTo(TriviaSource.Replace("+ /* after */", "* /* after */", StringComparison.Ordinal));
        _ = await Assert.That(mutated).Contains("// Combines two numbers.");
        _ = await Assert.That(mutated).Contains("/* leading */");
        _ = await Assert.That(mutated).Contains("left /* inner */ * /* after */ right; // tail");
    }

    [Test]
    public async Task CreateMutations_StringLiteralConcatenation_ReturnsEmpty()
    {
        var result = Mutate(StringLiteralSource);

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_StringOperands_ReturnsEmpty()
    {
        var result = Mutate(StringOperandSource);

        _ = await Assert.That(result.Node.Kind()).IsEqualTo(SyntaxKind.AddExpression);
        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_DelegateOperands_ReturnsEmpty()
    {
        var result = Mutate(DelegateOperandSource);

        _ = await Assert.That(result.Node.Kind()).IsEqualTo(SyntaxKind.AddExpression);
        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_UserDefinedOperatorWithoutCounterpart_ReturnsEmpty()
    {
        var result = Mutate(AddOnlyOperatorSource);

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_UserDefinedOperatorWithCounterpart_ProducesOnlyThatCounterpart()
    {
        string[] expectedIds = ["arithmetic.add-to-subtract"];
        string[] expectedDisplayNames = ["+ => -"];
        var result = Mutate(AddAndSubtractOperatorSource);

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.DisplayName)))
            .IsEquivalentTo(expectedDisplayNames);
    }

    private static string BinaryFixture(string symbol) =>
        $$"""
            namespace Fixtures;

            internal static class Calculator
            {
                internal static int Combine(int left, int right) => /*!*/left {{symbol}} right;
            }
            """;

    private static string ExpressionFixture(string expression) =>
        $$"""
            namespace Fixtures;

            internal static class Calculator
            {
                internal static void Apply(int left, int right)
                {
                    _ = /*!*/{{expression}};
                }
            }
            """;

    private static string SymbolOf(string name) =>
        name switch
        {
            "add" => "+",
            "subtract" => "-",
            "multiply" => "*",
            "divide" => "/",
            "modulo" => "%",
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown arithmetic operator name."),
        };

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
        var mutator = new ArithmeticOperatorMutator();

        return ([.. mutator.CreateMutations(node, semanticModel, CancellationToken.None)], tree, node);
    }
}
