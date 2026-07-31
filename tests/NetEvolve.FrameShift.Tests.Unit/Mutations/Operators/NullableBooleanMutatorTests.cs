namespace NetEvolve.FrameShift.Tests.Unit.Mutations.Operators;

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
/// Covers the nullable boolean literal operator: the mutations produced per source state, the rewritten
/// source, and every position it must leave alone, which is a plain <c>bool</c>, a constant context, a
/// <see langword="null" /> on a reference type and a <see langword="default" /> written in place of a
/// literal.
/// </summary>
public class NullableBooleanMutatorTests
{
    private const string TrueSource = "public class Sample { public bool? Get() => true; }";
    private const string FalseSource = "public class Sample { public bool? Get() => false; }";
    private const string NullSource = "public class Sample { public bool? Get() => null; }";
    private const string PlainBooleanSource = "public class Sample { public bool Get() => true; }";
    private const string ReferenceTypeNullSource = "public class Sample { public string Get() => null; }";
    private const string DefaultLiteralSource = "public class Sample { public bool? Get() => default; }";
    private const string ConstantPatternSource = "public class Sample { public bool Get(bool? flag) => flag is true; }";
    private const string DefaultParameterSource =
        "public class Sample { public bool? Get(bool? flag = true) => flag; }";
    private const string FieldInitializerSource =
        "public class Sample { private bool? _flag = false; public bool? Get() => _flag; }";
    private const string LiftedComparisonSource =
        "public class Sample { public bool Get(bool? flag) => flag == true; }";

    private const string TriviaSource = """
        public class Sample
        {
            public bool? Get() =>
                // the third state is the interesting one
                true;
        }
        """;

