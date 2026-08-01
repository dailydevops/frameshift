namespace NetEvolve.FrameShift.TestSurface;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Reads the number of elements of a sequence off the declaration of the member holding it, which is what
/// makes the case count of a data source exact. Shared by <see cref="MSTestTestMethodRecognizer" />,
/// <see cref="NUnitTestMethodRecognizer" /> and <see cref="XunitTestCaseCounter" />, whose data sources all
/// name a field, property or method the very same way.
/// </summary>
/// <remarks>
/// <para>
/// Only a length that is written down is read: an explicit element list, be it a collection expression, an
/// array initializer or a collection initializer, and a method body consisting of nothing but
/// <c>yield return</c> statements. Every other shape — a query, a loop, a call, an array of a computed size,
/// a spread element — leaves the length unknown, and the caller turns that into a lower bound. Resolving it
/// any further would mean evaluating the member, which is exactly what an analyzer must not do and what the
/// framework itself does at discovery time.
/// </para>
/// <para>
/// The elements themselves are never inspected. What the count needs is how many rows the source
/// contributes, and that is the number of elements whether a row is a literal, an array of arguments or a
/// constructed object.
/// </para>
/// </remarks>
internal static class SequenceLengthReader
{
    /// <summary>
    /// Reads the number of elements of the sequence a data source member declares.
    /// </summary>
    /// <param name="containingType">The type the member is looked up in, along with its base types.</param>
    /// <param name="name">The name of the member.</param>
    /// <returns>
    /// The number of elements, or <see langword="null" /> when there is no such member or its length cannot
    /// be read off the syntax.
    /// </returns>
    public static int? TryGetSequenceLength(INamedTypeSymbol? containingType, string name)
    {
        for (var type = containingType; type is not null; type = type.BaseType)
        {
            foreach (var member in type.GetMembers(name))
            {
                var length = TryGetLength(member);

                if (length.HasValue)
                {
                    return length;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the number of elements the sequence of <paramref name="member" /> has.
    /// </summary>
    /// <param name="member">The field, property or method holding the sequence.</param>
    /// <returns>
    /// The number of elements, or <see langword="null" /> when the member is of another kind or its length
    /// is not written down.
    /// </returns>
    public static int? TryGetLength(ISymbol member) =>
        member switch
        {
            IFieldSymbol field => CountElements(GetFieldValue(field)),
            IPropertySymbol property => CountElements(GetPropertyValue(property)),
            IMethodSymbol method => CountMethodElements(method),
            _ => null,
        };

    private static ExpressionSyntax? GetFieldValue(IFieldSymbol field) =>
        GetDeclaration<VariableDeclaratorSyntax>(field)?.Initializer?.Value;

    private static ExpressionSyntax? GetPropertyValue(IPropertySymbol property)
    {
        var declaration = GetDeclaration<PropertyDeclarationSyntax>(property);

        if (declaration is null)
        {
            return null;
        }

        return declaration.ExpressionBody?.Expression ?? declaration.Initializer?.Value ?? GetGetterValue(declaration);
    }

    private static ExpressionSyntax? GetGetterValue(PropertyDeclarationSyntax declaration)
    {
        var getter = declaration.AccessorList?.Accessors.FirstOrDefault(accessor =>
            accessor.IsKind(SyntaxKind.GetAccessorDeclaration)
        );

        return getter?.ExpressionBody?.Expression ?? GetReturnedValue(getter?.Body);
    }

    private static int? CountMethodElements(IMethodSymbol method)
    {
        var declaration = GetDeclaration<MethodDeclarationSyntax>(method);

        if (declaration is null)
        {
            return null;
        }

        var returned = declaration.ExpressionBody?.Expression ?? GetReturnedValue(declaration.Body);

        return CountElements(returned) ?? CountYieldedElements(declaration.Body);
    }

    private static ExpressionSyntax? GetReturnedValue(BlockSyntax? body) =>
        body is { Statements.Count: 1 } ? (body.Statements[0] as ReturnStatementSyntax)?.Expression : null;

    /// <summary>
    /// Counts the <c>yield return</c> statements of a body that consists of nothing else, because such a
    /// body yields exactly one element per statement.
    /// </summary>
    /// <param name="body">The body to inspect.</param>
    /// <returns>
    /// The number of yielded elements, or <see langword="null" /> when the body holds any other statement,
    /// which includes a <c>yield break</c> and every loop around a <c>yield return</c>.
    /// </returns>
    private static int? CountYieldedElements(BlockSyntax? body)
    {
        if (body is null || body.Statements.Count == 0)
        {
            return null;
        }

        var yielded = body.Statements.OfType<YieldStatementSyntax>().ToImmutableArray();

        return
            yielded.Length == body.Statements.Count
            && yielded.All(statement => statement.IsKind(SyntaxKind.YieldReturnStatement))
            ? yielded.Length
            : null;
    }

    private static int? CountElements(ExpressionSyntax? expression) =>
        expression switch
        {
            CollectionExpressionSyntax collection => CountElements(collection),
            ArrayCreationExpressionSyntax array => CountElements(array.Initializer),
            ImplicitArrayCreationExpressionSyntax array => CountElements(array.Initializer),
            BaseObjectCreationExpressionSyntax creation => CountElements(creation.Initializer),
            InitializerExpressionSyntax initializer => CountElements(initializer),
            _ => null,
        };

    /// <summary>
    /// Counts the elements of a collection expression, unless one of them is a spread element, whose own
    /// length is not written down here.
    /// </summary>
    /// <param name="collection">The collection expression to count.</param>
    /// <returns>The number of elements, or <see langword="null" /> when one of them is spread.</returns>
    private static int? CountElements(CollectionExpressionSyntax collection) =>
        collection.Elements.Any(element => element is SpreadElementSyntax) ? null : collection.Elements.Count;

    /// <summary>
    /// Counts the elements of an initializer, accepting only the two kinds that list elements. An object
    /// initializer lists property assignments instead, and counting those would invent a length.
    /// </summary>
    /// <param name="initializer">The initializer to count, which may be absent.</param>
    /// <returns>The number of elements, or <see langword="null" /> for any other initializer.</returns>
    private static int? CountElements(InitializerExpressionSyntax? initializer) =>
        initializer is not null
        && (
            initializer.IsKind(SyntaxKind.ArrayInitializerExpression)
            || initializer.IsKind(SyntaxKind.CollectionInitializerExpression)
        )
            ? initializer.Expressions.Count
            : null;

    private static TSyntax? GetDeclaration<TSyntax>(ISymbol member)
        where TSyntax : SyntaxNode =>
        member.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax()).OfType<TSyntax>().FirstOrDefault();
}
