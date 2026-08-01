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
/// Covers <see cref="MathMethodMutator" />, which mutates calls to well-known <see cref="System.Math" />
/// static methods along the co-function pairs <c>Sin</c> / <c>Cos</c>, <c>Asin</c> / <c>Acos</c>,
/// <c>Tan</c> / <c>Atan</c> and <c>Sinh</c> / <c>Cosh</c>, the extremes <c>Min</c> / <c>Max</c>, the
/// rounding directions <c>Floor</c> / <c>Ceiling</c>, and drops the call to <c>Abs</c> entirely.
/// </summary>
public class MathMethodMutatorTests
{
    private const string TypePlaceholder = "TYPE";
    private const string MethodPlaceholder = "METHOD";

    private const string UnaryCallTemplate = """
        internal static class Computations
        {
            public static TYPE Compute(TYPE value) => System.Math.METHOD(value);
        }
        """;

    private const string BinaryCallTemplate = """
        internal static class Computations
        {
            public static TYPE Compute(TYPE a, TYPE b) => System.Math.METHOD(a, b);
        }
        """;

    private const string UserDefinedTemplate = """
        internal sealed class Calculator
        {
            public static TYPE METHOD(TYPE value) => value;
        }

        internal static class Computations
        {
            public static TYPE Compute(TYPE value) => Calculator.METHOD(value);
        }
        """;

    private const string TriviaSource = """
        internal static class Computations
        {
            // a comment above the call
            public static double Compute(double value)
            {
                return System.Math /* between */.Sin(value); // a comment behind the call
            }
        }
        """;

    private const string ExtensionMethodSource = """
        internal static class MathExtensions
        {
            public static double Sin(this double value, int repeat) => value;
        }

        internal static class Computations
        {
            public static double Compute(double value) => /*!*/value.Sin(2);
        }
        """;

    /// <summary>
    /// A call brought in through <c>using static System.Math;</c> is invoked as a bare identifier rather
    /// than through a member access, so it never reaches the semantic-model lookup at all.
    /// </summary>
    private const string BareIdentifierSource = """
        using static System.Math;

        internal static class Computations
        {
            public static double Compute(double value) => /*!*/Sin(value);
        }
        """;

    /// <summary>
    /// A call the semantic model cannot resolve to any method at all - here, an ambiguous extension
    /// method brought in from two namespaces - binds to no <see cref="IMethodSymbol" />, which the
    /// operator has to reject rather than crash on. Mirrors <c>StringMethodMutatorTests</c>' own
    /// unresolved-invocation fixture, adapted to a <see cref="double" /> receiver.
    /// </summary>
    private const string UnresolvedCallSource = """
        using NamespaceA;
        using NamespaceB;

        namespace NamespaceA
        {
            internal static class Ext
            {
                internal static void Handle(this double value) { }
            }
        }

        namespace NamespaceB
        {
            internal static class Ext
            {
                internal static void Handle(this double value) { }
            }
        }

        internal static class Computations
        {
            public static void Compute(double value) => /*!*/value.Handle();
        }
        """;

    private static readonly MathMethodMutator _mutator = new MathMethodMutator();

