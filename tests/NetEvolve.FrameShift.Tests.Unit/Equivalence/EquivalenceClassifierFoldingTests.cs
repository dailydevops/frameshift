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
/// Covers the relational and the equality part of the constant folding of
/// <see cref="EquivalenceClassifier" />, meaning the decision whether a mutated comparison provably
/// computes the very same constant as the original comparison.
/// </summary>
/// <remarks>
/// <para>
/// The folding helpers are private, so every case drives the public <c>Classify</c> entry point with a
/// fixture whose operands are compile-time constants. The classifier then reads the constant of the
/// original comparison from the semantic model, folds the mutated operator over the constants of the
/// two operands and only reports a trivial verdict when both results are equal.
/// </para>
/// <para>
/// The negative cases carry the weight here. A wrong trivial verdict makes FrameShift report
/// <c>FSH0002</c> instead of <c>FSH0001</c>, which silently swallows a real testing gap the user never
/// gets to see, while a wrong non-trivial verdict only costs a warning a reviewer can dismiss. Every
/// case therefore states which of the two expectations it pins down: trivial because the fold proves
/// the results are equal, or not trivial because folding is impossible or the results differ.
/// </para>
/// </remarks>
public class EquivalenceClassifierFoldingTests
{
    private const string ConstantFoldingReason = "the mutated expression folds to the same constant";

    private const string NullConstantSource = """
        namespace Fixture;

        public sealed class Widget
        {
            private const string? Missing = null;

            public bool Compute() => /*!*/Missing == null;
        }
        """;

