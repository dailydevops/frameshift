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
/// Covers the string literal operator: the two directions of the mutation, verbatim literals, the
/// <c>nameof</c> exception, interpolated strings and the constant contexts.
/// </summary>
public class StringLiteralMutatorTests
{
    private const string NonEmptySource = "public class Sample { public string Get() => \"abc\"; }";
    private const string EmptySource = "public class Sample { public string Get() => \"\"; }";
    private const string VerbatimSource = "public class Sample { public string Get() => @\"abc\"; }";
    private const string NameOfSource = "public class Sample { public string Get() => nameof(\"abc\"); }";
    private const string InterpolatedSource = "public class Sample { public string Get(int v) => $\"a{v}\"; }";
    private const string RawSource = "public class Sample { public string Get() => \"\"\"abc\"\"\"; }";
    private const string Utf8Source = "public class Sample { public System.ReadOnlySpan<byte> Get() => \"abc\"u8; }";
    private const string InstanceFieldSource =
        "public class Sample { private string _text = \"abc\"; public string Get() => _text; }";
    private const string LocalDeclarationSource =
        "public class Sample { public string Get() { string text = \"abc\"; return text; } }";

    private const string MultiLineRawSource = """"
        public class Sample
        {
            public string Get() =>
                """
                abc
                """;
        }
        """";

    private const string AttributeSource = """
        public class Sample
        {
            [System.Obsolete("message")]
            public string Get() => "abc";
        }
        """;

    private const string CaseLabelSource = """
        public class Sample
        {
            public int Get(string text)
            {
                switch (text)
                {
                    case "a":
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
            public int Get(string text)
            {
                switch (text)
                {
                    case "a":
                        goto case /*!*/"b";
                    case "b":
                        return 2;
                    default:
                        return 0;
                }
            }
        }
        """;