    [Test]
    public async Task SupportedSyntaxKinds_Always_IsTheInvocationKind()
    {
        SyntaxKind[] expected = [SyntaxKind.InvocationExpression];

        _ = await Assert.That(_mutator.SupportedSyntaxKinds.ToArray()).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Metadata_Always_IdentifiesTheMathMethodFamily()
    {
        using (Assert.Multiple())
        {
            _ = await Assert.That(_mutator.Id).IsEqualTo("math.method");
            _ = await Assert.That(_mutator.Kind).IsEqualTo(MutationKind.MathMethod);
        }
    }

    /// <summary>
    /// Every trigonometric co-function is mutated into its counterpart. <see cref="System.Math" /> only
    /// declares these six as <c>double</c> overloads, so <c>double</c> is the only type exercised here.
    /// </summary>
    [Test]
    [Arguments("Sin", "Cos", "math.method.sin-to-cos")]
    [Arguments("Cos", "Sin", "math.method.cos-to-sin")]
    [Arguments("Asin", "Acos", "math.method.asin-to-acos")]
    [Arguments("Acos", "Asin", "math.method.acos-to-asin")]
    [Arguments("Tan", "Atan", "math.method.tan-to-atan")]
    [Arguments("Atan", "Tan", "math.method.atan-to-tan")]
    [Arguments("Sinh", "Cosh", "math.method.sinh-to-cosh")]
    [Arguments("Cosh", "Sinh", "math.method.cosh-to-sinh")]
    public async Task CreateMutations_TrigCoFunction_ProducesTheCounterpart(
        string source,
        string target,
        string expectedId
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(expectedId);

        var (mutations, node, _, _, errors) = MutateCall(CreateUnarySource("double", source), source);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(Join(DisplayNames(mutations))).IsEqualTo($"{source} => {target}");
            _ = await Assert.That(Join(OperatorIds(mutations))).IsEqualTo(expectedId);
            _ = await Assert.That(Join(Kinds(mutations))).IsEqualTo("MathMethod");
            _ = await Assert.That(mutations[0].Original.ToString()).IsEqualTo($"System.Math.{source}(value)");
            _ = await Assert.That(mutations[0].Location.SourceSpan).IsEqualTo(node.Span);
        }
    }

