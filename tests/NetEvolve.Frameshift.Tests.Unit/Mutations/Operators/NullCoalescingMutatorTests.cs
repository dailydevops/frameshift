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
/// Covers the null-coalescing operator: both operands replace the whole expression, unless the
/// replacement would not convert to the expected type or would be a <c>throw</c> expression.
/// </summary>
public class NullCoalescingMutatorTests
{
    private const string ReferenceSource = "public class Sample { public string Get(string? a, string b) => a ?? b; }";
    private const string ThrowSource =
        "public class Sample { public string Get(string? a) => a ?? throw new System.InvalidOperationException(); }";
    private const string NullableValueSource = "public class Sample { public int Get(int? a) => a ?? 0; }";
    private const string CoalesceAssignmentSource =
        "public class Sample { public string? Field; public void Set(string b) { Field ??= b; } }";

    [Test]
    public async Task Metadata_Operator_DescribesNullCoalescingFamily()
    {
        var mutator = new NullCoalescingMutator();
        SyntaxKind[] supported = [.. mutator.SupportedSyntaxKinds];

        _ = await Assert.That(mutator.Id).IsEqualTo("null-coalescing");
        _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.NullCoalescing);
        _ = await Assert.That(supported).Count().IsEqualTo(1);
        _ = await Assert.That(supported).Contains(SyntaxKind.CoalesceExpression);
    }

    [Test]
    public async Task CreateMutations_ReferenceOperands_KeepsLeftAndRight()
    {
        var (tree, mutations) = Run(ReferenceSource);

        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(mutations[0].Kind).IsEqualTo(MutationKind.NullCoalescing);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("null-coalescing.keep-left");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("a ?? b => a");
        _ = await Assert.That(mutations[1].OperatorId).IsEqualTo("null-coalescing.keep-right");
        _ = await Assert.That(mutations[1].DisplayName).IsEqualTo("a ?? b => b");
        _ = await Assert
            .That(Rewrite(tree, mutations[0]))
            .IsEqualTo("public class Sample { public string Get(string? a, string b) => a; }");
        _ = await Assert
            .That(Rewrite(tree, mutations[1]))
            .IsEqualTo("public class Sample { public string Get(string? a, string b) => b; }");
    }

    [Test]
    public async Task CreateMutations_ThrowExpressionOnTheRight_KeepsOnlyLeft()
    {
        var (tree, mutations) = Run(ThrowSource);

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("null-coalescing.keep-left");
        _ = await Assert
            .That(Rewrite(tree, mutations[0]))
            .IsEqualTo("public class Sample { public string Get(string? a) => a; }");
    }

    [Test]
    public async Task CreateMutations_NullableValueTypeOnTheLeft_KeepsOnlyRight()
    {
        var (tree, mutations) = Run(NullableValueSource);

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("null-coalescing.keep-right");
        _ = await Assert
            .That(Rewrite(tree, mutations[0]))
            .IsEqualTo("public class Sample { public int Get(int? a) => 0; }");
    }

    [Test]
    public async Task CreateMutations_CoalesceAssignment_ReturnsEmpty()
    {
        var (_, mutations) = Run(CoalesceAssignmentSource, FindAssignment);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_UnsupportedSyntaxKind_ReturnsEmpty()
    {
        var (_, mutations) = Run("public class Sample { public int Get(int a, int b) => a + b; }", FindAddition);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_MutatedNode_IsTheWholeCoalesceExpression()
    {
        var (tree, mutations) = Run(ReferenceSource);
        var coalesce = FindCoalesce(tree);

        _ = await Assert.That(mutations[0].Original.ToString()).IsEqualTo("a ?? b");
        _ = await Assert.That(mutations[0].Original.Span).IsEqualTo(coalesce.Span);
        _ = await Assert.That(mutations[0].Location.SourceSpan).IsEqualTo(coalesce.Span);
    }

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source) => Run(source, FindCoalesce);

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source, Func<SyntaxTree, SyntaxNode> select)
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var mutator = new NullCoalescingMutator();

        return (tree, [.. mutator.CreateMutations(select(tree), semanticModel, CancellationToken.None)]);
    }

    private static string Rewrite(SyntaxTree tree, Mutation mutation) => mutation.ApplyTo(tree).ToString();

    private static SyntaxNode FindCoalesce(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<BinaryExpressionSyntax>(
            tree,
            static binary => binary.IsKind(SyntaxKind.CoalesceExpression)
        );

    private static SyntaxNode FindAddition(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<BinaryExpressionSyntax>(
            tree,
            static binary => binary.IsKind(SyntaxKind.AddExpression)
        );

    private static SyntaxNode FindAssignment(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<AssignmentExpressionSyntax>(tree);
}
