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
/// Covers the decisions of <see cref="EquivalenceClassifier" /> the scenario tests in
/// <see cref="EquivalenceClassifierTests" /> never reach: the argument guards, the shapes the
/// replacement evaluation cannot fold, the arithmetic folds beyond the two happy paths, the second
/// way an unreachable diagnostic can cover a mutation, every kind of body that only throws, every
/// enclosing expression a mutated value can flow into and the member exclusion of a field, of a
/// containing type and of a member that is not excluded at all.
/// </summary>
/// <remarks>
/// Every test pins the exact verdict, including its reason, because a trivial verdict silently hides
/// a testing gap and a reason is reported to the user verbatim.
/// </remarks>
public class EquivalenceClassifierBranchTests
{
    private const string ConstantFoldingReason = "the mutated expression folds to the same constant";
    private const string UnreachableStatementReason = "the mutated statement is already unreachable";
    private const string ThrowOnlyBodyReason = "the containing member does nothing but throw";
    private const string DiscardAssignmentReason = "the mutated value is assigned to a discard";
    private const string AttributeArgumentReason = "the mutation only changes a compile-time attribute argument";
    private const string ConstantDeclarationReason = "the mutation only changes a compile-time constant";
    private const string WellKnownMemberReason = "the containing member is a well known infrastructure member";
    private const string ExcludedMemberReason = "the containing member is excluded from coverage";

    private const string AttributeNameSource = """
        namespace Fixture;

        using System;

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class LimitAttribute : Attribute
        {
            public LimitAttribute(int value) => Value = value;

            public int Value { get; }
        }

        public sealed class Widget
        {
            [/*!*/Limit(1)]
            public int Compute() => 2;
        }
        """;

    private const string ExcludedTypeSource = """
        namespace Fixture;

        using System.Diagnostics.CodeAnalysis;

        [ExcludeFromCodeCoverage]
        public sealed class Widget
        {
            public int Compute(int value) => value > /*!*/1 ? 2 : 3;
        }
        """;

    private const string UsingDirectiveSource = """
        using /*!*/System;

        namespace Fixture;

        public sealed class Widget
        {
            public int Value { get; set; }
        }
        """;

