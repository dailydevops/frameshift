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
/// Covers the collection initializer operator: emptying a non-empty array initializer, collection
/// initializer or collection expression, filling an empty collection expression with
/// <see langword="default" /> where it is safe, and leaving every already-empty node untouched in the
/// "make empty" direction.
/// </summary>
public class CollectionInitializerMutatorTests
{
    private const string ArrayInitializerSource = """
        internal static class Sample
        {
            public static int[] Values => new[] { 1, 2, 3 };
        }
        """;

    private const string EmptyArrayInitializerSource = """
        internal static class Sample
        {
            public static int[] Values => new int[] { };
        }
        """;

    private const string SingleElementArrayInitializerSource = """
        internal static class Sample
        {
            public static int[] Values => new[] { 1 };
        }
        """;

    private const string CollectionInitializerSource = """
        using System.Collections.Generic;

        internal static class Sample
        {
            public static List<int> Values => new List<int> { 1, 2, 3 };
        }
        """;

    private const string EmptyCollectionInitializerSource = """
        using System.Collections.Generic;

        internal static class Sample
        {
            public static List<int> Values => new List<int> { };
        }
        """;

    private const string CollectionExpressionSource = """
        internal static class Sample
        {
            public static int[] Values => [1, 2, 3];
        }
        """;

    private const string EmptyCollectionExpressionValueTypeSource = """
        using System.Collections.Generic;

        internal static class Sample
        {
            public static List<int> Values => [];
        }
        """;

    private const string EmptyCollectionExpressionArraySource = """
        internal static class Sample
        {
            public static int[] Values => [];
        }
        """;

    private const string EmptyCollectionExpressionNullableReferenceSource = """
        using System.Collections.Generic;

        internal static class Sample
        {
            public static List<string?> Values => [];
        }
        """;

    private const string EmptyCollectionExpressionNonNullableReferenceSource = """
        using System.Collections.Generic;

        internal static class Sample
        {
            public static List<string> Values => [];
        }
        """;

    private const string EmptyCollectionExpressionObjectSource = """
        using System.Collections.Generic;

        internal static class Sample
        {
            public static List<object> Values => [];
        }
        """;

    private const string EmptyCollectionExpressionDynamicSource = """
        using System.Collections.Generic;

        internal static class Sample
        {
            public static List<dynamic> Values => [];
        }
        """;

    private const string EmptyCollectionExpressionInterfaceTargetSource = """
        using System.Collections.Generic;

        internal static class Sample
        {
            public static IEnumerable<int> Values => [];
        }
        """;

    private const string EmptyCollectionExpressionUnconstrainedGenericSource = """
        internal static class Sample
        {
            public static T[] Values<T>() => [];
        }
        """;

    /// <summary>
    /// An attribute argument is one of the few places an array initializer is legal syntax, and it is a
    /// compile time constant context, so <see cref="ConstantContext.IsRequired(SyntaxNode)" /> must refuse
    /// the initializer here even though it carries more than one element.
    /// </summary>
    private const string AttributeArgumentArrayInitializerSource = """
        namespace Fixtures;

        internal sealed class NumbersAttribute : System.Attribute
        {
            public NumbersAttribute(int[] values) => Values = values;

            public int[] Values { get; }
        }

        [Numbers(new[] { 1, 2, 3 })]
        internal static class Sample
        {
        }
        """;

    /// <summary>
    /// A default parameter value is a compile time constant context as well. This fixture deliberately
    /// does not compile - a collection expression is not itself a constant - but it is the only way to
    /// place the collection expression in a position <see cref="ConstantContext.IsRequired(SyntaxNode)" />
    /// has to refuse.
    /// </summary>
    private const string DefaultParameterCollectionExpressionSource = """
        internal static class Sample
        {
            public static void Convert(int[] values = [1, 2, 3]) { }
        }
        """;

    private const string EmptyCollectionExpressionReadOnlySpanSource = """
        using System;

        internal static class Sample
        {
            public static ReadOnlySpan<int> Values() => [];
        }
        """;

    private const string EmptyCollectionExpressionSpanSource = """
        using System;

        internal static class Sample
        {
            public static Span<int> Values() => [];
        }
        """;

