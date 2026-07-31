namespace NetEvolve.FrameShift.Mutations.RegularExpressions;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

/// <summary>
/// The second viability dimension of the regular-expression mutation family: whether a pattern is a
/// legal regular expression under a given set of <see cref="RegexOptions" />.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MutantViability" /> answers a different question, namely whether a mutant is still a
/// legal C# program. A mutated pattern is only ever the content of a string literal, so it always
/// compiles, and a mutant that turns <c>[a-z]</c> into <c>[a-</c> would pass that check and then throw
/// at run time in every test that reaches it. Such a mutant is worthless as a coverage signal, because
/// it is killed by construction rather than by an assertion. This type is therefore consulted in
/// addition to the compile check, never instead of it.
/// </para>
/// <para>
/// Analysis safety: the check constructs a <see cref="Regex" /> and immediately drops it. Construction
/// only parses the pattern and builds the internal node tree; no input string is ever matched, so no
/// user code and no matching engine runs, and there is no backtracking, no catastrophic backtracking
/// and no unbounded run time to guard against. Parsing is linear in the length of the pattern. For
/// exactly that reason no match timeout is passed, because a timeout only ever applies to matching,
/// and <see cref="RegexOptions.Compiled" /> is deliberately not added either, since it would emit IL
/// for an object that is thrown away. Because parsing cannot block, the check takes no cancellation
/// token, and because it catches two specific exception types rather than
/// <see cref="Exception" />, it can never swallow an <see cref="OperationCanceledException" /> raised
/// somewhere up the stack.
/// </para>
/// <para>
/// Statelessness: the type holds no state at all, which keeps it thread-safe and free of the ambient
/// state an analyzer must not have. It also does not cache, because a static cache would live for the
/// lifetime of the compiler process and grow with every pattern of every compilation it ever sees. A
/// caller that needs memoisation owns a <see cref="ConcurrentDictionary{TKey, TValue}" /> keyed by the
/// pattern <em>and</em> the options together, scoped to a single compilation, the way
/// <see cref="MutantCompiler" /> scopes its own cache; keying by the pattern alone would be wrong,
/// because validity depends on the options.
/// </para>
/// <para>
/// Options dependence: <c>RegexOptions.NonBacktracking</c> accepts a strictly smaller grammar than the
/// backtracking engine. Constructs such as backreferences or atomic groups are perfectly valid
/// otherwise and are rejected under that option, and it cannot be combined with
/// <see cref="RegexOptions.RightToLeft" /> or <see cref="RegexOptions.ECMAScript" /> at all. The option
/// is not named in code here, because it does not exist on the <c>netstandard2.0</c> surface this
/// analyzer compiles against; it arrives as a bit in the value the caller resolved. On a host runtime
/// that does not know the bit, the constructor rejects it as an undefined option, which this method
/// reports as an invalid combination rather than letting it escape.
/// </para>
/// </remarks>
internal static class RegexPatternValidity
{
    /// <summary>
    /// Determines whether <paramref name="pattern" /> is a valid regular expression under
    /// <paramref name="options" />.
    /// </summary>
    /// <param name="pattern">The pattern text to check, as it would appear in the string literal.</param>
    /// <param name="options">
    /// The options the pattern is used with. They are part of the question, not decoration: the same
    /// pattern can be valid under one set of options and invalid under another.
    /// </param>
    /// <param name="error">
    /// When this method returns <see langword="false" />, the message of the exception the constructor
    /// threw, never empty; otherwise <see langword="null" />. The wording comes from the runtime and is
    /// localised, so it is suitable for a diagnostic message but must not be matched against.
    /// </param>
    /// <returns>
    /// <see langword="true" /> if a <see cref="Regex" /> can be constructed from
    /// <paramref name="pattern" /> and <paramref name="options" />; otherwise <see langword="false" />.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern" /> is <see langword="null" />.</exception>
    [SuppressMessage(
        "Security",
        "MA0009:Regular expressions should not be vulnerable to Denial of Service attacks",
        Justification = "Construction only parses the pattern, it never matches, so a match timeout cannot apply."
    )]
    [SuppressMessage(
        "Security Hotspot",
        "S6444:Pass a timeout to limit the execution time",
        Justification = "Construction only parses the pattern, it never matches, so a match timeout cannot apply."
    )]
    public static bool IsValid(string pattern, RegexOptions options, out string? error)
    {
        if (pattern is null)
        {
            throw new ArgumentNullException(nameof(pattern));
        }

        try
        {
            // Parsing is the whole point: the instance is not needed, only the fact that it could be built.
            _ = new Regex(pattern, options);
        }
        catch (ArgumentException exception)
        {
            // A malformed pattern arrives as RegexParseException where the runtime has that type, and as a
            // plain ArgumentException on the older ones; an option value the runtime does not define arrives
            // as ArgumentOutOfRangeException. All three are ArgumentException, and nothing else that derives
            // from it can reach this point, because the only arguments are the pattern and the options.
            error = Describe(exception);
            return false;
        }
        catch (NotSupportedException exception)
        {
            // The non-backtracking engine reports a construct it cannot represent, and an option
            // combination it cannot honour, as not supported rather than as a parse error.
            error = Describe(exception);
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Turns the exception the constructor threw into a message that is guaranteed to be non-empty.
    /// </summary>
    /// <param name="exception">The exception thrown while constructing the <see cref="Regex" />.</param>
    /// <returns>
    /// The exception message, or the exception type name if the runtime supplied no message, so that a
    /// caller reporting the error never ends up with a blank explanation.
    /// </returns>
    private static string Describe(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;
}