    [Test]
    public async Task Metadata_Operator_DescribesStringLiteralFamily()
    {
        var mutator = new StringLiteralMutator();
        SyntaxKind[] supported = [.. mutator.SupportedSyntaxKinds];

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutator.Id).IsEqualTo("string-literal");
            _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.StringLiteral);
            _ = await Assert.That(supported).Count().IsEqualTo(1);
            _ = await Assert.That(supported).Contains(SyntaxKind.StringLiteralExpression);
        }
    }

    [Test]
    public async Task CreateMutations_NonEmptyLiteral_ReplacesItByTheEmptyString()
    {
        var (tree, mutations) = Run(NonEmptySource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].Kind).IsEqualTo(MutationKind.StringLiteral);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("string-literal.to-empty");
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("\"...\" => \"\"");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(EmptySource);
        }
    }

    [Test]
    public async Task CreateMutations_EmptyLiteral_ReplacesItByANonEmptyString()
    {
        var (tree, mutations) = Run(EmptySource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("string-literal.empty-to-non-empty");
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("\"\" => \"FrameShift\"");
            _ = await Assert
                .That(Rewrite(tree, mutations[0]))
                .IsEqualTo("public class Sample { public string Get() => \"FrameShift\"; }");
        }
    }

    [Test]
    public async Task CreateMutations_VerbatimLiteral_ReplacesItByAPlainEmptyLiteral()
    {
        var (tree, mutations) = Run(VerbatimSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("string-literal.to-empty");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(EmptySource);
        }
    }

    /// <summary>
    /// A raw literal is a plain string literal expression, only its token differs, so it is mutated like
    /// any other literal and the replacement is written as a plain literal.
    /// </summary>
    [Test]
    public async Task CreateMutations_SingleLineRawLiteral_ReplacesItByAPlainEmptyLiteral()
    {
        var (tree, mutations) = Run(RawSource);
        var literal = (LiteralExpressionSyntax)FindStringLiteral(tree);

        using (Assert.Multiple())
        {
            _ = await Assert.That(literal.Token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)).IsTrue();
            _ = await Assert.That(literal.Token.ValueText).IsEqualTo("abc");
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("string-literal.to-empty");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(EmptySource);
        }
    }

    /// <summary>
    /// A multi-line raw literal keeps its own layout out of the replacement, which is the plain empty
    /// literal. The rewritten source is not compared as a whole, because its line endings belong to the
    /// fixture and not to the mutation.
    /// </summary>
    [Test]
    public async Task CreateMutations_MultiLineRawLiteral_ReplacesItByAPlainEmptyLiteral()
    {
        var (tree, mutations) = Run(MultiLineRawSource);
        var literal = (LiteralExpressionSyntax)FindStringLiteral(tree);

        using (Assert.Multiple())
        {
            _ = await Assert.That(literal.Token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken)).IsTrue();
            _ = await Assert.That(literal.Token.ValueText).IsEqualTo("abc");
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("string-literal.to-empty");
            _ = await Assert.That(mutations[0].Replacement.ToString()).IsEqualTo("\"\"");
        }
    }

    /// <summary>
    /// A UTF-8 literal is a syntax kind of its own and the operator is not offered for it, because the
    /// replacement would change the type of the expression.
    /// </summary>
    [Test]
    public async Task CreateMutations_Utf8Literal_ReturnsEmpty()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(Utf8Source);
        var literal = SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(tree);
        var mutations = Mutate(new StringLiteralMutator(), literal, semanticModel);

        using (Assert.Multiple())
        {
            _ = await Assert.That(literal.Kind()).IsEqualTo(SyntaxKind.Utf8StringLiteralExpression);
            _ = await Assert.That(mutations).IsEmpty();
        }
    }

    [Test]
    public async Task CreateMutations_NonConstantFieldInitializer_IsMutated()
    {
        var (tree, mutations) = Run(InstanceFieldSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("string-literal.to-empty");
            _ = await Assert
                .That(Rewrite(tree, mutations[0]))
                .IsEqualTo(InstanceFieldSource.Replace("\"abc\"", "\"\"", StringComparison.Ordinal));
        }
    }

    [Test]
    public async Task CreateMutations_NonConstantLocalDeclaration_IsMutated()
    {
        var (tree, mutations) = Run(LocalDeclarationSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("string-literal.to-empty");
            _ = await Assert
                .That(Rewrite(tree, mutations[0]))
                .IsEqualTo(LocalDeclarationSource.Replace("\"abc\"", "\"\"", StringComparison.Ordinal));
        }
    }

    [Test]
    public async Task CreateMutations_NameOfArgument_ReturnsEmpty()
    {
        var (_, mutations) = Run(NameOfSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_InterpolatedString_ReturnsEmpty()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(InterpolatedSource);
        var interpolated = SyntaxNodeLocator.FindFirst<InterpolatedStringExpressionSyntax>(tree);
        var text = SyntaxNodeLocator.FindFirst<InterpolatedStringTextSyntax>(tree);
        var mutator = new StringLiteralMutator();
        var literals = SyntaxNodeLocator
            .FindAll<LiteralExpressionSyntax>(tree)
            .Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression));

        using (Assert.Multiple())
        {
            _ = await Assert.That(literals).IsEmpty();
            _ = await Assert.That(Mutate(mutator, interpolated, semanticModel)).IsEmpty();
            _ = await Assert.That(Mutate(mutator, text, semanticModel)).IsEmpty();
        }
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
        var (_, mutations) = Run("public class Sample { private const string Value = \"abc\"; }");

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ConstantLocal_ReturnsEmpty()
    {
        var (_, mutations) = Run(
            "public class Sample { public string Get() { const string text = \"abc\"; return text; } }"
        );

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_DefaultParameterValue_ReturnsEmpty()
    {
        var (_, mutations) = Run("public class Sample { public string Get(string text = \"abc\") => text; }");

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

    /// <summary>
    /// A literal that was parsed on its own has no parent at all, so the walk up the parent chain of the
    /// constant context check ends immediately instead of at a member or compilation unit.
    /// </summary>
    [Test]
    public async Task CreateMutations_DetachedLiteralWithoutParent_IsMutated()
    {
        var (_, semanticModel, _) = CompilationFactory.CreateWithModel(NonEmptySource);
        var literal = (LiteralExpressionSyntax)SyntaxFactory.ParseExpression("\"abc\"");
        var mutations = Mutate(new StringLiteralMutator(), literal, semanticModel);

        using (Assert.Multiple())
        {
            _ = await Assert.That(literal.Parent).IsNull();
            _ = await Assert.That(literal.IsKind(SyntaxKind.StringLiteralExpression)).IsTrue();
            _ = await Assert.That(mutations.Length).IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("string-literal.to-empty");
        }
    }

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source) => Run(source, FindStringLiteral);

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source, Func<SyntaxTree, SyntaxNode> select)
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var mutator = new StringLiteralMutator();

        return (tree, Mutate(mutator, select(tree), semanticModel));
    }

    private static Mutation[] Mutate(StringLiteralMutator mutator, SyntaxNode node, SemanticModel semanticModel) =>
        [.. mutator.CreateMutations(node, semanticModel, CancellationToken.None)];

    private static string Rewrite(SyntaxTree tree, Mutation mutation) => mutation.ApplyTo(tree).ToString();

    private static SyntaxNode FindStringLiteral(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(
            tree,
            static literal => literal.IsKind(SyntaxKind.StringLiteralExpression)
        );

    private static SyntaxNode FindNumericLiteral(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(
            tree,
            static literal => literal.IsKind(SyntaxKind.NumericLiteralExpression)
        );
}
