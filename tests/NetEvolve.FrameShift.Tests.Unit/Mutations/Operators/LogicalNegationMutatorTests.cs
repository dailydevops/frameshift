namespace NetEvolve.FrameShift.Tests.Unit.Mutations.Operators;

using System.Collections.Immutable;
using System.Globalization;
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
/// Covers <see cref="LogicalNegationMutator" />, which unwraps an existing <c>!x</c> and wraps the
/// condition of an <c>if</c>, <c>while</c> or <c>do</c> statement and of a conditional expression into
/// <c>!(x)</c>.
/// </summary>
public class LogicalNegationMutatorTests
{
    private const string NegationSource = """
        internal static class Negations
        {
            public static bool Negate(bool value) => /*!*/!value;
        }
        """;

    private const string NullableNegationSource = """
        internal static class Negations
        {
            public static bool? Negate(bool? value) => /*!*/!value;
        }
        """;

    private const string UserDefinedNegationSource = """
        internal sealed class Flag
        {
            public static Flag operator !(Flag value) => value;
        }

        internal static class Negations
        {
            public static Flag Negate(Flag value) => /*!*/!value;
        }
        """;

    private const string UnaryMinusSource = """
        internal static class Negations
        {
            public static int Negate(int value) => /*!*/-value;
        }
        """;

    private const string IfSource = """
        internal static class Conditions
        {
            public static int Classify(int value)
            {
                /*!*/if (value > 0)
                {
                    return 1;
                }

                return 0;
            }
        }
        """;

    private const string NegatedIfSource = """
        internal static class Conditions
        {
            public static int Classify(bool flag)
            {
                /*!*/if (!flag)
                {
                    return 1;
                }

                return 0;
            }
        }
        """;

    private const string WhileSource = """
        internal static class Conditions
        {
            public static void Spin(bool flag)
            {
                /*!*/while (flag)
                {
                    return;
                }
            }
        }
        """;

    private const string DoSource = """
        internal static class Conditions
        {
            public static int Spin(bool flag)
            {
                var result = 0;
                /*!*/do
                {
                    result++;
                    flag = false;
                }
                while (flag);

                return result;
            }
        }
        """;

    private const string ConditionalSource = """
        internal static class Conditions
        {
            public static int Classify(bool flag) => /*!*/flag ? 1 : 0;
        }
        """;

    private const string NullableConditionalSource = """
        internal static class Conditions
        {
            public static int Classify(bool? flag) => /*!*/flag ? 1 : 0;
        }
        """;

    private const string MethodGroupOperandSource = """
        internal static class Negations
        {
            public static bool Flag() => true;

            public static object Negate() => /*!*/!Flag;
        }
        """;

    private const string TrueFalseOperatorConditionSource = """
        internal sealed class Flag
        {
            public static bool operator true(Flag value) => true;

            public static bool operator false(Flag value) => false;
        }

        internal static class Conditions
        {
            public static int Classify(Flag flag)
            {
                /*!*/if (flag)
                {
                    return 1;
                }

                return 0;
            }
        }
        """;

    private const string MethodGroupConditionSource = """
        internal static class Conditions
        {
            public static bool Flag() => true;

            public static int Classify()
            {
                /*!*/if (Flag)
                {
                    return 1;
                }

                return 0;
            }
        }
        """;

    private const string NegatedWhileSource = """
        internal static class Conditions
        {
            public static void Spin(bool flag)
            {
                /*!*/while (!flag)
                {
                    return;
                }
            }
        }
        """;

    private const string NegatedConditionalSource = """
        internal static class Conditions
        {
            public static int Classify(bool flag) => /*!*/!flag ? 1 : 0;
        }
        """;

    private static readonly LogicalNegationMutator _mutator = new LogicalNegationMutator();

