namespace NetEvolve.FrameShift.Tests.Unit.Mutations.Operators;

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
/// Covers the conditional expression operator: the branch swap, the condition negation including the
/// removal of an existing negation and the required parentheses, and the equivalent branch guard.
/// </summary>
public class ConditionalExpressionMutatorTests
{
    private const string TernarySource = "public class Sample { public int Get(bool flag) => flag ? 1 : 2; }";
    private const string NegatedSource = "public class Sample { public int Get(bool flag) => !flag ? 1 : 2; }";
    private const string EquivalentBranchSource = "public class Sample { public int Get(bool flag) => flag ? 1 : 1; }";
    private const string ComparisonSource = "public class Sample { public int Get(int a, int b) => a == b ? 1 : 2; }";

    private const string MemberAccessSource = """
        internal sealed class Holder
        {
            public bool Flag => true;
        }

        internal static class Sample
        {
            public static int Get(Holder holder) => holder.Flag ? 1 : 2;
        }
        """;

    private const string InvocationSource = """
        internal static class Sample
        {
            public static bool Check() => true;

            public static int Get() => Check() ? 1 : 2;
        }
        """;

    private const string ElementAccessSource = """
        internal static class Sample
        {
            public static int Get(bool[] flags) => flags[0] ? 1 : 2;
        }
        """;

    private const string ParenthesizedConditionSource = """
        internal static class Sample
        {
            public static int Get(bool flag) => (flag) ? 1 : 2;
        }
        """;

    private const string TrueLiteralSource = """
        internal static class Sample
        {
            public static int Get() => true ? 1 : 2;
        }
        """;

    private const string FalseLiteralSource = """
        internal static class Sample
        {
            public static int Get() => false ? 1 : 2;
        }
        """;

    private const string ThisSource = """
        internal sealed class Sample
        {
            public static bool operator true(Sample value) => true;

            public static bool operator false(Sample value) => false;

            public int Get() => this ? 1 : 2;
        }
        """;

    private const string BaseSource = """
        internal class Flagged
        {
            public static bool operator true(Flagged value) => true;

            public static bool operator false(Flagged value) => false;
        }

        internal sealed class Sample : Flagged
        {
            public int Get() => base ? 1 : 2;
        }
        """;

    private const string SuppressNullableWarningSource = """
        internal static class Sample
        {
            public static int Get(bool flag) => flag! ? 1 : 2;
        }
        """;

    private const string ConditionalAccessSource = """
        internal sealed class Node
        {
            public Node Next => this;

            public static bool operator true(Node value) => true;

            public static bool operator false(Node value) => false;
        }

        internal static class Sample
        {
            public static int Get(Node node) => node?.Next ? 1 : 2;
        }
        """;

    private const string PointerMemberAccessSource = """
        internal struct Holder
        {
            public bool Flag;
        }

        internal static class Sample
        {
            public static unsafe int Get(Holder* holder) => holder->Flag ? 1 : 2;
        }
        """;

    private const string LogicalAndSource = """
        internal static class Sample
        {
            public static int Get(bool a, bool b) => a && b ? 1 : 2;
        }
        """;

    private const string IsPatternSource = """
        internal static class Sample
        {
            public static int Get(object? value) => value is not null ? 1 : 2;
        }
        """;

    private const string CastSource = """
        internal static class Sample
        {
            public static int Get(object value) => (bool)value ? 1 : 2;
        }
        """;

    private const string NegatedComparisonSource = """
        internal static class Sample
        {
            public static int Get(int a, int b) => !(a == b) ? 1 : 2;
        }
        """;

    private const string ArgumentSource = """
        internal static class Sample
        {
            public static int Get(bool flag) => System.Math.Abs(flag ? 1 : 2);
        }
        """;

    private const string ReturnSource = """
        internal static class Sample
        {
            public static int Get(bool flag)
            {
                return flag ? 1 : 2;
            }
        }
        """;

    private const string LambdaSource = """
        internal static class Sample
        {
            public static System.Func<int> Get(bool flag) => () => flag ? 1 : 2;
        }
        """;

