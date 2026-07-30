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
/// Covers the compound arithmetic assignment mutations together with the guards that keep string
/// appends, delegate combinations and event subscriptions out of this operator family.
/// </summary>
public class ArithmeticAssignmentMutatorTests
{
    private const string StringAppendLiteralSource = """
        namespace Fixtures;

        internal static class Text
        {
            internal static string Append(string text)
            {
                /*!*/text += "x";
                return text;
            }
        }
        """;

    private const string StringAppendVariableSource = """
        namespace Fixtures;

        internal static class Text
        {
            internal static string Append(string text, string suffix)
            {
                /*!*/text += suffix;
                return text;
            }
        }
        """;

    private const string EventSubscriptionSource = """
        namespace Fixtures;

        internal sealed class Publisher
        {
            internal event System.EventHandler? Changed;

            internal void Subscribe(System.EventHandler handler) => /*!*/Changed += handler;
        }
        """;

    private const string DelegateAppendSource = """
        namespace Fixtures;

        internal static class Handlers
        {
            internal static System.Action Combine(System.Action left, System.Action right)
            {
                var total = left;
                /*!*/total += right;
                return total;
            }
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
            internal static Money Accumulate(Money left, Money right)
            {
                var total = left;
                /*!*/total += right;
                return total;
            }
        }
        """;

    private const string AddAndMultiplyOperatorSource = """
        namespace Fixtures;

        internal readonly struct Money
        {
            internal Money(int amount) => Amount = amount;

            internal int Amount { get; }

            public static Money operator +(Money left, Money right) => new Money(left.Amount + right.Amount);

            public static Money operator *(Money left, Money right) => new Money(left.Amount * right.Amount);
        }

        internal static class Wallet
        {
            internal static Money Accumulate(Money left, Money right)
            {
                var total = left;
                /*!*/total += right;
                return total;
            }
        }
        """;

    private const string TriviaSource = """
        namespace Fixtures;

        internal static class Calculator
        {
            // Accumulates a value.
            internal static int Accumulate(int total, int value)
            {
                /* leading */
                /*!*/total /* inner */ += /* after */ value; // tail
                return total;
            }
        }
        """;

