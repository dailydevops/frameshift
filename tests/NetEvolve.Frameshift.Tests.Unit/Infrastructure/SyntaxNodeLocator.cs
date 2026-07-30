namespace NetEvolve.Frameshift.Tests.Infrastructure;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Finds the syntax node a test wants to work on, either by node type or by a marker comment that
/// points at it.
/// </summary>
/// <remarks>
/// A mutation operator is asked about one specific node, so a test has to name that node without
/// ambiguity. For simple fixtures the first node of a type is enough; where a fixture contains several
/// candidates, a <c>/*!*/</c> comment directly in front of the interesting node keeps the fixture and
/// the expectation next to each other.
/// </remarks>
internal static class SyntaxNodeLocator
{
    /// <summary>
    /// The marker comment a fixture puts directly in front of the node it means.
    /// </summary>
    public const string Marker = "/*!*/";

    /// <summary>
    /// Finds the first node of type <typeparamref name="TNode" /> in document order.
    /// </summary>
    /// <typeparam name="TNode">The node type to look for.</typeparam>
    /// <param name="tree">The tree to search.</param>
    /// <returns>The first matching node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tree" /> is <see langword="null" />.</exception>
    /// <exception cref="InvalidOperationException">The tree contains no such node.</exception>
    public static TNode FindFirst<TNode>(SyntaxTree tree)
        where TNode : SyntaxNode => FindFirst<TNode>(tree, static _ => true);

    /// <summary>
    /// Finds the first node of type <typeparamref name="TNode" /> that satisfies
    /// <paramref name="predicate" />, in document order.
    /// </summary>
    /// <typeparam name="TNode">The node type to look for.</typeparam>
    /// <param name="tree">The tree to search.</param>
    /// <param name="predicate">The additional condition the node must satisfy.</param>
    /// <returns>The first matching node.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="tree" /> or <paramref name="predicate" /> is <see langword="null" />.
    /// </exception>
    /// <exception cref="InvalidOperationException">The tree contains no matching node.</exception>
    public static TNode FindFirst<TNode>(SyntaxTree tree, Func<TNode, bool> predicate)
        where TNode : SyntaxNode
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(predicate);

        var node = tree.GetRoot().DescendantNodesAndSelf().OfType<TNode>().FirstOrDefault(predicate);

        return node ?? throw NotFound<TNode>(tree, "matched");
    }

    /// <summary>
    /// Finds every node of type <typeparamref name="TNode" /> in document order.
    /// </summary>
    /// <typeparam name="TNode">The node type to look for.</typeparam>
    /// <param name="tree">The tree to search.</param>
    /// <returns>The matching nodes, possibly empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tree" /> is <see langword="null" />.</exception>
    public static ImmutableArray<TNode> FindAll<TNode>(SyntaxTree tree)
        where TNode : SyntaxNode
    {
        ArgumentNullException.ThrowIfNull(tree);

        return [.. tree.GetRoot().DescendantNodesAndSelf().OfType<TNode>()];
    }

    /// <summary>
    /// Finds the widest node that starts right behind the first <see cref="Marker" /> comment, which is
    /// the node a fixture means when it marks a position.
    /// </summary>
    /// <param name="tree">The tree to search.</param>
    /// <returns>The marked node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tree" /> is <see langword="null" />.</exception>
    /// <exception cref="InvalidOperationException">
    /// The tree contains no marker, or no node starts at the marked position.
    /// </exception>
    public static SyntaxNode FindMarked(SyntaxTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        var root = tree.GetRoot();
        var position = FindMarkedPosition(root, tree);
        var node = SelectWidest(root.DescendantNodesAndSelf(), position);

        return node
            ?? throw new InvalidOperationException(
                $"No syntax node starts at the marker '{Marker}' in '{Describe(tree)}'."
            );
    }

    /// <summary>
    /// Finds the widest node of type <typeparamref name="TNode" /> that starts right behind the first
    /// <see cref="Marker" /> comment.
    /// </summary>
    /// <typeparam name="TNode">The node type to look for.</typeparam>
    /// <param name="tree">The tree to search.</param>
    /// <returns>The marked node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tree" /> is <see langword="null" />.</exception>
    /// <exception cref="InvalidOperationException">
    /// The tree contains no marker, or no node of that type starts at the marked position.
    /// </exception>
    public static TNode FindMarked<TNode>(SyntaxTree tree)
        where TNode : SyntaxNode
    {
        ArgumentNullException.ThrowIfNull(tree);

        var root = tree.GetRoot();
        var position = FindMarkedPosition(root, tree);
        var node = SelectWidest(root.DescendantNodesAndSelf().OfType<TNode>(), position);

        return node ?? throw NotFound<TNode>(tree, "starts at the marker");
    }

    /// <summary>
    /// Picks the node with the largest span among the nodes starting at <paramref name="position" />,
    /// because several nodes share a start and the outermost one is the one a fixture points at.
    /// </summary>
    /// <typeparam name="TNode">The node type of the candidates.</typeparam>
    /// <param name="candidates">The nodes to choose from.</param>
    /// <param name="position">The source position the node has to start at.</param>
    /// <returns>The widest matching node, or <see langword="null" /> if there is none.</returns>
    private static TNode? SelectWidest<TNode>(IEnumerable<TNode> candidates, int position)
        where TNode : SyntaxNode =>
        candidates
            .Where(candidate => candidate.SpanStart == position)
            .OrderByDescending(candidate => candidate.Span.End)
            .FirstOrDefault();

    /// <summary>
    /// Resolves the source position the first <see cref="Marker" /> comment points at, which is the
    /// start of the first token behind it.
    /// </summary>
    /// <param name="root">The root of the searched tree.</param>
    /// <param name="tree">The searched tree, used for the error message.</param>
    /// <returns>The marked position.</returns>
    /// <exception cref="InvalidOperationException">
    /// There is no marker, or nothing follows it.
    /// </exception>
    private static int FindMarkedPosition(SyntaxNode root, SyntaxTree tree)
    {
        var markerEnd = FindMarkerEnd(root, tree);

        foreach (var token in root.DescendantTokens())
        {
            if (token.SpanStart >= markerEnd && !token.IsKind(SyntaxKind.EndOfFileToken))
            {
                return token.SpanStart;
            }
        }

        throw new InvalidOperationException(
            $"The marker '{Marker}' in '{Describe(tree)}' is not followed by any syntax token."
        );
    }

    private static int FindMarkerEnd(SyntaxNode root, SyntaxTree tree)
    {
        var markers = root.DescendantTrivia(descendIntoTrivia: true).Where(IsMarkerComment).Take(1).ToArray();

        if (markers.Length == 0)
        {
            throw new InvalidOperationException(
                $"The source of '{Describe(tree)}' does not contain the marker comment '{Marker}'."
            );
        }

        return markers[0].Span.End;
    }

    private static bool IsMarkerComment(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
        && string.Equals(trivia.ToString(), Marker, StringComparison.Ordinal);

    private static InvalidOperationException NotFound<TNode>(SyntaxTree tree, string what)
        where TNode : SyntaxNode =>
        new InvalidOperationException($"No syntax node of kind '{typeof(TNode).Name}' {what} in '{Describe(tree)}'.");

    private static string Describe(SyntaxTree tree) =>
        tree.FilePath.Length == 0 ? "<unnamed syntax tree>" : tree.FilePath;
}
