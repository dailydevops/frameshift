namespace NetEvolve.Frameshift.Mutations;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Shared plumbing for <see cref="IMutationOperator" /> implementations, handling argument
/// validation and the syntax kind filter.
/// </summary>
internal abstract class MutationOperatorBase : IMutationOperator
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MutationOperatorBase" /> class.
    /// </summary>
    /// <param name="id">The stable identifier prefix of the operator, e.g. <c>arithmetic</c>.</param>
    /// <param name="kind">The operator family the operator belongs to.</param>
    /// <param name="supportedSyntaxKinds">The syntax kinds the operator wants to be offered.</param>
    protected MutationOperatorBase(string id, MutationKind kind, ImmutableArray<SyntaxKind> supportedSyntaxKinds)
    {
        if (id is null)
        {
            throw new ArgumentNullException(nameof(id));
        }

        if (id.Length == 0)
        {
            throw new ArgumentException("The operator id must not be empty.", nameof(id));
        }

        if (supportedSyntaxKinds.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "At least one supported syntax kind must be specified.",
                nameof(supportedSyntaxKinds)
            );
        }

        Id = id;
        Kind = kind;
        SupportedSyntaxKinds = supportedSyntaxKinds;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public MutationKind Kind { get; }

    /// <inheritdoc />
    public ImmutableArray<SyntaxKind> SupportedSyntaxKinds { get; }

    /// <inheritdoc />
    public IEnumerable<Mutation> CreateMutations(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (node is null)
        {
            throw new ArgumentNullException(nameof(node));
        }

        if (semanticModel is null)
        {
            throw new ArgumentNullException(nameof(semanticModel));
        }

        if (!SupportedSyntaxKinds.Contains(node.Kind()))
        {
            return [];
        }

        return CreateMutationsCore(node, semanticModel, cancellationToken);
    }

    /// <summary>
    /// Creates all candidate mutations for <paramref name="node" />, which is guaranteed to be
    /// non-<see langword="null" /> and of one of the <see cref="SupportedSyntaxKinds" />.
    /// </summary>
    /// <param name="node">The node to mutate.</param>
    /// <param name="semanticModel">The semantic model of the tree <paramref name="node" /> belongs to.</param>
    /// <param name="cancellationToken">A token to observe while creating the mutations.</param>
    /// <returns>The candidate mutations, or an empty sequence if the node cannot be mutated.</returns>
    protected abstract IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Creates a <see cref="Mutation" /> whose <see cref="Mutation.OperatorId" /> is composed of
    /// <see cref="Id" /> and <paramref name="operatorSuffix" />.
    /// </summary>
    /// <param name="original">The syntax node that gets replaced.</param>
    /// <param name="replacement">The syntax node that replaces <paramref name="original" />.</param>
    /// <param name="operatorSuffix">The suffix identifying the concrete mutation, e.g. <c>add-to-subtract</c>.</param>
    /// <param name="displayName">The human readable description, e.g. <c>+ =&gt; -</c>.</param>
    /// <returns>The created mutation.</returns>
    protected Mutation CreateMutation(
        SyntaxNode original,
        SyntaxNode replacement,
        string operatorSuffix,
        string displayName
    )
    {
        if (operatorSuffix is null)
        {
            throw new ArgumentNullException(nameof(operatorSuffix));
        }

        return new Mutation(Kind, $"{Id}.{operatorSuffix}", displayName, original, replacement);
    }
}
