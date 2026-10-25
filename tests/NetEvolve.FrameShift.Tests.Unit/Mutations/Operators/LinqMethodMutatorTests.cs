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
/// Covers <see cref="LinqMethodMutator" />, which mutates well known <c>System.Linq.Enumerable</c> method
/// calls into their counterpart.
/// </summary>
public class LinqMethodMutatorTests
{
    private const string CallPlaceholder = "CALL";

    private const string QueryTemplate = """
        using System;
        using System.Collections.Generic;
        using System.Linq;

        internal static class Queries
        {
            public static object Convert(IEnumerable<int> values) => CALL;
        }
        """;

    private const string UnrelatedExtensionSource = """
        using System;

        internal interface IBag<T>
        {
        }

        internal static class BagExtensions
        {
            public static bool Any<T>(this IBag<T> bag, Func<T, bool> predicate) => true;
        }

        internal static class Queries
        {
            public static bool Convert(IBag<int> bag) => bag.Any(x => x > 0);
        }
        """;

    private const string UserDefinedTypeSource = """
        using System;

        internal sealed class Bag
        {
            public int First() => 0;

            public bool Any(Func<int, bool> predicate) => true;
        }

        internal static class Queries
        {
            public static object Convert(Bag bag) => bag.First();
        }
        """;

    private const string ParameterlessAnySource = """
        using System.Collections.Generic;
        using System.Linq;

        internal static class Queries
        {
            public static bool Convert(IEnumerable<int> values) => values.Any();
        }
        """;

    private const string StaticCallSource = """
        using System.Collections.Generic;
        using System.Linq;

        internal static class Queries
        {
            public static int Convert(IEnumerable<int> values) => Enumerable.First(values);
        }
        """;

    private static readonly LinqMethodMutator _mutator = new LinqMethodMutator();

    [Test]
    public async Task SupportedSyntaxKinds_Always_IsTheInvocationKind()
    {
        SyntaxKind[] expected = [SyntaxKind.InvocationExpression];

        _ = await Assert.That(_mutator.SupportedSyntaxKinds.ToArray()).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Metadata_Always_IdentifiesTheLinqMethodFamily()
    {
        using (Assert.Multiple())
        {
            _ = await Assert.That(_mutator.Id).IsEqualTo("linq.method");
            _ = await Assert.That(_mutator.Kind).IsEqualTo(MutationKind.LinqMethod);
        }
    }

    /// <summary>
    /// Every pair with a single counterpart is renamed in both directions, whether the call takes no
    /// argument or a predicate, key selector or count of matching shape.
    /// </summary>
    /// <param name="call">The extension method call the fixture makes.</param>
    /// <param name="methodName">The name of the called method.</param>
    /// <param name="expectedId">The expected operator id of the produced mutation.</param>
    /// <param name="expectedName">The expected display name of the produced mutation.</param>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    [Arguments("values.All(x => x > 0)", "All", "linq.method.all-to-any", "All => Any")]
    [Arguments("values.Any(x => x > 0)", "Any", "linq.method.any-to-all", "Any => All")]
    [Arguments("values.First()", "First", "linq.method.first-to-first-or-default", "First => FirstOrDefault")]
    [Arguments(
        "values.FirstOrDefault()",
        "FirstOrDefault",
        "linq.method.first-or-default-to-first",
        "FirstOrDefault => First"
    )]
    [Arguments("values.First(x => x > 0)", "First", "linq.method.first-to-first-or-default", "First => FirstOrDefault")]
    [Arguments(
        "values.FirstOrDefault(x => x > 0)",
        "FirstOrDefault",
        "linq.method.first-or-default-to-first",
        "FirstOrDefault => First"
    )]
    [Arguments("values.Single()", "Single", "linq.method.single-to-single-or-default", "Single => SingleOrDefault")]
    [Arguments(
        "values.SingleOrDefault()",
        "SingleOrDefault",
        "linq.method.single-or-default-to-single",
        "SingleOrDefault => Single"
    )]
    [Arguments("values.Last()", "Last", "linq.method.last-to-last-or-default", "Last => LastOrDefault")]
    [Arguments(
        "values.LastOrDefault()",
        "LastOrDefault",
        "linq.method.last-or-default-to-last",
        "LastOrDefault => Last"
    )]
    [Arguments(
        "values.OrderBy(x => x)",
        "OrderBy",
        "linq.method.order-by-to-order-by-descending",
        "OrderBy => OrderByDescending"
    )]
    [Arguments(
        "values.OrderByDescending(x => x)",
        "OrderByDescending",
        "linq.method.order-by-descending-to-order-by",
        "OrderByDescending => OrderBy"
    )]
    [Arguments(
        "values.OrderBy(x => x).ThenBy(x => x)",
        "ThenBy",
        "linq.method.then-by-to-then-by-descending",
        "ThenBy => ThenByDescending"
    )]
    [Arguments(
        "values.OrderBy(x => x).ThenByDescending(x => x)",
        "ThenByDescending",
        "linq.method.then-by-descending-to-then-by",
        "ThenByDescending => ThenBy"
    )]
    [Arguments("values.Min()", "Min", "linq.method.min-to-max", "Min => Max")]
    [Arguments("values.Max()", "Max", "linq.method.max-to-min", "Max => Min")]
#if NET6_0_OR_GREATER
    [Arguments("values.MinBy(x => x)", "MinBy", "linq.method.min-by-to-max-by", "MinBy => MaxBy")]
    [Arguments("values.MaxBy(x => x)", "MaxBy", "linq.method.max-by-to-min-by", "MaxBy => MinBy")]
#endif
    [Arguments("values.Take(2)", "Take", "linq.method.take-to-skip", "Take => Skip")]
#if NET6_0_OR_GREATER
    [Arguments("values.SkipLast(2)", "SkipLast", "linq.method.skip-last-to-skip", "SkipLast => Skip")]
#endif
    [Arguments(
        "values.SkipWhile(x => x > 0)",
        "SkipWhile",
        "linq.method.skip-while-to-take-while",
        "SkipWhile => TakeWhile"
    )]
    [Arguments(
        "values.TakeWhile(x => x > 0)",
        "TakeWhile",
        "linq.method.take-while-to-skip-while",
        "TakeWhile => SkipWhile"
    )]
    public async Task CreateMutations_SingleCounterpartCall_ProducesThatCounterpart(
        string call,
        string methodName,
        string expectedId,
        string expectedName
    )
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(methodName);
        ArgumentNullException.ThrowIfNull(expectedId);
        ArgumentNullException.ThrowIfNull(expectedName);

        var (mutations, _, _, errors) = MutateCall(call, methodName);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(mutations.Length).IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo(expectedId);
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo(expectedName);
            _ = await Assert.That(mutations[0].Kind).IsEqualTo(MutationKind.LinqMethod);
        }
    }

