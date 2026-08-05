namespace NetEvolve.FrameShift.Tests.Unit.Mutations.Operators;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Mutations;
using NetEvolve.FrameShift.Mutations.Operators;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the boolean literal operator: the two produced mutations, the rewritten source and every
/// position that requires a compile time constant and therefore must not be mutated.
/// </summary>
public class BooleanLiteralMutatorTests
{
    private const string TrueSource = "public class Sample { public bool Get() => true; }";
    private const string FalseSource = "public class Sample { public bool Get() => false; }";
    private const string InstanceFieldSource =
        "public class Sample { private bool _flag = true; public bool Get() => _flag; }";
    private const string ConstFieldSource = "public class Sample { private const bool Flag = true; }";
    private const string ConstLocalSource =
        "public class Sample { public bool Get() { const bool flag = true; return flag; } }";
    private const string DefaultParameterSource = "public class Sample { public bool Get(bool flag = true) => flag; }";
    private const string LocalDeclarationSource =
        "public class Sample { public bool Get() { bool flag = true; return flag; } }";
    private const string ConstantPatternSource = "public class Sample { public bool Get(bool flag) => flag is true; }";

    private const string AttributeSource = """
        public class Sample
        {
            [System.Obsolete("m", true)]
            public bool Get() => false;
        }
        """;

    private const string CaseLabelSource = """
        public class Sample
        {
            public int Get(bool flag)
            {
                switch (flag)
                {
                    case true:
                        return 1;
                    default:
                        return 0;
                }
            }
        }
        """;

    private const string GotoCaseSource = """
        public class Sample
        {
            public int Get(bool flag)
            {
                switch (flag)
                {
                    case true:
                        goto case /*!*/false;
                    case false:
                        return 0;
                }

                return 0;
            }
        }
        """;

    [Test]
    public async Task Metadata_Operator_DescribesBooleanLiteralFamily()
    {
        var mutator = new BooleanLiteralMutator();
        SyntaxKind[] supported = [.. mutator.SupportedSyntaxKinds];

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutator.Id).IsEqualTo("boolean-literal");
            _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.BooleanLiteral);
            _ = await Assert.That(supported).Count().IsEqualTo(2);
            _ = await Assert.That(supported).Contains(SyntaxKind.TrueLiteralExpression);
            _ = await Assert.That(supported).Contains(SyntaxKind.FalseLiteralExpression);
        }
    }

    [Test]
    public async Task CreateMutations_TrueLiteral_ReplacesItByFalse()
    {
        var (tree, mutations) = Run(TrueSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].Kind).IsEqualTo(MutationKind.BooleanLiteral);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("boolean-literal.true-to-false");
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("true => false");
            _ = await Assert.That(mutations[0].Replacement.IsKind(SyntaxKind.FalseLiteralExpression)).IsTrue();
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(FalseSource);
        }
    }

    [Test]
    public async Task CreateMutations_FalseLiteral_ReplacesItByTrue()
    {
        var (tree, mutations) = Run(FalseSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("boolean-literal.false-to-true");
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("false => true");
            _ = await Assert.That(mutations[0].Replacement.IsKind(SyntaxKind.TrueLiteralExpression)).IsTrue();
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(TrueSource);
        }
    }

    [Test]
    public async Task CreateMutations_NonConstantFieldInitializer_IsMutated()
    {
        var (_, mutations) = Run(InstanceFieldSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("boolean-literal.true-to-false");
        }
    }

    /// <summary>
    /// A local declaration without the <see langword="const" /> modifier is an ordinary initializer, so
    /// the walk up the parent chain has to continue past it instead of treating it as a constant context.
    /// </summary>
    [Test]
    public async Task CreateMutations_NonConstantLocalDeclaration_IsMutated()
    {
        var expected = LocalDeclarationSource.Replace("= true", "= false", StringComparison.Ordinal);
        var (tree, mutations) = Run(LocalDeclarationSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("boolean-literal.true-to-false");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task CreateMutations_ConstantPattern_ReturnsEmpty()
    {
        var (_, mutations) = Run(ConstantPatternSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_AttributeArgument_ReturnsEmpty()
    {
        var (_, mutations) = Run(AttributeSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ConstantField_ReturnsEmpty()
    {
        var (_, mutations) = Run(ConstFieldSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ConstantLocal_ReturnsEmpty()
    {
        var (_, mutations) = Run(ConstLocalSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_DefaultParameterValue_ReturnsEmpty()
    {
        var (_, mutations) = Run(DefaultParameterSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_CaseLabel_ReturnsEmpty()
    {
        var (_, mutations) = Run(CaseLabelSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_GotoCaseLabel_ReturnsEmpty()
    {
        var (_, mutations) = Run(GotoCaseSource, SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>);

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
        var mutator = new BooleanLiteralMutator();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(null!, semanticModel, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("node");
    }

    [Test]
    public async Task CreateMutations_SemanticModelNull_ThrowsArgumentNullException()
    {
        var (_, _, tree) = CompilationFactory.CreateWithModel(TrueSource);
        var mutator = new BooleanLiteralMutator();
        var node = FindBooleanLiteral(tree);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(node, null!, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("semanticModel");
    }

    [Test]
    public async Task CreateMutations_CancelledToken_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(TrueSource);
        var mutator = new BooleanLiteralMutator();
        var node = FindBooleanLiteral(tree);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = mutator.CreateMutations(node, semanticModel, cancellation.Token).ToList()
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    /// <summary>
    /// A literal that was created on its own has no parent at all, so the walk up the parent chain of the
    /// constant context check ends immediately instead of at a member or compilation unit.
    /// </summary>
    [Test]
    public async Task CreateMutations_DetachedLiteralWithoutParent_IsMutated()
    {
        var (_, semanticModel, _) = CompilationFactory.CreateWithModel(TrueSource);
        var literal = SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression);
        var mutator = new BooleanLiteralMutator();

        var mutations = mutator.CreateMutations(literal, semanticModel, CancellationToken.None).ToArray();

        using (Assert.Multiple())
        {
            _ = await Assert.That(literal.Parent).IsNull();
            _ = await Assert.That(mutations.Length).IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("boolean-literal.true-to-false");
        }
    }

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source) => Run(source, FindBooleanLiteral);

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source, Func<SyntaxTree, SyntaxNode> select)
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var mutator = new BooleanLiteralMutator();

        return (tree, [.. mutator.CreateMutations(select(tree), semanticModel, CancellationToken.None)]);
    }

    private static string Rewrite(SyntaxTree tree, Mutation mutation) => mutation.ApplyTo(tree).ToString();

    private static SyntaxNode FindBooleanLiteral(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(
            tree,
            static literal =>
                literal.IsKind(SyntaxKind.TrueLiteralExpression) || literal.IsKind(SyntaxKind.FalseLiteralExpression)
        );

    private static SyntaxNode FindNumericLiteral(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(
            tree,
            static literal => literal.IsKind(SyntaxKind.NumericLiteralExpression)
        );
}
