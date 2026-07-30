namespace NetEvolve.Frameshift.Tests.Unit.Mutations.Operators;

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
/// Covers the conditional expression operator: the branch swap, the condition negation including the
/// removal of an existing negation and the required parentheses, and the equivalent branch guard.
/// </summary>
public class ConditionalExpressionMutatorTests
{
    private const string TernarySource = "public class Sample { public int Get(bool flag) => flag ? 1 : 2; }";
    private const string NegatedSource = "public class Sample { public int Get(bool flag) => !flag ? 1 : 2; }";
    private const string EquivalentBranchSource = "public class Sample { public int Get(bool flag) => flag ? 1 : 1; }";
    private const string ComparisonSource = "public class Sample { public int Get(int a, int b) => a == b ? 1 : 2; }";

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

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source) => Run(source, FindConditional);

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source, Func<SyntaxTree, SyntaxNode> select)
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var mutator = new ConditionalExpressionMutator();

        return (tree, [.. mutator.CreateMutations(select(tree), semanticModel, CancellationToken.None)]);
    }

    private static string Rewrite(SyntaxTree tree, Mutation mutation) => mutation.ApplyTo(tree).ToString();

    private static SyntaxNode FindConditional(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<ConditionalExpressionSyntax>(tree);

    private static SyntaxNode FindNumericLiteral(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(
            tree,
            static literal => literal.IsKind(SyntaxKind.NumericLiteralExpression)
        );
}
