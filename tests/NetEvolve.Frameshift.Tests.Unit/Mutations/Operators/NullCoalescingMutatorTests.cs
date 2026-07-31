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

    /// <summary>
    /// The two operands have nothing in common, so the whole expression has no type. The fixture
    /// deliberately does not compile, and the <c>var</c> initializer keeps the expression from being
    /// converted to anything, which is the only way to reach the unknown target type.
    /// </summary>
    private const string NoCommonTypeSource = """
        internal static class Sample
        {
            public static void Get(int? a, string b)
            {
                var value = a ?? b;
            }
        }
        """;

    /// <summary>
    /// The coalesce expression is a <c>Cents</c> and the expected type is a <c>Euro</c>, which is two
    /// user-defined conversions away. C# never chains two of them, so neither operand converts to the
    /// expected type and the fixture deliberately does not compile.
    /// </summary>
    private const string NoConversionSource = """
        internal readonly struct Cents
        {
            public static implicit operator Money(Cents value) => default;
        }

        internal readonly struct Money
        {
            public static implicit operator Euro(Money value) => default;
        }

        internal readonly struct Euro
        {
        }

        internal static class Sample
        {
            public static Euro Get(Cents? a, Cents b) => a ?? b;
        }
        """;

    /// <summary>
    /// A non-nullable value type on the left makes the whole expression meaningless, so the fixture
    /// deliberately does not compile.
    /// </summary>
    private const string NonNullableValueLeftSource = """
        internal static class Sample
        {
            public static int Get(int a, int b) => a ?? b;
        }
        """;

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

    /// <summary>
    /// Without a known target type there is nothing the replacement could fail to convert to, so both
    /// operands are offered and the decision is left to the later compilation viability check.
    /// </summary>
    [Test]
    public async Task CreateMutations_UnknownTargetType_KeepsLeftAndRight()
    {
        var (semanticModel, tree, mutations) = RunWithModel(NoCommonTypeSource);
        var coalesce = (BinaryExpressionSyntax)FindCoalesce(tree);
        var targetType = semanticModel.GetTypeInfo(coalesce).ConvertedType;

        _ = await Assert.That(targetType is null || targetType.TypeKind == TypeKind.Error).IsTrue();
        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("null-coalescing.keep-left");
        _ = await Assert.That(mutations[1].OperatorId).IsEqualTo("null-coalescing.keep-right");
    }

    /// <summary>
    /// Neither operand converts to the expected type on its own, so no replacement is offered at all.
    /// </summary>
    [Test]
    public async Task CreateMutations_OperandsWithoutAConversionToTheTargetType_ReturnsEmpty()
    {
        var (semanticModel, tree, mutations) = RunWithModel(NoConversionSource);
        var coalesce = (BinaryExpressionSyntax)FindCoalesce(tree);
        var targetType = semanticModel.GetTypeInfo(coalesce).ConvertedType!;

        _ = await Assert.That(targetType.Name).IsEqualTo("Euro");
        _ = await Assert.That(semanticModel.ClassifyConversion(coalesce.Left, targetType).Exists).IsFalse();
        _ = await Assert.That(semanticModel.ClassifyConversion(coalesce.Right, targetType).Exists).IsFalse();
        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// A non-nullable value type on the left is a programming error rather than a mutation target, but the
    /// operator works on what the tree says and offers both operands.
    /// </summary>
    [Test]
    public async Task CreateMutations_NonNullableValueTypeOnTheLeft_KeepsLeftAndRight()
    {
        var (tree, mutations) = Run(NonNullableValueLeftSource);

        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("null-coalescing.keep-left");
        _ = await Assert.That(mutations[1].OperatorId).IsEqualTo("null-coalescing.keep-right");
        _ = await Assert
            .That(Rewrite(tree, mutations[0]))
            .IsEqualTo(NonNullableValueLeftSource.Replace("a ?? b", "a", StringComparison.Ordinal));
        _ = await Assert
            .That(Rewrite(tree, mutations[1]))
            .IsEqualTo(NonNullableValueLeftSource.Replace("a ?? b", "b", StringComparison.Ordinal));
    }

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source) => Run(source, FindCoalesce);

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source, Func<SyntaxTree, SyntaxNode> select)
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var mutator = new NullCoalescingMutator();

        return (tree, [.. mutator.CreateMutations(select(tree), semanticModel, CancellationToken.None)]);
    }

    private static (SemanticModel SemanticModel, SyntaxTree Tree, Mutation[] Mutations) RunWithModel(string source)
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var mutator = new NullCoalescingMutator();
        var node = FindCoalesce(tree);

        return (semanticModel, tree, [.. mutator.CreateMutations(node, semanticModel, CancellationToken.None)]);
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
