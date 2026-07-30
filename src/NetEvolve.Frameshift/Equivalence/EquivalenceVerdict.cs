namespace NetEvolve.Frameshift.Equivalence;

/// <summary>
/// The immutable outcome of classifying a mutant as trivial, meaning that no test could ever
/// distinguish the mutant from the original code.
/// </summary>
/// <remarks>
/// A verdict is either <see cref="NotTrivial" /> or a trivial verdict carrying the
/// <see cref="Reason" /> that made it trivial. The reason is used as the second message argument of
/// <c>FSH0002</c>, therefore it is written lowercase and phrased as a clause, so that it reads
/// naturally inside the parentheses of the diagnostic message.
/// </remarks>
internal sealed class EquivalenceVerdict
{
    private static readonly EquivalenceVerdict _notTrivial = new EquivalenceVerdict(isTrivial: false, reason: null);

    /// <summary>
    /// Initializes a new instance of the <see cref="EquivalenceVerdict" /> class.
    /// </summary>
    /// <param name="isTrivial">Whether the mutant cannot change observable behaviour.</param>
    /// <param name="reason">The reason the mutant is trivial, or <see langword="null" />.</param>
    private EquivalenceVerdict(bool isTrivial, string? reason)
    {
        IsTrivial = isTrivial;
        Reason = reason;
    }

    /// <summary>
    /// Gets the verdict used whenever triviality could not be proven, which is the conservative
    /// default of the classification.
    /// </summary>
    public static EquivalenceVerdict NotTrivial => _notTrivial;

    /// <summary>
    /// Gets a value indicating whether the mutant cannot change observable behaviour.
    /// </summary>
    public bool IsTrivial { get; }

    /// <summary>
    /// Gets the short, lowercase reason clause explaining why the mutant is trivial, or
    /// <see langword="null" /> if <see cref="IsTrivial" /> is <see langword="false" />.
    /// </summary>
    public string? Reason { get; }

    /// <summary>
    /// Creates a trivial verdict carrying <paramref name="reason" />.
    /// </summary>
    /// <param name="reason">
    /// The short, lowercase reason clause, e.g. <c>the mutated expression folds to the same constant</c>.
    /// </param>
    /// <returns>A trivial verdict.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reason" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="reason" /> is empty.</exception>
    public static EquivalenceVerdict Trivial(string reason)
    {
        if (reason is null)
        {
            throw new ArgumentNullException(nameof(reason));
        }

        if (reason.Length == 0)
        {
            throw new ArgumentException("The reason must not be empty.", nameof(reason));
        }

        return new EquivalenceVerdict(isTrivial: true, reason: reason);
    }

    /// <inheritdoc />
    public override string ToString() => Reason ?? "not trivial";
}