    [Test]
    public async Task SupportedSyntaxKinds_Always_AreTheNegationAndConditionKinds()
    {
        SyntaxKind[] expected =
        [
            SyntaxKind.LogicalNotExpression,
            SyntaxKind.IfStatement,
            SyntaxKind.WhileStatement,
            SyntaxKind.DoStatement,
            SyntaxKind.ConditionalExpression,
        ];

        _ = await Assert.That(_mutator.SupportedSyntaxKinds.ToArray()).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Metadata_Always_IdentifiesTheNegationFamily()
    {
        _ = await Assert.That(_mutator.Id).IsEqualTo("negation");
        _ = await Assert.That(_mutator.Kind).IsEqualTo(MutationKind.LogicalNegation);
    }

    [Test]
    [Arguments(NegationSource)]
    [Arguments(NullableNegationSource)]
    public async Task CreateMutations_LogicalNotExpression_UnwrapsTheOperand(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        string[] expected = ["!x => x"];
        var (mutations, tree, _, errors) = Mutate(source);
        var mutation = mutations.Single();

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(DisplayNames(mutations)).IsEquivalentTo(expected);
        _ = await Assert.That(mutation.OperatorId).IsEqualTo("negation.remove-negation");
        _ = await Assert.That(mutation.Kind).IsEqualTo(MutationKind.LogicalNegation);
        _ = await Assert.That(mutation.ApplyTo(tree).ToString()).IsEqualTo(Unnegated(source));
    }

    [Test]
    public async Task CreateMutations_NonBooleanOperand_ReturnsEmpty()
    {
        var (mutations, tree, model, errors) = Mutate(UserDefinedNegationSource);
        var negation = SyntaxNodeLocator.FindMarked<PrefixUnaryExpressionSyntax>(tree);
        var operandType = model.GetTypeInfo(negation.Operand).Type;

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(operandType?.ToDisplayString()).IsEqualTo("Flag");
        _ = await Assert.That(mutations.ToArray()).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_UnaryMinusExpression_ReturnsEmpty()
    {
        var (mutations, _, _, errors) = Mutate(UnaryMinusSource);

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(mutations.ToArray()).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_IfStatement_WrapsTheConditionIntoParentheses()
    {
        var expected = IfSource.Replace("if (value > 0)", "if (!(value > 0))", StringComparison.Ordinal);
        var (mutations, tree, _, errors) = Mutate(IfSource);
        var mutation = mutations.Single();

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(mutation.DisplayName).IsEqualTo("x => !(x)");
        _ = await Assert.That(mutation.OperatorId).IsEqualTo("negation.negate-condition");
        _ = await Assert.That(mutation.Original.ToString()).IsEqualTo("value > 0");
        _ = await Assert.That(mutation.ApplyTo(tree).ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task CreateMutations_WhileStatement_WrapsTheConditionIntoParentheses()
    {
        var expected = WhileSource.Replace("while (flag)", "while (!(flag))", StringComparison.Ordinal);
        var (mutations, tree, _, errors) = Mutate(WhileSource);
        var mutation = mutations.Single();

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(mutation.DisplayName).IsEqualTo("x => !(x)");
        _ = await Assert.That(mutation.Original.ToString()).IsEqualTo("flag");
        _ = await Assert.That(mutation.ApplyTo(tree).ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task CreateMutations_DoStatement_WrapsTheConditionIntoParentheses()
    {
        var expected = DoSource.Replace("while (flag);", "while (!(flag));", StringComparison.Ordinal);
        var (mutations, tree, _, errors) = Mutate(DoSource);
        var mutation = mutations.Single();

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(mutation.DisplayName).IsEqualTo("x => !(x)");
        _ = await Assert.That(mutation.Original.ToString()).IsEqualTo("flag");
        _ = await Assert.That(mutation.ApplyTo(tree).ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task CreateMutations_ConditionalExpression_WrapsTheConditionIntoParentheses()
    {
        var expected = ConditionalSource.Replace("flag ? 1 : 0", "!(flag) ? 1 : 0", StringComparison.Ordinal);
        var (mutations, tree, _, errors) = Mutate(ConditionalSource);
        var mutation = mutations.Single();

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(mutation.DisplayName).IsEqualTo("x => !(x)");
        _ = await Assert.That(mutation.Original.ToString()).IsEqualTo("flag");
        _ = await Assert.That(mutation.ApplyTo(tree).ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task CreateMutations_AlreadyNegatedCondition_ReturnsEmpty()
    {
        var (mutations, tree, _, errors) = Mutate(NegatedIfSource);
        var ifStatement = SyntaxNodeLocator.FindMarked<IfStatementSyntax>(tree);

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(ifStatement.Condition.ToString()).IsEqualTo("!flag");
        _ = await Assert.That(mutations.ToArray()).IsEmpty();
    }

    /// <summary>
    /// No condition of a supported statement accepts a <see langword="bool" />? operand, so this fixture
    /// deliberately does not compile. The type assertion pins the shape of the fixture, which is the only
    /// way to reach the guard that keeps a nullable condition out of a <c>!(...)</c> wrapping.
    /// </summary>
    [Test]
    public async Task CreateMutations_NullableBooleanCondition_ReturnsEmpty()
    {
        var (mutations, tree, model, _) = Mutate(NullableConditionalSource);
        var conditional = SyntaxNodeLocator.FindMarked<ConditionalExpressionSyntax>(tree);
        var conditionType = model.GetTypeInfo(conditional.Condition).Type;

        _ = await Assert.That(conditionType?.ToDisplayString()).IsEqualTo("bool?");
        _ = await Assert.That(mutations.ToArray()).IsEmpty();
    }

    [Test]
    public async Task ApplyTo_NegatedCondition_ProducesCompilableSource()
    {
        var (mutations, tree, _, _) = Mutate(IfSource);
        var mutated = mutations.Single().ApplyTo(tree).ToString();
        var compilation = CompilationFactory.Create(mutated);

        _ = await Assert.That(mutated).Contains("if (!(value > 0))");
        _ = await Assert.That(Describe(CompilationFactory.GetCompileErrors(compilation))).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ApplyTo_RemovedNegation_ProducesCompilableSource()
    {
        var (mutations, tree, _, _) = Mutate(NegationSource);
        var mutated = mutations.Single().ApplyTo(tree).ToString();
        var compilation = CompilationFactory.Create(mutated);

        _ = await Assert.That(mutated).Contains("Negate(bool value) => /*!*/value;");
        _ = await Assert.That(Describe(CompilationFactory.GetCompileErrors(compilation))).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// A method group has no type at all, so the boolean guard has to reject a <see langword="null" />
    /// type instead of assuming every operand carries one. C# rejects the fixture, which is exactly what
    /// makes the operand typeless.
    /// </summary>
    [Test]
    public async Task CreateMutations_TypelessOperand_ReturnsEmpty()
    {
        var (mutations, tree, model, _) = Mutate(MethodGroupOperandSource);
        var negation = SyntaxNodeLocator.FindMarked<PrefixUnaryExpressionSyntax>(tree);

        _ = await Assert.That(negation.Kind()).IsEqualTo(SyntaxKind.LogicalNotExpression);
        _ = await Assert.That(model.GetTypeInfo(negation.Operand).Type).IsNull();
        _ = await Assert.That(mutations.ToArray()).IsEmpty();
    }

    /// <summary>
    /// A type with <c>true</c> and <c>false</c> operators is a legal <c>if</c> condition, but its type is
    /// not <see langword="bool" />. Wrapping it into <c>!(...)</c> would require a <c>!</c> operator the
    /// type does not declare, so no mutation is offered.
    /// </summary>
    [Test]
    public async Task CreateMutations_ConditionWithTrueAndFalseOperators_ReturnsEmpty()
    {
        var (mutations, tree, model, errors) = Mutate(TrueFalseOperatorConditionSource);
        var ifStatement = SyntaxNodeLocator.FindMarked<IfStatementSyntax>(tree);

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(model.GetTypeInfo(ifStatement.Condition).Type?.ToDisplayString()).IsEqualTo("Flag");
        _ = await Assert.That(mutations.ToArray()).IsEmpty();
    }

    /// <summary>
    /// The wrapping guard rejects an already negated condition of every supported node, not only of an
    /// <c>if</c> statement, because <c>!(!x)</c> is no useful mutant.
    /// </summary>
    [Test]
    [Arguments(NegatedWhileSource)]
    [Arguments(NegatedConditionalSource)]
    public async Task CreateMutations_AlreadyNegatedConditionOfEveryNode_ReturnsEmpty(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var (mutations, _, _, errors) = Mutate(source);

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(mutations.ToArray()).IsEmpty();
    }

    /// <summary>
    /// A method group has no type at all, so the wrapping guard has to reject a <see langword="null" />
    /// condition type instead of assuming every condition carries one. C# rejects the fixture, which is
    /// exactly what makes the condition typeless.
    /// </summary>
    [Test]
    public async Task CreateMutations_TypelessCondition_ReturnsEmpty()
    {
        var (mutations, tree, model, _) = Mutate(MethodGroupConditionSource);
        var ifStatement = SyntaxNodeLocator.FindMarked<IfStatementSyntax>(tree);

        _ = await Assert.That(ifStatement.Condition.ToString()).IsEqualTo("Flag");
        _ = await Assert.That(model.GetTypeInfo(ifStatement.Condition).Type).IsNull();
        _ = await Assert.That(mutations.ToArray()).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_CancelledToken_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(IfSource);
        var node = SyntaxNodeLocator.FindMarked(tree);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = _mutator.CreateMutations(node, semanticModel, cancellation.Token).ToList()
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    private static string[] DisplayNames(ImmutableArray<Mutation> mutations) =>
        [.. mutations.Select(mutation => mutation.DisplayName)];

    /// <summary>
    /// Removes the negation from a fixture source, which is exactly what the mutator has to produce.
    /// </summary>
    /// <param name="source">The fixture source of an inline data case.</param>
    /// <returns>The expected mutated source.</returns>
    private static string Unnegated(string source) => source.Replace("!value", "value", StringComparison.Ordinal);

    private static (ImmutableArray<Mutation> Mutations, SyntaxTree Tree, SemanticModel Model, string Errors) Mutate(
        string source
    )
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var node = SyntaxNodeLocator.FindMarked(tree);
        var created = _mutator.CreateMutations(node, semanticModel, CancellationToken.None);
        ImmutableArray<Mutation> mutations = [.. created];

        return (mutations, tree, semanticModel, Describe(CompilationFactory.GetCompileErrors(compilation)));
    }

    private static string Describe(ImmutableArray<Diagnostic> errors) =>
        string.Join(", ", errors.Select(error => error.GetMessage(CultureInfo.InvariantCulture)));
}