    [Test]
    public async Task Metadata_Operator_DescribesNullableBooleanLiteralFamily()
    {
        var mutator = new NullableBooleanMutator();
        SyntaxKind[] supported = [.. mutator.SupportedSyntaxKinds];

        _ = await Assert.That(mutator.Id).IsEqualTo("nullable-boolean");
        _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.NullableBooleanLiteral);
        _ = await Assert.That(supported).Count().IsEqualTo(3);
        _ = await Assert.That(supported).Contains(SyntaxKind.TrueLiteralExpression);
        _ = await Assert.That(supported).Contains(SyntaxKind.FalseLiteralExpression);
        _ = await Assert.That(supported).Contains(SyntaxKind.NullLiteralExpression);
    }

    [Test]
    public async Task CreateMutations_TrueLiteral_ReplacesItByNull()
    {
        var (tree, mutations) = Run(TrueSource);

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].Kind).IsEqualTo(MutationKind.NullableBooleanLiteral);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("nullable-boolean.true-to-null");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("true => null");
        _ = await Assert.That(mutations[0].Replacement.IsKind(SyntaxKind.NullLiteralExpression)).IsTrue();
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(NullSource);
    }

    [Test]
    public async Task CreateMutations_FalseLiteral_ReplacesItByNull()
    {
        var (tree, mutations) = Run(FalseSource);

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("nullable-boolean.false-to-null");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("false => null");
        _ = await Assert.That(mutations[0].Replacement.IsKind(SyntaxKind.NullLiteralExpression)).IsTrue();
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(NullSource);
    }

    [Test]
    public async Task CreateMutations_NullLiteral_ReplacesItByBothBooleanStates()
    {
        var (tree, mutations) = Run(NullSource);

        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("nullable-boolean.null-to-true");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("null => true");
        _ = await Assert.That(mutations[0].Replacement.IsKind(SyntaxKind.TrueLiteralExpression)).IsTrue();
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(TrueSource);
        _ = await Assert.That(mutations[1].OperatorId).IsEqualTo("nullable-boolean.null-to-false");
        _ = await Assert.That(mutations[1].DisplayName).IsEqualTo("null => false");
        _ = await Assert.That(mutations[1].Replacement.IsKind(SyntaxKind.FalseLiteralExpression)).IsTrue();
        _ = await Assert.That(Rewrite(tree, mutations[1])).IsEqualTo(FalseSource);
    }

    [Test]
    public async Task CreateMutations_NullableFieldInitializer_IsMutated()
    {
        var expected = FieldInitializerSource.Replace("= false", "= null", StringComparison.Ordinal);
        var (tree, mutations) = Run(FieldInitializerSource);

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("nullable-boolean.false-to-null");
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
    }

    /// <summary>
    /// In <c>flag == true</c> with a <c>bool?</c> operand the comparison is lifted, so the literal is
    /// converted to <c>bool?</c> even though it is spelled exactly like a plain boolean literal. That is
    /// the position the three valued logic actually shows up in.
    /// </summary>
    [Test]
    public async Task CreateMutations_LiftedComparison_IsMutated()
    {
        var expected = LiftedComparisonSource.Replace("== true", "== null", StringComparison.Ordinal);
        var (tree, mutations) = Run(LiftedComparisonSource);

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("nullable-boolean.true-to-null");
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
    }

    /// <summary>
    /// The replacement is a bare literal without trivia of its own, so the comment and the line breaks
    /// around the original literal have to survive the rewrite.
    /// </summary>
    [Test]
    public async Task CreateMutations_LiteralWithTrivia_KeepsTheSurroundingTrivia()
    {
        var expected = TriviaSource.Replace("true;", "null;", StringComparison.Ordinal);
        var (tree, mutations) = Run(TriviaSource);

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
    }

    [Test]
    public async Task CreateMutations_PlainBoolean_ReturnsEmpty()
    {
        var (_, mutations) = Run(PlainBooleanSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ReferenceTypeNull_ReturnsEmpty()
    {
        var (_, mutations) = Run(ReferenceTypeNullSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// A <see langword="default" /> expression is not a literal of one of the supported kinds, so the
    /// operator has nothing to offer for it even though its type is <c>bool?</c>.
    /// </summary>
    [Test]
    public async Task CreateMutations_DefaultLiteral_ReturnsEmpty()
    {
        var (_, mutations) = Run(DefaultLiteralSource, FindDefaultLiteral);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ConstantPattern_ReturnsEmpty()
    {
        var (_, mutations) = Run(ConstantPatternSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_DefaultParameterValue_ReturnsEmpty()
    {
        var (_, mutations) = Run(DefaultParameterSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_UnsupportedSyntaxKind_ReturnsEmpty()
    {
        var (_, mutations) = Run("public class Sample { public int Get() => 42; }", FindNumericLiteral);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_NodeNull_ThrowsArgumentNullException()
    {
        var (_, semanticModel, _) = CompilationFactory.CreateWithModel(TrueSource);
        var mutator = new NullableBooleanMutator();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(null!, semanticModel, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("node");
    }

    [Test]
    public async Task CreateMutations_SemanticModelNull_ThrowsArgumentNullException()
    {
        var (_, _, tree) = CompilationFactory.CreateWithModel(TrueSource);
        var mutator = new NullableBooleanMutator();
        var node = FindNullableBooleanLiteral(tree);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(node, null!, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("semanticModel");
    }

    [Test]
    public async Task CreateMutations_CancelledToken_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(TrueSource);
        var mutator = new NullableBooleanMutator();
        var node = FindNullableBooleanLiteral(tree);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = mutator.CreateMutations(node, semanticModel, cancellation.Token).ToList()
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source) =>
        Run(source, FindNullableBooleanLiteral);

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source, Func<SyntaxTree, SyntaxNode> select)
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var mutator = new NullableBooleanMutator();

        return (tree, [.. mutator.CreateMutations(select(tree), semanticModel, CancellationToken.None)]);
    }

    private static string Rewrite(SyntaxTree tree, Mutation mutation) => mutation.ApplyTo(tree).ToString();

    private static SyntaxNode FindNullableBooleanLiteral(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(
            tree,
            static literal =>
                literal.IsKind(SyntaxKind.TrueLiteralExpression)
                || literal.IsKind(SyntaxKind.FalseLiteralExpression)
                || literal.IsKind(SyntaxKind.NullLiteralExpression)
        );

    private static SyntaxNode FindDefaultLiteral(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(
            tree,
            static literal => literal.IsKind(SyntaxKind.DefaultLiteralExpression)
        );

    private static SyntaxNode FindNumericLiteral(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(
            tree,
            static literal => literal.IsKind(SyntaxKind.NumericLiteralExpression)
        );
}
