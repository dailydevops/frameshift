namespace NetEvolve.FrameShift.TestSurface;

/// <summary>
/// A name-based heuristic for recognising assertion calls inside a test method's reachable code, used to
/// decide whether an invoked production member has a credible basis for being behaviorally verified.
/// </summary>
/// <remarks>
/// <para>
/// The heuristic is deliberately name-based instead of assembly- or type-based: every mainstream .NET
/// test framework and assertion library (xUnit, NUnit, MSTest, TUnit, Shouldly, FluentAssertions, Verify)
/// spells the same handful of verbs, and matching only a closed list of fully qualified types would miss
/// every one of them that this analyzer does not special-case. The false-positive risk of the name match
/// - a production helper that happens to be named <c>IsTrue</c> - only ever widens what counts as
/// behaviorally verified, which is the same generous-by-default error direction the rest of this analyzer
/// already accepts for reachability.
/// </para>
/// <para>
/// The trivial names are kept apart on purpose: a test that only ever calls <see cref="IsTrivialCheck"/>
/// style assertions - most commonly a bare null check on a captured delegate - asserts nothing about the
/// behaviour of the code it references, which is precisely the gap this heuristic exists to close.
/// </para>
/// </remarks>
internal static class AssertionRecognition
{
    /// <summary>
    /// Method names that, on their own, do not establish that a test asserts on the behaviour of the
    /// member it invoked - typically a bare null or non-null check taken on a captured delegate or
    /// reference, which any object satisfies regardless of what it does.
    /// </summary>
    private static readonly HashSet<string> _trivialCheckNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "IsNotNull",
        "NotNull",
        "IsNull",
        "Null",
        "NotDefault",
        "IsDefault",
    };

    /// <summary>
    /// Method names recognised as asserting on an actual value or behaviour, across the mainstream .NET
    /// test frameworks and assertion libraries.
    /// </summary>
    private static readonly HashSet<string> _nonTrivialAssertionNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "AreEqual",
        "AreNotEqual",
        "AreSame",
        "AreNotSame",
        "Equal",
        "NotEqual",
        "EqualTo",
        "NotEqualTo",
        "Same",
        "NotSame",
        "IsEqualTo",
        "IsNotEqualTo",
        "Throws",
        "ThrowsAsync",
        "ThrowsExactly",
        "ThrowsExactlyAsync",
        "DoesNotThrow",
        "DoesNotThrowAsync",
        "Contains",
        "DoesNotContain",
        "Matches",
        "DoesNotMatch",
        "StartsWith",
        "EndsWith",
        "IsTrue",
        "IsFalse",
        "IsInstanceOf",
        "IsInstanceOfType",
        "IsNotInstanceOfType",
        "IsAssignableFrom",
        "IsNotAssignableFrom",
        "SequenceEqual",
        "IsEmpty",
        "IsNotEmpty",
        "Verify",
        "VerifyAsync",
        "Fail",
        "GreaterThan",
        "GreaterThanOrEqualTo",
        "LessThan",
        "LessThanOrEqualTo",
        "InRange",
        "NotInRange",
        "ShouldBe",
        "ShouldNotBe",
        "ShouldEqual",
        "ShouldContain",
        "ShouldThrow",
    };

    /// <summary>
    /// Determines whether <paramref name="methodName" /> is one of the trivial checks that, on their own,
    /// do not establish a behavioral assertion.
    /// </summary>
    /// <param name="methodName">The simple name of the invoked method.</param>
    /// <returns><see langword="true" /> if the name is a trivial check; otherwise <see langword="false" />.</returns>
    public static bool IsTrivialCheck(string methodName) => _trivialCheckNames.Contains(methodName);

    /// <summary>
    /// Determines whether <paramref name="methodName" /> is recognised as a non-trivial assertion call.
    /// </summary>
    /// <param name="methodName">The simple name of the invoked method.</param>
    /// <returns>
    /// <see langword="true" /> if the name is a recognised, non-trivial assertion; otherwise
    /// <see langword="false" />.
    /// </returns>
    public static bool IsNonTrivialAssertion(string methodName) => _nonTrivialAssertionNames.Contains(methodName);
}
