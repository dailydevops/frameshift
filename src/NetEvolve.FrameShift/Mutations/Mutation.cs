namespace NetEvolve.FrameShift.Mutations;

using Microsoft.CodeAnalysis;

/// <summary>
/// Describes a single candidate mutation, meaning the replacement of exactly one syntax node
/// by another one inside an otherwise unchanged syntax tree.
/// </summary>
internal sealed class Mutation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Mutation" /> class, deriving the reported
    /// location from <paramref name="original" />.
    /// </summary>
    /// <param name="kind">The operator family this mutation belongs to.</param>
    /// <param name="operatorId">The stable identifier of the concrete mutation, e.g. <c>arithmetic.add-to-subtract</c>.</param>
    /// <param name="displayName">The human readable description, e.g. <c>+ =&gt; -</c>.</param>
    /// <param name="original">The syntax node that gets replaced.</param>
    /// <param name="replacement">The syntax node that replaces <paramref name="original" />.</param>
    public Mutation(
        MutationKind kind,
        string operatorId,
        string displayName,
        SyntaxNode original,
        SyntaxNode replacement
    )
        : this(kind, operatorId, displayName, original, replacement, location: null) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="Mutation" /> class.
    /// </summary>
    /// <param name="kind">The operator family this mutation belongs to.</param>
    /// <param name="operatorId">The stable identifier of the concrete mutation, e.g. <c>arithmetic.add-to-subtract</c>.</param>
    /// <param name="displayName">The human readable description, e.g. <c>+ =&gt; -</c>.</param>
    /// <param name="original">The syntax node that gets replaced.</param>
    /// <param name="replacement">The syntax node that replaces <paramref name="original" />.</param>
    /// <param name="location">
    /// The location to report the mutation at, or <see langword="null" /> to use the location of
    /// <paramref name="original" />.
    /// </param>
    public Mutation(
        MutationKind kind,
        string operatorId,
        string displayName,
        SyntaxNode original,
        SyntaxNode replacement,
        Location? location
    )
    {
        if (operatorId is null)
        {
            throw new ArgumentNullException(nameof(operatorId));
        }

        if (displayName is null)
        {
            throw new ArgumentNullException(nameof(displayName));
        }

        if (operatorId.Length == 0)
        {
            throw new ArgumentException("The operator id must not be empty.", nameof(operatorId));
        }

        if (displayName.Length == 0)
        {
            throw new ArgumentException("The display name must not be empty.", nameof(displayName));
        }

        Kind = kind;
        OperatorId = operatorId;
        DisplayName = displayName;
        Original = original ?? throw new ArgumentNullException(nameof(original));
        Replacement = replacement ?? throw new ArgumentNullException(nameof(replacement));
        Location = location ?? original.GetLocation();
    }

    /// <summary>
    /// Gets the operator family this mutation belongs to.
    /// </summary>
    public MutationKind Kind { get; }

    /// <summary>
    /// Gets the stable identifier of the concrete mutation, e.g. <c>arithmetic.add-to-subtract</c>.
    /// </summary>
    public string OperatorId { get; }

    /// <summary>
    /// Gets the human readable description of the mutation, e.g. <c>+ =&gt; -</c>.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the syntax node that gets replaced.
    /// </summary>
    public SyntaxNode Original { get; }

    /// <summary>
    /// Gets the syntax node that replaces <see cref="Original" />.
    /// </summary>
    public SyntaxNode Replacement { get; }

    /// <summary>
    /// Gets the location the mutation is reported at.
    /// </summary>
    public Location Location { get; }

    /// <summary>
    /// Applies the mutation to <paramref name="tree" /> and returns the mutated tree, keeping the
    /// original parse options and the trivia surrounding <see cref="Original" />.
    /// </summary>
    /// <param name="tree">The syntax tree containing <see cref="Original" />.</param>
    /// <returns>A new <see cref="SyntaxTree" /> with <see cref="Original" /> replaced.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tree" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="tree" /> does not contain <see cref="Original" />.</exception>
    public SyntaxTree ApplyTo(SyntaxTree tree)
    {
        if (tree is null)
        {
            throw new ArgumentNullException(nameof(tree));
        }

        var root = tree.GetRoot();
        if (!root.Contains(Original))
        {
            throw new ArgumentException(
                "The syntax tree does not contain the node this mutation applies to.",
                nameof(tree)
            );
        }

        var mutatedRoot = root.ReplaceNode(Original, Replacement.WithTriviaFrom(Original));
        return tree.WithRootAndOptions(mutatedRoot, tree.Options);
    }

    /// <inheritdoc />
    public override string ToString() => DisplayName;
}
