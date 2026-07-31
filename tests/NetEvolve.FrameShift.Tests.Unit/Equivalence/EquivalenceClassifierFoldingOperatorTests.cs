namespace NetEvolve.FrameShift.Tests.Unit.Equivalence;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Equivalence;
using NetEvolve.FrameShift.Mutations;
using NetEvolve.FrameShift.Tests.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Tests the constant folding of <see cref="EquivalenceClassifier" /> for the shift, the bitwise, the
/// logical and the unary operators.
/// </summary>
/// <remarks>
/// <para>
/// The folding helpers are private, so every case drives them through the public entry point with a
/// fixture whose operands are compile-time constants. A mutation that folds to the very same constant
/// is trivial and is suppressed; every other mutation has to survive the classification, because a
/// wrong trivial verdict silently turns a real testing gap into no diagnostic at all.
/// </para>
/// <para>
/// The negative cases therefore carry the weight here: unsupported operand types, operand types that
/// do not match each other and operator kinds a fold does not understand must never be proven trivial,
/// and neither must a fold whose result would overflow.
/// </para>
/// </remarks>
public class EquivalenceClassifierFoldingOperatorTests
{
    private const string ConstantFoldingReason = "the mutated expression folds to the same constant";

    [Test]
    [Arguments("7 << 32", SyntaxKind.RightShiftExpression)]
    [Arguments("5 >> 32", SyntaxKind.LeftShiftExpression)]
    [Arguments("7 << -32", SyntaxKind.RightShiftExpression)]
    public async Task Classify_ShiftCountMasksToZero_BothShiftsReturnTheOperand_IsTrivial(
        string expression,
        SyntaxKind replacementKind
    )
    {
        // 32, -32 and every other multiple of 32 mask to a shift count of zero, so that the mutated
        // shift returns the left operand unchanged, exactly like the original one does.
        var verdict = ClassifyBinary(expression, replacementKind, MutationKind.ShiftOperator);

        await AssertVerdictAsync(verdict, expectedTrivial: true).ConfigureAwait(false);
    }

