namespace NetEvolve.Frameshift.Mutations;

/// <summary>
/// The outcome of verifying that a generated mutant is a legal C# program.
/// </summary>
internal enum MutantViability
{
    /// <summary>
    /// The mutant compiles and is therefore a usable mutation point.
    /// </summary>
    Viable = 0,

    /// <summary>
    /// The mutant introduces at least one compile error and must be discarded, because a mutant that
    /// cannot be built can never be covered by a test.
    /// </summary>
    DoesNotCompile,
}