    [Test]
    // Equal operands: only the pair of operators that agree on equality folds to the same result.
    [Arguments("1", "1", "<", ">")]
    [Arguments("1", "1", ">", "<")]
    [Arguments("1", "1", "<=", ">=")]
    [Arguments("1", "1", ">=", "<=")]
    // Left operand smaller.
    [Arguments("1", "2", "<", "<=")]
    [Arguments("1", "2", "<=", "<")]
    [Arguments("1", "2", ">", ">=")]
    [Arguments("1", "2", ">=", ">")]
    // Left operand greater.
    [Arguments("2", "1", "<", "<=")]
    [Arguments("2", "1", "<=", "<")]
    [Arguments("2", "1", ">", ">=")]
    [Arguments("2", "1", ">=", ">")]
    public async Task Classify_RelationalIntegerFoldProvesTheSameResult_IsTrivialConstantFolding(
        string left,
        string right,
        string originalOperator,
        string mutatedOperator
    )
    {
        var verdict = await ClassifyAsync(left, right, originalOperator, mutatedOperator).ConfigureAwait(false);

        await AssertTrivialFoldAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    // Equal operands.
    [Arguments("1", "1", "<", "<=")]
    [Arguments("1", "1", "<", ">=")]
    [Arguments("1", "1", "<=", "<")]
    [Arguments("1", "1", "<=", ">")]
    [Arguments("1", "1", ">", "<=")]
    [Arguments("1", "1", ">", ">=")]
    [Arguments("1", "1", ">=", "<")]
    [Arguments("1", "1", ">=", ">")]
    // Left operand smaller.
    [Arguments("1", "2", "<", ">")]
    [Arguments("1", "2", "<", ">=")]
    [Arguments("1", "2", "<=", ">")]
    [Arguments("1", "2", "<=", ">=")]
    [Arguments("1", "2", ">", "<")]
    [Arguments("1", "2", ">", "<=")]
    [Arguments("1", "2", ">=", "<")]
    [Arguments("1", "2", ">=", "<=")]
    // Left operand greater.
    [Arguments("2", "1", "<", ">")]
    [Arguments("2", "1", "<", ">=")]
    [Arguments("2", "1", "<=", ">")]
    [Arguments("2", "1", "<=", ">=")]
    [Arguments("2", "1", ">", "<")]
    [Arguments("2", "1", ">", "<=")]
    [Arguments("2", "1", ">=", "<")]
    [Arguments("2", "1", ">=", "<=")]
    public async Task Classify_RelationalIntegerFoldProvesADifferentResult_IsNotTrivial(
        string left,
        string right,
        string originalOperator,
        string mutatedOperator
    )
    {
        var verdict = await ClassifyAsync(left, right, originalOperator, mutatedOperator).ConfigureAwait(false);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    // Equal operands.
    [Arguments("1.5", "1.5", "<", ">")]
    [Arguments("1.5", "1.5", ">", "<")]
    [Arguments("1.5", "1.5", "<=", ">=")]
    [Arguments("1.5", "1.5", ">=", "<=")]
    // Left operand smaller.
    [Arguments("1.5", "2.5", "<", "<=")]
    [Arguments("1.5", "2.5", "<=", "<")]
    [Arguments("1.5", "2.5", ">", ">=")]
    [Arguments("1.5", "2.5", ">=", ">")]
    // Left operand greater.
    [Arguments("2.5", "1.5", "<", "<=")]
    [Arguments("2.5", "1.5", "<=", "<")]
    [Arguments("2.5", "1.5", ">", ">=")]
    [Arguments("2.5", "1.5", ">=", ">")]
    public async Task Classify_RelationalDoubleFoldProvesTheSameResult_IsTrivialConstantFolding(
        string left,
        string right,
        string originalOperator,
        string mutatedOperator
    )
    {
        var verdict = await ClassifyAsync(left, right, originalOperator, mutatedOperator).ConfigureAwait(false);

        await AssertTrivialFoldAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    // Equal operands.
    [Arguments("1.5", "1.5", "<", "<=")]
    [Arguments("1.5", "1.5", "<", ">=")]
    [Arguments("1.5", "1.5", "<=", "<")]
    [Arguments("1.5", "1.5", "<=", ">")]
    [Arguments("1.5", "1.5", ">", "<=")]
    [Arguments("1.5", "1.5", ">", ">=")]
    [Arguments("1.5", "1.5", ">=", "<")]
    [Arguments("1.5", "1.5", ">=", ">")]
    // Left operand smaller.
    [Arguments("1.5", "2.5", "<", ">")]
    [Arguments("1.5", "2.5", "<", ">=")]
    [Arguments("1.5", "2.5", "<=", ">")]
    [Arguments("1.5", "2.5", "<=", ">=")]
    [Arguments("1.5", "2.5", ">", "<")]
    [Arguments("1.5", "2.5", ">", "<=")]
    [Arguments("1.5", "2.5", ">=", "<")]
    [Arguments("1.5", "2.5", ">=", "<=")]
    // Left operand greater.
    [Arguments("2.5", "1.5", "<", ">")]
    [Arguments("2.5", "1.5", "<", ">=")]
    [Arguments("2.5", "1.5", "<=", ">")]
    [Arguments("2.5", "1.5", "<=", ">=")]
    [Arguments("2.5", "1.5", ">", "<")]
    [Arguments("2.5", "1.5", ">", "<=")]
    [Arguments("2.5", "1.5", ">=", "<")]
    [Arguments("2.5", "1.5", ">=", "<=")]
    public async Task Classify_RelationalDoubleFoldProvesADifferentResult_IsNotTrivial(
        string left,
        string right,
        string originalOperator,
        string mutatedOperator
    )
    {
        var verdict = await ClassifyAsync(left, right, originalOperator, mutatedOperator).ConfigureAwait(false);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    [Arguments("double.NaN", "1.0", "<", "<=")]
    [Arguments("double.NaN", "1.0", "<", ">")]
    [Arguments("double.NaN", "1.0", ">", ">=")]
    [Arguments("1.0", "double.NaN", "<", "<=")]
    [Arguments("1.0", "double.NaN", ">=", ">")]
    [Arguments("double.NaN", "double.NaN", "<", "<=")]
    [Arguments("double.NaN", "double.NaN", ">", ">=")]
    public async Task Classify_RelationalOperandIsNotANumber_IsNotTrivialBecauseFoldingIsImpossible(
        string left,
        string right,
        string originalOperator,
        string mutatedOperator
    )
    {
        // Every relational operator is false as soon as one operand is NaN, so some of these mutants
        // really are equivalent. The classifier still must not say so: it refuses to fold NaN at all,
        // and the conservative verdict is the only safe one.
        var verdict = await ClassifyAsync(left, right, originalOperator, mutatedOperator).ConfigureAwait(false);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    [Arguments("double.PositiveInfinity", "1.0", ">", ">=")]
    [Arguments("double.NegativeInfinity", "1.0", "<", "<=")]
    [Arguments("double.NegativeInfinity", "double.PositiveInfinity", "<", "<=")]
    [Arguments("double.PositiveInfinity", "double.NegativeInfinity", ">", ">=")]
    [Arguments("double.PositiveInfinity", "double.PositiveInfinity", ">=", "<=")]
    [Arguments("double.PositiveInfinity", "double.PositiveInfinity", ">", "<")]
    public async Task Classify_RelationalInfinityFoldProvesTheSameResult_IsTrivialConstantFolding(
        string left,
        string right,
        string originalOperator,
        string mutatedOperator
    )
    {
        var verdict = await ClassifyAsync(left, right, originalOperator, mutatedOperator).ConfigureAwait(false);

        await AssertTrivialFoldAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    [Arguments("double.PositiveInfinity", "1.0", ">", "<")]
    [Arguments("double.PositiveInfinity", "1.0", ">=", "<=")]
    [Arguments("double.NegativeInfinity", "1.0", "<", ">=")]
    [Arguments("double.PositiveInfinity", "double.PositiveInfinity", ">=", ">")]
    [Arguments("double.NegativeInfinity", "double.PositiveInfinity", "<", ">")]
    public async Task Classify_RelationalInfinityFoldProvesADifferentResult_IsNotTrivial(
        string left,
        string right,
        string originalOperator,
        string mutatedOperator
    )
    {
        var verdict = await ClassifyAsync(left, right, originalOperator, mutatedOperator).ConfigureAwait(false);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    [Arguments("0.0", "-0.0", "<=", ">=")]
    [Arguments("0.0", "-0.0", "<", ">")]
    [Arguments("-0.0", "0.0", ">=", "<=")]
    [Arguments("-0.0", "0.0", ">", "<")]
    public async Task Classify_RelationalSignedZerosCompareEqual_IsTrivialConstantFolding(
        string left,
        string right,
        string originalOperator,
        string mutatedOperator
    )
    {
        // Positive and negative zero compare equal, therefore both "not less" operators and both
        // "not greater" operators agree, exactly like they do for two equal non-zero operands.
        var verdict = await ClassifyAsync(left, right, originalOperator, mutatedOperator).ConfigureAwait(false);

        await AssertTrivialFoldAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    [Arguments("0.0", "-0.0", "<=", "<")]
    [Arguments("0.0", "-0.0", "<", "<=")]
    [Arguments("-0.0", "0.0", "<", ">=")]
    [Arguments("-0.0", "0.0", ">=", ">")]
    public async Task Classify_RelationalSignedZerosFoldProvesADifferentResult_IsNotTrivial(
        string left,
        string right,
        string originalOperator,
        string mutatedOperator
    )
    {
        var verdict = await ClassifyAsync(left, right, originalOperator, mutatedOperator).ConfigureAwait(false);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    [Arguments("1", "2.0", "<", "<=")]
    [Arguments("1.5", "2", "<", "<=")]
    [Arguments("2.0", "1", ">", ">=")]
    [Arguments("'a'", "'b'", "<", "<=")]
    [Arguments("1L", "2L", "<", "<=")]
    [Arguments("1u", "2u", "<", "<=")]
    [Arguments("1f", "2f", "<", "<=")]
    [Arguments("1m", "2m", "<", "<=")]
    public async Task Classify_RelationalOperandsAreNotBothIntOrBothDouble_IsNotTrivialBecauseFoldingIsImpossible(
        string left,
        string right,
        string originalOperator,
        string mutatedOperator
    )
    {
        // Each of these mutants computes the same value as its original, so a fold would prove
        // triviality. The classifier only folds two ints or two doubles and therefore must stay
        // conservative for a mixed pair and for every other constant type.
        var verdict = await ClassifyAsync(left, right, originalOperator, mutatedOperator).ConfigureAwait(false);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    [Arguments("1", "1", "==", "!=")]
    [Arguments("1", "1", "!=", "==")]
    [Arguments("1", "2", "==", "!=")]
    [Arguments("1", "2", "!=", "==")]
    [Arguments("1.5", "1.5", "==", "!=")]
    [Arguments("1.5", "2.5", "!=", "==")]
    [Arguments("\"a\"", "\"a\"", "==", "!=")]
    [Arguments("\"a\"", "\"b\"", "!=", "==")]
    [Arguments("true", "true", "==", "!=")]
    [Arguments("true", "false", "!=", "==")]
    [Arguments("'a'", "'a'", "==", "!=")]
    [Arguments("'a'", "'b'", "!=", "==")]
    public async Task Classify_EqualitySwapFlipsTheFoldedResult_IsNotTrivial(
        string left,
        string right,
        string originalOperator,
        string mutatedOperator
    )
    {
        // Swapping == and != over the same operands always folds to the negated result, so such a
        // mutant is observable for every operand type the fold understands.
        var verdict = await ClassifyAsync(left, right, originalOperator, mutatedOperator).ConfigureAwait(false);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    [Arguments("1", "1", "<=", "==")]
    [Arguments("1", "1", ">=", "==")]
    [Arguments("1", "1", "<", "!=")]
    [Arguments("1", "1", ">", "!=")]
    [Arguments("1", "2", "<", "!=")]
    [Arguments("1", "2", ">=", "==")]
    [Arguments("2", "1", ">", "!=")]
    [Arguments("2", "1", "<=", "==")]
    [Arguments("1.5", "1.5", "<=", "==")]
    [Arguments("1.5", "2.5", "<", "!=")]
    [Arguments("'a'", "'b'", "<", "!=")]
    public async Task Classify_RelationalMutatedIntoEqualityFoldProvesTheSameResult_IsTrivialConstantFolding(
        string left,
        string right,
        string originalOperator,
        string mutatedOperator
    )
    {
        // Unlike a == / != swap, a relational original really can fold to the same constant as an
        // equality mutant, which is the only way the equality fold ever proves triviality.
        var verdict = await ClassifyAsync(left, right, originalOperator, mutatedOperator).ConfigureAwait(false);

        await AssertTrivialFoldAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    [Arguments("1", "1", "<", "==")]
    [Arguments("1", "1", "<=", "!=")]
    [Arguments("1", "1", ">=", "!=")]
    [Arguments("1", "2", "<", "==")]
    [Arguments("1", "2", ">=", "!=")]
    [Arguments("2", "1", ">", "==")]
    [Arguments("1.5", "2.5", "<", "==")]
    [Arguments("'a'", "'b'", "<", "==")]
    public async Task Classify_RelationalMutatedIntoEqualityFoldProvesADifferentResult_IsNotTrivial(
        string left,
        string right,
        string originalOperator,
        string mutatedOperator
    )
    {
        var verdict = await ClassifyAsync(left, right, originalOperator, mutatedOperator).ConfigureAwait(false);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    [Arguments("1", "1L", "<=", "==")]
    [Arguments("1", "2L", "<", "!=")]
    [Arguments("1", "1.0", ">=", "==")]
    [Arguments("1", "2.0", "<", "!=")]
    [Arguments("'a'", "98", "<", "!=")]
    [Arguments("'a'", "97", "<=", "==")]
    public async Task Classify_EqualityOperandsHaveDifferentRuntimeTypes_IsNotTrivialBecauseFoldingIsImpossible(
        string left,
        string right,
        string originalOperator,
        string mutatedOperator
    )
    {
        // The widened comparison would fold to the same constant, but the fold refuses operands of
        // different runtime types instead of reasoning about the conversion, so nothing is proven.
        var verdict = await ClassifyAsync(left, right, originalOperator, mutatedOperator).ConfigureAwait(false);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    [Arguments("double.NaN", "double.NaN", "!=", "==")]
    [Arguments("double.NaN", "double.NaN", "==", "!=")]
    public async Task Classify_EqualityOperandsAreNotANumber_IsNotTrivial(
        string left,
        string right,
        string originalOperator,
        string mutatedOperator
    )
    {
        // NaN is the one value where Equals and == disagree: NaN.Equals(NaN) is true while NaN == NaN
        // is false. "NaN != NaN" is true and its mutant "NaN == NaN" is false, so the mutant changes
        // observable behaviour and the only correct verdict is the non-trivial one.
        var verdict = await ClassifyAsync(left, right, originalOperator, mutatedOperator).ConfigureAwait(false);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    /// <summary>
    /// Pins the <see cref="float" /> side of the not-a-number test inside the equality fold, which the
    /// <see cref="double" /> cases above never reach: both operands are single precision values that are
    /// numbers, so the fold proves the mutant computes the same constant.
    /// </summary>
    [Test]
    public async Task Classify_EqualityFoldOverSingleOperandsThatAreNumbers_IsTrivialConstantFolding()
    {
        // 1.5f <= 1.5f and 1.5f == 1.5f are both true, and single precision operands have to be folded
        // exactly like double ones, otherwise this mutant is reported as a gap that no test can close.
        var verdict = await ClassifyAsync("1.5f", "1.5f", "<=", "==").ConfigureAwait(false);

        await AssertTrivialFoldAsync(verdict).ConfigureAwait(false);
    }

    /// <summary>
    /// The other side of the same test: a single precision not-a-number operand makes the fold report
    /// the operands as different, which is what <see cref="object.Equals(object)" /> alone would get
    /// wrong.
    /// </summary>
    [Test]
    public async Task Classify_EqualityFoldOverSingleOperandsThatAreNotANumber_IsNotTrivial()
    {
        // float.NaN != float.NaN is true while its mutant float.NaN == float.NaN is false, so the
        // mutant is observable. Folding through Equals would report both as NaN.Equals(NaN), call the
        // mutant equivalent and swallow the gap.
        var verdict = await ClassifyAsync("float.NaN", "float.NaN", "!=", "==").ConfigureAwait(false);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_EqualityOperandIsANullConstant_IsNotTrivialBecauseFoldingIsImpossible()
    {
        var (compilation, model, tree) = CompilationFactory.CreateWithModel(NullConstantSource);
        _ = await Assert
            .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);

        var original = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var replacement = SyntaxFactory.BinaryExpression(SyntaxKind.NotEqualsExpression, original.Left, original.Right);
        var mutation = CreateMutation(MutationKind.EqualityOperator, original, replacement);

        var verdict = EquivalenceClassifier.Classify(mutation, model, CancellationToken.None);

        // The comparison itself is a compile-time constant, but an operand whose constant value is
        // null is never folded, so the classifier cannot prove anything about the mutant.
        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_ReplacementUsesAnOperatorTheFoldCannotEvaluate_IsNotTrivial()
    {
        var source = WrapComparison("1 < 2");
        var (compilation, model, tree) = CompilationFactory.CreateWithModel(source);
        _ = await Assert
            .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);

        var original = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var replacement = SyntaxFactory.BinaryExpression(SyntaxKind.CoalesceExpression, original.Left, original.Right);
        var mutation = CreateMutation(MutationKind.NullCoalescing, original, replacement);

        var verdict = EquivalenceClassifier.Classify(mutation, model, CancellationToken.None);

        // An operator none of the fold helpers knows must never end up in a trivial verdict, no matter
        // how well the operands themselves are understood.
        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    private static async Task AssertTrivialFoldAsync(EquivalenceVerdict verdict)
    {
        _ = await Assert.That(verdict.IsTrivial).IsTrue();
        _ = await Assert.That(verdict.Reason).IsEqualTo(ConstantFoldingReason);
    }

    private static async Task AssertNotTrivialAsync(EquivalenceVerdict verdict)
    {
        _ = await Assert.That(verdict.Reason).IsNull();
        _ = await Assert.That(verdict.IsTrivial).IsFalse();
    }

    /// <summary>
    /// Compiles a fixture comparing two constants, replaces its operator and classifies the result.
    /// </summary>
    /// <param name="left">The source text of the left operand.</param>
    /// <param name="right">The source text of the right operand.</param>
    /// <param name="originalOperator">The operator the fixture is written with.</param>
    /// <param name="mutatedOperator">The operator the mutant uses instead.</param>
    /// <returns>The verdict of the classifier.</returns>
    private static async Task<EquivalenceVerdict> ClassifyAsync(
        string left,
        string right,
        string originalOperator,
        string mutatedOperator
    )
    {
        var source = WrapComparison($"{left} {originalOperator} {right}");
        var (compilation, model, tree) = CompilationFactory.CreateWithModel(source);

        _ = await Assert
            .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);

        var original = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var replacement = SyntaxFactory.BinaryExpression(ToKind(mutatedOperator), original.Left, original.Right);
        var mutation = CreateMutation(ToMutationKind(mutatedOperator), original, replacement);

        return EquivalenceClassifier.Classify(mutation, model, CancellationToken.None);
    }

    private static Mutation CreateMutation(MutationKind kind, SyntaxNode original, SyntaxNode replacement) =>
        new Mutation(kind, "fixture.fold", "fixture fold", original, replacement);

    private static SyntaxKind ToKind(string comparisonOperator) =>
        comparisonOperator switch
        {
            "<" => SyntaxKind.LessThanExpression,
            "<=" => SyntaxKind.LessThanOrEqualExpression,
            ">" => SyntaxKind.GreaterThanExpression,
            ">=" => SyntaxKind.GreaterThanOrEqualExpression,
            "==" => SyntaxKind.EqualsExpression,
            "!=" => SyntaxKind.NotEqualsExpression,
            _ => throw new ArgumentOutOfRangeException(
                nameof(comparisonOperator),
                comparisonOperator,
                "The fixture uses an operator the test does not know."
            ),
        };

    private static MutationKind ToMutationKind(string comparisonOperator) =>
        comparisonOperator is "==" or "!=" ? MutationKind.EqualityOperator : MutationKind.RelationalOperator;

    /// <summary>
    /// Wraps a comparison into an ordinary method, so that no other check of the classifier applies:
    /// the value is consumed, the member is neither well known nor excluded, and nothing around the
    /// comparison is a compile-time only context.
    /// </summary>
    /// <param name="comparison">The comparison of two constants.</param>
    /// <returns>The fixture source, with the comparison marked.</returns>
    private static string WrapComparison(string comparison) =>
        $$"""
            namespace Fixture;

            public sealed class Widget
            {
                public bool Compute() => /*!*/{{comparison}};
            }
            """;
}