    [Test]
    public async Task Metadata_Operator_DescribesCollectionInitializerFamily()
    {
        var mutator = new CollectionInitializerMutator();
        SyntaxKind[] supported = [.. mutator.SupportedSyntaxKinds];

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutator.Id).IsEqualTo("collection-initializer");
            _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.CollectionInitializer);
            _ = await Assert.That(supported).Count().IsEqualTo(3);
            _ = await Assert.That(supported).Contains(SyntaxKind.ArrayInitializerExpression);
            _ = await Assert.That(supported).Contains(SyntaxKind.CollectionInitializerExpression);
            _ = await Assert.That(supported).Contains(SyntaxKind.CollectionExpression);
        }
    }

    [Test]
    public async Task CreateMutations_MultiElementArrayInitializer_EmptiesIt()
    {
        var (tree, mutations) = RunInitializer(ArrayInitializerSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].Kind).IsEqualTo(MutationKind.CollectionInitializer);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("collection-initializer.to-empty");
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("{ ... } => { }");
            _ = await Assert
                .That(Rewrite(tree, mutations[0]))
                .IsEqualTo(ArrayInitializerSource.Replace("{ 1, 2, 3 }", "{ }", StringComparison.Ordinal));
        }
    }

    [Test]
    public async Task CreateMutations_MultiElementCollectionInitializer_EmptiesIt()
    {
        var (tree, mutations) = RunInitializer(CollectionInitializerSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("collection-initializer.to-empty");
            _ = await Assert
                .That(Rewrite(tree, mutations[0]))
                .IsEqualTo(CollectionInitializerSource.Replace("{ 1, 2, 3 }", "{ }", StringComparison.Ordinal));
        }
    }

    [Test]
    public async Task CreateMutations_MultiElementCollectionExpression_EmptiesIt()
    {
        var (tree, mutations) = RunCollectionExpression(CollectionExpressionSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("collection-initializer.to-empty");
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("[ ... ] => []");
            _ = await Assert
                .That(Rewrite(tree, mutations[0]))
                .IsEqualTo(CollectionExpressionSource.Replace("[1, 2, 3]", "[]", StringComparison.Ordinal));
        }
    }

    [Test]
    public async Task CreateMutations_SingleElementArrayInitializer_StillEmptiesIt()
    {
        var (tree, mutations) = RunInitializer(SingleElementArrayInitializerSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("collection-initializer.to-empty");
            _ = await Assert
                .That(Rewrite(tree, mutations[0]))
                .IsEqualTo(SingleElementArrayInitializerSource.Replace("{ 1 }", "{ }", StringComparison.Ordinal));
        }
    }

    [Test]
    public async Task CreateMutations_EmptyArrayInitializer_ReturnsEmpty()
    {
        var (_, mutations) = RunInitializer(EmptyArrayInitializerSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_EmptyCollectionInitializer_ReturnsEmpty()
    {
        var (_, mutations) = RunInitializer(EmptyCollectionInitializerSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_EmptyCollectionExpressionTargetingValueTypeElement_OffersDefaultElement()
    {
        var (tree, mutations) = RunCollectionExpression(EmptyCollectionExpressionValueTypeSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("collection-initializer.empty-to-default");
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("[] => [default]");
            _ = await Assert
                .That(Rewrite(tree, mutations[0]))
                .IsEqualTo(
                    EmptyCollectionExpressionValueTypeSource.Replace("[]", "[default]", StringComparison.Ordinal)
                );
        }
    }

    [Test]
    public async Task CreateMutations_EmptyCollectionExpressionTargetingArray_OffersDefaultElement()
    {
        var (_, mutations) = RunCollectionExpression(EmptyCollectionExpressionArraySource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("collection-initializer.empty-to-default");
        }
    }

    [Test]
    public async Task CreateMutations_EmptyCollectionExpressionTargetingInterface_OffersDefaultElement()
    {
        var (_, mutations) = RunCollectionExpression(EmptyCollectionExpressionInterfaceTargetSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("collection-initializer.empty-to-default");
        }
    }

    [Test]
    public async Task CreateMutations_EmptyCollectionExpressionTargetingNullableReference_OffersDefaultElement()
    {
        var (_, mutations) = RunCollectionExpression(EmptyCollectionExpressionNullableReferenceSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("collection-initializer.empty-to-default");
        }
    }

    [Test]
    public async Task CreateMutations_EmptyCollectionExpressionTargetingObject_OffersDefaultElement()
    {
        var (_, mutations) = RunCollectionExpression(EmptyCollectionExpressionObjectSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("collection-initializer.empty-to-default");
        }
    }

    [Test]
    public async Task CreateMutations_EmptyCollectionExpressionTargetingDynamic_OffersDefaultElement()
    {
        var (_, mutations) = RunCollectionExpression(EmptyCollectionExpressionDynamicSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("collection-initializer.empty-to-default");
        }
    }

    [Test]
    public async Task CreateMutations_EmptyCollectionExpressionTargetingNonNullableReference_ReturnsEmpty()
    {
        var (_, mutations) = RunCollectionExpression(EmptyCollectionExpressionNonNullableReferenceSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_EmptyCollectionExpressionTargetingUnconstrainedGeneric_ReturnsEmpty()
    {
        var (_, mutations) = RunCollectionExpression(EmptyCollectionExpressionUnconstrainedGenericSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// An array initializer sitting in an attribute argument must never be emptied, no matter how many
    /// elements it carries, because <see cref="ConstantContext.IsRequired(SyntaxNode)" /> refuses the
    /// position before the element count is ever considered.
    /// </summary>
    [Test]
    public async Task CreateMutations_ArrayInitializerInAttributeArgument_ReturnsEmpty()
    {
        var (_, mutations) = RunInitializer(AttributeArgumentArrayInitializerSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// A collection expression sitting in a default parameter value must never be emptied either, for the
    /// same reason: the position is a compile time constant context.
    /// </summary>
    [Test]
    public async Task CreateMutations_CollectionExpressionInDefaultParameterValue_ReturnsEmpty()
    {
        var (_, mutations) = RunCollectionExpression(DefaultParameterCollectionExpressionSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// A <see cref="System.ReadOnlySpan{T}" />-returning member exercises the <c>Span</c>/<c>ReadOnlySpan</c>
    /// branch of <c>ResolveElementType</c>, which is distinct from both the array and the
    /// <see cref="System.Collections.Generic.IEnumerable{T}" /> paths.
    /// </summary>
    [Test]
    public async Task CreateMutations_EmptyCollectionExpressionTargetingReadOnlySpan_OffersDefaultElement()
    {
        var (tree, mutations) = RunCollectionExpression(EmptyCollectionExpressionReadOnlySpanSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("collection-initializer.empty-to-default");
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("[] => [default]");
            _ = await Assert
                .That(Rewrite(tree, mutations[0]))
                .IsEqualTo(
                    EmptyCollectionExpressionReadOnlySpanSource.Replace("[]", "[default]", StringComparison.Ordinal)
                );
        }
    }

    /// <summary>
    /// A <see cref="System.Span{T}" />-returning member hits the same <c>Span</c>/<c>ReadOnlySpan</c> branch,
    /// just through the mutable span type rather than the read only one.
    /// </summary>
    [Test]
    public async Task CreateMutations_EmptyCollectionExpressionTargetingSpan_OffersDefaultElement()
    {
        var (_, mutations) = RunCollectionExpression(EmptyCollectionExpressionSpanSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("collection-initializer.empty-to-default");
        }
    }

    [Test]
    public async Task CreateMutations_UnsupportedSyntaxKind_ReturnsEmpty()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(ArrayInitializerSource);
        var mutator = new CollectionInitializerMutator();
        var literal = SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(
            tree,
            static candidate => candidate.IsKind(SyntaxKind.NumericLiteralExpression)
        );

        Mutation[] mutations = [.. mutator.CreateMutations(literal, semanticModel, CancellationToken.None)];

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_NodeNull_ThrowsArgumentNullException()
    {
        var (_, semanticModel, _) = CompilationFactory.CreateWithModel(ArrayInitializerSource);
        var mutator = new CollectionInitializerMutator();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(null!, semanticModel, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("node");
    }

    [Test]
    public async Task CreateMutations_SemanticModelNull_ThrowsArgumentNullException()
    {
        var (_, _, tree) = CompilationFactory.CreateWithModel(ArrayInitializerSource);
        var mutator = new CollectionInitializerMutator();
        var node = SyntaxNodeLocator.FindFirst<InitializerExpressionSyntax>(tree);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(node, null!, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("semanticModel");
    }

    [Test]
    public async Task CreateMutations_CancelledToken_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(ArrayInitializerSource);
        var mutator = new CollectionInitializerMutator();
        var node = SyntaxNodeLocator.FindFirst<InitializerExpressionSyntax>(tree);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = mutator.CreateMutations(node, semanticModel, cancellation.Token).ToList()
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    private static (SyntaxTree Tree, Mutation[] Mutations) RunInitializer(string source) =>
        Run(source, static tree => SyntaxNodeLocator.FindFirst<InitializerExpressionSyntax>(tree));

    private static (SyntaxTree Tree, Mutation[] Mutations) RunCollectionExpression(string source) =>
        Run(source, static tree => SyntaxNodeLocator.FindFirst<CollectionExpressionSyntax>(tree));

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source, Func<SyntaxTree, SyntaxNode> select)
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var mutator = new CollectionInitializerMutator();
        var node = select(tree);

        return (tree, [.. mutator.CreateMutations(node, semanticModel, CancellationToken.None)]);
    }

    private static string Rewrite(SyntaxTree tree, Mutation mutation) => mutation.ApplyTo(tree).ToString();
}
