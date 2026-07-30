namespace NetEvolve.Frameshift.Tests.Unit.Mutations.Operators;

using System.Collections.Immutable;
using System.Globalization;
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
/// Covers <see cref="LogicalOperatorMutator" />, which swaps <c>&amp;&amp;</c> with <c>||</c> and the
/// boolean <c>&amp;</c> with <c>|</c>, while leaving integral <c>&amp;</c> and <c>|</c> to the bitwise
/// mutation operator.
/// </summary>
public class LogicalOperatorMutatorTests
{
    private const string OperatorPlaceholder = "OPERATOR";

    private const string BooleanTemplate = """
        internal static class Combinations
        {
            public static bool Combine(bool left, bool right) => /*!*/left OPERATOR right;
        }
        """;

    private const string IntegralTemplate = """
        internal static class Combinations
        {
            public static int Combine(int left, int right) => /*!*/left OPERATOR right;
        }
        """;

    private const string NullableBooleanTemplate = """
        internal static class Combinations
        {
            public static bool? Combine(bool? left, bool? right) => /*!*/left OPERATOR right;
        }
        """;

    private const string TriviaSource = """
        internal static class Combinations
        {
            // a comment above the combination
            public static bool Combine(bool left, bool right)
            {
                return /*!*/left /* between */ && right; // a comment behind the combination
            }
        }
        """;

    private static readonly LogicalOperatorMutator _mutator = new LogicalOperatorMutator();

    [Test]
    public async Task SupportedSyntaxKinds_Always_AreTheConditionalAndBooleanKinds()
    {
        SyntaxKind[] expected =
        [
            SyntaxKind.LogicalAndExpression,
            SyntaxKind.LogicalOrExpression,
            SyntaxKind.BitwiseAndExpression,
            SyntaxKind.BitwiseOrExpression,
        ];

        _ = await Assert.That(_mutator.SupportedSyntaxKinds.ToArray()).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Metadata_Always_IdentifiesTheLogicalFamily()
    {
        _ = await Assert.That(_mutator.Id).IsEqualTo("logical");
        _ = await Assert.That(_mutator.Kind).IsEqualTo(MutationKind.LogicalOperator);
    }

    [Test]
    [Arguments("&&", "&& => ||", "logical.conditional-and-to-conditional-or")]
    [Arguments("||", "|| => &&", "logical.conditional-or-to-conditional-and")]
    [Arguments("&", "& => |", "logical.boolean-and-to-boolean-or")]
    [Arguments("|", "| => &", "logical.boolean-or-to-boolean-and")]
    public async Task CreateMutations_BooleanOperands_ProducesTheOppositeOperator(
        string source,
        string expectedName,
        string expectedId
    )
    {
        string[] expected = [expectedName];
        var (mutations, _, _, errors) = Mutate(CreateSource(BooleanTemplate, source));

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(DisplayNames(mutations)).IsEquivalentTo(expected);
        _ = await Assert.That(mutations.Single().OperatorId).IsEqualTo(expectedId);
        _ = await Assert.That(mutations.Single().Kind).IsEqualTo(MutationKind.LogicalOperator);
    }

    [Test]
    [Arguments("&", "& => |")]
    [Arguments("|", "| => &")]
    public async Task CreateMutations_NullableBooleanOperands_ProducesTheOppositeOperator(
        string source,
        string expectedName
    )
    {
        string[] expected = [expectedName];
        var (mutations, _, _, errors) = Mutate(CreateSource(NullableBooleanTemplate, source));

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(DisplayNames(mutations)).IsEquivalentTo(expected);
    }

    [Test]
    [Arguments("&")]
    [Arguments("|")]
    public async Task CreateMutations_IntegralOperands_ReturnsEmptyBecauseTheBitwiseOperatorOwnsThem(string source)
    {
        var (mutations, tree, model, errors) = Mutate(CreateSource(IntegralTemplate, source));
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var leftType = model.GetTypeInfo(binary.Left).ConvertedType;

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(leftType?.ToDisplayString()).IsEqualTo("int");
        _ = await Assert.That(mutations.ToArray()).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ExclusiveOrExpression_ReturnsEmpty()
    {
        var (mutations, _, _, errors) = Mutate(CreateSource(IntegralTemplate, "^"));

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(mutations.ToArray()).IsEmpty();
    }

    [Test]
    public async Task ApplyTo_ConditionalAndToConditionalOr_KeepsTheSurroundingTrivia()
    {
        var expected = TriviaSource.Replace("&& right", "|| right", StringComparison.Ordinal);
        var (mutations, tree, _, errors) = Mutate(TriviaSource);

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(mutations.Single().ApplyTo(tree).ToString()).IsEqualTo(expected);
    }

    [Test]
    [Arguments("&&", "left || right")]
    [Arguments("||", "left && right")]
    [Arguments("&", "left | right")]
    [Arguments("|", "left & right")]
    public async Task ApplyTo_BooleanOperands_ProducesCompilableSource(string source, string expectedText)
    {
        var (mutations, tree, _, _) = Mutate(CreateSource(BooleanTemplate, source));
        var mutated = mutations.Single().ApplyTo(tree).ToString();
        var compilation = CompilationFactory.Create(mutated);

        _ = await Assert.That(mutated).Contains(expectedText);
        _ = await Assert.That(Describe(CompilationFactory.GetCompileErrors(compilation))).IsEqualTo(string.Empty);
    }

    private static string CreateSource(string template, string source) =>
        template.Replace(OperatorPlaceholder, source, StringComparison.Ordinal);

    private static string[] DisplayNames(ImmutableArray<Mutation> mutations) =>
        [.. mutations.Select(mutation => mutation.DisplayName)];

    private static (ImmutableArray<Mutation> Mutations, SyntaxTree Tree, SemanticModel Model, string Errors) Mutate(
        string source
    )
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var node = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var created = _mutator.CreateMutations(node, semanticModel, CancellationToken.None);
        ImmutableArray<Mutation> mutations = [.. created];

        return (mutations, tree, semanticModel, Describe(CompilationFactory.GetCompileErrors(compilation)));
    }

    private static string Describe(ImmutableArray<Diagnostic> errors) =>
        string.Join(", ", errors.Select(error => error.GetMessage(CultureInfo.InvariantCulture)));
}
