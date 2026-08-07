namespace NetEvolve.FrameShift.Tests.Unit.Equivalence;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Equivalence;
using NetEvolve.FrameShift.Mutations;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Tests <see cref="EquivalenceClassifier" />. Every trivial verdict is pinned down together with its
/// exact reason, because that reason is reported to the user, and every case the classifier must not
/// prove is checked as well: a wrong trivial verdict silently hides the testing gap FrameShift exists
/// to surface.
/// </summary>
public class EquivalenceClassifierTests
{
    private const string NoOpReason = "the mutant is syntactically identical to the original code";
    private const string ConstantFoldingReason = "the mutated expression folds to the same constant";
    private const string UnreachableStatementReason = "the mutated statement is already unreachable";
    private const string ThrowOnlyBodyReason = "the containing member does nothing but throw";
    private const string DiscardedStatementReason = "the mutated value is never consumed by its statement";
    private const string DiscardAssignmentReason = "the mutated value is assigned to a discard";
    private const string AttributeArgumentReason = "the mutation only changes a compile-time attribute argument";
    private const string ConstantDeclarationReason = "the mutation only changes a compile-time constant";
    private const string DefaultParameterReason = "the mutation only changes a default parameter value";
    private const string CaseLabelReason = "the mutation only changes a compile-time case label";
    private const string ConfigureAwaitArgumentReason =
        "the mutation only flips the captured-context argument of ConfigureAwait, which no test can observe";
    private const string WellKnownMemberReason = "the containing member is a well known infrastructure member";
    private const string ExcludedMemberReason = "the containing member is excluded from coverage";
    private const string ObsoleteMemberReason = "the containing member is marked obsolete";

    private const string AttributeArgumentSource = """
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
            [Limit(/*!*/1)]
            public int Compute() => 2;
        }
        """;

