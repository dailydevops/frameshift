namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Mutates the element list of an array or collection initializer and of a collection expression,
/// emptying a non-empty one and, where it is provably safe, filling an empty collection expression with
/// a single <see langword="default" /> element.
/// </summary>
/// <remarks>
/// <para>
/// Only <see cref="SyntaxKind.ArrayInitializerExpression" /> and
/// <see cref="SyntaxKind.CollectionInitializerExpression" /> are covered on the
/// <see cref="InitializerExpressionSyntax" /> side. An object initializer
/// (<see cref="SyntaxKind.ObjectInitializerExpression" />) and the complex element initializer of a
/// dictionary initializer (<see cref="SyntaxKind.ComplexElementInitializerExpression" />) are a different
/// construct and are never offered to this operator, because neither kind is part of
/// <see cref="MutationOperatorBase.SupportedSyntaxKinds" />.
/// </para>
/// <para>
/// A non-empty initializer or collection expression always loses its whole element list, including one
/// that carries a single element: <c>{ 1 }</c> collapsing to <c>{ }</c> removes the initializer
/// altogether, which is a materially different mutant from <see cref="NumericLiteralMutator" /> changing
/// <c>1</c> to a neighbouring number, so the two operators do not duplicate each other and both stay in
/// effect. An initializer or collection expression that is already empty has nothing left to empty, so
/// that direction is skipped for it.
/// </para>
/// <para>
/// The reverse direction, turning <c>[]</c> into <c>[default]</c>, is only offered for a collection
/// expression, never for the brace initializer syntax, and only when it is safe: the target type of the
/// collection expression has to resolve, through the semantic model, to a concrete element type, and that
/// element type has to be a value type, a nullable reference type, or <c>dynamic</c>/<c>object</c>.
/// Everything else - an unresolved element type, an unconstrained type parameter, or a non-nullable
/// reference type the nullable context has not annotated - is skipped, because <see langword="default" />
/// is not guaranteed to compile there without deeper analysis this operator deliberately does not
/// perform. This project's referenced Roslyn version does not expose the purpose built
/// <c>GetCollectionExpressionTypeInfo</c> API, so the element type is derived from
/// <c>SemanticModel.GetTypeInfo</c>'s converted type instead, unwrapping an array or span type and
/// searching a named type and its interfaces for a constructed
/// <see cref="System.Collections.Generic.IEnumerable{T}" />.
/// </para>
/// </remarks>
internal sealed class CollectionInitializerMutator : MutationOperatorBase
{
    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds =
    [
        SyntaxKind.ArrayInitializerExpression,
        SyntaxKind.CollectionInitializerExpression,
        SyntaxKind.CollectionExpression,
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionInitializerMutator" /> class.
    /// </summary>
    public CollectionInitializerMutator()
        : base("collection-initializer", MutationKind.CollectionInitializer, _supportedSyntaxKinds) { }

    /// <inheritdoc />
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // MutationOperatorBase.CreateMutations only hands over nodes of one of the SupportedSyntaxKinds,
        // and every one of them is either an initializer expression or a collection expression, so the
        // type test below is exhaustive and no other shape ever reaches this method.
        return node switch
        {
            InitializerExpressionSyntax initializer => CreateInitializerMutations(initializer),
            CollectionExpressionSyntax collection => CreateCollectionExpressionMutations(
                collection,
                semanticModel,
                cancellationToken
            ),
            _ => [],
        };
    }

    private IEnumerable<Mutation> CreateInitializerMutations(InitializerExpressionSyntax initializer)
    {
        if (ConstantContext.IsRequired(initializer) || initializer.Expressions.Count == 0)
        {
            yield break;
        }

        yield return CreateMutation(initializer, CreateEmptyInitializer(initializer), "to-empty", "{ ... } => { }");
    }

