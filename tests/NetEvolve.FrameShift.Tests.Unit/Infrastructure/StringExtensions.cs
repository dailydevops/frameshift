#if NETFRAMEWORK
namespace NetEvolve.FrameShift.Tests.Unit.Infrastructure;

using System.Text;

/// <summary>
/// String helpers that the classic .NET Framework targets do not ship themselves.
/// </summary>
/// <remarks>
/// <c>String.Replace(string, string, StringComparison)</c> arrived with .NET Core 2.0 and is therefore
/// missing on net472, net48 and net481, while the test suite relies on it to build the expected source of a
/// mutation. The extension method is named exactly like the instance method, so a three-argument call binds
/// here on the classic targets and to the built-in member everywhere else, and no call site needs a
/// conditional block. It is compiled only where the instance method is absent, so it can never shadow it.
/// </remarks>
internal static class StringExtensions
{
    /// <summary>
    /// Replaces every occurrence of <paramref name="oldValue" /> in <paramref name="target" /> using the
    /// given comparison.
    /// </summary>
    /// <param name="target">The string to search.</param>
    /// <param name="oldValue">The value to replace.</param>
    /// <param name="newValue">The replacement, or <see langword="null" /> to remove the occurrences.</param>
    /// <param name="comparisonType">The comparison that decides what counts as an occurrence.</param>
    /// <returns>The resulting string, or <paramref name="target" /> when there is no occurrence.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="target" /> or <paramref name="oldValue" /> is <see langword="null" />.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="oldValue" /> is empty.</exception>
    public static string Replace(this string target, string oldValue, string? newValue, StringComparison comparisonType)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(oldValue);

        if (oldValue.Length == 0)
        {
            throw new ArgumentException("The value to replace must not be empty.", nameof(oldValue));
        }

        var index = target.IndexOf(oldValue, 0, comparisonType);
        if (index < 0)
        {
            return target;
        }

        var builder = new StringBuilder(target.Length);
        var position = 0;

        while (index >= 0)
        {
            _ = builder.Append(target, position, index - position).Append(newValue);
            position = index + oldValue.Length;
            index = position > target.Length ? -1 : target.IndexOf(oldValue, position, comparisonType);
        }

        return builder.Append(target, position, target.Length - position).ToString();
    }
}
#endif
