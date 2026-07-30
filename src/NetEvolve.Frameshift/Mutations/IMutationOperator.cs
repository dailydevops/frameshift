namespace NetEvolve.Frameshift.Mutations;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Produces the candidate <see cref="Mutation" /> instances for a single syntax node.
/// </summary>
/// <remarks>
/// <para>
/// Implementations MUST be stateless and thread-safe. A single instance is shared by the whole
/// analyzer and is called concurrently for many nodes, compilations and syntax trees, so no
/// instance state may be mutated after construction and no ambient state may be captured.
/// </para>
/// <para>
/// Implementations MUST return an empty sequence for every node they do not handle instead of
/// throwing. That includes nodes whose <c>SyntaxNode.Kind()</c> is not part of
/// <see cref="SupportedSyntaxKinds" /> and nodes that are syntactically supported but cannot be
/// mutated in the given semantic context.
/// </para>
/// </remarks>
internal interface IMutationOperator
{
    /// <summary>
    /// Gets the stable identifier prefix of this operator, e.g. <c>arithmetic</c>.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Gets the operator family this operator belongs to.
    /// </summary>
    MutationKind Kind { get; }

    /// <summary>
    /// Gets the syntax kinds this operator wants to be offered.
    /// </summary>
    ImmutableArray<SyntaxKind> SupportedSyntaxKinds { get; }

    /// <summary>
    /// Creates all candidate mutations for <paramref name="node" />.
    /// </summary>
    /// <param name="node">The node to mutate.</param>
    /// <param name="semanticModel">The semantic model of the tree <paramref name="node" /> belongs to.</param>
    /// <param name="cancellationToken">A token to observe while creating the mutations.</param>
    /// <returns>
    /// The candidate mutations, or an empty sequence if this operator does not handle
    /// <paramref name="node" />.
    /// </returns>
    IEnumerable<Mutation> CreateMutations(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    );
}
