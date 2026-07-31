namespace NetEvolve.FrameShift.Tests.Unit.Mutations.RegularExpressions;

using NetEvolve.FrameShift.Mutations.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Pins the value type every operator of the regular expression pattern family produces: the argument
/// guards of its constructor, the deliberate decision that an empty pattern is a legal rewrite, and the
/// diagnostic form a failing assertion shows.
/// </summary>
/// <remarks>
/// The empty pattern is the one case worth stating twice, in the constructor test and in the
/// <c>ToString</c> test: removing the only anchor of <c>^</c> legitimately yields it, so a guard against
/// it would reject a mutation the anchor operator is expected to offer. The suffix is the opposite - it
/// becomes part of an operator identifier and an empty one would produce an identifier ending in a dot,
/// which is why it is rejected.
/// </remarks>
public class RegexPatternRewriteTests
{
    private const string Suffix = "remove-caret";

    [Test]
    public async Task Constructor_PatternIsNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => _ = new RegexPatternRewrite(null!, Suffix));

        _ = await Assert.That(exception.ParamName).IsEqualTo("pattern");
    }

    [Test]
    public async Task Constructor_OperatorSuffixIsNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => _ = new RegexPatternRewrite("a$", null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("operatorSuffix");
    }

    /// <summary>
    /// The type of the exception is asserted as well, because <see cref="ArgumentNullException" /> derives
    /// from <see cref="ArgumentException" /> and would satisfy the catch of a less specific expectation.
    /// </summary>
    [Test]
    public async Task Constructor_OperatorSuffixIsEmpty_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() => _ = new RegexPatternRewrite("a$", string.Empty));

        _ = await Assert.That(exception.GetType()).IsEqualTo(typeof(ArgumentException));
        _ = await Assert.That(exception.ParamName).IsEqualTo("operatorSuffix");
        _ = await Assert.That(exception.Message).Contains("The operator suffix must not be empty.");
    }

    [Test]
    public async Task Constructor_ValidArguments_ExposesPatternAndSuffix()
    {
        var rewrite = new RegexPatternRewrite("a$", Suffix);

        _ = await Assert.That(rewrite.Pattern).IsEqualTo("a$");
        _ = await Assert.That(rewrite.OperatorSuffix).IsEqualTo(Suffix);
    }

    /// <summary>
    /// Removing the only construct of a one construct pattern leaves nothing behind, and the empty pattern
    /// is a valid regular expression that matches at every position. It is therefore accepted unchanged.
    /// </summary>
    [Test]
    public async Task Constructor_EmptyPattern_IsAccepted()
    {
        var rewrite = new RegexPatternRewrite(string.Empty, Suffix);

        _ = await Assert.That(rewrite.Pattern).IsEqualTo(string.Empty);
        _ = await Assert.That(rewrite.OperatorSuffix).IsEqualTo(Suffix);
    }

    [Test]
    [Arguments("a$", "remove-caret", "remove-caret: 'a$'")]
    [Arguments("", "remove-caret", "remove-caret: ''")]
    [Arguments(@"\Aa", "remove-string-end", @"remove-string-end: '\Aa'")]
    public async Task ToString_AnyRewrite_ShowsTheSuffixAndTheQuotedPattern(
        string pattern,
        string operatorSuffix,
        string expected
    )
    {
        var rewrite = new RegexPatternRewrite(pattern, operatorSuffix);

        _ = await Assert.That(rewrite.ToString()).IsEqualTo(expected);
    }
}