    private IEnumerable<Mutation> CreateCollectionExpressionMutations(
        CollectionExpressionSyntax collection,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (ConstantContext.IsRequired(collection))
        {
            yield break;
        }

        if (collection.Elements.Count > 0)
        {
            yield return CreateMutation(
                collection,
                CreateEmptyCollectionExpression(collection),
                "to-empty",
                "[ ... ] => []"
            );

            yield break;
        }

        var elementType = ResolveElementType(semanticModel, collection, cancellationToken);
        if (elementType is null || !AllowsDefault(elementType))
        {
            yield break;
        }

        yield return CreateMutation(
            collection,
            CreateDefaultCollectionExpression(collection),
            "empty-to-default",
            "[] => [default]"
        );
    }

    private static InitializerExpressionSyntax CreateEmptyInitializer(InitializerExpressionSyntax initializer) =>
        SyntaxFactory
            .InitializerExpression(
                initializer.Kind(),
                SyntaxFactory.Token(SyntaxKind.OpenBraceToken).WithTrailingTrivia(SyntaxFactory.Space),
                SyntaxFactory.SeparatedList<ExpressionSyntax>(),
                SyntaxFactory.Token(SyntaxKind.CloseBraceToken)
            )
            .WithTriviaFrom(initializer);

    private static CollectionExpressionSyntax CreateEmptyCollectionExpression(CollectionExpressionSyntax collection) =>
        collection.WithElements(SyntaxFactory.SeparatedList<CollectionElementSyntax>());

    private static CollectionExpressionSyntax CreateDefaultCollectionExpression(CollectionExpressionSyntax collection)
    {
        var defaultElement = SyntaxFactory.ExpressionElement(
            SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression)
        );

        return collection.WithElements(SyntaxFactory.SingletonSeparatedList<CollectionElementSyntax>(defaultElement));
    }

    /// <summary>
    /// Resolves the element type a collection expression's target type carries, so that
    /// <see cref="AllowsDefault" /> can decide whether <see langword="default" /> is safe there.
    /// </summary>
    /// <param name="semanticModel">The semantic model of the tree <paramref name="collection" /> belongs to.</param>
    /// <param name="collection">The (empty) collection expression to resolve the element type of.</param>
    /// <param name="cancellationToken">A token to observe while querying the semantic model.</param>
    /// <returns>The resolved element type, or <see langword="null" /> if it cannot be determined.</returns>
    private static ITypeSymbol? ResolveElementType(
        SemanticModel semanticModel,
        CollectionExpressionSyntax collection,
        CancellationToken cancellationToken
    )
    {
        var convertedType = semanticModel.GetTypeInfo(collection, cancellationToken).ConvertedType;

        return convertedType switch
        {
            IArrayTypeSymbol arrayType => arrayType.ElementType,
            INamedTypeSymbol
            {
                Name: "Span" or "ReadOnlySpan",
                ContainingNamespace.Name: "System",
                TypeArguments.Length: 1
            } spanType => spanType.TypeArguments[0],
            INamedTypeSymbol named => ResolveEnumerableElementType(named),
            _ => null,
        };
    }

    private static ITypeSymbol? ResolveEnumerableElementType(INamedTypeSymbol named)
    {
        if (IsGenericEnumerable(named))
        {
            return named.TypeArguments[0];
        }

        var enumerableInterface = named.AllInterfaces.FirstOrDefault(IsGenericEnumerable);

        return enumerableInterface?.TypeArguments[0];
    }

    private static bool IsGenericEnumerable(INamedTypeSymbol type) =>
        type.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T
        && type.TypeArguments.Length == 1;

    /// <summary>
    /// Determines whether <see langword="default" /> is safe to write for <paramref name="elementType" />:
    /// a value type, a nullable reference type, or <c>dynamic</c>/<c>object</c>. Every other reference
    /// type, including one the nullable context leaves oblivious, is rejected, because whether
    /// <see langword="default" /> compiles there without a warning depends on analysis this operator does
    /// not perform.
    /// </summary>
    /// <param name="elementType">The resolved element type.</param>
    /// <returns><see langword="true" /> if <see langword="default" /> is safe to write.</returns>
    private static bool AllowsDefault(ITypeSymbol elementType) =>
        elementType.TypeKind == TypeKind.Dynamic
        || elementType.IsValueType
        || elementType.SpecialType == SpecialType.System_Object
        || elementType.NullableAnnotation == NullableAnnotation.Annotated;
}