    [Test]
    public async Task Classify_ReplacementIsTheSameSyntax_IsTrivialNoOp()
    {
        var source = WrapStatements("var result = /*!*/left + right;\n        return result;");
        var (_, model, tree) = CompilationFactory.CreateWithModel(source);
        var original = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var mutation = CreateMutation(original, SyntaxFactory.ParseExpression("left + right"));

        var verdict = EquivalenceClassifier.Classify(mutation, model, CancellationToken.None);

        await AssertTrivialAsync(verdict, NoOpReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutatedExpressionFoldsToTheSameConstant_IsTrivialConstantFolding()
    {
        var source = WrapMember("public int Ratio() => /*!*/1 * 1;");

        var verdict = ClassifyBinary(source, SyntaxKind.DivideExpression);

        await AssertTrivialAsync(verdict, ConstantFoldingReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutationInUnreachableStatement_IsTrivialUnreachable()
    {
        var source = WrapStatements("return left;\n        var unused = /*!*/2 + 3;\n        return unused;");

        var verdict = ClassifyBinary(source, SyntaxKind.SubtractExpression);

        await AssertTrivialAsync(verdict, UnreachableStatementReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_ContainingMemberOnlyThrows_IsTrivialThrowOnlyBody()
    {
        var source = WrapMember(
            """
            public int Compute(int value)
                {
                    throw new NotSupportedException(value > /*!*/1 ? "positive" : "other");
                }
            """
        );

        var verdict = ClassifyLiteral(source);

        await AssertTrivialAsync(verdict, ThrowOnlyBodyReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutatedValueIsNeverConsumed_IsTrivialDiscardedStatement()
    {
        // The compiler rejects an expression statement that only computes a value, so this shape can
        // only ever be reached through a mutation. The classifier still has to recognise it.
        var source = WrapStatements("/*!*/left + right;\n        return left;");

        var verdict = ClassifyBinary(source, SyntaxKind.SubtractExpression);

        await AssertTrivialAsync(verdict, DiscardedStatementReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutatedValueIsAssignedToADiscard_IsTrivialDiscardAssignment()
    {
        var source = WrapStatements("_ = /*!*/left + right;\n        return left;");

        var verdict = ClassifyBinary(source, SyntaxKind.SubtractExpression);

        await AssertTrivialAsync(verdict, DiscardAssignmentReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutationInAttributeArgument_IsTrivialAttributeArgument()
    {
        var verdict = ClassifyLiteral(AttributeArgumentSource);

        await AssertTrivialAsync(verdict, AttributeArgumentReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutationInDefaultParameterValue_IsTrivialDefaultParameter()
    {
        var source = WrapMember("public int Compute(int value = /*!*/1) => value;");

        var verdict = ClassifyLiteral(source);

        await AssertTrivialAsync(verdict, DefaultParameterReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutationInCaseLabel_IsTrivialCaseLabel()
    {
        var source = WrapMember(
            """
            public string Compute(int value)
                {
                    switch (value)
                    {
                        case /*!*/1:
                            return "one";
                        default:
                            return "other";
                    }
                }
            """
        );

        var verdict = ClassifyLiteral(source);

        await AssertTrivialAsync(verdict, CaseLabelReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutationInConstantField_IsTrivialConstantDeclaration()
    {
        var source = WrapMember("private const int Limit = /*!*/1;");

        var verdict = ClassifyLiteral(source);

        await AssertTrivialAsync(verdict, ConstantDeclarationReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutationInConstantLocal_IsTrivialConstantDeclaration()
    {
        var source = WrapStatements("const int limit = /*!*/1;\n        return left + limit;");

        var verdict = ClassifyLiteral(source);

        await AssertTrivialAsync(verdict, ConstantDeclarationReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutationInEnumMember_IsTrivialConstantDeclaration()
    {
        var source = WrapMember("public enum Level { Low = /*!*/1 }");

        var verdict = ClassifyLiteral(source);

        await AssertTrivialAsync(verdict, ConstantDeclarationReason).ConfigureAwait(false);
    }

    [Test]
    [Arguments("""public override string ToString() => Value > /*!*/1 ? "big" : "small";""")]
    [Arguments("public override int GetHashCode() => Value > /*!*/1 ? 2 : 3;")]
    [Arguments("public override bool Equals(object? other) => Value > /*!*/1;")]
    [Arguments("public void Dispose() => Value = Value > /*!*/1 ? 0 : 1;")]
    public async Task Classify_MutationInWellKnownMember_IsTrivialWellKnownMember(string member)
    {
        var verdict = ClassifyLiteral(WrapMember(member));

        await AssertTrivialAsync(verdict, WellKnownMemberReason).ConfigureAwait(false);
    }

    [Test]
    [Arguments("[ExcludeFromCodeCoverage]")]
    [Arguments("""[GeneratedCode("fixture", "1.0")]""")]
    public async Task Classify_ContainingMemberIsExcluded_IsTrivialExcludedMember(string attribute)
    {
        var source = WrapMember($"{attribute} public int Compute() => Value > /*!*/1 ? 2 : 3;");

        var verdict = ClassifyLiteral(source);

        await AssertTrivialAsync(verdict, ExcludedMemberReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_ContainingMemberIsObsolete_IsTrivialObsoleteMember()
    {
        var source = WrapMember("[Obsolete] public int Compute() => Value > /*!*/1 ? 2 : 3;");

        var verdict = ClassifyLiteral(source);

        await AssertTrivialAsync(verdict, ObsoleteMemberReason).ConfigureAwait(false);
    }

    [Test]
    [Arguments("public async Task RunAsync(Task work) => await work.ConfigureAwait(/*!*/false);")]
    [Arguments("public async Task<int> RunAsync(Task<int> work) => await work.ConfigureAwait(/*!*/false);")]
    [Arguments("public async ValueTask RunAsync(ValueTask work) => await work.ConfigureAwait(/*!*/false);")]
    [Arguments("public async ValueTask<int> RunAsync(ValueTask<int> work) => await work.ConfigureAwait(/*!*/false);")]
    public async Task Classify_MutationOfConfigureAwaitArgument_IsTrivialConfigureAwaitArgument(string member)
    {
        var source = WrapTaskMember(member);

        var verdict = ClassifyBooleanLiteral(source);

        await AssertTrivialAsync(verdict, ConfigureAwaitArgumentReason).ConfigureAwait(false);
    }

    /// <summary>
    /// The check resolves the invoked method through the semantic model rather than by name, exactly
    /// like the culture-sensitivity family does, so a type of the caller's own that happens to declare
    /// a same-named <c>ConfigureAwait(bool)</c> method must not be mistaken for the real one.
    /// </summary>
    [Test]
    public async Task Classify_MutationOfUserDefinedConfigureAwaitArgument_IsNotTrivial()
    {
        const string source = """
            namespace Fixture;

            public sealed class FakeAwaitable
            {
                public FakeAwaitable ConfigureAwait(bool continueOnCapturedContext) => this;
            }

            public sealed class Widget
            {
                public FakeAwaitable Compute(FakeAwaitable awaitable) => awaitable.ConfigureAwait(/*!*/false);
            }
            """;

        var verdict = ClassifyBooleanLiteral(source);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    /// <summary>
    /// A boolean literal outside any argument list is not what this check looks for at all - the
    /// mutation still has to be classified as a genuine behaviour change through the ordinary path.
    /// </summary>
    [Test]
    public async Task Classify_MutationOfUnrelatedBooleanLiteral_IsNotTrivial()
    {
        var source = WrapMember("public bool Compute() => Value > 0 && /*!*/false;");

        var verdict = ClassifyBooleanLiteral(source);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    /// <summary>
    /// A call through a <see langword="dynamic"/> receiver is late-bound: the semantic model reports no method
    /// symbol for it at all, since resolution only happens at run time. The check must treat that
    /// exactly like any other invocation it cannot resolve, not assume it is <c>ConfigureAwait</c>.
    /// </summary>
    [Test]
    public async Task Classify_MutationOfConfigureAwaitArgumentOnDynamicReceiver_IsNotTrivial()
    {
        const string source = """
            namespace Fixture;

            public sealed class Widget
            {
                public dynamic Compute(dynamic awaitable) => awaitable.ConfigureAwait(/*!*/false);
            }
            """;

        var verdict = ClassifyBooleanLiteral(source);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_BehaviourChangingMutationInNormalMethod_IsNotTrivial()
    {
        var source = WrapMember("public int Add(int left, int right) => /*!*/left + right;");

        var verdict = ClassifyBinary(source, SyntaxKind.SubtractExpression);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutatedLiteralChangesTheConstant_IsNotTrivial()
    {
        var source = WrapMember("public int Compute() => Value > /*!*/1 ? 2 : 3;");

        var verdict = ClassifyLiteral(source);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutantWouldDivideByZero_IsNotTrivial()
    {
        // The original folds to zero, but 5 / 0 throws at run time, therefore the mutant is observable
        // and must never be folded away.
        var source = WrapMember("public int Compute() => /*!*/5 * 0;");

        var verdict = ClassifyBinary(source, SyntaxKind.DivideExpression);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutantWouldOverflowTheFold_IsNotTrivial()
    {
        // 2000000000 + 2000000000 does not fit into an int, so nothing can be proven about the mutant.
        var source = WrapMember("public int Compute() => /*!*/2000000000 - 2000000000;");

        var verdict = ClassifyBinary(source, SyntaxKind.AddExpression);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_MutatedValueFlowsIntoAnInvocation_IsNotTrivial()
    {
        var source = WrapStatements("return (/*!*/left + right).GetHashCode();");

        var verdict = ClassifyBinary(source, SyntaxKind.SubtractExpression);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_NodeBelongsToAnotherTree_IsNotTrivial()
    {
        var source = WrapMember("public int Compute() => /*!*/1 * 1;");
        var (_, model, _) = CompilationFactory.CreateWithModel(source);
        var foreign = CompilationFactory.ParseTree(source, filePath: "Foreign.cs");
        var original = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(foreign);
        var mutation = CreateMutation(original, Swap(original, SyntaxKind.DivideExpression));

        var verdict = EquivalenceClassifier.Classify(mutation, model, CancellationToken.None);

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
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

        return EquivalenceClassifier.Classify(CreateMutation(original, replacement), model, CancellationToken.None);
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

        return EquivalenceClassifier.Classify(
            CreateMutation(original, Swap(original, replacementKind)),
            model,
            CancellationToken.None
        );
    }

    /// <summary>
    /// Classifies a mutation replacing the marked boolean literal with its opposite.
    /// </summary>
    /// <param name="source">The fixture source containing the marker.</param>
    /// <returns>The verdict.</returns>
    private static EquivalenceVerdict ClassifyBooleanLiteral(string source)
    {
        var (_, model, tree) = CompilationFactory.CreateWithModel(source);
        var original = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        var replacementKind = original.IsKind(SyntaxKind.TrueLiteralExpression)
            ? SyntaxKind.FalseLiteralExpression
            : SyntaxKind.TrueLiteralExpression;
        var replacement = SyntaxFactory.LiteralExpression(replacementKind);

        return EquivalenceClassifier.Classify(
            new Mutation(MutationKind.BooleanLiteral, "fixture.mutation", "fixture mutation", original, replacement),
            model,
            CancellationToken.None
        );
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
    /// Wraps a member declaration that awaits a <c>Task</c> or <c>ValueTask</c> parameter, giving the
    /// <c>ConfigureAwait</c> check something to resolve against.
    /// </summary>
    /// <param name="member">The member declaration, containing the marker.</param>
    /// <returns>The fixture source.</returns>
    private static string WrapTaskMember(string member) =>
        $$"""
            namespace Fixture;

            using System.Threading.Tasks;

            public sealed class Widget
            {
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
}