    private const string InitializerSource = """
        internal sealed class Sample
        {
            public int Value { get; set; }

            public static Sample Create(bool flag) => new Sample { Value = flag ? 1 : 2 };
        }
        """;

    private const string CommonBaseSource = """
        internal class Animal { }

        internal sealed class Dog : Animal { }

        internal static class Sample
        {
            public static Animal Get(bool flag, Dog dog, Animal animal) => flag ? dog : animal;
        }
        """;

    private const string NoCommonTypeSource = """
        internal readonly struct Money
        {
            public static implicit operator Money(int amount) => default;

            public static implicit operator Money(string amount) => default;
        }

        internal static class Sample
        {
            public static Money Get(bool flag) => flag ? 1 : "one";
        }
        """;

    private const string RefConditionalSource = """
        internal static class Sample
        {
            public static ref int Get(bool flag, ref int a, ref int b) => ref flag ? ref a : ref b;
        }
        """;

    private const string ThrowBranchesSource = """
        internal static class Sample
        {
            public static int Get(bool flag) =>
                flag ? throw new System.NotSupportedException("a") : throw new System.NotSupportedException("b");
        }
        """;

    private const string ThrowBranchesSwappedSource = """
        internal static class Sample
        {
            public static int Get(bool flag) =>
                flag ? throw new System.NotSupportedException("b") : throw new System.NotSupportedException("a");
        }
        """;

    private const string CommentEquivalentSource = """
        internal static class Sample
        {
            public static int Get(bool flag) => flag ? 1 : /* the very same value */ 1;
        }
        """;

    private const string LayoutEquivalentSource = """
        internal static class Sample
        {
            public static int Get(bool flag) =>
                flag
                    ? 1
                    : 1;
        }
        """;

    private const string ParenthesizedBranchSource = """
        internal static class Sample
        {
            public static int Get(bool flag, int value) => flag ? value : (value);
        }
        """;

    private const string NestedConditionSource = """
        internal static class Sample
        {
            public static int Get(bool a, bool b) => (a ? b : !b) ? 1 : 2;
        }
        """;

    private const string NestedTrueBranchSource = """
        internal static class Sample
        {
            public static int Get(bool a, bool b) => a ? (b ? 1 : 2) : 3;
        }
        """;

    private const string NestedFalseBranchSource = """
        internal static class Sample
        {
            public static int Get(bool a, bool b) => a ? 1 : b ? 2 : 3;
        }
        """;

    private const string MarkedInnerConditionalSource = """
        internal static class Sample
        {
            public static int Get(bool a, bool b) => a ? 1 : /*!*/b ? 2 : 3;
        }
        """;

    private const string CommentedSource = """
        internal static class Sample
        {
            public static int Get(bool flag) =>
                // decide
                flag
                    ? 1 // when true
                    : 2; // when false
        }
        """;

