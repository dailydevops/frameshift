namespace NetEvolve.FrameShift.TestSurface;

using System.Globalization;

/// <summary>
/// The number of test cases a single test method contributes, either known exactly or known as a
/// lower bound.
/// </summary>
/// <remarks>
/// <para>
/// A test method that carries inline data attributes contributes exactly as many cases as it has
/// attributes, and a parameterless test method contributes exactly one case. A data source, in
/// contrast, is only resolved by the test framework at discovery time, which an analyzer must not do,
/// so all a static inspection can state about it is that it contributes <em>at least</em> one case.
/// </para>
/// <para>
/// The distinction matters, because a heuristic that reports a single test case must stay silent as
/// soon as any part of the aggregation is a lower bound: the true number could be far higher.
/// </para>
/// </remarks>
internal readonly struct TestCaseCount : IEquatable<TestCaseCount>
{
    private TestCaseCount(int value, bool isExact)
    {
        Value = value;
        IsExact = isExact;
    }

    /// <summary>
    /// Gets the number of test cases, which is the exact number when <see cref="IsExact" /> is
    /// <see langword="true" /> and a lower bound otherwise. The value is never negative.
    /// </summary>
    public int Value { get; }

    /// <summary>
    /// Gets a value indicating whether <see cref="Value" /> is the exact number of test cases instead
    /// of a lower bound.
    /// </summary>
    /// <remarks>
    /// The default instance is the lower bound <c>0+</c>, which states nothing at all and therefore
    /// never satisfies a heuristic that requires an exact count.
    /// </remarks>
    public bool IsExact { get; }

    /// <summary>
    /// Creates an exactly known count.
    /// </summary>
    /// <param name="value">The exact number of test cases, zero or more.</param>
    /// <returns>The created count.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value" /> is negative.</exception>
    public static TestCaseCount Exact(int value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A test case count cannot be negative.");
        }

        return new TestCaseCount(value, true);
    }

    /// <summary>
    /// Creates a count that is only known as a lower bound.
    /// </summary>
    /// <param name="value">The lower bound of the number of test cases, zero or more.</param>
    /// <returns>The created count.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value" /> is negative.</exception>
    public static TestCaseCount AtLeast(int value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A test case count cannot be negative.");
        }

        return new TestCaseCount(value, false);
    }

    /// <summary>
    /// Tries to parse the textual representation of a count, as produced by <see cref="ToString" />.
    /// </summary>
    /// <param name="text">
    /// The text to parse: digits only for an exact count, digits followed by
    /// <see cref="TestSurfaceManifestFormat.LowerBoundSuffix" /> for a lower bound.
    /// </param>
    /// <param name="count">
    /// When this method returns <see langword="true" />, the parsed count; otherwise the default
    /// instance.
    /// </param>
    /// <returns>
    /// <see langword="true" /> if <paramref name="text" /> is a well-formed count; otherwise
    /// <see langword="false" />. A sign, surrounding white space, a thousands separator, a value that
    /// does not fit into an <see cref="int" /> and <see langword="null" /> are all rejected.
    /// </returns>
    public static bool TryParse(string? text, out TestCaseCount count)
    {
        count = default;

        if (text is null || text.Length == 0)
        {
            return false;
        }

        var isExact = text[text.Length - 1] != TestSurfaceManifestFormat.LowerBoundSuffix;
        var digits = isExact ? text : text.Substring(0, text.Length - 1);

        if (digits.Length == 0 || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        count = isExact ? Exact(value) : AtLeast(value);

        return true;
    }

    /// <summary>
    /// Adds <paramref name="other" /> to this count, keeping the result exact only when both operands
    /// are exact.
    /// </summary>
    /// <param name="other">The count to add.</param>
    /// <returns>
    /// The sum of both values, saturated at <see cref="int.MaxValue" />, which is exact if and only if
    /// both operands are exact.
    /// </returns>
    public TestCaseCount Add(TestCaseCount other)
    {
        var sum = (long)Value + other.Value;
        var value = sum > int.MaxValue ? int.MaxValue : (int)sum;

        return new TestCaseCount(value, IsExact && other.IsExact);
    }

    /// <inheritdoc />
    public bool Equals(TestCaseCount other) => Value == other.Value && IsExact == other.IsExact;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TestCaseCount other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => (Value * 2) + (IsExact ? 1 : 0);

    /// <summary>
    /// Returns the textual representation used inside a test-surface manifest.
    /// </summary>
    /// <returns>
    /// The value in invariant digits, followed by
    /// <see cref="TestSurfaceManifestFormat.LowerBoundSuffix" /> when the count is a lower bound.
    /// </returns>
    public override string ToString()
    {
        var digits = Value.ToString(CultureInfo.InvariantCulture);

        return IsExact ? digits : digits + TestSurfaceManifestFormat.LowerBoundSuffix;
    }

    /// <summary>
    /// Determines whether two counts describe the same value and the same exactness.
    /// </summary>
    /// <param name="left">The first count to compare.</param>
    /// <param name="right">The second count to compare.</param>
    /// <returns><see langword="true" /> if both counts are equal; otherwise <see langword="false" />.</returns>
    public static bool operator ==(TestCaseCount left, TestCaseCount right) => left.Equals(right);

    /// <summary>
    /// Determines whether two counts differ in value or in exactness.
    /// </summary>
    /// <param name="left">The first count to compare.</param>
    /// <param name="right">The second count to compare.</param>
    /// <returns><see langword="true" /> if both counts differ; otherwise <see langword="false" />.</returns>
    public static bool operator !=(TestCaseCount left, TestCaseCount right) => !left.Equals(right);
}
