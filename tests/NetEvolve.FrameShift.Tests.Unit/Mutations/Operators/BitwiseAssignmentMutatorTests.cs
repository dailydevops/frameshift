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
/// Covers the compound bitwise assignment matrix, the shift assignment swap and the operand guards
/// that keep this operator in step with its non-compound sibling <see cref="BitwiseOperatorMutator" />.
/// </summary>
public class BitwiseAssignmentMutatorTests
{
    private const string OperatorPlaceholder = "OPERATOR";

    private const string UserDefinedBitwiseSource = """
        namespace Fixtures;

        internal readonly struct Mask
        {
            internal Mask(int bits) => Bits = bits;

            internal int Bits { get; }

            public static Mask operator &(Mask left, Mask right) => new Mask(left.Bits & right.Bits);

            public static Mask operator |(Mask left, Mask right) => new Mask(left.Bits | right.Bits);

            public static Mask operator ^(Mask left, Mask right) => new Mask(left.Bits ^ right.Bits);
        }

        internal static class Masks
        {
            internal static Mask Combine(Mask left, Mask right)
            {
                var total = left;
                /*!*/total OPERATOR right;
                return total;
            }
        }
        """;

    private const string UserDefinedShiftSource = """
        namespace Fixtures;

        internal readonly struct Mask
        {
            internal Mask(int bits) => Bits = bits;

            internal int Bits { get; }

            public static Mask operator <<(Mask left, int count) => new Mask(left.Bits << count);

            public static Mask operator >>(Mask left, int count) => new Mask(left.Bits >> count);
        }

        internal static class Masks
        {
            internal static Mask Shift(Mask left, int count)
            {
                var total = left;
                /*!*/total OPERATOR count;
                return total;
            }
        }
        """;

    private const string TriviaSource = """
        namespace Fixtures;

        internal static class Bits
        {
            // Masks a value.
            internal static int Mask(int total, int value)
            {
                /* leading */
                /*!*/total /* inner */ &= /* after */ value; // tail
                return total;
            }
        }
        """;

    private const string ShiftTriviaSource = """
        namespace Fixtures;

        internal static class Bits
        {
            // Shifts a value.
            internal static int Shift(int total, int value)
            {
                /* leading */
                /*!*/total /* inner */ <<= /* after */ value; // tail
                return total;
            }
        }
        """;