    [Test]
    public async Task Metadata_Operator_DescribesConditionalExpressionFamily()
    {
        var mutator = new ConditionalExpressionMutator();
        SyntaxKind[] supported = [.. mutator.SupportedSyntaxKinds];

        _ = await Assert.That(mutator.Id).IsEqualTo("conditional-expression");
        _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.ConditionalExpression);
        _ = await Assert.That(supported).Count().IsEqualTo(1);
        _ = await Assert.That(supported).Contains(SyntaxKind.ConditionalExpression);
    }

    [Test]
    public async Task CreateMutations_Ternary_SwapsBranchesAndNegatesTheCondition()
    {
        var (_, mutations) = Run(TernarySource);

        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(mutations[0].Kind).IsEqualTo(MutationKind.ConditionalExpression);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("conditional-expression.swap-branches");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("c ? a : b => c ? b : a");
        _ = await Assert.That(mutations[1].OperatorId).IsEqualTo("conditional-expression.negate-condition");
        _ = await Assert.That(mutations[1].DisplayName).IsEqualTo("c ? a : b => !c ? a : b");
    }

    [Test]
    public async Task CreateMutations_Ternary_RewritesTheSwappedBranches()
    {
        var (tree, mutations) = Run(TernarySource);

        _ = await Assert
            .That(Rewrite(tree, mutations[0]))
            .IsEqualTo("public class Sample { public int Get(bool flag) => flag ? 2 : 1; }");
    }

    [Test]
    public async Task CreateMutations_Ternary_RewritesTheNegatedCondition()
    {
        var (tree, mutations) = Run(TernarySource);

        _ = await Assert.That(Rewrite(tree, mutations[1])).IsEqualTo(NegatedSource);
    }

    [Test]
    public async Task CreateMutations_NegatedCondition_RemovesTheNegation()
    {
        var (tree, mutations) = Run(NegatedSource);

        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(mutations[1].OperatorId).IsEqualTo("conditional-expression.negate-condition");
        _ = await Assert.That(Rewrite(tree, mutations[1])).IsEqualTo(TernarySource);
    }

    [Test]
    public async Task CreateMutations_ComparisonCondition_ParenthesizesTheNegatedCondition()
    {
        var (_, mutations) = Run(ComparisonSource);
        var negated = (ConditionalExpressionSyntax)mutations[1].Replacement;
        var negation = (PrefixUnaryExpressionSyntax)negated.Condition;
        var parenthesized = (ParenthesizedExpressionSyntax)negation.Operand;

        _ = await Assert.That(negation.IsKind(SyntaxKind.LogicalNotExpression)).IsTrue();
        _ = await Assert.That(parenthesized.Expression.ToString().Trim()).IsEqualTo("a == b");
    }

    [Test]
    public async Task CreateMutations_ComparisonCondition_KeepsTheBranchesOfTheSwap()
    {
        var (tree, mutations) = Run(ComparisonSource);

        _ = await Assert
            .That(Rewrite(tree, mutations[0]))
            .IsEqualTo("public class Sample { public int Get(int a, int b) => a == b ? 2 : 1; }");
    }

    [Test]
    public async Task CreateMutations_EquivalentBranches_ReturnsEmpty()
    {
        var (_, mutations) = Run(EquivalentBranchSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_UnsupportedSyntaxKind_ReturnsEmpty()
    {
        var (_, mutations) = Run(TernarySource, FindNumericLiteral);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_MutatedNode_IsTheWholeConditionalExpression()
    {
        var (tree, mutations) = Run(TernarySource);
        var conditional = FindConditional(tree);

        _ = await Assert.That(mutations[0].Original.ToString()).IsEqualTo("flag ? 1 : 2");
        _ = await Assert.That(mutations[0].Location.SourceSpan).IsEqualTo(conditional.Span);
    }

    /// <summary>
    /// Pins the conditions the negation is allowed to prefix directly. The fixtures using
    /// <see langword="base" /> and a pointer member access do not compile, because neither expression is
    /// legal in the position the fixture puts it in, and both are only reachable syntactically.
    /// </summary>
    /// <param name="source">The fixture source.</param>
    /// <param name="expected">The condition the negation has to produce.</param>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    [Arguments(MemberAccessSource, "!holder.Flag")]
    [Arguments(InvocationSource, "!Check()")]
    [Arguments(ElementAccessSource, "!flags[0]")]
    [Arguments(ParenthesizedConditionSource, "!(flag)")]
    [Arguments(TrueLiteralSource, "!true")]
    [Arguments(FalseLiteralSource, "!false")]
    [Arguments(ThisSource, "!this")]
    [Arguments(BaseSource, "!base")]
    [Arguments(SuppressNullableWarningSource, "!flag!")]
    [Arguments(ConditionalAccessSource, "!node?.Next")]
    [Arguments(PointerMemberAccessSource, "!holder->Flag")]
    public async Task CreateMutations_ConditionNeedingNoParentheses_NegatesItInPlace(string source, string expected)
    {
        ArgumentNullException.ThrowIfNull(source);

        var (_, mutations) = Run(source);

        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(mutations[1].OperatorId).IsEqualTo("conditional-expression.negate-condition");
        _ = await Assert.That(NegatedCondition(mutations[1])).IsEqualTo(expected);
    }

    [Test]
    [Arguments(LogicalAndSource, "!(a && b)")]
    [Arguments(IsPatternSource, "!(value is not null)")]
    [Arguments(CastSource, "!((bool)value)")]
    public async Task CreateMutations_ConditionNeedingParentheses_WrapsItBeforeNegating(string source, string expected)
    {
        ArgumentNullException.ThrowIfNull(source);

        var (_, mutations) = Run(source);

        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(NegatedCondition(mutations[1])).IsEqualTo(expected);
    }

    [Test]
    public async Task CreateMutations_NegatedComparison_UnwrapsTheNegationAndKeepsTheParentheses()
    {
        var (tree, mutations) = Run(NegatedComparisonSource);
        var expected = Expected(NegatedComparisonSource, "!(a == b)", "(a == b)");

        _ = await Assert.That(NegatedCondition(mutations[1])).IsEqualTo("(a == b)");
        _ = await Assert.That(Rewrite(tree, mutations[1])).IsEqualTo(expected);
    }

    [Test]
    [Arguments(ArgumentSource)]
    [Arguments(ReturnSource)]
    [Arguments(LambdaSource)]
    [Arguments(InitializerSource)]
    public async Task CreateMutations_ConditionalInAnyExpressionContext_SwapsTheBranchesInPlace(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var (tree, mutations) = Run(source);
        var expected = Expected(source, "flag ? 1 : 2", "flag ? 2 : 1");
        var mutated = Rewrite(tree, mutations[0]);

        _ = await Assert.That(CompileErrors(source)).IsEqualTo(string.Empty);
        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(mutated).IsEqualTo(expected);
        _ = await Assert.That(CompileErrors(mutated)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task CreateMutations_BranchesSharingACommonBase_SwapsThemAndStaysCompilable()
    {
        var (tree, mutations) = Run(CommonBaseSource);
        var expected = Expected(CommonBaseSource, "flag ? dog : animal", "flag ? animal : dog");
        var mutated = Rewrite(tree, mutations[0]);

        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(mutated).IsEqualTo(expected);
        _ = await Assert.That(CompileErrors(mutated)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task CreateMutations_BranchesWithoutACommonType_SwapsTheTargetTypedBranches()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(NoCommonTypeSource);
        var conditional = SyntaxNodeLocator.FindFirst<ConditionalExpressionSyntax>(tree);
        var typeInfo = semanticModel.GetTypeInfo(conditional);
        var mutator = new ConditionalExpressionMutator();
        Mutation[] mutations = [.. mutator.CreateMutations(conditional, semanticModel, CancellationToken.None)];
        var expected = Expected(NoCommonTypeSource, "flag ? 1 : \"one\"", "flag ? \"one\" : 1");
        var mutated = Rewrite(tree, mutations[0]);

        _ = await Assert.That(typeInfo.Type).IsNull();
        _ = await Assert.That(typeInfo.ConvertedType?.ToDisplayString()).IsEqualTo("Money");
        _ = await Assert.That(mutated).IsEqualTo(expected);
        _ = await Assert.That(CompileErrors(mutated)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task CreateMutations_RefConditional_SwapsTheRefBranches()
    {
        var (tree, mutations) = Run(RefConditionalSource);
        var expected = Expected(RefConditionalSource, "ref flag ? ref a : ref b", "ref flag ? ref b : ref a");
        var mutated = Rewrite(tree, mutations[0]);

        _ = await Assert.That(CompileErrors(RefConditionalSource)).IsEqualTo(string.Empty);
        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(mutated).IsEqualTo(expected);
        _ = await Assert.That(CompileErrors(mutated)).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// A conditional whose two branches both throw has no type, so the fixture does not compile. The
    /// mutator works on syntax alone and still has to swap the two throw expressions.
    /// </summary>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    public async Task CreateMutations_ThrowBranches_SwapsTheThrowExpressions()
    {
        var (tree, mutations) = Run(ThrowBranchesSource);

        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(ThrowBranchesSwappedSource);
    }

    [Test]
    [Arguments(CommentEquivalentSource)]
    [Arguments(LayoutEquivalentSource)]
    public async Task CreateMutations_BranchesDifferingOnlyInTrivia_ReturnsEmpty(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var (tree, mutations) = Run(source);
        var conditional = (ConditionalExpressionSyntax)FindConditional(tree);

        _ = await Assert.That(conditional.WhenTrue.ToFullString()).IsNotEqualTo(conditional.WhenFalse.ToFullString());
        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ParenthesizedBranch_IsNotEquivalentToItsBareCounterpart()
    {
        var (tree, mutations) = Run(ParenthesizedBranchSource);
        var expected = Expected(ParenthesizedBranchSource, "flag ? value : (value)", "flag ? (value) : value");

        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
    }

    [Test]
    public async Task CreateMutations_NestedConditionalInTheCondition_MutatesTheOuterConditional()
    {
        var (tree, mutations) = Run(NestedConditionSource);
        var swapped = Expected(NestedConditionSource, "(a ? b : !b) ? 1 : 2", "(a ? b : !b) ? 2 : 1");
        var negated = Expected(NestedConditionSource, "(a ? b : !b) ? 1 : 2", "!(a ? b : !b) ? 1 : 2");

        _ = await Assert.That(mutations[0].Original.ToString()).IsEqualTo("(a ? b : !b) ? 1 : 2");
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(swapped);
        _ = await Assert.That(Rewrite(tree, mutations[1])).IsEqualTo(negated);
    }

    [Test]
    public async Task CreateMutations_NestedConditionalInTheTrueBranch_SwapsTheWholeBranch()
    {
        var (tree, mutations) = Run(NestedTrueBranchSource);
        var expected = Expected(NestedTrueBranchSource, "a ? (b ? 1 : 2) : 3", "a ? 3 : (b ? 1 : 2)");

        _ = await Assert.That(mutations[0].Original.ToString()).IsEqualTo("a ? (b ? 1 : 2) : 3");
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
        _ = await Assert.That(CompileErrors(Rewrite(tree, mutations[0]))).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task CreateMutations_NestedConditionalInTheFalseBranch_SwapsTheWholeBranch()
    {
        var (tree, mutations) = Run(NestedFalseBranchSource);
        var expected = Expected(NestedFalseBranchSource, "a ? 1 : b ? 2 : 3", "a ? b ? 2 : 3 : 1");

        _ = await Assert.That(mutations[0].Original.ToString()).IsEqualTo("a ? 1 : b ? 2 : 3");
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
        _ = await Assert.That(CompileErrors(Rewrite(tree, mutations[0]))).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task CreateMutations_MarkedInnerConditional_LeavesTheOuterConditionalUntouched()
    {
        var (tree, mutations) = RunMarked(MarkedInnerConditionalSource);
        var expected = Expected(MarkedInnerConditionalSource, "/*!*/b ? 2 : 3", "/*!*/b ? 3 : 2");

        _ = await Assert.That(mutations[0].Original.ToString()).IsEqualTo("b ? 2 : 3");
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
    }

    [Test]
    public async Task CreateMutations_CommentedConditional_KeepsTheCommentsInPlaceWhenSwapping()
    {
        var (tree, mutations) = Run(CommentedSource);
        var expected = Expected(Expected(CommentedSource, "? 1 //", "? 2 //"), ": 2; //", ": 1; //");

        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
    }

    [Test]
    public async Task CreateMutations_CommentedConditional_KeepsTheCommentsInPlaceWhenNegating()
    {
        var (tree, mutations) = Run(CommentedSource);
        var expected = Expected(CommentedSource, "        flag", "        !flag");

        _ = await Assert.That(Rewrite(tree, mutations[1])).IsEqualTo(expected);
        _ = await Assert.That(CompileErrors(Rewrite(tree, mutations[1]))).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task CreateMutations_NodeNull_ThrowsArgumentNullException()
    {
        var (_, semanticModel, _) = CompilationFactory.CreateWithModel(TernarySource);
        var mutator = new ConditionalExpressionMutator();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(null!, semanticModel, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("node");
    }

    [Test]
    public async Task CreateMutations_SemanticModelNull_ThrowsArgumentNullException()
    {
        var (_, _, tree) = CompilationFactory.CreateWithModel(TernarySource);
        var mutator = new ConditionalExpressionMutator();
        var node = FindConditional(tree);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(node, null!, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("semanticModel");
    }

    [Test]
    public async Task CreateMutations_CancelledToken_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(TernarySource);
        var mutator = new ConditionalExpressionMutator();
        var node = FindConditional(tree);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = mutator.CreateMutations(node, semanticModel, cancellation.Token).ToList()
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    /// <summary>
    /// A member binding expression never becomes the condition of a conditional expression in parsed
    /// source, because the conditional access it belongs to always becomes the condition as a whole. The
    /// negation still has to accept it, which a constructed conditional proves.
    /// </summary>
    [Test]
    public async Task CreateMutations_MemberBindingCondition_NegatesItWithoutParentheses()
    {
        var (_, semanticModel, _) = CompilationFactory.CreateWithModel(TernarySource);
        var conditional = SyntaxFactory.ConditionalExpression(
            SyntaxFactory.MemberBindingExpression(SyntaxFactory.IdentifierName("Flag")),
            NumericLiteral(1),
            NumericLiteral(2)
        );
        var mutator = new ConditionalExpressionMutator();
        Mutation[] mutations = [.. mutator.CreateMutations(conditional, semanticModel, CancellationToken.None)];
        var swapped = (ConditionalExpressionSyntax)mutations[0].Replacement;

        _ = await Assert.That(conditional.Condition.Kind()).IsEqualTo(SyntaxKind.MemberBindingExpression);
        _ = await Assert.That(mutations.Length).IsEqualTo(2);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("conditional-expression.swap-branches");
        _ = await Assert.That(swapped.WhenTrue.ToString()).IsEqualTo("2");
        _ = await Assert.That(swapped.WhenFalse.ToString()).IsEqualTo("1");
        _ = await Assert.That(mutations[1].OperatorId).IsEqualTo("conditional-expression.negate-condition");
        _ = await Assert.That(NegatedCondition(mutations[1])).IsEqualTo("!.Flag");
    }

    private static LiteralExpressionSyntax NumericLiteral(int value) =>
        SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(value));

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source) => Run(source, FindConditional);

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source, Func<SyntaxTree, SyntaxNode> select)
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var mutator = new ConditionalExpressionMutator();

        return (tree, [.. mutator.CreateMutations(select(tree), semanticModel, CancellationToken.None)]);
    }

    private static (SyntaxTree Tree, Mutation[] Mutations) RunMarked(string source) =>
        Run(source, SyntaxNodeLocator.FindMarked<ConditionalExpressionSyntax>);

    private static string Rewrite(SyntaxTree tree, Mutation mutation) => mutation.ApplyTo(tree).ToString();

    /// <summary>
    /// Builds the source a mutation has to produce, by replacing the interesting part of a fixture.
    /// Everything outside <paramref name="original" /> has to survive a mutation unchanged.
    /// </summary>
    /// <param name="source">The fixture source.</param>
    /// <param name="original">The part of the fixture the mutation rewrites.</param>
    /// <param name="mutated">The text the mutation has to put in its place.</param>
    /// <returns>The expected mutated source.</returns>
    private static string Expected(string source, string original, string mutated) =>
        source.Replace(original, mutated, StringComparison.Ordinal);

    /// <summary>
    /// Returns the condition the negation produced, so that a test can pin the exact shape of the
    /// negated condition without spelling out the whole mutated source.
    /// </summary>
    /// <param name="mutation">The negation mutation.</param>
    /// <returns>The text of the negated condition.</returns>
    private static string NegatedCondition(Mutation mutation) =>
        ((ConditionalExpressionSyntax)mutation.Replacement).Condition.ToString().Trim();

    private static string CompileErrors(string source)
    {
        var compilation = CompilationFactory.Create(source);
        var errors = CompilationFactory.GetCompileErrors(compilation);

        return string.Join(", ", errors.Select(error => error.GetMessage(CultureInfo.InvariantCulture)));
    }

    private static SyntaxNode FindConditional(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<ConditionalExpressionSyntax>(tree);

    private static SyntaxNode FindNumericLiteral(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(
            tree,
            static literal => literal.IsKind(SyntaxKind.NumericLiteralExpression)
        );
}