    [Test]
    public async Task Metadata_Operator_ExposesIdKindAndSupportedKinds()
    {
        var mutator = new ArithmeticAssignmentMutator();

        _ = await Assert.That(mutator.Id).IsEqualTo("arithmetic-assignment");
        _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.ArithmeticAssignment);
        _ = await Assert
            .That(mutator.SupportedSyntaxKinds)
            .IsEquivalentTo(
                new[]
                {
                    SyntaxKind.AddAssignmentExpression,
                    SyntaxKind.SubtractAssignmentExpression,
                    SyntaxKind.MultiplyAssignmentExpression,
                    SyntaxKind.DivideAssignmentExpression,
                    SyntaxKind.ModuloAssignmentExpression,
                }
            );
    }

    [Test]
    public async Task Fixture_CompoundAssignment_Compiles()
    {
        var (compilation, _, _) = CompilationFactory.CreateWithModel(AssignmentFixture("+="));

        _ = await Assert.That(CompilationFactory.GetCompileErrors(compilation)).IsEmpty();
    }

    [Test]
    [Arguments("+=", "add-assign", "subtract-assign,multiply-assign,divide-assign,modulo-assign")]
    [Arguments("-=", "subtract-assign", "add-assign,multiply-assign,divide-assign,modulo-assign")]
    [Arguments("*=", "multiply-assign", "add-assign,subtract-assign,divide-assign,modulo-assign")]
    [Arguments("/=", "divide-assign", "add-assign,subtract-assign,multiply-assign,modulo-assign")]
    [Arguments("%=", "modulo-assign", "add-assign,subtract-assign,multiply-assign,divide-assign")]
    public async Task CreateMutations_CompoundAssignment_ProducesEveryCounterpart(
        string symbol,
        string originalName,
        string targetNames
    )
    {
        ArgumentNullException.ThrowIfNull(targetNames);

        var targets = SplitValues(targetNames);
        var result = Mutate(AssignmentFixture(symbol));

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(Sorted(targets.Select(target => $"arithmetic-assignment.{originalName}-to-{target}")));
        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.DisplayName)))
            .IsEquivalentTo(Sorted(targets.Select(target => $"{symbol} => {SymbolOf(target)}")));
        _ = await Assert
            .That(result.Mutations.Select(mutation => mutation.Kind).Distinct())
            .IsEquivalentTo(new[] { MutationKind.ArithmeticAssignment });
    }

    [Test]
    [Arguments("+=", SyntaxKind.AddAssignmentExpression)]
    [Arguments("-=", SyntaxKind.SubtractAssignmentExpression)]
    [Arguments("*=", SyntaxKind.MultiplyAssignmentExpression)]
    [Arguments("/=", SyntaxKind.DivideAssignmentExpression)]
    [Arguments("%=", SyntaxKind.ModuloAssignmentExpression)]
    public async Task SupportedSyntaxKinds_EveryKind_IsHandledByCreateMutations(string symbol, SyntaxKind kind)
    {
        var mutator = new ArithmeticAssignmentMutator();
        var result = Mutate(AssignmentFixture(symbol));

        _ = await Assert.That(result.Node.Kind()).IsEqualTo(kind);
        _ = await Assert.That(mutator.SupportedSyntaxKinds).Contains(kind);
        _ = await Assert.That(result.Mutations).Count().IsEqualTo(4);
    }

    [Test]
    [Arguments("=")]
    [Arguments("<<=")]
    [Arguments(">>=")]
    [Arguments("&=")]
    [Arguments("|=")]
    [Arguments("^=")]
    public async Task CreateMutations_UnsupportedAssignmentKind_ReturnsEmpty(string symbol)
    {
        var result = Mutate(AssignmentFixture(symbol));

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_MutatedNodeIsTheOriginal_ReplacesOnlyTheOperator()
    {
        var result = Mutate(AssignmentFixture("+="));
        var mutation = Single(result.Mutations, "arithmetic-assignment.add-assign-to-modulo-assign");

        _ = await Assert.That(mutation.Original).IsEqualTo(result.Node);
        _ = await Assert.That(mutation.Replacement.ToString()).IsEqualTo("total %= value");
    }

    [Test]
    public async Task ApplyTo_AddAssignToDivideAssign_RewritesOperatorAndKeepsTrivia()
    {
        var result = Mutate(TriviaSource);
        var mutation = Single(result.Mutations, "arithmetic-assignment.add-assign-to-divide-assign");

        var mutated = mutation.ApplyTo(result.Tree).ToString();

        _ = await Assert
            .That(mutated)
            .IsEqualTo(TriviaSource.Replace("+= /* after */", "/= /* after */", StringComparison.Ordinal));
        _ = await Assert.That(mutated).Contains("// Accumulates a value.");
        _ = await Assert.That(mutated).Contains("/* leading */");
        _ = await Assert.That(mutated).Contains("total /* inner */ /= /* after */ value; // tail");
    }

    [Test]
    public async Task CreateMutations_StringAppendOfLiteral_ReturnsEmpty()
    {
        var result = Mutate(StringAppendLiteralSource);

        _ = await Assert.That(result.Node.Kind()).IsEqualTo(SyntaxKind.AddAssignmentExpression);
        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_StringAppendOfVariable_ReturnsEmpty()
    {
        var result = Mutate(StringAppendVariableSource);

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_EventSubscription_ReturnsEmpty()
    {
        var result = Mutate(EventSubscriptionSource);

        _ = await Assert.That(result.Node.Kind()).IsEqualTo(SyntaxKind.AddAssignmentExpression);
        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_DelegateAppend_ReturnsEmpty()
    {
        var result = Mutate(DelegateAppendSource);

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
        string[] expectedIds = ["arithmetic-assignment.add-assign-to-multiply-assign"];
        string[] expectedDisplayNames = ["+= => *="];
        var result = Mutate(AddAndMultiplyOperatorSource);

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.DisplayName)))
            .IsEquivalentTo(expectedDisplayNames);
    }

    private static string AssignmentFixture(string symbol) =>
        $$"""
            namespace Fixtures;

            internal static class Calculator
            {
                internal static int Accumulate(int total, int value)
                {
                    /*!*/total {{symbol}} value;
                    return total;
                }
            }
            """;

    private static string SymbolOf(string name) =>
        name switch
        {
            "add-assign" => "+=",
            "subtract-assign" => "-=",
            "multiply-assign" => "*=",
            "divide-assign" => "/=",
            "modulo-assign" => "%=",
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown assignment operator name."),
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
        var mutator = new ArithmeticAssignmentMutator();

        return ([.. mutator.CreateMutations(node, semanticModel, CancellationToken.None)], tree, node);
    }
}
