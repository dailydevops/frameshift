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
/// Covers <see cref="EqualityOperatorMutator" />, which swaps <c>==</c> and <c>!=</c> unless the
/// comparison is bound to a user-defined operator that has no declared counterpart.
/// </summary>
public class EqualityOperatorMutatorTests
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
                return /*!*/left /* between */ == right; // a comment behind the comparison
            }
        }
        """;

    private const string UserDefinedPairSource = """
        internal sealed class Money
        {
            public static bool operator ==(Money? left, Money? right) => true;

            public static bool operator !=(Money? left, Money? right) => false;

            public override bool Equals(object? other) => true;

            public override int GetHashCode() => 0;
        }

        internal static class Comparisons
        {
            public static bool Compare(Money left, Money right) => /*!*/left == right;
        }
        """;

    private const string UserDefinedEqualsOnlySource = """
        internal sealed class Money
        {
            public static bool operator ==(Money? left, Money? right) => true;

            public override bool Equals(object? other) => true;

            public override int GetHashCode() => 0;
        }

        internal static class Comparisons
        {
            public static bool Compare(Money left, Money right) => /*!*/left == right;
        }
        """;

    private const string UserDefinedNotEqualsOnlySource = """
        internal sealed class Money
        {
            public static bool operator !=(Money? left, Money? right) => false;

            public override bool Equals(object? other) => true;

            public override int GetHashCode() => 0;
        }

        internal static class Comparisons
        {
            public static bool Compare(Money left, Money right) => /*!*/left != right;
        }
        """;

    private const string RelationalSource = """
        internal static class Comparisons
        {
            public static bool Compare(int left, int right) => /*!*/left < right;
        }
        """;

    private static readonly EqualityOperatorMutator _mutator = new EqualityOperatorMutator();

    [Test]
    public async Task SupportedSyntaxKinds_Always_AreTheTwoEqualityKinds()
    {
        SyntaxKind[] expected = [SyntaxKind.EqualsExpression, SyntaxKind.NotEqualsExpression];

        _ = await Assert.That(_mutator.SupportedSyntaxKinds.ToArray()).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Metadata_Always_IdentifiesTheEqualityFamily()
    {
        _ = await Assert.That(_mutator.Id).IsEqualTo("equality");
        _ = await Assert.That(_mutator.Kind).IsEqualTo(MutationKind.EqualityOperator);
    }

    [Test]
    [Arguments("==", "== => !=", "equality.equals-to-not-equals")]
    [Arguments("!=", "!= => ==", "equality.not-equals-to-equals")]
    public async Task CreateMutations_BuiltInComparison_ProducesTheCounterpart(
        string source,
        string expectedName,
        string expectedId
    )
    {
        string[] expected = [expectedName];
        var (mutations, _, _, errors) = Mutate(CreateSource(source));

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(DisplayNames(mutations)).IsEquivalentTo(expected);
        _ = await Assert.That(mutations.Single().OperatorId).IsEqualTo(expectedId);
        _ = await Assert.That(mutations.Single().Kind).IsEqualTo(MutationKind.EqualityOperator);
    }

    [Test]
    public async Task CreateMutations_UserDefinedOperatorWithCounterpart_ProducesTheCounterpart()
    {
        string[] expected = ["== => !="];
        var (mutations, tree, model, errors) = Mutate(UserDefinedPairSource);
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var method = model.GetSymbolInfo(binary).Symbol as IMethodSymbol;

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(method?.MethodKind).IsEqualTo(MethodKind.UserDefinedOperator);
        _ = await Assert.That(DisplayNames(mutations)).IsEquivalentTo(expected);
    }

    /// <summary>
    /// A type that declares <c>==</c> without <c>!=</c> is rejected by the C# compiler, so this fixture
    /// deliberately does not compile. It is the only way to bind a comparison to a user-defined operator
    /// whose counterpart is missing, which is exactly the situation the mutator has to skip. The symbol
    /// assertions pin the shape of the fixture instead of its compile errors.
    /// </summary>
    [Test]
    public async Task CreateMutations_UserDefinedEqualsWithoutCounterpart_ReturnsEmpty()
    {
        var (mutations, tree, model, _) = Mutate(UserDefinedEqualsOnlySource);
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var method = model.GetSymbolInfo(binary).Symbol as IMethodSymbol;

        _ = await Assert.That(method?.Name).IsEqualTo("op_Equality");
        _ = await Assert.That(method?.ContainingType.Name).IsEqualTo("Money");
        _ = await Assert.That(CounterpartCount(method, "op_Inequality")).IsEqualTo(0);
        _ = await Assert.That(mutations.ToArray()).IsEmpty();
    }

    /// <summary>
    /// The mirrored case of <see cref="CreateMutations_UserDefinedEqualsWithoutCounterpart_ReturnsEmpty" />,
    /// with the same deliberate compile error.
    /// </summary>
    [Test]
    public async Task CreateMutations_UserDefinedNotEqualsWithoutCounterpart_ReturnsEmpty()
    {
        var (mutations, tree, model, _) = Mutate(UserDefinedNotEqualsOnlySource);
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var method = model.GetSymbolInfo(binary).Symbol as IMethodSymbol;

        _ = await Assert.That(method?.Name).IsEqualTo("op_Inequality");
        _ = await Assert.That(method?.ContainingType.Name).IsEqualTo("Money");
        _ = await Assert.That(CounterpartCount(method, "op_Equality")).IsEqualTo(0);
        _ = await Assert.That(mutations.ToArray()).IsEmpty();
    }

    [Test]
    public async Task ApplyTo_EqualsToNotEquals_KeepsTheSurroundingTrivia()
    {
        var expected = TriviaSource.Replace("== right", "!= right", StringComparison.Ordinal);
        var (mutations, tree, _, errors) = Mutate(TriviaSource);

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(mutations.Single().ApplyTo(tree).ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task ApplyTo_EqualsToNotEquals_ProducesCompilableSource()
    {
        var (mutations, tree, _, _) = Mutate(CreateSource("=="));
        var mutated = mutations.Single().ApplyTo(tree).ToString();
        var compilation = CompilationFactory.Create(mutated);

        _ = await Assert.That(mutated).Contains("left != right");
        _ = await Assert.That(Describe(CompilationFactory.GetCompileErrors(compilation))).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task CreateMutations_RelationalExpression_ReturnsEmpty()
    {
        var (mutations, _, _, errors) = Mutate(RelationalSource);

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(mutations.ToArray()).IsEmpty();
    }

    private static string CreateSource(string source) =>
        ComparisonTemplate.Replace(OperatorPlaceholder, source, StringComparison.Ordinal);

    private static int? CounterpartCount(IMethodSymbol? method, string name) =>
        method?.ContainingType.GetMembers(name).Length;

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