    /// <summary>
    /// The rewrite renames the call and keeps the argument list, so every mutant of a trig co-function
    /// still compiles.
    /// </summary>
    [Test]
    [Arguments("Sin", "Cos")]
    [Arguments("Cos", "Sin")]
    public async Task ApplyTo_TrigCoFunction_ProducesTheRenamedCall(string source, string target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var (mutations, _, tree, _, errors) = MutateCall(CreateUnarySource("double", source), source);
        var mutated = mutations.Single().ApplyTo(tree).ToString();
        var compilation = CompilationFactory.Create(mutated);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(mutated).IsEqualTo(CreateUnarySource("double", target));
            _ = await Assert.That(Describe(CompilationFactory.GetCompileErrors(compilation))).IsEqualTo(string.Empty);
        }
    }

    /// <summary>
    /// <c>Min</c> and <c>Max</c> are mutated into each other for both <c>double</c> and <c>decimal</c>,
    /// the two types <see cref="System.Math" /> declares overloads for that the issue asks to cover.
    /// </summary>
    [Test]
    [Arguments("double", "Min", "Max")]
    [Arguments("double", "Max", "Min")]
    [Arguments("decimal", "Min", "Max")]
    [Arguments("decimal", "Max", "Min")]
    public async Task CreateMutations_MinMax_ProducesTheCounterpart(string type, string source, string target)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var (mutations, node, _, _, errors) = MutateCall(CreateBinarySource(type, source), source);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(Join(DisplayNames(mutations))).IsEqualTo($"{source} => {target}");
            _ = await Assert.That(mutations[0].Original.ToString()).IsEqualTo($"System.Math.{source}(a, b)");
            _ = await Assert.That(mutations[0].Location.SourceSpan).IsEqualTo(node.Span);
        }
    }

    [Test]
    [Arguments("double", "Min", "Max")]
    [Arguments("decimal", "Max", "Min")]
    public async Task ApplyTo_MinMax_ProducesTheRenamedCall(string type, string source, string target)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var (mutations, _, tree, _, errors) = MutateCall(CreateBinarySource(type, source), source);
        var mutated = mutations.Single().ApplyTo(tree).ToString();
        var compilation = CompilationFactory.Create(mutated);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(mutated).IsEqualTo(CreateBinarySource(type, target));
            _ = await Assert.That(Describe(CompilationFactory.GetCompileErrors(compilation))).IsEqualTo(string.Empty);
        }
    }

    /// <summary>
    /// <c>Floor</c> and <c>Ceiling</c> are mutated into each other for both <c>double</c> and
    /// <c>decimal</c>.
    /// </summary>
    [Test]
    [Arguments("double", "Floor", "Ceiling")]
    [Arguments("double", "Ceiling", "Floor")]
    [Arguments("decimal", "Floor", "Ceiling")]
    [Arguments("decimal", "Ceiling", "Floor")]
    public async Task CreateMutations_FloorCeiling_ProducesTheCounterpart(string type, string source, string target)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var (mutations, node, _, _, errors) = MutateCall(CreateUnarySource(type, source), source);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(Join(DisplayNames(mutations))).IsEqualTo($"{source} => {target}");
            _ = await Assert.That(mutations[0].Original.ToString()).IsEqualTo($"System.Math.{source}(value)");
            _ = await Assert.That(mutations[0].Location.SourceSpan).IsEqualTo(node.Span);
        }
    }

    [Test]
    [Arguments("double", "Floor", "Ceiling")]
    [Arguments("decimal", "Ceiling", "Floor")]
    public async Task ApplyTo_FloorCeiling_ProducesTheRenamedCall(string type, string source, string target)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var (mutations, _, tree, _, errors) = MutateCall(CreateUnarySource(type, source), source);
        var mutated = mutations.Single().ApplyTo(tree).ToString();
        var compilation = CompilationFactory.Create(mutated);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(mutated).IsEqualTo(CreateUnarySource(type, target));
            _ = await Assert.That(Describe(CompilationFactory.GetCompileErrors(compilation))).IsEqualTo(string.Empty);
        }
    }

    /// <summary>
    /// <c>Math.Abs</c> has no counterpart of its own: the whole call is dropped in favour of its single
    /// argument expression, for both <c>double</c> and <c>decimal</c>.
    /// </summary>
    [Test]
    [Arguments("double")]
    [Arguments("decimal")]
    public async Task CreateMutations_Abs_DropsTheCall(string type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var (mutations, node, _, _, errors) = MutateCall(CreateUnarySource(type, "Abs"), "Abs");

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(mutations.Length).IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("math.method.abs.remove");
            _ = await Assert.That(mutations[0].Kind).IsEqualTo(MutationKind.MathMethod);
            _ = await Assert.That(mutations[0].Original.ToString()).IsEqualTo("System.Math.Abs(value)");
            _ = await Assert.That(mutations[0].Location.SourceSpan).IsEqualTo(node.Span);
        }
    }

    [Test]
    [Arguments("double")]
    [Arguments("decimal")]
    public async Task ApplyTo_Abs_ReplacesTheCallWithItsArgument(string type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var expected = CreateUnarySource(type, "Abs")
            .Replace("System.Math.Abs(value)", "value", StringComparison.Ordinal);
        var (mutations, _, tree, _, errors) = MutateCall(CreateUnarySource(type, "Abs"), "Abs");
        var mutated = mutations.Single().ApplyTo(tree).ToString();
        var compilation = CompilationFactory.Create(mutated);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(mutated).IsEqualTo(expected);
            _ = await Assert.That(Describe(CompilationFactory.GetCompileErrors(compilation))).IsEqualTo(string.Empty);
        }
    }

    [Test]
    public async Task ApplyTo_Sin_KeepsTheSurroundingTrivia()
    {
        var expected = TriviaSource.Replace(
            "Math /* between */.Sin(",
            "Math /* between */.Cos(",
            StringComparison.Ordinal
        );
        var (mutations, _, tree, _, errors) = MutateCall(TriviaSource, "Sin");
        var mutated = mutations.Single().ApplyTo(tree).ToString();

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(mutated).IsEqualTo(expected);
        }
    }

    /// <summary>
    /// A user-defined type may declare a static method with a name and shape identical to a known
    /// <see cref="System.Math" /> method; none of them is a call to <see cref="System.Math" /> itself, so
    /// none of them is mutated.
    /// </summary>
    [Test]
    [Arguments("double", "Sin")]
    [Arguments("double", "Min")]
    [Arguments("decimal", "Floor")]
    [Arguments("decimal", "Abs")]
    public async Task CreateMutations_MethodOnAnotherType_ReturnsEmpty(string type, string methodName)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(methodName);

        var source = UserDefinedTemplate
            .Replace(TypePlaceholder, type, StringComparison.Ordinal)
            .Replace(MethodPlaceholder, methodName, StringComparison.Ordinal);
        var (mutations, node, _, model, errors) = MutateCall(source, methodName);
        var method = model.GetSymbolInfo(node).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(method?.Name).IsEqualTo(methodName);
            _ = await Assert.That(method?.ContainingType.Name).IsEqualTo("Calculator");
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// An extension method may well be called <c>Sin</c>, but it is declared by another type, so it is
    /// none of the calls this operator knows.
    /// </summary>
    [Test]
    public async Task CreateMutations_ExtensionMethodNamedLikeAMathMethod_ReturnsEmpty()
    {
        var (mutations, node, _, model, errors) = Mutate(
            ExtensionMethodSource,
            SyntaxNodeLocator.FindMarked<InvocationExpressionSyntax>
        );
        var method = model.GetSymbolInfo(node).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(method?.IsExtensionMethod).IsTrue();
            _ = await Assert.That(method?.ContainingType.Name).IsEqualTo("MathExtensions");
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// A call to a well-known <see cref="System.Math" /> method brought in via <c>using static</c> is
    /// invoked as a bare identifier, not as a member access; the operator's first guard rejects it before
    /// the semantic model is ever consulted.
    /// </summary>
    [Test]
    public async Task CreateMutations_BareIdentifierInvocation_ReturnsEmpty()
    {
        var (mutations, node, _, model, errors) = Mutate(
            BareIdentifierSource,
            SyntaxNodeLocator.FindMarked<InvocationExpressionSyntax>
        );
        var invocation = (InvocationExpressionSyntax)node;
        var method = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(invocation.Expression).IsNotAssignableTo<MemberAccessExpressionSyntax>();
            _ = await Assert.That(method?.Name).IsEqualTo("Sin");
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// A call the semantic model cannot resolve to a single method - here, an ambiguous extension method
    /// call - binds to no <see cref="IMethodSymbol" /> at all, which the operator has to reject rather
    /// than crash on.
    /// </summary>
    [Test]
    public async Task CreateMutations_UnresolvedInvocation_ReturnsEmpty()
    {
        var (mutations, node, _, model, errors) = Mutate(
            UnresolvedCallSource,
            SyntaxNodeLocator.FindMarked<InvocationExpressionSyntax>
        );
        var invocation = (InvocationExpressionSyntax)node;
        var symbol = model.GetSymbolInfo(invocation).Symbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsNotEqualTo(string.Empty);
            _ = await Assert.That(symbol as IMethodSymbol).IsNull();
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// <see cref="System.Math" /> declares every counterpart pair this operator knows with matching
    /// parameter shapes on both sides - <c>Sin</c>/<c>Cos</c>, <c>Asin</c>/<c>Acos</c>, <c>Tan</c>/
    /// <c>Atan</c> and <c>Sinh</c>/<c>Cosh</c> only for <see cref="double" />, and <c>Min</c>/<c>Max</c>
    /// as well as <c>Floor</c>/<c>Ceiling</c> for every numeric type either declares an overload for.
    /// There is therefore no well-known <see cref="System.Math" /> call whose counterpart name exists but
    /// whose parameter shape does not match, which makes the <c>!HasMatchingOverload</c> branch in
    /// <see cref="MathMethodMutator" /> unreachable through this operator's own counterpart table; it
    /// only guards against a hypothetical future counterpart pair that is not symmetrical this way. This
    /// test pins down that symmetry for every pair the operator currently maps, so a change that breaks
    /// it is caught here rather than by a missing mutation somewhere else.
    /// </summary>
    [Test]
    [Arguments("Sin", "Cos")]
    [Arguments("Asin", "Acos")]
    [Arguments("Tan", "Atan")]
    [Arguments("Sinh", "Cosh")]
    [Arguments("Min", "Max")]
    [Arguments("Floor", "Ceiling")]
    public async Task MathCounterpartPairs_Always_DeclareMatchingOverloadsBothWays(string first, string second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        var mathType = typeof(Math);
        var firstShapes = ParameterShapes(mathType, first);
        var secondShapes = ParameterShapes(mathType, second);

        using (Assert.Multiple())
        {
            _ = await Assert.That(firstShapes).IsNotEmpty();
            _ = await Assert.That(secondShapes).IsEquivalentTo(firstShapes);
        }
    }

    private static string[] ParameterShapes(Type type, string methodName) =>
        [
            .. type.GetMethods()
                .Where(candidate => string.Equals(candidate.Name, methodName, StringComparison.Ordinal))
                .Select(candidate =>
                    string.Join(",", candidate.GetParameters().Select(parameter => parameter.ParameterType.Name))
                )
                .OrderBy(shape => shape, StringComparer.Ordinal),
        ];

    private static string CreateUnarySource(string type, string methodName) =>
        UnaryCallTemplate
            .Replace(TypePlaceholder, type, StringComparison.Ordinal)
            .Replace(MethodPlaceholder, methodName, StringComparison.Ordinal);

    private static string CreateBinarySource(string type, string methodName) =>
        BinaryCallTemplate
            .Replace(TypePlaceholder, type, StringComparison.Ordinal)
            .Replace(MethodPlaceholder, methodName, StringComparison.Ordinal);

    private static InvocationExpressionSyntax FindCall(SyntaxTree tree, string methodName) =>
        SyntaxNodeLocator.FindFirst<InvocationExpressionSyntax>(
            tree,
            invocation =>
                invocation.Expression is MemberAccessExpressionSyntax access
                && string.Equals(access.Name.Identifier.ValueText, methodName, StringComparison.Ordinal)
        );

    private static string[] DisplayNames(ImmutableArray<Mutation> mutations) =>
        [.. mutations.Select(mutation => mutation.DisplayName)];

    private static string[] OperatorIds(ImmutableArray<Mutation> mutations) =>
        [.. mutations.Select(mutation => mutation.OperatorId)];

    private static string[] Kinds(ImmutableArray<Mutation> mutations) =>
        [.. mutations.Select(mutation => mutation.Kind.ToString())];

    private static string Join(IEnumerable<string> values) => string.Join(", ", values);

    private static (
        ImmutableArray<Mutation> Mutations,
        SyntaxNode Node,
        SyntaxTree Tree,
        SemanticModel Model,
        string Errors
    ) MutateCall(string source, string methodName) => Mutate(source, syntaxTree => FindCall(syntaxTree, methodName));

    private static (
        ImmutableArray<Mutation> Mutations,
        SyntaxNode Node,
        SyntaxTree Tree,
        SemanticModel Model,
        string Errors
    ) Mutate(string source, Func<SyntaxTree, SyntaxNode> selector)
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var node = selector(tree);
        var created = _mutator.CreateMutations(node, semanticModel, CancellationToken.None);
        ImmutableArray<Mutation> mutations = [.. created];

        return (mutations, node, tree, semanticModel, Describe(CompilationFactory.GetCompileErrors(compilation)));
    }

    private static string Describe(ImmutableArray<Diagnostic> errors) =>
        string.Join(", ", errors.Select(error => error.GetMessage(CultureInfo.InvariantCulture)));
}