    [Test]
    public async Task Metadata_Operator_ExposesIdKindAndSupportedKinds()
    {
        var mutator = new BitwiseAssignmentMutator();

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutator.Id).IsEqualTo("bitwise-assignment");
            _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.BitwiseOperator);
            _ = await Assert
                .That(mutator.SupportedSyntaxKinds)
                .IsEquivalentTo([
                    SyntaxKind.AndAssignmentExpression,
                    SyntaxKind.OrAssignmentExpression,
                    SyntaxKind.ExclusiveOrAssignmentExpression,
                    SyntaxKind.LeftShiftAssignmentExpression,
                    SyntaxKind.RightShiftAssignmentExpression,
                ]);
        }
    }

    [Test]
    public async Task Fixture_CompoundBitwiseAssignment_Compiles()
    {
        var (compilation, _, _) = CompilationFactory.CreateWithModel(AssignmentFixture("&="));

        _ = await Assert.That(CompilationFactory.GetCompileErrors(compilation)).IsEmpty();
    }

    [Test]
    [Arguments("&=", "and-assign", "or-assign,xor-assign")]
    [Arguments("|=", "or-assign", "and-assign,xor-assign")]
    [Arguments("^=", "xor-assign", "and-assign,or-assign")]
    public async Task CreateMutations_BitwiseAssignment_ProducesEveryCounterpart(
        string symbol,
        string originalName,
        string targetNames
    )
    {
        ArgumentNullException.ThrowIfNull(targetNames);

        var targets = SplitValues(targetNames);
        var result = Mutate(AssignmentFixture(symbol));

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
                .IsEquivalentTo(Sorted(targets.Select(target => $"bitwise-assignment.{originalName}-to-{target}")));
            _ = await Assert
                .That(Sorted(result.Mutations.Select(mutation => mutation.DisplayName)))
                .IsEquivalentTo(Sorted(targets.Select(target => $"{symbol} => {SymbolOf(target)}")));
            _ = await Assert
                .That(result.Mutations.Select(mutation => mutation.Kind).Distinct())
                .IsEquivalentTo([MutationKind.BitwiseOperator]);
        }
    }

    [Test]
    [Arguments("<<=", "bitwise-assignment.left-shift-assign-to-right-shift-assign", "<<= => >>=", "target >>= value")]
    [Arguments(">>=", "bitwise-assignment.right-shift-assign-to-left-shift-assign", ">>= => <<=", "target <<= value")]
    public async Task CreateMutations_ShiftAssignment_SwapsTheDirection(
        string symbol,
        string expectedId,
        string expectedDisplayName,
        string expectedReplacement
    )
    {
        var result = Mutate(AssignmentFixture(symbol));

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
                .IsEquivalentTo([expectedId]);
            _ = await Assert.That(result.Mutations[0].DisplayName).IsEqualTo(expectedDisplayName);
            _ = await Assert.That(result.Mutations[0].Replacement.ToString()).IsEqualTo(expectedReplacement);
            _ = await Assert.That(result.Mutations[0].Kind).IsEqualTo(MutationKind.ShiftOperator);
            _ = await Assert.That(result.Mutations[0].Original).IsEqualTo(result.Node);
        }
    }

    [Test]
    [Arguments("target &= value;", SyntaxKind.AndAssignmentExpression, 2)]
    [Arguments("target |= value;", SyntaxKind.OrAssignmentExpression, 2)]
    [Arguments("target ^= value;", SyntaxKind.ExclusiveOrAssignmentExpression, 2)]
    [Arguments("target <<= value;", SyntaxKind.LeftShiftAssignmentExpression, 1)]
    [Arguments("target >>= value;", SyntaxKind.RightShiftAssignmentExpression, 1)]
    public async Task SupportedSyntaxKinds_EveryKind_IsHandledByCreateMutations(
        string statement,
        SyntaxKind kind,
        int expectedCount
    )
    {
        var mutator = new BitwiseAssignmentMutator();
        var result = Mutate(OperandFixture("int", "int", $"/*!*/{statement}"));

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.Node.Kind()).IsEqualTo(kind);
            _ = await Assert.That(mutator.SupportedSyntaxKinds).Contains(kind);
            _ = await Assert.That(result.Mutations).Count().IsEqualTo(expectedCount);
        }
    }

    [Test]
    [Arguments("=")]
    [Arguments("+=")]
    [Arguments("-=")]
    [Arguments("*=")]
    [Arguments("/=")]
    [Arguments("%=")]
    public async Task CreateMutations_UnsupportedAssignmentKind_ReturnsEmpty(string symbol)
    {
        var result = Mutate(AssignmentFixture(symbol));

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task ApplyTo_AndAssignToOrAssign_RewritesOperatorAndKeepsTrivia()
    {
        var result = Mutate(TriviaSource);
        var mutation = Single(result.Mutations, "bitwise-assignment.and-assign-to-or-assign");

        var mutated = mutation.ApplyTo(result.Tree).ToString();

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(mutated)
                .IsEqualTo(TriviaSource.Replace("&= /* after */", "|= /* after */", StringComparison.Ordinal));
            _ = await Assert.That(mutated).Contains("// Masks a value.");
            _ = await Assert.That(mutated).Contains("/* leading */");
            _ = await Assert.That(mutated).Contains("total /* inner */ |= /* after */ value; // tail");
        }
    }

    [Test]
    public async Task ApplyTo_LeftShiftAssignToRightShiftAssign_RewritesOperatorAndKeepsTrivia()
    {
        var result = Mutate(ShiftTriviaSource);
        var mutation = Single(result.Mutations, "bitwise-assignment.left-shift-assign-to-right-shift-assign");

        var mutated = mutation.ApplyTo(result.Tree).ToString();

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(mutated)
                .IsEqualTo(ShiftTriviaSource.Replace("<<= /* after */", ">>= /* after */", StringComparison.Ordinal));
            _ = await Assert.That(mutated).Contains("// Shifts a value.");
            _ = await Assert.That(mutated).Contains("/* leading */");
            _ = await Assert.That(mutated).Contains("total /* inner */ >>= /* after */ value; // tail");
        }
    }

    [Test]
    [Arguments("&=")]
    [Arguments("|=")]
    [Arguments("^=")]
    public async Task CreateMutations_BooleanOperands_ReturnsEmpty(string symbol)
    {
        var result = Mutate(OperandFixture("bool", "bool", $"/*!*/target {symbol} value;"));

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    [Arguments("&=", "bitwise-assignment.and-assign-to-or-assign,bitwise-assignment.and-assign-to-xor-assign")]
    [Arguments("|=", "bitwise-assignment.or-assign-to-and-assign,bitwise-assignment.or-assign-to-xor-assign")]
    [Arguments("^=", "bitwise-assignment.xor-assign-to-and-assign,bitwise-assignment.xor-assign-to-or-assign")]
    public async Task CreateMutations_EnumOperands_ProducesTheBitwiseCounterparts(string symbol, string expectedIds)
    {
        ArgumentNullException.ThrowIfNull(expectedIds);

        var result = Mutate(OperandFixture("Flags", "Flags", $"/*!*/target {symbol} value;"));

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(Sorted(SplitValues(expectedIds)));
    }

    /// <summary>
    /// A nullable integral operand still unwraps to its underlying integral type, mirroring the
    /// nullable-unwrap guard <see cref="BitwiseOperatorMutatorTests" /> exercises for the non-compound
    /// operator.
    /// </summary>
    [Test]
    [Arguments("&=", "bitwise-assignment.and-assign-to-or-assign,bitwise-assignment.and-assign-to-xor-assign")]
    [Arguments("|=", "bitwise-assignment.or-assign-to-and-assign,bitwise-assignment.or-assign-to-xor-assign")]
    [Arguments("^=", "bitwise-assignment.xor-assign-to-and-assign,bitwise-assignment.xor-assign-to-or-assign")]
    public async Task CreateMutations_NullableIntegralOperands_ProducesTheBitwiseCounterparts(
        string symbol,
        string expectedIds
    )
    {
        ArgumentNullException.ThrowIfNull(expectedIds);

        var result = Mutate(OperandFixture("int?", "int?", $"/*!*/target {symbol} value;"));

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(Sorted(SplitValues(expectedIds)));
    }

    /// <summary>
    /// A <c>char</c> operand is one of the allowed integral special types, so it takes part in the
    /// bitwise assignment mutations just like the other narrow integral types.
    /// </summary>
    [Test]
    [Arguments("&=", "bitwise-assignment.and-assign-to-or-assign,bitwise-assignment.and-assign-to-xor-assign")]
    [Arguments("|=", "bitwise-assignment.or-assign-to-and-assign,bitwise-assignment.or-assign-to-xor-assign")]
    [Arguments("^=", "bitwise-assignment.xor-assign-to-and-assign,bitwise-assignment.xor-assign-to-or-assign")]
    public async Task CreateMutations_CharOperands_ProducesTheBitwiseCounterparts(string symbol, string expectedIds)
    {
        ArgumentNullException.ThrowIfNull(expectedIds);

        var result = Mutate(OperandFixture("char", "char", $"/*!*/target {symbol} value;"));

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(Sorted(SplitValues(expectedIds)));
    }

    /// <summary>
    /// A floating point operand does not bind to any predefined <c>&amp;=</c> operator at all, so the
    /// fixture deliberately does not compile. The reported error keeps the fixture honest, mirroring how
    /// <see cref="BitwiseOperatorMutatorTests" /> pins non-compiling operand shapes through the semantic
    /// model rather than through a compiling program.
    /// </summary>
    [Test]
    public async Task CreateMutations_FloatingPointOperands_ReturnsEmpty()
    {
        var (mutations, _, node, model) = MutateAllowingErrors(
            OperandFixture("double", "double", "/*!*/target &= value;")
        );
        var assignment = (AssignmentExpressionSyntax)node;

        using (Assert.Multiple())
        {
            _ = await Assert.That(node.Kind()).IsEqualTo(SyntaxKind.AndAssignmentExpression);
            _ = await Assert
                .That(model.GetTypeInfo(assignment.Left).ConvertedType?.ToDisplayString())
                .IsEqualTo("double");
            _ = await Assert.That(mutations).IsEmpty();
        }
    }

    [Test]
    public async Task CreateMutations_DecimalOperands_ReturnsEmpty()
    {
        var (mutations, _, node, model) = MutateAllowingErrors(
            OperandFixture("decimal", "decimal", "/*!*/target &= value;")
        );
        var assignment = (AssignmentExpressionSyntax)node;

        using (Assert.Multiple())
        {
            _ = await Assert.That(node.Kind()).IsEqualTo(SyntaxKind.AndAssignmentExpression);
            _ = await Assert
                .That(model.GetTypeInfo(assignment.Left).ConvertedType?.ToDisplayString())
                .IsEqualTo("decimal");
            _ = await Assert.That(mutations).IsEmpty();
        }
    }

    [Test]
    public async Task CreateMutations_StringOperands_ReturnsEmpty()
    {
        var (mutations, _, node, model) = MutateAllowingErrors(
            OperandFixture("string", "string", "/*!*/target &= value;")
        );
        var assignment = (AssignmentExpressionSyntax)node;

        using (Assert.Multiple())
        {
            _ = await Assert.That(node.Kind()).IsEqualTo(SyntaxKind.AndAssignmentExpression);
            _ = await Assert
                .That(model.GetTypeInfo(assignment.Left).ConvertedType?.ToDisplayString())
                .IsEqualTo("string");
            _ = await Assert.That(mutations).IsEmpty();
        }
    }

    /// <summary>
    /// An enum is integral enough for the three bitwise assignments, but never for a shift count. C#
    /// rejects the fixture, which is the only way to bind a shift assignment to an enum count at all.
    /// </summary>
    [Test]
    public async Task CreateMutations_EnumShiftCount_ReturnsEmpty()
    {
        var (mutations, _, node, model) = MutateAllowingErrors(
            OperandFixture("int", "Flags", "/*!*/target <<= value;")
        );
        var assignment = (AssignmentExpressionSyntax)node;

        using (Assert.Multiple())
        {
            _ = await Assert.That(node.Kind()).IsEqualTo(SyntaxKind.LeftShiftAssignmentExpression);
            _ = await Assert.That(model.GetTypeInfo(assignment.Left).ConvertedType?.ToDisplayString()).IsEqualTo("int");
            _ = await Assert.That(model.GetTypeInfo(assignment.Right).ConvertedType?.TypeKind).IsEqualTo(TypeKind.Enum);
            _ = await Assert.That(mutations).IsEmpty();
        }
    }

    /// <summary>
    /// A user defined bitwise operator on a struct produces a value of that struct, which is not an
    /// integral type. Such an assignment belongs to no bitwise mutation, mirroring
    /// <see cref="BitwiseOperatorMutatorTests.CreateMutations_UserDefinedBitwiseOperator_ReturnsEmpty" />.
    /// </summary>
    [Test]
    [Arguments("&=")]
    [Arguments("|=")]
    [Arguments("^=")]
    public async Task CreateMutations_UserDefinedBitwiseOperator_ReturnsEmpty(string symbol)
    {
        var result = Mutate(CreateSource(UserDefinedBitwiseSource, symbol));

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    /// <summary>
    /// Mirrors <see cref="BitwiseOperatorMutatorTests.CreateMutations_UserDefinedShiftOperator_ReturnsEmpty" />
    /// for the compound shift assignments.
    /// </summary>
    [Test]
    [Arguments("<<=")]
    [Arguments(">>=")]
    public async Task CreateMutations_UserDefinedShiftOperator_ReturnsEmpty(string symbol)
    {
        var result = Mutate(CreateSource(UserDefinedShiftSource, symbol));

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_CancelledToken_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(AssignmentFixture("&="));
        var node = SyntaxNodeLocator.FindMarked<ExpressionSyntax>(tree);
        var mutator = new BitwiseAssignmentMutator();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = mutator.CreateMutations(node, semanticModel, cancellation.Token).ToList()
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    private static string SymbolOf(string name) =>
        name switch
        {
            "and-assign" => "&=",
            "or-assign" => "|=",
            "xor-assign" => "^=",
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown bitwise assignment operator name."),
        };

    private static string CreateSource(string template, string symbol) =>
        template.Replace(OperatorPlaceholder, symbol, StringComparison.Ordinal);

    private static string AssignmentFixture(string symbol) =>
        $$"""
            namespace Fixtures;

            internal static class Bits
            {
                internal static int Apply(int target, int value)
                {
                    /*!*/target {{symbol}} value;
                    return target;
                }
            }
            """;

    /// <summary>
    /// Builds a fixture over a target and a value of the given types, together with the enum the enum
    /// operand tests need. The statement carries the marker itself.
    /// </summary>
    /// <param name="targetType">The declared type of the assignment target.</param>
    /// <param name="valueType">The declared type of the assigned value.</param>
    /// <param name="statement">The statement, containing the marker in front of the assignment.</param>
    /// <returns>The fixture source.</returns>
    private static string OperandFixture(string targetType, string valueType, string statement) =>
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
                internal static void Apply({{targetType}} target, {{valueType}} value)
                {
                    {{statement}}
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
        var mutator = new BitwiseAssignmentMutator();

        return ([.. mutator.CreateMutations(node, semanticModel, CancellationToken.None)], tree, node);
    }

    /// <summary>
    /// Mutates a fixture that deliberately does not compile, which is the only way to bind an operand
    /// that has no predefined bitwise or shift assignment operator at all. The tests using this overload
    /// pin the shape of the fixture through the semantic model instead of through its compile errors.
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
        var mutator = new BitwiseAssignmentMutator();

        return ([.. mutator.CreateMutations(node, semanticModel, CancellationToken.None)], tree, node, semanticModel);
    }
}
