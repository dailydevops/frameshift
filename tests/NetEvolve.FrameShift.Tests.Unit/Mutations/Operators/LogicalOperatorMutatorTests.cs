namespace NetEvolve.FrameShift.Tests.Unit.Mutations.Operators;

using System.Collections.Immutable;
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

    private const string MixedNullableBooleanTemplate = """
        internal static class Combinations
        {
            public static bool? Combine(bool left, bool? right) => /*!*/left OPERATOR right;
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

    private const string TrueFalseOperatorTemplate = """
        internal sealed class Flag
        {
            public static Flag operator &(Flag left, Flag right) => left;

            public static Flag operator |(Flag left, Flag right) => right;

            public static bool operator true(Flag value) => true;

            public static bool operator false(Flag value) => false;
        }

        internal static class Combinations
        {
            public static Flag Combine(Flag left, Flag right) => /*!*/left OPERATOR right;
        }
        """;

    private const string MethodGroupOperandSource = """
        internal static class Combinations
        {
            public static int Value() => 0;

            public static object Combine() => /*!*/Value & Value;
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

    /// <summary>
    /// A type with <c>true</c> and <c>false</c> operators may be used with <c>&amp;&amp;</c> and
    /// <c>||</c>, which resolve through its <c>&amp;</c> and <c>|</c> operators. The conditional form is
    /// mutated, because both underlying operators exist.
    /// </summary>
    [Test]
    [Arguments("&&", "&& => ||")]
    [Arguments("||", "|| => &&")]
    public async Task CreateMutations_ConditionalOperatorOverATypeWithTrueAndFalse_ProducesTheOppositeOperator(
        string source,
        string expectedName
    )
    {
        string[] expected = [expectedName];
        var (mutations, tree, model, errors) = Mutate(CreateSource(TrueFalseOperatorTemplate, source));
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(model.GetTypeInfo(binary.Left).ConvertedType?.ToDisplayString()).IsEqualTo("Flag");
        _ = await Assert.That(DisplayNames(mutations)).IsEquivalentTo(expected);
    }

    /// <summary>
    /// The non-conditional form over the same type is a user defined bitwise operator over non-boolean
    /// operands, which this operator family leaves alone.
    /// </summary>
    [Test]
    [Arguments("&")]
    [Arguments("|")]
    public async Task CreateMutations_UserDefinedBooleanLikeOperator_ReturnsEmpty(string source)
    {
        var (mutations, _, _, errors) = Mutate(CreateSource(TrueFalseOperatorTemplate, source));

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(mutations.ToArray()).IsEmpty();
    }

    /// <summary>
    /// A method group has no type at all, so the boolean guard has to reject a <see langword="null" />
    /// type instead of assuming every operand carries one. C# rejects the fixture, which is exactly what
    /// makes the operands typeless.
    /// </summary>
    [Test]
    public async Task CreateMutations_TypelessOperands_ReturnsEmpty()
    {
        var (mutations, tree, model, _) = Mutate(MethodGroupOperandSource);
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);

        _ = await Assert.That(binary.Kind()).IsEqualTo(SyntaxKind.BitwiseAndExpression);
        _ = await Assert.That(model.GetTypeInfo(binary.Left).ConvertedType).IsNull();
        _ = await Assert.That(mutations.ToArray()).IsEmpty();
    }

    /// <summary>
    /// One nullable and one non-nullable boolean operand are both lifted to <see langword="bool" />?, so
    /// the mixed pair is mutated exactly like a pair of plain boolean operands.
    /// </summary>
    [Test]
    [Arguments("&", "& => |", "logical.boolean-and-to-boolean-or")]
    [Arguments("|", "| => &", "logical.boolean-or-to-boolean-and")]
    public async Task CreateMutations_MixedNullableAndPlainBooleanOperands_ProducesTheOppositeOperator(
        string source,
        string expectedName,
        string expectedId
    )
    {
        string[] expected = [expectedName];
        var (mutations, tree, model, errors) = Mutate(CreateSource(MixedNullableBooleanTemplate, source));
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(model.GetTypeInfo(binary.Left).Type?.ToDisplayString()).IsEqualTo("bool");
        _ = await Assert.That(model.GetTypeInfo(binary.Right).Type?.ToDisplayString()).IsEqualTo("bool?");
        _ = await Assert.That(DisplayNames(mutations)).IsEquivalentTo(expected);
        _ = await Assert.That(mutations.Single().OperatorId).IsEqualTo(expectedId);
    }

    [Test]
    public async Task CreateMutations_CancelledToken_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(CreateSource(BooleanTemplate, "&&"));
        var node = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = _mutator.CreateMutations(node, semanticModel, cancellation.Token).ToList()
        );

        _ = await Assert.That(exception).IsNotNull();
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
