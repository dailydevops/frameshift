namespace NetEvolve.FrameShift.Tests.Unit.Mutations.RegularExpressions;

using System.Text.RegularExpressions;
using NetEvolve.FrameShift.Mutations.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Pins the second viability dimension of the regular-expression family: a mutated pattern always
/// compiles as C#, so only this check can tell a usable mutant from one that would throw the moment a
/// test reaches it. The interesting failure classes of the pattern grammar are covered one by one,
/// together with the fact that validity depends on the options rather than on the pattern alone.
/// </summary>
/// <remarks>
/// No assertion looks at the wording of an error, because the runtime supplies it: the classic
/// frameworks phrase it differently from the modern ones, and every framework localises it. What is
/// asserted is that a message exists, that it is not blank, that it names the offending pattern and
/// that two different failure classes do not collapse into the same text.
/// </remarks>
public class RegexPatternValidityTests
{
    private const string UnterminatedClass = "[a-z";

    private static readonly string[] _malformedPatterns =
    [
        "(a", // unbalanced parenthesis, nothing closes the group
        "a)", // unbalanced parenthesis, nothing opened the group
        UnterminatedClass, // unterminated character class
        "a{2,1}", // invalid quantifier, the lower bound exceeds the upper one
        "*a", // dangling quantifier, there is nothing to repeat
        "a**", // nested quantifier
        "(?z:a)", // unknown group construct
        @"\q", // invalid escape sequence
        @"\x1", // invalid escape sequence, not enough hexadecimal digits
        @"(a)\2", // invalid backreference, group 2 does not exist
        @"\k<none>", // invalid named backreference
        @"\p{Nonsense}", // invalid unicode property name
        "[z-a]", // character class range in reverse order
    ];

    /// <summary>
    /// Feeds one malformed pattern per failure class of the pattern grammar into the rejection tests.
    /// </summary>
    /// <returns>One factory per malformed pattern.</returns>
    public static IEnumerable<Func<string>> MalformedPatterns() =>
        _malformedPatterns.Select(pattern => (Func<string>)(() => pattern));

    [Test]
    public async Task MalformedPatterns_DataSource_CoversEveryListedPattern()
    {
        var patterns = MalformedPatterns().Select(factory => factory()).ToArray();

        _ = await Assert.That(patterns).IsEquivalentTo(_malformedPatterns);
        _ = await Assert.That(patterns.Length).IsEqualTo(13);
    }

