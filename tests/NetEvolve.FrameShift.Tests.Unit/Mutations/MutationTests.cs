namespace NetEvolve.FrameShift.Tests.Unit.Mutations;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using NetEvolve.FrameShift.Mutations;
using NetEvolve.FrameShift.Tests.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the mutation value object: what it rejects, what it reports as its location and how it
/// rewrites the syntax tree it belongs to.
/// </summary>
public class MutationTests
{
    private const string Source = "public class Sample { public bool Get() => true; }";
    private const string MutatedSource = "public class Sample { public bool Get() => false; }";

    [Test]
    public async Task Constructor_OperatorIdNull_ThrowsArgumentNullException()
    {
        var (_, original, replacement) = CreateNodes();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = new Mutation(MutationKind.BooleanLiteral, null!, "true => false", original, replacement)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("operatorId");
    }

    [Test]
    public async Task Constructor_DisplayNameNull_ThrowsArgumentNullException()
    {
        var (_, original, replacement) = CreateNodes();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = new Mutation(MutationKind.BooleanLiteral, "boolean-literal.true-to-false", null!, original, replacement)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("displayName");
    }

    [Test]
    public async Task Constructor_OperatorIdEmpty_ThrowsArgumentException()
    {
        var (_, original, replacement) = CreateNodes();

        var exception = Assert.ThrowsExactly<ArgumentException>(() =>
            _ = new Mutation(MutationKind.BooleanLiteral, string.Empty, "true => false", original, replacement)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("operatorId");
    }

    [Test]
    public async Task Constructor_DisplayNameEmpty_ThrowsArgumentException()
    {
        var (_, original, replacement) = CreateNodes();

        var exception = Assert.ThrowsExactly<ArgumentException>(() =>
            _ = new Mutation(
                MutationKind.BooleanLiteral,
                "boolean-literal.true-to-false",
                string.Empty,
                original,
                replacement
            )
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("displayName");
    }

    [Test]
    public async Task Constructor_OriginalNull_ThrowsArgumentNullException()
    {
        var (_, _, replacement) = CreateNodes();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = new Mutation(MutationKind.BooleanLiteral, "boolean-literal", "true => false", null!, replacement)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("original");
    }

    [Test]
    public async Task Constructor_ReplacementNull_ThrowsArgumentNullException()
    {
        var (_, original, _) = CreateNodes();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = new Mutation(MutationKind.BooleanLiteral, "boolean-literal", "true => false", original, null!)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("replacement");
    }

    [Test]
    public async Task Constructor_WithoutLocation_UsesTheLocationOfTheOriginalNode()
    {
        var (tree, original, replacement) = CreateNodes();

        var mutation = CreateMutation(original, replacement);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutation.Location.SourceTree).IsEqualTo(tree);
            _ = await Assert.That(mutation.Location.SourceSpan).IsEqualTo(original.Span);
        }
    }

    [Test]
    public async Task Constructor_WithLocation_UsesTheGivenLocation()
    {
        var (tree, original, replacement) = CreateNodes();
        var location = Location.Create(tree, new TextSpan(0, 6));

        var mutation = new Mutation(
            MutationKind.BooleanLiteral,
            "boolean-literal.true-to-false",
            "true => false",
            original,
            replacement,
            location
        );

        _ = await Assert.That(mutation.Location.SourceSpan).IsEqualTo(new TextSpan(0, 6));
    }

    [Test]
    public async Task Constructor_ValidArguments_KeepsEveryValue()
    {
        var (_, original, replacement) = CreateNodes();

        var mutation = CreateMutation(original, replacement);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutation.Kind).IsEqualTo(MutationKind.BooleanLiteral);
            _ = await Assert.That(mutation.OperatorId).IsEqualTo("boolean-literal.true-to-false");
            _ = await Assert.That(mutation.DisplayName).IsEqualTo("true => false");
            _ = await Assert.That(mutation.Original.ToString()).IsEqualTo("true");
            _ = await Assert.That(mutation.Replacement.ToString()).IsEqualTo("false");
        }
    }

    [Test]
    public async Task ToString_Always_ReturnsTheDisplayName()
    {
        var (_, original, replacement) = CreateNodes();

        var mutation = CreateMutation(original, replacement);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutation.ToString()).IsEqualTo(mutation.DisplayName);
            _ = await Assert.That(mutation.ToString()).IsEqualTo("true => false");
        }
    }

    [Test]
    public async Task ApplyTo_OwningTree_ReplacesOnlyTheMutatedNode()
    {
        var (tree, original, replacement) = CreateNodes();
        var mutation = CreateMutation(original, replacement);

        var mutated = mutation.ApplyTo(tree);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutated.ToString()).IsEqualTo(MutatedSource);
            _ = await Assert.That(tree.ToString()).IsEqualTo(Source);
        }
    }

    [Test]
    public async Task ApplyTo_OwningTree_KeepsFilePathAndParseOptions()
    {
        var (tree, original, replacement) = CreateNodes();
        var mutation = CreateMutation(original, replacement);

        var mutated = mutation.ApplyTo(tree);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutated.FilePath).IsEqualTo(CompilationFactory.DefaultFilePath);
            _ = await Assert.That(mutated.Options).IsEqualTo(tree.Options);
            _ = await Assert.That(ReferenceEquals(mutated, tree)).IsFalse();
        }
    }

    [Test]
    public async Task ApplyTo_OwningTree_KeepsEveryOtherToken()
    {
        var (tree, original, replacement) = CreateNodes();
        var mutation = CreateMutation(original, replacement);

        var mutated = mutation.ApplyTo(tree);
        var originalTokens = TokenTexts(tree).Where(text => !string.Equals(text, "true", StringComparison.Ordinal));
        var mutatedTokens = TokenTexts(mutated).Where(text => !string.Equals(text, "false", StringComparison.Ordinal));

        _ = await Assert.That(mutatedTokens).IsEquivalentTo(originalTokens);
    }

    [Test]
    public async Task ApplyTo_TreeNull_ThrowsArgumentNullException()
    {
        var (_, original, replacement) = CreateNodes();
        var mutation = CreateMutation(original, replacement);

        var exception = Assert.Throws<ArgumentNullException>(() => mutation.ApplyTo(null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("tree");
    }

    [Test]
    public async Task ApplyTo_ForeignTree_ThrowsArgumentException()
    {
        var (_, original, replacement) = CreateNodes();
        var mutation = CreateMutation(original, replacement);
        var foreignTree = CompilationFactory.ParseTree(Source, "Other.cs");

        var exception = Assert.ThrowsExactly<ArgumentException>(() => mutation.ApplyTo(foreignTree));

        _ = await Assert.That(exception.ParamName).IsEqualTo("tree");
    }

    private static Mutation CreateMutation(SyntaxNode original, SyntaxNode replacement) =>
        new Mutation(
            MutationKind.BooleanLiteral,
            "boolean-literal.true-to-false",
            "true => false",
            original,
            replacement
        );

    private static (
        SyntaxTree Tree,
        LiteralExpressionSyntax Original,
        LiteralExpressionSyntax Replacement
    ) CreateNodes()
    {
        var tree = CompilationFactory.ParseTree(Source);
        var original = SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(tree);
        var replacement = SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression);

        return (tree, original, replacement);
    }

    private static List<string> TokenTexts(SyntaxTree tree) =>
        [.. tree.GetRoot().DescendantTokens().Select(token => token.Text)];
}