#if NET6_0_OR_GREATER
    /// <summary>
    /// <c>Skip</c> is the one method of this operator with two counterparts, so it is the only call that
    /// produces two mutations instead of one. <c>SkipLast</c> only exists from .NET 6 on, so this test -
    /// and the two-counterpart behaviour it pins - only applies to those target frameworks; on the
    /// classic frameworks <c>Skip</c> falls back to its single counterpart, <c>Take</c>, which the
    /// parameterised <see cref="CreateMutations_SingleCounterpartCall_ProducesThatCounterpart" /> already
    /// covers unconditionally.
    /// </summary>
    [Test]
    public async Task CreateMutations_Skip_ProducesTakeAndSkipLast()
    {
        string[] expectedNames = ["Skip => Take", "Skip => SkipLast"];
        string[] expectedIds = ["linq.method.skip-to-take", "linq.method.skip-to-skip-last"];
        var (mutations, _, _, errors) = MutateCall("values.Skip(2)", "Skip");

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert
                .That(Join(mutations.Select(mutation => mutation.DisplayName)))
                .IsEqualTo(Join(expectedNames));
            _ = await Assert.That(Join(mutations.Select(mutation => mutation.OperatorId))).IsEqualTo(Join(expectedIds));
        }
    }
#else
    /// <summary>
    /// <c>SkipLast</c> does not exist before .NET 6, so on the classic frameworks <c>Skip</c> only ever
    /// offers its other counterpart, <c>Take</c> - the same single-counterpart shape every other pair
    /// has.
    /// </summary>
    [Test]
    public async Task CreateMutations_Skip_ProducesOnlyTake()
    {
        string[] expectedNames = ["Skip => Take"];
        string[] expectedIds = ["linq.method.skip-to-take"];
        var (mutations, _, _, errors) = MutateCall("values.Skip(2)", "Skip");

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert
                .That(Join(mutations.Select(mutation => mutation.DisplayName)))
                .IsEqualTo(Join(expectedNames));
            _ = await Assert.That(Join(mutations.Select(mutation => mutation.OperatorId))).IsEqualTo(Join(expectedIds));
        }
    }