    [Test]
    public async Task Classify_MutationIsNull_ThrowsArgumentNullException()
    {
        var (_, model, _) = CompilationFactory.CreateWithModel(WrapStatements("return left;"));

        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = EquivalenceClassifier.Classify(null!, model, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("mutation");
    }

    [Test]
    public async Task Classify_SemanticModelIsNull_ThrowsArgumentNullException()
    {
        var (_, _, tree) = CompilationFactory.CreateWithModel(WrapStatements("return /*!*/left + right;"));
        var original = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var mutation = CreateMutation(original, Swap(original, SyntaxKind.SubtractExpression));

        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = EquivalenceClassifier.Classify(mutation, null!, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("semanticModel");
    }

    [Test]
    public async Task Classify_CancelledToken_ThrowsOperationCanceledException()
    {
        var (_, model, tree) = CompilationFactory.CreateWithModel(WrapStatements("return /*!*/left + right;"));
        var original = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var mutation = CreateMutation(original, Swap(original, SyntaxKind.SubtractExpression));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = EquivalenceClassifier.Classify(mutation, model, cancellation.Token)
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    [Test]
    public async Task Classify_UnaryOperandIsUnchangedAndFoldsEqually_IsTrivialConstantFolding()
    {
        // -0 and +0 are the same constant, so no test could ever tell the two apart.
        var (_, model, tree) = CompilationFactory.CreateWithModel(WrapExpression("-0"));
        var original = SyntaxNodeLocator.FindMarked<PrefixUnaryExpressionSyntax>(tree);
        var replacement = SyntaxFactory.PrefixUnaryExpression(SyntaxKind.UnaryPlusExpression, original.Operand);

        var verdict = Classify(original, replacement, model);

        await AssertTrivialAsync(verdict, ConstantFoldingReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_UnaryOperandIsReplacedAsWell_IsNotTrivial()
    {
        // The fold only knows the operands of the original, therefore a replacement that changes the
        // operand cannot be evaluated and must never be proven trivial.
        var (_, model, tree) = CompilationFactory.CreateWithModel(WrapExpression("-1"));
        var original = SyntaxNodeLocator.FindMarked<PrefixUnaryExpressionSyntax>(tree);
        var replacement = SyntaxFactory.ParseExpression("-2");

        var verdict = Classify(original, replacement, model);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_ReplacementLiteralIsTheSameValueInHexadecimal_IsTrivialConstantFolding()
    {
        var (_, model, tree) = CompilationFactory.CreateWithModel(WrapExpression("1"));
        var original = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        var replacement = SyntaxFactory.ParseExpression("0x1");

        var verdict = Classify(original, replacement, model);

        await AssertTrivialAsync(verdict, ConstantFoldingReason).ConfigureAwait(false);
    }

    /// <summary>
    /// Pins the <see cref="float" /> arm of the constant comparison: a replacement literal spelled
    /// differently but carrying the very same single precision value is trivial.
    /// </summary>
    [Test]
    public async Task Classify_ReplacementLiteralIsTheSameSingleValueSpelledDifferently_IsTrivialConstantFolding()
    {
        var (_, model, tree) = CompilationFactory.CreateWithModel(WrapExpression("1.5f"));
        var original = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        var replacement = SyntaxFactory.ParseExpression("1.50f");

        var verdict = Classify(original, replacement, model);

        await AssertTrivialAsync(verdict, ConstantFoldingReason).ConfigureAwait(false);
    }

    /// <summary>
    /// The other side of that arm: the two single precision zeros are compared by their bit pattern, not
    /// by <see cref="object.Equals(object)" />, which reports them as equal.
    /// </summary>
    [Test]
    public async Task Classify_ReplacementLiteralFlipsTheSignOfSingleZero_IsNotTrivial()
    {
        // 1.0f / +0.0f is positive infinity while 1.0f / -0.0f is negative infinity, so a test can tell
        // this mutant from the original and it must never be folded away.
        var (_, model, tree) = CompilationFactory.CreateWithModel(WrapExpression("0.0f"));
        var original = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        var replacement = SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression,
            SyntaxFactory.Literal("-0.0f", -0.0f)
        );

        var verdict = Classify(original, replacement, model);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_ReplacementLiteralCarriesNoValue_IsNotTrivial()
    {
        var (_, model, tree) = CompilationFactory.CreateWithModel(WrapStringExpression("\"abc\""));
        var original = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        var replacement = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);

        var verdict = Classify(original, replacement, model);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_ReplacementIsAnUnrelatedShape_IsNotTrivial()
    {
        // A literal replaced by an identifier is neither a binary, a unary nor a literal rewrite, so
        // nothing about the mutant can be evaluated.
        var (_, model, tree) = CompilationFactory.CreateWithModel(WrapMember("public int Compute() => /*!*/1;"));
        var original = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        var replacement = SyntaxFactory.IdentifierName("Value");

        var verdict = Classify(original, replacement, model);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_OperandHasNoConstantValue_IsNotTrivial()
    {
        // The whole expression is the constant "abc", but the right operand is the null constant, so
        // there is nothing to fold the mutated operator over.
        var source = WrapStringExpression("\"abc\" + null");

        var verdict = ClassifyBinary(source, SyntaxKind.SubtractExpression);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    [Arguments("0 - 0", SyntaxKind.AddExpression)]
    [Arguments("0 + 0", SyntaxKind.SubtractExpression)]
    [Arguments("1 / 1", SyntaxKind.MultiplyExpression)]
    [Arguments("2 - 2", SyntaxKind.ModuloExpression)]
    [Arguments("0.0 - 0.0", SyntaxKind.AddExpression)]
    [Arguments("0.0 + 0.0", SyntaxKind.SubtractExpression)]
    [Arguments("1.0 / 1.0", SyntaxKind.MultiplyExpression)]
    [Arguments("1.0 * 1.0", SyntaxKind.DivideExpression)]
    [Arguments("2.0 - 2.0", SyntaxKind.ModuloExpression)]
    public async Task Classify_ArithmeticMutantFoldsToTheSameConstant_IsTrivialConstantFolding(
        string expression,
        SyntaxKind replacementKind
    )
    {
        var verdict = ClassifyBinary(WrapExpression(expression), replacementKind);

        await AssertTrivialAsync(verdict, ConstantFoldingReason).ConfigureAwait(false);
    }

    [Test]
    [Arguments("5 * 0", SyntaxKind.ModuloExpression)]
    [Arguments("-2000000000 + 2000000000", SyntaxKind.SubtractExpression)]
    [Arguments("1L * 1L", SyntaxKind.DivideExpression)]
    [Arguments("2 + 3", SyntaxKind.MultiplyExpression)]
    public async Task Classify_ArithmeticMutantCannotBeFolded_IsNotTrivial(
        string expression,
        SyntaxKind replacementKind
    )
    {
        // Modulo by zero throws, the subtraction underflows an int, long is not a type the fold
        // supports, and the last one simply is a different constant.
        var verdict = ClassifyBinary(WrapExpression(expression), replacementKind);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_UnreachableDiagnosticIsReportedOnTheMutation_IsTrivialUnreachable()
    {
        // The compiler reports the unreachable code on the very first token of the statement, which
        // here is the mutated literal itself.
        var source = WrapStatements(
            """
            return left;
                    /*!*/"abc".ToString();
            """
        );
        var (_, model, tree) = CompilationFactory.CreateWithModel(source);
        var original = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        var replacement = SyntaxFactory.LiteralExpression(
            SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal("xyz")
        );

        var verdict = Classify(original, replacement, model);

        await AssertTrivialAsync(verdict, UnreachableStatementReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_UnreachableDiagnosticBelongsToAnotherStatement_IsNotTrivial()
    {
        // The compiler reports the unreachable code only once, on the first dead statement. The
        // mutation lives in the second one, which the classifier does not prove unreachable and
        // therefore reports, because a wrong trivial verdict would hide a real gap.
        var source = WrapStatements(
            """
            return left;
                    var unused = 1;
                    var other = /*!*/2 + 3;
                    return other;
            """
        );

        var verdict = ClassifyBinary(source, SyntaxKind.SubtractExpression);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    [Arguments("""public int Ratio { get { throw new NotSupportedException(Value > /*!*/1 ? "a" : "b"); } }""")]
    [Arguments("""public int Ratio => throw new NotSupportedException(Value > /*!*/1 ? "a" : "b");""")]
    [Arguments("""public int this[int index] => throw new NotSupportedException(index > /*!*/1 ? "a" : "b");""")]
    public async Task Classify_ContainingBodyOnlyThrows_IsTrivialThrowOnlyBody(string member)
    {
        var verdict = ClassifyLiteral(WrapMember(member));

        await AssertTrivialAsync(verdict, ThrowOnlyBodyReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_ContainingLocalFunctionOnlyThrows_IsTrivialThrowOnlyBody()
    {
        var source = WrapMember(
            """
            public int Compute(int input)
                {
                    int Local() => throw new NotSupportedException(input > /*!*/1 ? "a" : "b");

                    return Local();
                }
            """
        );

        var verdict = ClassifyLiteral(source);

        await AssertTrivialAsync(verdict, ThrowOnlyBodyReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutatedNodeIsAStatement_IsNotTrivial()
    {
        // A statement carries no value that could be discarded, so the discard check must step aside
        // instead of walking a parent chain that does not exist.
        var (_, model, tree) = CompilationFactory.CreateWithModel(WrapStatements("/*!*/return 1;"));
        var original = SyntaxNodeLocator.FindMarked<ReturnStatementSyntax>(tree);
        var replacement = SyntaxFactory.ParseStatement("return 2;");

        var verdict = Classify(original, replacement, model);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_ValueIsForwardedThroughSeveralParentsIntoADiscard_IsTrivialDiscardAssignment()
    {
        var source = WrapStatements(
            """
            _ = checked((int)(/*!*/left + right)) > 0 ? 1 : 2;
                    return left;
            """
        );

        var verdict = ClassifyBinary(source, SyntaxKind.SubtractExpression);

        await AssertTrivialAsync(verdict, DiscardAssignmentReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_ValueIsForwardedThroughANegationIntoADiscard_IsTrivialDiscardAssignment()
    {
        var source = WrapStatements(
            """
            _ = !(/*!*/left == right);
                    return left;
            """
        );

        var verdict = ClassifyBinary(source, SyntaxKind.NotEqualsExpression);

        await AssertTrivialAsync(verdict, DiscardAssignmentReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutatedSubtreeIncrementsAVariable_IsNotTrivial()
    {
        // The increment survives the discard, so the mutant is observable even though its value is
        // thrown away.
        var source = WrapStatements(
            """
            _ = /*!*/left + ++right;
                    return left;
            """
        );

        var verdict = ClassifyBinary(source, SyntaxKind.SubtractExpression);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    [Arguments("return Math.Max(/*!*/left + right, 0);")]
    [Arguments("return /*!*/left + right;")]
    [Arguments("var result = /*!*/left + right;\n        return result;")]
    public async Task Classify_MutatedValueIsConsumed_IsNotTrivial(string statements)
    {
        var verdict = ClassifyBinary(WrapStatements(statements), SyntaxKind.SubtractExpression);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    [Arguments("result = /*!*/left + right;")]
    [Arguments("result *= /*!*/left + right;")]
    public async Task Classify_MutatedValueIsAssignedToARealTarget_IsNotTrivial(string assignment)
    {
        var source = WrapStatements($"var result = 0;\n        {assignment}\n        return result;");

        var verdict = ClassifyBinary(source, SyntaxKind.SubtractExpression);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutatedNodeIsTheAssignmentTarget_IsNotTrivial()
    {
        var (_, model, tree) = CompilationFactory.CreateWithModel(
            WrapStatements("/*!*/left = right;\n        return left;")
        );
        var original = SyntaxNodeLocator.FindMarked<IdentifierNameSyntax>(tree);
        var replacement = SyntaxFactory.IdentifierName("right");

        var verdict = Classify(original, replacement, model);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_DiscardOfAForeignTreeIsRecognisedSyntactically_IsTrivialDiscardAssignment()
    {
        // Without a semantic model for the tree the classifier falls back to the name of the target,
        // which is the only thing left to look at.
        var verdict = ClassifyInForeignTree("_ = /*!*/left + right;\n        return left;");

        await AssertTrivialAsync(verdict, DiscardAssignmentReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_ForeignTreeAssignsToANamedTarget_IsNotTrivial()
    {
        var verdict = ClassifyInForeignTree(
            "var total = 0;\n        total = /*!*/left + right;\n        return total;"
        );

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutationInAttributeName_IsTrivialAttributeArgument()
    {
        var (_, model, tree) = CompilationFactory.CreateWithModel(AttributeNameSource);
        var original = SyntaxNodeLocator.FindMarked<IdentifierNameSyntax>(tree);
        var replacement = SyntaxFactory.IdentifierName("Bound");

        var verdict = Classify(original, replacement, model);

        await AssertTrivialAsync(verdict, AttributeArgumentReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutationInNestedAttributeArgumentExpression_IsTrivialAttributeArgument()
    {
        // The mutated literal sits several ancestors below the attribute argument itself - a binary
        // expression, then a parenthesized expression, then another binary expression - so the
        // constant-only-context walk has to step past all of them before it reaches the
        // AttributeArgumentSyntax that actually proves triviality.
        var source = """
            namespace Fixture;

            using System;

            [AttributeUsage(AttributeTargets.Method)]
            public sealed class LimitAttribute : Attribute
            {
                public LimitAttribute(int value) => Value = value;

                public int Value { get; }
            }

            public sealed class Widget
            {
                [Limit(1 + (2 * /*!*/3))]
                public int Compute() => 2;
            }
            """;

        var verdict = ClassifyLiteral(source);

        await AssertTrivialAsync(verdict, AttributeArgumentReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutationInNestedConstantLocalExpression_IsTrivialConstantDeclaration()
    {
        // Same idea for a constant local: the walk has to pass a binary expression and a
        // parenthesized expression before it reaches the LocalDeclarationStatementSyntax carrying the
        // const modifier.
        var source = WrapStatements("const int limit = (1 + /*!*/2) * 3;\n        return left + limit;");

        var verdict = ClassifyLiteral(source);

        await AssertTrivialAsync(verdict, ConstantDeclarationReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutationInGotoCaseLabel_IsNotTrivial()
    {
        // A goto case target is not a switch label, so the constant-only check does not apply and the
        // conservative verdict is reported.
        var source = WrapMember(
            """
            public string Compute(int value)
                {
                    switch (value)
                    {
                        case 0:
                            goto case /*!*/1;
                        case 1:
                            return "one";
                        default:
                            return "other";
                    }
                }
            """
        );

        var verdict = ClassifyLiteral(source);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutationHasNoSurroundingDeclaration_IsNotTrivial()
    {
        var (_, model, tree) = CompilationFactory.CreateWithModel(UsingDirectiveSource);
        var original = SyntaxNodeLocator.FindMarked<IdentifierNameSyntax>(tree);
        var replacement = SyntaxFactory.IdentifierName("Fixture");

        var verdict = Classify(original, replacement, model);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutationInExcludedFieldInitializer_IsTrivialExcludedMember()
    {
        // A field can only be reached through its declarator, which is the only node carrying a symbol.
        var source = WrapMember("""[GeneratedCode("fixture", "1.0")] private int _limit = /*!*/1;""");

        var verdict = ClassifyLiteral(source);

        await AssertTrivialAsync(verdict, ExcludedMemberReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutationInPlainFieldInitializer_IsNotTrivial()
    {
        var source = WrapMember("private int _limit = /*!*/1;");

        var verdict = ClassifyLiteral(source);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_ContainingTypeIsExcluded_IsTrivialExcludedMember()
    {
        var verdict = ClassifyLiteral(ExcludedTypeSource);

        await AssertTrivialAsync(verdict, ExcludedMemberReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutationInWellKnownProperty_IsTrivialWellKnownMember()
    {
        var source = WrapMember("public int Dispose => Value > /*!*/1 ? 2 : 3;");

        var verdict = ClassifyLiteral(source);

        await AssertTrivialAsync(verdict, WellKnownMemberReason).ConfigureAwait(false);
    }

    private static async Task AssertTrivialAsync(EquivalenceVerdict verdict, string expectedReason)
    {
        using (Assert.Multiple())
        {
            _ = await Assert.That(verdict.IsTrivial).IsTrue();
            _ = await Assert.That(verdict.Reason).IsEqualTo(expectedReason);
        }
    }

    private static async Task AssertNotTrivialAsync(EquivalenceVerdict verdict)
    {
        using (Assert.Multiple())
        {
            _ = await Assert.That(verdict.Reason).IsNull();
            _ = await Assert.That(verdict.IsTrivial).IsFalse();
        }
    }

    private static EquivalenceVerdict Classify(SyntaxNode original, SyntaxNode replacement, SemanticModel model) =>
        EquivalenceClassifier.Classify(CreateMutation(original, replacement), model, CancellationToken.None);

    /// <summary>
    /// Classifies a mutation replacing the marked numeric literal by a value no fixture uses, so that
    /// the mutant really is a different constant.
    /// </summary>
    /// <param name="source">The fixture source containing the marker.</param>
    /// <returns>The verdict.</returns>
    private static EquivalenceVerdict ClassifyLiteral(string source)
    {
        var (_, model, tree) = CompilationFactory.CreateWithModel(source);
        var original = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        var replacement = SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression,
            SyntaxFactory.Literal(99)
        );

        return Classify(original, replacement, model);
    }

    /// <summary>
    /// Classifies a mutation swapping the operator of the marked binary expression, keeping both operands.
    /// </summary>
    /// <param name="source">The fixture source containing the marker.</param>
    /// <param name="replacementKind">The syntax kind of the mutated expression.</param>
    /// <returns>The verdict.</returns>
    private static EquivalenceVerdict ClassifyBinary(string source, SyntaxKind replacementKind)
    {
        var (_, model, tree) = CompilationFactory.CreateWithModel(source);
        var original = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);

        return Classify(original, Swap(original, replacementKind), model);
    }

    /// <summary>
    /// Classifies a mutation that lives in a tree the semantic model knows nothing about, which is how
    /// the purely syntactic fallbacks of the classifier are reached.
    /// </summary>
    /// <param name="statements">The statements of the foreign tree, containing the marker.</param>
    /// <returns>The verdict.</returns>
    private static EquivalenceVerdict ClassifyInForeignTree(string statements)
    {
        var (_, model, _) = CompilationFactory.CreateWithModel(WrapStatements("return left;"));
        var foreign = CompilationFactory.ParseTree(WrapStatements(statements), filePath: "Foreign.cs");
        var original = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(foreign);

        return Classify(original, Swap(original, SyntaxKind.SubtractExpression), model);
    }

    private static BinaryExpressionSyntax Swap(BinaryExpressionSyntax original, SyntaxKind replacementKind) =>
        SyntaxFactory.BinaryExpression(replacementKind, original.Left, original.Right);

    private static Mutation CreateMutation(SyntaxNode original, SyntaxNode replacement) =>
        new Mutation(MutationKind.ArithmeticOperator, "fixture.mutation", "fixture mutation", original, replacement);

    /// <summary>
    /// Wraps a member declaration into a class that offers a mutable property, so that a fixture can
    /// mutate something outside a constant context.
    /// </summary>
    /// <param name="member">The member declaration, containing the marker.</param>
    /// <returns>The fixture source.</returns>
    private static string WrapMember(string member) =>
        $$"""
            namespace Fixture;

            using System;
            using System.CodeDom.Compiler;
            using System.Diagnostics.CodeAnalysis;

            public sealed class Widget
            {
                public int Value { get; set; }

                {{member}}
            }
            """;

    /// <summary>
    /// Wraps statements into an ordinary method with two parameters, which is the shape a real
    /// behaviour-changing mutation lives in.
    /// </summary>
    /// <param name="statements">The statements, containing the marker.</param>
    /// <returns>The fixture source.</returns>
    private static string WrapStatements(string statements) =>
        $$"""
            namespace Fixture;

            using System;

            public sealed class Widget
            {
                public int Compute(int left, int right)
                {
                    {{statements}}
                }
            }
            """;

    /// <summary>
    /// Wraps a constant expression into a member returning <see cref="object" />, so that the fold can
    /// be exercised for every operand type without changing the fixture around it.
    /// </summary>
    /// <param name="expression">The expression to mutate, without the marker.</param>
    /// <returns>The fixture source.</returns>
    private static string WrapExpression(string expression) =>
        WrapMember($"public object Compute() => /*!*/{expression};");

    /// <summary>
    /// Wraps a constant string expression, which needs a member returning <see cref="string" />.
    /// </summary>
    /// <param name="expression">The expression to mutate, without the marker.</param>
    /// <returns>The fixture source.</returns>
    private static string WrapStringExpression(string expression) =>
        WrapMember($"public string Compute() => /*!*/{expression};");
}
