namespace NetEvolve.FrameShift.Mutations.RegularExpressions;

using System;

/// <summary>
/// One lexical unit of a regular expression pattern: its category, its exact span inside the pattern and
/// the text that span covers.
/// </summary>
/// <remarks>
/// The tokens of a pattern tile it completely and without overlap, so
/// <c>string.Concat(tokens.Select(token =&gt; token.Text))</c> reproduces the pattern and a rewriter can
/// splice a replacement into <see cref="Start" />..<see cref="End" /> without parsing the pattern again.
/// </remarks>
internal sealed class RegexToken : IEquatable<RegexToken>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RegexToken" /> class.
    /// </summary>
    /// <param name="kind">The lexical category of the token.</param>
    /// <param name="start">The zero based index of the first character of the token inside the pattern.</param>
    /// <param name="text">The exact text the token covers.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="text" /> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="start" /> is negative.</exception>
    public RegexToken(RegexTokenKind kind, int start, string text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        if (text.Length == 0)
        {
            throw new ArgumentException("A regular expression token must cover at least one character.", nameof(text));
        }

        if (start < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, "The start index must not be negative.");
        }

        Kind = kind;
        Start = start;
        Text = text;
    }

    /// <summary>
    /// Gets the lexical category of the token.
    /// </summary>
    public RegexTokenKind Kind { get; }

    /// <summary>
    /// Gets the zero based index of the first character of the token inside the pattern.
    /// </summary>
    public int Start { get; }

    /// <summary>
    /// Gets the exact text the token covers.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Gets the number of characters the token covers.
    /// </summary>
    public int Length => Text.Length;

    /// <summary>
    /// Gets the index one past the last character of the token, so that the token spans
    /// <see cref="Start" /> up to but excluding <see cref="End" />.
    /// </summary>
    public int End => Start + Text.Length;

    /// <inheritdoc />
    public bool Equals(RegexToken? other) =>
        other is not null
        && Kind == other.Kind
        && Start == other.Start
        && string.Equals(Text, other.Text, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as RegexToken);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            hash = (hash * 31) + (int)Kind;
            hash = (hash * 31) + Start;
            hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(Text);

            return hash;
        }
    }

    /// <summary>
    /// Returns the diagnostic form <c>Kind[start..end)='text'</c>, which is what a failing assertion shows.
    /// </summary>
    /// <returns>The diagnostic form of the token.</returns>
    public override string ToString() => $"{Kind}[{Start}..{End})='{Text}'";
}