#endif

    /// <summary>
    /// The rewrite renames the call and reuses the rest of it unchanged, and the produced mutant compiles.
    /// </summary>
    [Test]
    public async Task ApplyTo_First_ProducesTheRenamedCallAndCompiles()
    {
        var expected = "values.FirstOrDefault()";
        var (mutations, tree, _, errors) = MutateCall("values.First()", "First");
        var mutated = mutations[0].ApplyTo(tree).ToString();
        var compilation = CompilationFactory.Create(mutated);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(mutated).Contains(expected);
            _ = await Assert.That(Describe(CompilationFactory.GetCompileErrors(compilation))).IsEqualTo(string.Empty);
        }
    }

    /// <summary>
    /// A call written as <c>Enumerable.First(values)</c> binds to the very same method symbol as
    /// <c>values.First()</c>, so it is renamed exactly the same way.
    /// </summary>
    [Test]
    public async Task CreateMutations_StaticCallSyntax_ProducesTheSameCounterpart()
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(StaticCallSource);
        var node = SyntaxNodeLocator.FindFirst<InvocationExpressionSyntax>(tree);
        var created = _mutator.CreateMutations(node, semanticModel, CancellationToken.None);
        ImmutableArray<Mutation> mutations = [.. created];
        var mutated = mutations[0].ApplyTo(tree).ToString();

        using (Assert.Multiple())
        {
            _ = await Assert.That(Describe(CompilationFactory.GetCompileErrors(compilation))).IsEqualTo(string.Empty);
            _ = await Assert.That(mutations.Length).IsEqualTo(1);
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("First => FirstOrDefault");
            _ = await Assert.That(mutated).Contains("Enumerable.FirstOrDefault(values)");
        }
    }

    /// <summary>
    /// <c>Any</c> has a parameterless overload that <c>All</c> does not declare, so a bare <c>Any()</c>
    /// call has no compatible counterpart and is left untouched.
    /// </summary>
    [Test]
    public async Task CreateMutations_ParameterlessAny_ReturnsEmpty()
    {
        var (mutations, tree, model, errors) = Mutate(
            ParameterlessAnySource,
            SyntaxNodeLocator.FindFirst<InvocationExpressionSyntax>
        );
        var method = model.GetSymbolInfo(FindCall(tree, "Any")).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(method?.Parameters.Length).IsEqualTo(0);
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

#if NET6_0_OR_GREATER
    /// <summary>
    /// <c>FirstOrDefault(defaultValue)</c> takes a plain value, not a predicate, and <c>First</c> has no
    /// overload of that shape, so the call is left untouched even though the name and the argument count
    /// match. That overload of <c>FirstOrDefault</c> was only added in .NET 6, so the fixture only
    /// compiles from that target framework on.
    /// </summary>
    [Test]
    public async Task CreateMutations_FirstOrDefaultWithADefaultValue_ReturnsEmpty()
    {
        var (mutations, _, _, errors) = MutateCall("values.FirstOrDefault(-1)", "FirstOrDefault");

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }
#endif

    /// <summary>
    /// An unrelated extension method sharing the name and the shape of one of the well known methods is
    /// declared on another type, so it is never mutated.
    /// </summary>
    [Test]
    public async Task CreateMutations_UnrelatedExtensionMethod_ReturnsEmpty()
    {
        var (mutations, tree, model, errors) = Mutate(
            UnrelatedExtensionSource,
            SyntaxNodeLocator.FindFirst<InvocationExpressionSyntax>
        );
        var node = SyntaxNodeLocator.FindFirst<InvocationExpressionSyntax>(tree);
        var method = model.GetSymbolInfo(node).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(method?.Name).IsEqualTo("Any");
            _ = await Assert.That(method?.ContainingType.Name).IsEqualTo("BagExtensions");
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// An ordinary instance method of a user-defined type, sharing both name and shape with a well known
    /// method, is not bound to <c>System.Linq.Enumerable</c> and is therefore left untouched.
    /// </summary>
    [Test]
    public async Task CreateMutations_UserDefinedType_ReturnsEmpty()
    {
        var (mutations, tree, model, errors) = Mutate(
            UserDefinedTypeSource,
            SyntaxNodeLocator.FindFirst<InvocationExpressionSyntax>
        );
        var node = SyntaxNodeLocator.FindFirst<InvocationExpressionSyntax>(tree);
        var method = model.GetSymbolInfo(node).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(method?.Name).IsEqualTo("First");
            _ = await Assert.That(method?.ContainingType.Name).IsEqualTo("Bag");
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    private static string CreateSource(string call) =>
        QueryTemplate.Replace(CallPlaceholder, call, StringComparison.Ordinal);

    private static InvocationExpressionSyntax FindCall(SyntaxTree tree, string methodName) =>
        SyntaxNodeLocator.FindFirst<InvocationExpressionSyntax>(
            tree,
            invocation =>
                invocation.Expression is MemberAccessExpressionSyntax access
                && string.Equals(access.Name.Identifier.ValueText, methodName, StringComparison.Ordinal)
        );

    private static string Join(IEnumerable<string> values) => string.Join(", ", values);

    private static (ImmutableArray<Mutation> Mutations, SyntaxTree Tree, SemanticModel Model, string Errors) MutateCall(
        string call,
        string methodName
    )
    {
        var source = CreateSource(call);
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var node = FindCall(tree, methodName);
        var created = _mutator.CreateMutations(node, semanticModel, CancellationToken.None);
        ImmutableArray<Mutation> mutations = [.. created];

        return (mutations, tree, semanticModel, Describe(CompilationFactory.GetCompileErrors(compilation)));
    }

    private static (ImmutableArray<Mutation> Mutations, SyntaxTree Tree, SemanticModel Model, string Errors) Mutate(
        string source,
        Func<SyntaxTree, SyntaxNode> selector
    )
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var node = selector(tree);
        var created = _mutator.CreateMutations(node, semanticModel, CancellationToken.None);
        ImmutableArray<Mutation> mutations = [.. created];

        return (mutations, tree, semanticModel, Describe(CompilationFactory.GetCompileErrors(compilation)));
    }

    private static string Describe(ImmutableArray<Diagnostic> errors) =>
        string.Join(", ", errors.Select(error => error.GetMessage(CultureInfo.InvariantCulture)));
}
