namespace NetEvolve.FrameShift.Tests.Unit.Mutations.Operators;

using System.Collections.Immutable;
using System.Globalization;
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

    private const string StructPairSource = """
        internal readonly struct Money
        {
            public static bool operator ==(Money left, Money right) => true;

            public static bool operator !=(Money left, Money right) => false;

            public override bool Equals(object? other) => true;

            public override int GetHashCode() => 0;
        }

        internal static class Comparisons
        {
            public static bool Compare(Money left, Money right) => /*!*/left == right;
        }
        """;

    private const string GenericPairSource = """
        internal readonly struct Box<TValue>
        {
            public static bool operator ==(Box<TValue> left, Box<TValue> right) => true;

            public static bool operator !=(Box<TValue> left, Box<TValue> right) => false;

            public override bool Equals(object? other) => true;

            public override int GetHashCode() => 0;
        }

        internal static class Comparisons
        {
            public static bool Compare(Box<int> left, Box<int> right) => /*!*/left == right;
        }
        """;

    private const string LiftedPairSource = """
        internal readonly struct Money
        {
            public static bool operator ==(Money left, Money right) => true;

            public static bool operator !=(Money left, Money right) => false;

            public override bool Equals(object? other) => true;

            public override int GetHashCode() => 0;
        }

        internal static class Comparisons
        {
            public static bool Compare(Money? left, Money? right) => /*!*/left == right;
        }
        """;

    private const string NullableCounterpartSource = """
        internal readonly struct Money
        {
            public static bool operator ==(Money left, Money right) => true;

            public static bool operator !=(Money? left, Money? right) => false;

            public override bool Equals(object? other) => true;

            public override int GetHashCode() => 0;
        }

        internal static class Comparisons
        {
            public static bool Compare(Money left, Money right) => /*!*/left == right;
        }
        """;

    private const string MismatchedCounterpartSource = """
        internal readonly struct Money
        {
            public static bool operator ==(Money left, Money right) => true;

            public static bool operator !=(Money left, int right) => false;

            public override bool Equals(object? other) => true;

            public override int GetHashCode() => 0;
        }

        internal static class Comparisons
        {
            public static bool Compare(Money left, Money right) => /*!*/left == right;
        }
        """;

    private const string ImplicitConversionSource = """
        internal readonly struct Cents
        {
            public static implicit operator Money(Cents value) => default;
        }

        internal readonly struct Money
        {
            public static bool operator ==(Money left, Money right) => true;

            public static bool operator !=(Money left, Money right) => false;

            public override bool Equals(object? other) => true;

            public override int GetHashCode() => 0;
        }

        internal static class Comparisons
        {
            public static bool Compare(Money left, Cents right) => /*!*/left == right;
        }
        """;

    private const string RelationalSource = """
        internal static class Comparisons
        {
            public static bool Compare(int left, int right) => /*!*/left < right;
        }
        """;

    private const string NullableValueNullSource = """
        internal static class Comparisons
        {
            public static bool Compare(int? left) => /*!*/left == null;
        }
        """;

    private const string ReferenceNullSource = """
        internal static class Comparisons
        {
            public static bool Compare(string? left) => /*!*/left == null;
        }
        """;

    private const string ConstrainedGenericNullSource = """
        internal static class Comparisons
        {
            public static bool Compare<TValue>(TValue? left)
                where TValue : class => /*!*/left == null;
        }
        """;

    private const string UnconstrainedGenericNullSource = """
        internal static class Comparisons
        {
            public static bool Compare<TValue>(TValue left) => /*!*/left == null;
        }
        """;

    private const string CounterpartFieldSource = """
        internal sealed class Money
        {
            public static bool operator ==(Money? left, Money? right) => true;

            public int op_Inequality;

            public override bool Equals(object? other) => true;

            public override int GetHashCode() => 0;
        }

        internal static class Comparisons
        {
            public static bool Compare(Money left, Money right) => /*!*/left == right;
        }
        """;

    private const string CounterpartMethodSource = """
        internal sealed class Money
        {
            public static bool operator ==(Money? left, Money? right) => true;

            public static bool op_Inequality(Money? left, Money? right) => false;

            public override bool Equals(object? other) => true;

            public override int GetHashCode() => 0;
        }

        internal static class Comparisons
        {
            public static bool Compare(Money left, Money right) => /*!*/left == right;
        }
        """;

    private const string SingleParameterCounterpartSource = """
        internal readonly struct Money
        {
            public static bool operator ==(Money left, Money right) => true;

            public static bool operator !=(Money left) => false;

            public override bool Equals(object? other) => true;

            public override int GetHashCode() => 0;
        }

        internal static class Comparisons
        {
            public static bool Compare(Money left, Money right) => /*!*/left == right;
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
        using (Assert.Multiple())
        {
            _ = await Assert.That(_mutator.Id).IsEqualTo("equality");
            _ = await Assert.That(_mutator.Kind).IsEqualTo(MutationKind.EqualityOperator);
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(DisplayNames(mutations)).IsEquivalentTo(expected);
            _ = await Assert.That(mutations.Single().OperatorId).IsEqualTo(expectedId);
            _ = await Assert.That(mutations.Single().Kind).IsEqualTo(MutationKind.EqualityOperator);
        }
    }

    [Test]
    public async Task CreateMutations_UserDefinedOperatorWithCounterpart_ProducesTheCounterpart()
    {
        string[] expected = ["== => !="];
        var (mutations, tree, model, errors) = Mutate(UserDefinedPairSource);
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var method = model.GetSymbolInfo(binary).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(method?.MethodKind).IsEqualTo(MethodKind.UserDefinedOperator);
            _ = await Assert.That(DisplayNames(mutations)).IsEquivalentTo(expected);
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(method?.Name).IsEqualTo("op_Equality");
            _ = await Assert.That(method?.ContainingType.Name).IsEqualTo("Money");
            _ = await Assert.That(CounterpartCount(method, "op_Inequality")).IsEqualTo(0);
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(method?.Name).IsEqualTo("op_Inequality");
            _ = await Assert.That(method?.ContainingType.Name).IsEqualTo("Money");
            _ = await Assert.That(CounterpartCount(method, "op_Equality")).IsEqualTo(0);
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    [Test]
    public async Task ApplyTo_EqualsToNotEquals_KeepsTheSurroundingTrivia()
    {
        var expected = TriviaSource.Replace("== right", "!= right", StringComparison.Ordinal);
        var (mutations, tree, _, errors) = Mutate(TriviaSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(mutations.Single().ApplyTo(tree).ToString()).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task ApplyTo_EqualsToNotEquals_ProducesCompilableSource()
    {
        var (mutations, tree, _, _) = Mutate(CreateSource("=="));
        var mutated = mutations.Single().ApplyTo(tree).ToString();
        var compilation = CompilationFactory.Create(mutated);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutated).Contains("left != right");
            _ = await Assert.That(Describe(CompilationFactory.GetCompileErrors(compilation))).IsEqualTo(string.Empty);
        }
    }

    [Test]
    public async Task CreateMutations_RelationalExpression_ReturnsEmpty()
    {
        var (mutations, _, _, errors) = Mutate(RelationalSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    [Test]
    [Arguments(StructPairSource, "Money")]
    [Arguments(GenericPairSource, "Box")]
    public async Task CreateMutations_UserDefinedPairOnAValueType_ProducesTheCounterpart(
        string source,
        string expectedTypeName
    )
    {
        ArgumentNullException.ThrowIfNull(source);

        string[] expected = ["== => !="];
        var (mutations, tree, model, errors) = Mutate(source);
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var method = model.GetSymbolInfo(binary).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(method?.ContainingType.Name).IsEqualTo(expectedTypeName);
            _ = await Assert.That(DisplayNames(mutations)).IsEquivalentTo(expected);
        }
    }

    /// <summary>
    /// A comparison of two nullable value types is bound to the lifted form of the operator declared on
    /// the underlying type, so the counterpart has to be found on that underlying type.
    /// </summary>
    [Test]
    public async Task CreateMutations_LiftedUserDefinedPair_ProducesTheCounterpart()
    {
        string[] expected = ["== => !="];
        var (mutations, tree, model, errors) = Mutate(LiftedPairSource);
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(model.GetTypeInfo(binary.Left).Type?.ToDisplayString()).IsEqualTo("Money?");
            _ = await Assert.That(DisplayNames(mutations)).IsEquivalentTo(expected);
        }
    }

    /// <summary>
    /// The declared counterpart takes the nullable form of the operand type. Unwrapping the nullable
    /// makes the two signatures match, so the mutant binds and the mutation is offered. C# insists on a
    /// literally matching pair, which is why the fixture deliberately does not compile.
    /// </summary>
    [Test]
    public async Task CreateMutations_CounterpartDeclaredOnTheNullableOperandType_ProducesTheCounterpart()
    {
        string[] expected = ["== => !="];
        var (mutations, tree, model, _) = Mutate(NullableCounterpartSource);
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var method = model.GetSymbolInfo(binary).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(method?.Name).IsEqualTo("op_Equality");
            _ = await Assert.That(CounterpartCount(method, "op_Inequality")).IsEqualTo(1);
            _ = await Assert.That(DisplayNames(mutations)).IsEquivalentTo(expected);
        }
    }

    /// <summary>
    /// The declared counterpart has a different second parameter type, so the mutant would bind to
    /// nothing. The fixture deliberately does not compile, because C# rejects an unmatched pair.
    /// </summary>
    [Test]
    public async Task CreateMutations_CounterpartWithDifferentParameterTypes_ReturnsEmpty()
    {
        var (mutations, tree, model, _) = Mutate(MismatchedCounterpartSource);
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var method = model.GetSymbolInfo(binary).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(method?.Name).IsEqualTo("op_Equality");
            _ = await Assert.That(CounterpartCount(method, "op_Inequality")).IsEqualTo(1);
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// The right operand is a <c>Cents</c>, but the bound operator is declared on <c>Money</c> and only
    /// reached through an implicit conversion, so the counterpart is looked up on <c>Money</c>.
    /// </summary>
    [Test]
    public async Task CreateMutations_OperatorReachedThroughAnImplicitConversion_ProducesTheCounterpart()
    {
        string[] expected = ["== => !="];
        var (mutations, tree, model, errors) = Mutate(ImplicitConversionSource);
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var method = model.GetSymbolInfo(binary).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(method?.ContainingType.Name).IsEqualTo("Money");
            _ = await Assert.That(model.GetTypeInfo(binary.Right).Type?.Name).IsEqualTo("Cents");
            _ = await Assert.That(DisplayNames(mutations)).IsEquivalentTo(expected);
        }
    }

    /// <summary>
    /// A comparison against <see langword="null" /> is bound to a built-in operator, whatever the type of
    /// the other operand is, so the counterpart always exists and the mutation is offered.
    /// </summary>
    /// <param name="source">The fixture source.</param>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    [Arguments(NullableValueNullSource)]
    [Arguments(ReferenceNullSource)]
    [Arguments(ConstrainedGenericNullSource)]
    public async Task CreateMutations_ComparisonAgainstNull_ProducesTheCounterpart(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        string[] expected = ["== => !="];
        var (mutations, tree, model, errors) = Mutate(source);
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var method = model.GetSymbolInfo(binary).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(method?.MethodKind).IsNotEqualTo(MethodKind.UserDefinedOperator);
            _ = await Assert.That(DisplayNames(mutations)).IsEquivalentTo(expected);
            _ = await Assert.That(mutations.Single().OperatorId).IsEqualTo("equality.equals-to-not-equals");
        }
    }

    /// <summary>
    /// An unconstrained type parameter cannot be compared against <see langword="null" />, so the fixture
    /// deliberately does not compile. The comparison still binds, and it binds to the built-in reference
    /// equality of <see cref="object" />, not to a user-defined operator, so the mutation is offered.
    /// </summary>
    [Test]
    public async Task CreateMutations_ComparisonBoundToTheBuiltInOperator_ProducesTheCounterpart()
    {
        string[] expected = ["== => !="];
        var (mutations, tree, model, _) = Mutate(UnconstrainedGenericNullSource);
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var bound = model.GetSymbolInfo(binary).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(bound?.MethodKind).IsEqualTo(MethodKind.BuiltinOperator);
            _ = await Assert.That(bound?.ToDisplayString()).IsEqualTo("object.operator ==(object, object)");
            _ = await Assert.That(DisplayNames(mutations)).IsEquivalentTo(expected);
        }
    }

    /// <summary>
    /// The counterpart is looked up by its metadata name, so the declaring type may well contain a member
    /// of that name which is not an operator at all. Neither a field nor an ordinary method makes the
    /// mutant bind, so both fixtures keep the comparison untouched. Both deliberately do not compile,
    /// because C# insists on a declared <c>!=</c> next to the <c>==</c>.
    /// </summary>
    /// <param name="source">The fixture source.</param>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    [Arguments(CounterpartFieldSource)]
    [Arguments(CounterpartMethodSource)]
    public async Task CreateMutations_MemberNamedLikeTheCounterpartIsNoOperator_ReturnsEmpty(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var (mutations, tree, model, _) = Mutate(source);
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var method = model.GetSymbolInfo(binary).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(method?.Name).IsEqualTo("op_Equality");
            _ = await Assert.That(CounterpartCount(method, "op_Inequality")).IsEqualTo(1);
            _ = await Assert.That(CounterpartOperatorCount(method, "op_Inequality")).IsEqualTo(0);
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// The declared counterpart takes a single parameter, so the mutant would bind to nothing. The fixture
    /// deliberately does not compile, because a binary operator needs two parameters.
    /// </summary>
    [Test]
    public async Task CreateMutations_CounterpartWithADifferentParameterCount_ReturnsEmpty()
    {
        int[] expectedParameterCounts = [1];
        var (mutations, tree, model, _) = Mutate(SingleParameterCounterpartSource);
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var method = model.GetSymbolInfo(binary).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(method?.Parameters.Length).IsEqualTo(2);
            _ = await Assert.That(CounterpartCount(method, "op_Inequality")).IsEqualTo(1);
            _ = await Assert
                .That(CounterpartParameterCounts(method, "op_Inequality"))
                .IsEquivalentTo(expectedParameterCounts);
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    private static string CreateSource(string source) =>
        ComparisonTemplate.Replace(OperatorPlaceholder, source, StringComparison.Ordinal);

    private static int? CounterpartCount(IMethodSymbol? method, string name) =>
        method?.ContainingType.GetMembers(name).Length;

    private static int CounterpartOperatorCount(IMethodSymbol? method, string name) =>
        Counterparts(method, name).Count(counterpart => counterpart.MethodKind == MethodKind.UserDefinedOperator);

    private static int[] CounterpartParameterCounts(IMethodSymbol? method, string name) =>
        [.. Counterparts(method, name).Select(counterpart => counterpart.Parameters.Length)];

    private static ImmutableArray<IMethodSymbol> Counterparts(IMethodSymbol? method, string name)
    {
        if (method is null)
        {
            return [];
        }

        return [.. method.ContainingType.GetMembers(name).OfType<IMethodSymbol>()];
    }

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
