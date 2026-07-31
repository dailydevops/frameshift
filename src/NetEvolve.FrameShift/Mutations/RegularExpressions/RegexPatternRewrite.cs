namespace NetEvolve.FrameShift.Mutations.RegularExpressions;

using System;

/// <summary>
/// One candidate rewrite of a regular expression pattern: the complete pattern text a mutation would
/// put into the literal, together with the suffix identifying the concrete mutation.
/// </summary>
/// <remarks>
/// The type carries the whole pattern rather than a span and a replacement, because that is what every
/// consumer needs: the validity check parses the whole pattern, the display name shows the whole
/// pattern, and the literal that gets spliced into the syntax tree holds the whole pattern. Producing
/// it is still a span operation - an operator splices into a <see cref="RegexToken" /> span and never
/// searches the raw pattern for a character - the result of that splice is simply what travels.
/// </remarks>
internal sealed class RegexPatternRewrite
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RegexPatternRewrite" /> class.
    /// </summary>
    /// <param name="pattern">The complete rewritten pattern, which may be empty.</param>
    /// <param name="operatorSuffix">
    /// The suffix identifying the concrete mutation, e.g. <c>remove-caret</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="pattern" /> or <paramref name="operatorSuffix" /> is <see langword="null" />.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="operatorSuffix" /> is empty.</exception>
    public RegexPatternRewrite(string pattern, string operatorSuffix)
    {
        if (pattern is null)
        {
            throw new ArgumentNullException(nameof(pattern));
        }

        if (operatorSuffix is null)
        {
            throw new ArgumentNullException(nameof(operatorSuffix));
        }

        if (operatorSuffix.Length == 0)
        {
            throw new ArgumentException("The operator suffix must not be empty.", nameof(operatorSuffix));
        }

        Pattern = pattern;
        OperatorSuffix = operatorSuffix;
    }

    /// <summary>
    /// Gets the complete rewritten pattern. An empty pattern is legal - removing the only anchor of
    /// <c>^</c> yields it - and is a mutation like any other.
    /// </summary>
    public string Pattern { get; }

    /// <summary>
    /// Gets the suffix identifying the concrete mutation, which the operator identifier ends with.
    /// </summary>
    public string OperatorSuffix { get; }

    /// <inheritdoc />
    public override string ToString() => $"{OperatorSuffix}: '{Pattern}'";
}