    [Test]
    [Arguments("7 << 33", SyntaxKind.RightShiftExpression)]
    [Arguments("8 >> 1", SyntaxKind.LeftShiftExpression)]
    [Arguments("7 << -1", SyntaxKind.RightShiftExpression)]
    public async Task Classify_ShiftCountMasksToANonZeroCount_ShiftsDiffer_IsNotTrivial(
        string expression,
        SyntaxKind replacementKind
    )
    {
        // 33 masks to one and -1 masks to 31, so the two shift directions produce different values and
        // the mutant stays observable.
        var verdict = ClassifyBinary(expression, replacementKind, MutationKind.ShiftOperator);

        await AssertVerdictAsync(verdict, expectedTrivial: false).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_ShiftedOperandIsLong_DoesNotFold_IsNotTrivial()
    {
        // 7L << 32 and 7L >> 32 both yield 7L, so a fold that accepted long operands would call this
        // trivial. Only int is folded, because the shift count of a long shift masks with 63, not 31.
        var verdict = ClassifyBinary("7L << 32", SyntaxKind.RightShiftExpression, MutationKind.ShiftOperator);

        await AssertVerdictAsync(verdict, expectedTrivial: false).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_ShiftCountIsChar_DoesNotFold_IsNotTrivial()
    {
        // '@' is 64 and would mask to a shift count of zero, which would make the mutant trivial. The
        // constant of the right operand is a char, not an int, therefore nothing may be proven.
        var verdict = ClassifyBinary("7 << '@'", SyntaxKind.RightShiftExpression, MutationKind.ShiftOperator);

        await AssertVerdictAsync(verdict, expectedTrivial: false).ConfigureAwait(false);
    }

    [Test]
    [Arguments("true & true", SyntaxKind.BitwiseOrExpression)]
    [Arguments("false & false", SyntaxKind.BitwiseOrExpression)]
    [Arguments("false & false", SyntaxKind.ExclusiveOrExpression)]
    [Arguments("false | false", SyntaxKind.BitwiseAndExpression)]
    [Arguments("true | false", SyntaxKind.ExclusiveOrExpression)]
    [Arguments("false | true", SyntaxKind.ExclusiveOrExpression)]
    [Arguments("true ^ false", SyntaxKind.LogicalOrExpression)]
    [Arguments("true && true", SyntaxKind.LogicalOrExpression)]
    [Arguments("true && false", SyntaxKind.BitwiseAndExpression)]
    [Arguments("false || false", SyntaxKind.LogicalAndExpression)]
    public async Task Classify_BooleanOperandsFoldToTheSameValue_IsTrivial(
        string expression,
        SyntaxKind replacementKind
    )
    {
        var verdict = ClassifyBinary(expression, replacementKind, MutationKind.LogicalOperator);

        await AssertVerdictAsync(verdict, expectedTrivial: true).ConfigureAwait(false);
    }

    [Test]
    [Arguments("true & true", SyntaxKind.ExclusiveOrExpression)]
    [Arguments("true | true", SyntaxKind.ExclusiveOrExpression)]
    [Arguments("true | false", SyntaxKind.BitwiseAndExpression)]
    [Arguments("false | true", SyntaxKind.BitwiseAndExpression)]
    [Arguments("true ^ true", SyntaxKind.LogicalOrExpression)]
    [Arguments("true ^ true", SyntaxKind.BitwiseAndExpression)]
    [Arguments("false ^ true", SyntaxKind.BitwiseAndExpression)]
    [Arguments("true || false", SyntaxKind.LogicalAndExpression)]
    public async Task Classify_BooleanOperandsFoldToDifferentValues_IsNotTrivial(
        string expression,
        SyntaxKind replacementKind
    )
    {
        var verdict = ClassifyBinary(expression, replacementKind, MutationKind.LogicalOperator);

        await AssertVerdictAsync(verdict, expectedTrivial: false).ConfigureAwait(false);
    }

    [Test]
    [Arguments("0 & 0", SyntaxKind.BitwiseOrExpression)]
    [Arguments("0 & 0", SyntaxKind.ExclusiveOrExpression)]
    [Arguments("5 | 0", SyntaxKind.ExclusiveOrExpression)]
    public async Task Classify_IntegerOperandsFoldToTheSameValue_IsTrivial(
        string expression,
        SyntaxKind replacementKind
    )
    {
        var verdict = ClassifyBinary(expression, replacementKind, MutationKind.BitwiseOperator);

        await AssertVerdictAsync(verdict, expectedTrivial: true).ConfigureAwait(false);
    }

    [Test]
    [Arguments("5 & 0", SyntaxKind.ExclusiveOrExpression)]
    [Arguments("6 & 3", SyntaxKind.BitwiseOrExpression)]
    [Arguments("6 | 3", SyntaxKind.BitwiseAndExpression)]
    [Arguments("6 ^ 3", SyntaxKind.BitwiseOrExpression)]
    public async Task Classify_IntegerOperandsFoldToDifferentValues_IsNotTrivial(
        string expression,
        SyntaxKind replacementKind
    )
    {
        var verdict = ClassifyBinary(expression, replacementKind, MutationKind.BitwiseOperator);

        await AssertVerdictAsync(verdict, expectedTrivial: false).ConfigureAwait(false);
    }

    [Test]
    [Arguments("6 & 6", SyntaxKind.LogicalAndExpression)]
    [Arguments("6 | 6", SyntaxKind.LogicalOrExpression)]
    [Arguments("true | true", SyntaxKind.CoalesceExpression)]
    [Arguments("true & true", SyntaxKind.CoalesceExpression)]
    public async Task Classify_OperatorKindIsNotFoldable_IsNotTrivial(string expression, SyntaxKind replacementKind)
    {
        // The conditional operators only exist for bool, and no fold understands ??. Each of these
        // mutants would fold to the value of the original if the operator were handled, so a missing
        // guard would show up as a trivial verdict here.
        var verdict = ClassifyBinary(expression, replacementKind, MutationKind.LogicalOperator);

        await AssertVerdictAsync(verdict, expectedTrivial: false).ConfigureAwait(false);
    }

    [Test]
    [Arguments("2 & 2L", SyntaxKind.BitwiseOrExpression)]
    [Arguments("'@' & 1", SyntaxKind.BitwiseOrExpression)]
    public async Task Classify_OperandTypesDoNotMatch_DoesNotFold_IsNotTrivial(
        string expression,
        SyntaxKind replacementKind
    )
    {
        var verdict = ClassifyBinary(expression, replacementKind, MutationKind.BitwiseOperator);

        await AssertVerdictAsync(verdict, expectedTrivial: false).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_BooleanAndIntegerOperands_DoesNotFold_IsNotTrivial()
    {
        // A bool cannot be combined with an int, so this shape has no constant value at all and the
        // classifier has nothing it could prove. The fixture deliberately does not bind.
        var verdict = ClassifyBinary("true & 1", SyntaxKind.BitwiseOrExpression, MutationKind.BitwiseOperator);

        await AssertVerdictAsync(verdict, expectedTrivial: false).ConfigureAwait(false);
    }

    [Test]
    [Arguments("-0", SyntaxKind.UnaryPlusExpression)]
    [Arguments("+0", SyntaxKind.UnaryMinusExpression)]
    public async Task Classify_UnaryOperatorOnIntegerZeroFoldsToTheSameValue_IsTrivial(
        string expression,
        SyntaxKind replacementKind
    )
    {
        var verdict = ClassifyUnary(expression, replacementKind);

        await AssertVerdictAsync(verdict, expectedTrivial: true).ConfigureAwait(false);
    }

    [Test]
    [Arguments("-5", SyntaxKind.UnaryPlusExpression)]
    [Arguments("+5", SyntaxKind.UnaryMinusExpression)]
    [Arguments("-0", SyntaxKind.BitwiseNotExpression)]
    [Arguments("~0", SyntaxKind.UnaryMinusExpression)]
    [Arguments("~5", SyntaxKind.UnaryPlusExpression)]
    public async Task Classify_UnaryOperatorOnIntegerFoldsToADifferentValue_IsNotTrivial(
        string expression,
        SyntaxKind replacementKind
    )
    {
        var verdict = ClassifyUnary(expression, replacementKind);

        await AssertVerdictAsync(verdict, expectedTrivial: false).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_NegationOfIntMinValue_DoesNotFold_IsNotTrivial()
    {
        // Unchecked, -int.MinValue wraps back to int.MinValue, which would make the mutant look
        // trivial. In a constant expression the negation overflows instead, so the mutant is a real
        // change and must never be suppressed.
        var verdict = ClassifyUnary("+int.MinValue", SyntaxKind.UnaryMinusExpression);

        await AssertVerdictAsync(verdict, expectedTrivial: false).ConfigureAwait(false);
    }

    [Test]
    [Arguments("!true", SyntaxKind.BitwiseNotExpression)]
    [Arguments("!false", SyntaxKind.UnaryMinusExpression)]
    public async Task Classify_UnaryOperatorKindDoesNotApplyToBoolean_DoesNotFold_IsNotTrivial(
        string expression,
        SyntaxKind replacementKind
    )
    {
        var verdict = ClassifyUnary(expression, replacementKind);

        await AssertVerdictAsync(verdict, expectedTrivial: false).ConfigureAwait(false);
    }

    [Test]
    [Arguments("-double.NaN", SyntaxKind.UnaryPlusExpression)]
    [Arguments("+double.NaN", SyntaxKind.UnaryMinusExpression)]
    public async Task Classify_UnaryOperatorOnNaN_FoldsToNaN_IsTrivial(string expression, SyntaxKind replacementKind)
    {
        // Both operators produce a NaN, and nothing but a bit pattern inspection can tell two NaN
        // values apart, so the sign of a NaN carries no behaviour a test could pin down.
        var verdict = ClassifyUnary(expression, replacementKind);

        await AssertVerdictAsync(verdict, expectedTrivial: true).ConfigureAwait(false);
    }

    [Test]
    [Arguments("+double.PositiveInfinity", SyntaxKind.UnaryMinusExpression)]
    [Arguments("+double.NegativeInfinity", SyntaxKind.UnaryMinusExpression)]
    [Arguments("-double.PositiveInfinity", SyntaxKind.UnaryPlusExpression)]
    [Arguments("-0.5", SyntaxKind.UnaryPlusExpression)]
    public async Task Classify_UnaryOperatorOnDoubleFoldsToADifferentValue_IsNotTrivial(
        string expression,
        SyntaxKind replacementKind
    )
    {
        var verdict = ClassifyUnary(expression, replacementKind);

        await AssertVerdictAsync(verdict, expectedTrivial: false).ConfigureAwait(false);
    }

    [Test]
    [Arguments("+0.0", SyntaxKind.UnaryMinusExpression)]
    [Arguments("-0.0", SyntaxKind.UnaryPlusExpression)]
    public async Task Classify_UnaryOperatorFlipsTheSignOfZero_IsNotTrivial(
        string expression,
        SyntaxKind replacementKind
    )
    {
        // Positive and negative zero are two different doubles: 1.0 / +0.0 is positive infinity while
        // 1.0 / -0.0 is negative infinity, and double.IsNegative separates them as well. A test can
        // therefore observe this mutant, so it must not be folded away.
        var verdict = ClassifyUnary(expression, replacementKind);

        await AssertVerdictAsync(verdict, expectedTrivial: false).ConfigureAwait(false);
    }

    [Test]
    [Arguments("-1L", SyntaxKind.UnaryPlusExpression)]
    [Arguments("-1.0m", SyntaxKind.UnaryPlusExpression)]
    public async Task Classify_UnaryOperandTypeIsNotFoldable_IsNotTrivial(string expression, SyntaxKind replacementKind)
    {
        // -1 and +(-1) are the same value, so folding long or decimal would report these as trivial.
        // Only bool, int and double are folded, everything else stays observable by contract.
        var verdict = ClassifyUnary(expression, replacementKind);

        await AssertVerdictAsync(verdict, expectedTrivial: false).ConfigureAwait(false);
    }

    private static async Task AssertVerdictAsync(EquivalenceVerdict verdict, bool expectedTrivial)
    {
        if (expectedTrivial)
        {
            using (Assert.Multiple())
            {
                _ = await Assert.That(verdict.IsTrivial).IsTrue();
                _ = await Assert.That(verdict.Reason).IsEqualTo(ConstantFoldingReason);
            }
            return;
        }

        using (Assert.Multiple())
        {
            _ = await Assert.That(verdict.Reason).IsNull();
            _ = await Assert.That(verdict.IsTrivial).IsFalse();
        }
    }

    /// <summary>
    /// Classifies a mutation that swaps the operator of the marked binary expression, keeping both
    /// operands, which is the only shape the constant folding of a binary expression is reached with.
    /// </summary>
    /// <param name="expression">The original expression of the fixture.</param>
    /// <param name="replacementKind">The syntax kind of the mutated expression.</param>
    /// <param name="mutationKind">The family the mutation belongs to.</param>
    /// <returns>The verdict.</returns>
    private static EquivalenceVerdict ClassifyBinary(
        string expression,
        SyntaxKind replacementKind,
        MutationKind mutationKind
    )
    {
        var (_, model, tree) = CompilationFactory.CreateWithModel(WrapExpression(expression));
        var original = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var replacement = SyntaxFactory.BinaryExpression(replacementKind, original.Left, original.Right);

        return EquivalenceClassifier.Classify(
            CreateMutation(mutationKind, original, replacement),
            model,
            CancellationToken.None
        );
    }

    /// <summary>
    /// Classifies a mutation that swaps the operator of the marked prefix unary expression, keeping the
    /// operand.
    /// </summary>
    /// <param name="expression">The original expression of the fixture.</param>
    /// <param name="replacementKind">The syntax kind of the mutated expression.</param>
    /// <returns>The verdict.</returns>
    private static EquivalenceVerdict ClassifyUnary(string expression, SyntaxKind replacementKind)
    {
        var (_, model, tree) = CompilationFactory.CreateWithModel(WrapExpression(expression));
        var original = SyntaxNodeLocator.FindMarked<PrefixUnaryExpressionSyntax>(tree);
        var replacement = SyntaxFactory.PrefixUnaryExpression(replacementKind, original.Operand);

        return EquivalenceClassifier.Classify(
            CreateMutation(MutationKind.UnaryOperator, original, replacement),
            model,
            CancellationToken.None
        );
    }

    private static Mutation CreateMutation(MutationKind kind, SyntaxNode original, SyntaxNode replacement) =>
        new Mutation(kind, "fixture.mutation", "fixture mutation", original, replacement);

    /// <summary>
    /// Wraps a constant expression into an ordinary member, so that only the constant folding can make
    /// the mutation trivial: the member is neither well known nor excluded, and the value is returned
    /// instead of being discarded or baked into a compile-time context.
    /// </summary>
    /// <param name="expression">The expression, which the marker is placed in front of.</param>
    /// <returns>The fixture source.</returns>
    private static string WrapExpression(string expression) =>
        $$"""
            namespace Fixture;

            public sealed class Widget
            {
                public object Evaluate() => /*!*/{{expression}};
            }
            """;
}
