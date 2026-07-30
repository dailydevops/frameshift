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
/// Covers the bitwise operator matrix, the shift swap and the operand guards that keep boolean
/// operands out of this operator family, because those belong to the logical operators.
/// </summary>
public class BitwiseOperatorMutatorTests
{
    private const string TriviaSource = """
        namespace Fixtures;

        internal static class Bits
        {
            // Masks two values.
            internal static int Mask(int left, int right)
            {
                /* leading */
                return /*!*/left /* inner */ & /* after */ right; // tail
            }
        }
        """;

    [Test]
    public async Task Metadata_Operator_ExposesIdKindAndSupportedKinds()
    {
        var mutator = new BitwiseOperatorMutator();

        _ = await Assert.That(mutator.Id).IsEqualTo("bitwise");
        _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.BitwiseOperator);
        _ = await Assert
            .That(mutator.SupportedSyntaxKinds)
            .IsEquivalentTo(
                new[]
                {
                    SyntaxKind.BitwiseAndExpression,
                    SyntaxKind.BitwiseOrExpression,
                    SyntaxKind.ExclusiveOrExpression,
                    SyntaxKind.LeftShiftExpression,
                    SyntaxKind.RightShiftExpression,
                }
            );
    }

    [Test]
    public async Task Fixture_BitwiseExpression_Compiles()
    {
        var (compilation, _, _) = CompilationFactory.CreateWithModel(Fixture("left & right"));

        _ = await Assert.That(CompilationFactory.GetCompileErrors(compilation)).IsEmpty();
    }

    [Test]
    [Arguments("&", "bitwise.and-to-or,bitwise.and-to-xor", "& => |,& => ^")]
    [Arguments("|", "bitwise.or-to-and,bitwise.or-to-xor", "| => &,| => ^")]
    [Arguments("^", "bitwise.xor-to-and,bitwise.xor-to-or", "^ => &,^ => |")]
    public async Task CreateMutations_BitwiseExpression_ProducesEveryCounterpart(
        string symbol,
        string expectedIds,
        string expectedDisplayNames
    )
    {
        ArgumentNullException.ThrowIfNull(expectedIds);
        ArgumentNullException.ThrowIfNull(expectedDisplayNames);

        var result = Mutate(Fixture($"left {symbol} right"));

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(Sorted(SplitValues(expectedIds)));
        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.DisplayName)))
            .IsEquivalentTo(Sorted(SplitValues(expectedDisplayNames)));
        _ = await Assert
            .That(result.Mutations.Select(mutation => mutation.Kind).Distinct())
            .IsEquivalentTo(new[] { MutationKind.BitwiseOperator });
    }

    [Test]
    [Arguments("<<", "bitwise.left-shift-to-right-shift", "<< => >>", "left >> right")]
    [Arguments(">>", "bitwise.right-shift-to-left-shift", ">> => <<", "left << right")]
    public async Task CreateMutations_ShiftExpression_SwapsTheDirection(
        string symbol,
        string expectedId,
        string expectedDisplayName,
        string expectedReplacement
    )
    {
        var result = Mutate(Fixture($"left {symbol} right"));

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(new[] { expectedId });
        _ = await Assert.That(result.Mutations[0].DisplayName).IsEqualTo(expectedDisplayName);
        _ = await Assert.That(result.Mutations[0].Replacement.ToString()).IsEqualTo(expectedReplacement);
        _ = await Assert.That(result.Mutations[0].Kind.ToString()).IsEqualTo("ShiftOperator");
        _ = await Assert.That(result.Mutations[0].Original).IsEqualTo(result.Node);
    }

    [Test]
    [Arguments("left & right", SyntaxKind.BitwiseAndExpression, 2)]
    [Arguments("left | right", SyntaxKind.BitwiseOrExpression, 2)]
    [Arguments("left ^ right", SyntaxKind.ExclusiveOrExpression, 2)]
    [Arguments("left << right", SyntaxKind.LeftShiftExpression, 1)]
    [Arguments("left >> right", SyntaxKind.RightShiftExpression, 1)]
    public async Task SupportedSyntaxKinds_EveryKind_IsHandledByCreateMutations(
        string expression,
        SyntaxKind kind,
        int expectedCount
    )
    {
        var mutator = new BitwiseOperatorMutator();
        var result = Mutate(Fixture(expression));

        _ = await Assert.That(result.Node.Kind()).IsEqualTo(kind);
        _ = await Assert.That(mutator.SupportedSyntaxKinds).Contains(kind);
        _ = await Assert.That(result.Mutations).Count().IsEqualTo(expectedCount);
    }

    [Test]
    [Arguments("left >>> right")]
    [Arguments("flag && other")]
    [Arguments("flag || other")]
    [Arguments("left + right")]
    public async Task CreateMutations_UnsupportedSyntaxKind_ReturnsEmpty(string expression)
    {
        var result = Mutate(Fixture(expression));

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    [Arguments("flag & other")]
    [Arguments("flag | other")]
    [Arguments("flag ^ other")]
    [Arguments("nullableFlag & nullableOther")]
    [Arguments("nullableFlag | nullableOther")]
    [Arguments("nullableFlag ^ nullableOther")]
    public async Task CreateMutations_BooleanOperands_ReturnsEmpty(string expression)
    {
        var result = Mutate(Fixture(expression));

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    [Arguments("firstFlag & secondFlag", "bitwise.and-to-or,bitwise.and-to-xor")]
    [Arguments("firstFlag | secondFlag", "bitwise.or-to-and,bitwise.or-to-xor")]
    public async Task CreateMutations_EnumOperands_ProducesTheBitwiseCounterparts(string expression, string expectedIds)
    {
        ArgumentNullException.ThrowIfNull(expectedIds);

        var result = Mutate(Fixture(expression));

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(Sorted(SplitValues(expectedIds)));
    }

    [Test]
    public async Task CreateMutations_NullableIntegralOperands_ProducesTheBitwiseCounterparts()
    {
        string[] expectedIds = ["bitwise.and-to-or", "bitwise.and-to-xor"];
        var result = Mutate(Fixture("nullableLeft & nullableRight"));

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
    }

    [Test]
    public async Task ApplyTo_AndToXor_RewritesOperatorAndKeepsTrivia()
    {
        var result = Mutate(TriviaSource);
        var mutation = Single(result.Mutations, "bitwise.and-to-xor");

        var mutated = mutation.ApplyTo(result.Tree).ToString();

        _ = await Assert
            .That(mutated)
            .IsEqualTo(TriviaSource.Replace("& /* after */", "^ /* after */", StringComparison.Ordinal));
        _ = await Assert.That(mutated).Contains("// Masks two values.");
        _ = await Assert.That(mutated).Contains("/* leading */");
        _ = await Assert.That(mutated).Contains("left /* inner */ ^ /* after */ right; // tail");
    }

    private static string Fixture(string expression) =>
        $$"""
            namespace Fixtures;

            internal enum Flags
            {
                None = 0,
                First = 1,
                Second = 2,
            }

            internal static class Bits
            {
                internal static void Apply(
                    int left,
                    int right,
                    bool flag,
                    bool other,
                    bool? nullableFlag,
                    bool? nullableOther,
                    Flags firstFlag,
                    Flags secondFlag,
                    int? nullableLeft,
                    int? nullableRight
                )
                {
                    _ = /*!*/{{expression}};
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
        var mutator = new BitwiseOperatorMutator();

        return ([.. mutator.CreateMutations(node, semanticModel, CancellationToken.None)], tree, node);
    }
}