    [Test]
    public async Task IsValid_PatternIsNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = RegexPatternValidity.IsValid(null!, RegexOptions.None, out _)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("pattern");
    }

    [Test]
    public async Task IsValid_EmptyPattern_ReturnsTrue()
    {
        var valid = RegexPatternValidity.IsValid(string.Empty, RegexOptions.None, out var error);

        _ = await Assert.That(valid).IsTrue();
        _ = await Assert.That(error).IsNull();
    }

    [Test]
    [Arguments("^abc$")]
    [Arguments("a|b")]
    [Arguments("[a-z]+")]
    [Arguments("a{2,3}")]
    [Arguments(@"\d{2,4}")]
    [Arguments(@"(?<year>\d{4})-(?<month>\d{2})")]
    [Arguments(@"\p{Lu}+")]
    [Arguments(@"(a)\1")]
    [Arguments("(?i)abc")]
    [Arguments("(?#a comment group)a")]
    [Arguments("(?>a+)b")]
    public async Task IsValid_WellFormedPattern_ReturnsTrueWithoutError(string pattern)
    {
        var valid = RegexPatternValidity.IsValid(pattern, RegexOptions.None, out var error);

        _ = await Assert.That(valid).IsTrue();
        _ = await Assert.That(error).IsNull();
    }

    [Test]
    [MethodDataSource(nameof(MalformedPatterns))]
    public async Task IsValid_MalformedPattern_ReturnsFalseWithError(string pattern)
    {
        var valid = RegexPatternValidity.IsValid(pattern, RegexOptions.None, out var error);

        _ = await Assert.That(valid).IsFalse();
        _ = await Assert.That(string.IsNullOrWhiteSpace(error)).IsFalse();
    }

    [Test]
    public async Task IsValid_MalformedPattern_ErrorNamesTheOffendingPattern()
    {
        var valid = RegexPatternValidity.IsValid(UnterminatedClass, RegexOptions.None, out var error);

        _ = await Assert.That(valid).IsFalse();
        _ = await Assert.That(error!.Contains(UnterminatedClass, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task IsValid_DifferentFailureClasses_ProduceDifferentErrors()
    {
        var errors = new List<string>(_malformedPatterns.Length);

        foreach (var pattern in _malformedPatterns)
        {
            _ = RegexPatternValidity.IsValid(pattern, RegexOptions.None, out var error);
            errors.Add(error!);
        }

        _ = await Assert.That(errors.Count).IsEqualTo(_malformedPatterns.Length);
        _ = await Assert.That(errors.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(errors.Count);
    }

    [Test]
    public async Task IsValid_UndefinedOptionValue_ReturnsFalseWithError()
    {
        // No runtime of the matrix defines this bit, so the constructor rejects the option value itself
        // rather than the pattern. That is the same door RegexOptions.NonBacktracking comes through on a
        // runtime that does not know it, and it must not escape as an exception.
        var valid = RegexPatternValidity.IsValid("abc", (RegexOptions)0x8000, out var error);

        _ = await Assert.That(valid).IsFalse();
        _ = await Assert.That(string.IsNullOrWhiteSpace(error)).IsFalse();
    }

    [Test]
    [Arguments("^abc$", true)]
    [Arguments(UnterminatedClass, false)]
    public async Task IsValid_CalledRepeatedly_ReturnsTheSameAnswer(string pattern, bool expected)
    {
        var first = RegexPatternValidity.IsValid(pattern, RegexOptions.None, out var firstError);
        var second = RegexPatternValidity.IsValid(pattern, RegexOptions.None, out var secondError);
        var third = RegexPatternValidity.IsValid(pattern, RegexOptions.None, out var thirdError);

        _ = await Assert.That(first).IsEqualTo(expected);
        _ = await Assert.That(second).IsEqualTo(expected);
        _ = await Assert.That(third).IsEqualTo(expected);
        _ = await Assert.That(secondError).IsEqualTo(firstError);
        _ = await Assert.That(thirdError).IsEqualTo(firstError);
    }

    [Test]
    [Arguments("^abc$")]
    [Arguments("[a-z]+")]
    public async Task IsValid_IgnoreCaseOption_DoesNotChangeValidity(string pattern)
    {
        var withoutOption = RegexPatternValidity.IsValid(pattern, RegexOptions.None, out var withoutError);
        var withOption = RegexPatternValidity.IsValid(pattern, RegexOptions.IgnoreCase, out var withError);

        _ = await Assert.That(withoutOption).IsTrue();
        _ = await Assert.That(withOption).IsTrue();
        _ = await Assert.That(withoutError).IsNull();
        _ = await Assert.That(withError).IsNull();
    }

#if NET7_0_OR_GREATER
    // RegexOptions.NonBacktracking arrived with .NET 7. On net6.0 and the classic targets the enum member
    // does not exist, and the bit is reported as an undefined option value instead, which
    // IsValid_UndefinedOptionValue_ReturnsFalseWithError already covers on every framework.

    [Test]
    [Arguments(@"(a)\1")] // a backreference, which the non-backtracking engine cannot represent
    [Arguments("(?>a+)b")] // an atomic subexpression, likewise unsupported there
    public async Task IsValid_UnsupportedUnderNonBacktracking_IsValidUnderNoneOnly(string pattern)
    {
        var underNone = RegexPatternValidity.IsValid(pattern, RegexOptions.None, out var noneError);
        var underNonBacktracking = RegexPatternValidity.IsValid(
            pattern,
            RegexOptions.NonBacktracking,
            out var nonBacktrackingError
        );

        _ = await Assert.That(underNone).IsTrue();
        _ = await Assert.That(noneError).IsNull();
        _ = await Assert.That(underNonBacktracking).IsFalse();
        _ = await Assert.That(string.IsNullOrWhiteSpace(nonBacktrackingError)).IsFalse();
    }

    [Test]
    [Arguments("^abc$")]
    [Arguments(@"\d{2,4}")]
    [Arguments("[a-z]+|[0-9]+")]
    public async Task IsValid_SupportedUnderNonBacktracking_ReturnsTrue(string pattern)
    {
        var valid = RegexPatternValidity.IsValid(pattern, RegexOptions.NonBacktracking, out var error);

        _ = await Assert.That(valid).IsTrue();
        _ = await Assert.That(error).IsNull();
    }

    [Test]
    public async Task IsValid_NonBacktrackingCombinedWithRightToLeft_ReturnsFalseWithError()
    {
        var valid = RegexPatternValidity.IsValid(
            "abc",
            RegexOptions.NonBacktracking | RegexOptions.RightToLeft,
            out var error
        );

        _ = await Assert.That(valid).IsFalse();
        _ = await Assert.That(string.IsNullOrWhiteSpace(error)).IsFalse();
    }

    [Test]
    public async Task IsValid_MalformedPatternUnderNonBacktracking_ReturnsFalseWithError()
    {
        var valid = RegexPatternValidity.IsValid(UnterminatedClass, RegexOptions.NonBacktracking, out var error);

        _ = await Assert.That(valid).IsFalse();
        _ = await Assert.That(string.IsNullOrWhiteSpace(error)).IsFalse();
    }
#endif
}
