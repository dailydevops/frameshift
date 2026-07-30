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
/// Covers <see cref="RelationalOperatorMutator" />, which turns every relational operator into the three
/// remaining ones, including the boundary flipping combinations such as <c>&lt;</c> to <c>&lt;=</c>.
/// </summary>
public class RelationalOperatorMutatorTests
{
    private const string OperatorPlaceholder = "OPERATOR";

    private const string ComparisonTemplate = """
        internal static class Comparisons
        {
            public static bool Compare(int left, int right) => /*!*/left OPERATOR right;
        }
        """;

    private const string TriviaSource = """
        internal static class Comparisons
        {
            // a comment above the comparison
            public static bool Compare(int left, int right)
            {
                return /*!*/left /* between */ < right; // a comment behind the comparison
            }
        }
        """;

    private const string EqualitySource = """
        internal static class Comparisons
        {
            public static bool Compare(int left, int right) => /*!*/left == right;
        }
        """;

    private static readonly RelationalOperatorMutator _mutator = new RelationalOperatorMutator();

    [Test]
    public async Task SupportedSyntaxKinds_Always_AreTheFourRelationalKinds()
    {
        SyntaxKind[] expected =
        [
            SyntaxKind.LessThanExpression,
            SyntaxKind.LessThanOrEqualExpression,
            SyntaxKind.GreaterThanExpression,
            SyntaxKind.GreaterThanOrEqualExpression,
        ];

        _ = await Assert.That(_mutator.SupportedSyntaxKinds.ToArray()).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Metadata_Always_IdentifiesTheRelationalFamily()
    {
        _ = await Assert.That(_mutator.Id).IsEqualTo("relational");
        _ = await Assert.That(_mutator.Kind).IsEqualTo(MutationKind.RelationalOperator);
    }

    [Test]
    [Arguments("<", "< => <=", "< => >", "< => >=")]
    [Arguments("<=", "<= => <", "<= => >", "<= => >=")]
    [Arguments(">", "> => <", "> => <=", "> => >=")]
    [Arguments(">=", ">= => <", ">= => <=", ">= => >")]
    public async Task CreateMutations_RelationalExpression_ProducesTheThreeRemainingOperators(
        string source,
        string first,
        string second,
        string third
    )
    {
        string[] expected = [first, second, third];
        var (mutations, _, _, errors) = Mutate(CreateSource(source));

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(DisplayNames(mutations)).IsEquivalentTo(expected);
    }

    [Test]
    [Arguments("<", "<=", "relational.less-than-to-less-than-or-equal")]
    [Arguments("<", ">", "relational.less-than-to-greater-than")]
    [Arguments("<", ">=", "relational.less-than-to-greater-than-or-equal")]
    [Arguments("<=", "<", "relational.less-than-or-equal-to-less-than")]
    [Arguments("<=", ">", "relational.less-than-or-equal-to-greater-than")]
    [Arguments("<=", ">=", "relational.less-than-or-equal-to-greater-than-or-equal")]
    [Arguments(">", "<", "relational.greater-than-to-less-than")]
    [Arguments(">", "<=", "relational.greater-than-to-less-than-or-equal")]
    [Arguments(">", ">=", "relational.greater-than-to-greater-than-or-equal")]
    [Arguments(">=", "<", "relational.greater-than-or-equal-to-less-than")]
    [Arguments(">=", "<=", "relational.greater-than-or-equal-to-less-than-or-equal")]
    [Arguments(">=", ">", "relational.greater-than-or-equal-to-greater-than")]
    public async Task CreateMutations_RelationalExpression_UsesStableOperatorIds(
        string source,
        string target,
        string expectedId
    )
    {
        var (mutations, _, _, _) = Mutate(CreateSource(source));
        var mutation = Single(mutations, $"{source} => {target}");

        _ = await Assert.That(mutation.OperatorId).IsEqualTo(expectedId);
        _ = await Assert.That(mutation.Kind).IsEqualTo(MutationKind.RelationalOperator);
    }

    [Test]
    public async Task CreateMutations_RelationalExpression_ReplacesTheWholeComparison()
    {
        var (mutations, tree, _, _) = Mutate(CreateSource("<"));
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);

        foreach (var mutation in mutations)
        {
            _ = await Assert.That(mutation.Original.Span).IsEqualTo(binary.Span);
            _ = await Assert.That(mutation.Original.ToString()).IsEqualTo("left < right");
            _ = await Assert.That(mutation.Location.GetLineSpan().StartLinePosition.Line).IsEqualTo(2);
        }
    }

    [Test]
    [Arguments("<=", "<= right")]
    [Arguments(">", "> right")]
    [Arguments(">=", ">= right")]
    public async Task ApplyTo_BoundaryFlip_KeepsTheSurroundingTrivia(string target, string replacement)
    {
        var expected = TriviaSource.Replace("< right", replacement, StringComparison.Ordinal);
        var (mutations, tree, _, errors) = Mutate(TriviaSource);
        var mutation = Single(mutations, $"< => {target}");

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(mutation.ApplyTo(tree).ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task ApplyTo_EveryMutation_ProducesCompilableSource()
    {
        var (mutations, tree, _, _) = Mutate(CreateSource("<"));

        foreach (var mutation in mutations)
        {
            var mutated = mutation.ApplyTo(tree).ToString();
            var compilation = CompilationFactory.Create(mutated);

            _ = await Assert.That(Describe(CompilationFactory.GetCompileErrors(compilation))).IsEqualTo(string.Empty);
        }
    }

    [Test]
    public async Task CreateMutations_EqualityExpression_ReturnsEmpty()
    {
        var (mutations, _, _, errors) = Mutate(EqualitySource);

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(mutations.ToArray()).IsEmpty();
    }

    private static string CreateSource(string source) =>
        ComparisonTemplate.Replace(OperatorPlaceholder, source, StringComparison.Ordinal);

    private static string[] DisplayNames(ImmutableArray<Mutation> mutations) =>
        [.. mutations.Select(mutation => mutation.DisplayName)];

    private static Mutation Single(ImmutableArray<Mutation> mutations, string displayName) =>
        mutations.Single(mutation => string.Equals(mutation.DisplayName, displayName, StringComparison.Ordinal));

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
