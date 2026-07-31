namespace NetEvolve.FrameShift.Tests.Unit.Equivalence;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Equivalence;
using NetEvolve.FrameShift.Mutations;
using NetEvolve.FrameShift.Tests.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the quantifier shorthand check of <see cref="EquivalenceClassifier" />: the narrow, provably
/// sound rule that an exact-one counted quantifier is the same as no quantifier at all and that
/// <c>{0,1}</c>, <c>{1,}</c> and <c>{0,}</c> are the counted spellings of <c>?</c>, <c>+</c> and <c>*</c>.
/// Every case the check must not prove is covered as well, because a wrong trivial verdict here would
/// silently hide a real testing gap.
/// </summary>
public class EquivalenceClassifierRegexTests
{
    private const string ExactOneReason =
        "the quantifier repeats its atom exactly once, which leaving the quantifier out already does";
    private const string OptionalReason = "the counted quantifier is the same as the optional operator";
    private const string OneOrMoreReason = "the counted quantifier is the same as the one-or-more operator";
    private const string ZeroOrMoreReason = "the counted quantifier is the same as the zero-or-more operator";

    [Test]
    public async Task Classify_ExactOneToNoQuantifier_IsTrivialExactOne()
    {
        var verdict = ClassifyRegex("a{1}", "a");

        await AssertTrivialAsync(verdict, ExactOneReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_NoQuantifierToExactOne_IsTrivialExactOne()
    {
        var verdict = ClassifyRegex("a", "a{1}");

        await AssertTrivialAsync(verdict, ExactOneReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_ExactOneCommaOneToNoQuantifier_IsTrivialExactOne()
    {
        var verdict = ClassifyRegex("a{1,1}", "a");

        await AssertTrivialAsync(verdict, ExactOneReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_NoQuantifierToExactOneCommaOne_IsTrivialExactOne()
    {
        var verdict = ClassifyRegex("a", "a{1,1}");

        await AssertTrivialAsync(verdict, ExactOneReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_GreedyZeroToOneCountedToOptional_IsTrivialOptional()
    {
        var verdict = ClassifyRegex("a{0,1}", "a?");

        await AssertTrivialAsync(verdict, OptionalReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_LazyZeroToOneCountedToLazyOptional_IsTrivialOptional()
    {
        var verdict = ClassifyRegex("a{0,1}?", "a??");

        await AssertTrivialAsync(verdict, OptionalReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_GreedyOneToInfinityCountedToOneOrMore_IsTrivialOneOrMore()
    {
        var verdict = ClassifyRegex("a{1,}", "a+");

        await AssertTrivialAsync(verdict, OneOrMoreReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_LazyOneToInfinityCountedToLazyOneOrMore_IsTrivialOneOrMore()
    {
        var verdict = ClassifyRegex("a{1,}?", "a+?");

        await AssertTrivialAsync(verdict, OneOrMoreReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_GreedyZeroToInfinityCountedToZeroOrMore_IsTrivialZeroOrMore()
    {
        var verdict = ClassifyRegex("a{0,}", "a*");

        await AssertTrivialAsync(verdict, ZeroOrMoreReason).ConfigureAwait(false);
    }

    [Test]
    public async Task Classify_LazyZeroToInfinityCountedToLazyZeroOrMore_IsTrivialZeroOrMore()
    {
        var verdict = ClassifyRegex("a{0,}?", "a*?");

        await AssertTrivialAsync(verdict, ZeroOrMoreReason).ConfigureAwait(false);
    }

    /// <summary>
    /// An ordinary bound mutation, unrelated to any of the four shorthand forms, must not be proven
    /// trivial: it demonstrates the check does not over-fire on ordinary quantifier bound mutations.
    /// </summary>
    [Test]
    public async Task Classify_ExactCountChangesToADifferentExactCount_IsNotTrivial()
    {
        var verdict = ClassifyRegex("a{2}", "a{3}");

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    /// <summary>
    /// A direct regression test for the conservatism boundary named in FSH0002.md itself: <c>a+</c> and
    /// <c>aa*</c> describe the same language, but general pattern equivalence is out of scope and stays
    /// out of scope.
    /// </summary>
    [Test]
    public async Task Classify_DifferentPatternsThatMatchTheSameLanguage_IsNotTrivial()
    {
        var verdict = ClassifyRegex("a+", "aa*");

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    /// <summary>
    /// When the options of the site are not statically determinable, the check must fall through to
    /// <see cref="EquivalenceVerdict.NotTrivial" /> rather than guessing or erroring.
    /// </summary>
    [Test]
    public async Task Classify_OptionsNotStaticallyDeterminable_IsNotTrivial()
    {
        var source = """
            namespace Fixtures;

            using System.Text.RegularExpressions;

            internal static class Patterns
            {
                internal static Regex Create(RegexOptions options) => new Regex(/*!*/"a{1}", options);
            }
            """;

        var (_, model, tree) = CompilationFactory.CreateWithModel(source);
        var original = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        var replacement = CreateStringLiteral("a");

        var verdict = EquivalenceClassifier.Classify(
            CreateMutation(original, replacement),
            model,
            CancellationToken.None
        );

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    /// <summary>
    /// A mutation on an ordinary string literal that is not a regular expression pattern must fall
    /// through cleanly, without the check misfiring.
    /// </summary>
    [Test]
    public async Task Classify_OrdinaryStringLiteral_IsNotTrivial()
    {
        var source = """
            namespace Fixtures;

            internal static class Greeter
            {
                internal static string Greet() => /*!*/"a{1}";
            }
            """;

        var (_, model, tree) = CompilationFactory.CreateWithModel(source);
        var original = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        var replacement = CreateStringLiteral("a");

        var verdict = EquivalenceClassifier.Classify(
            CreateMutation(original, replacement),
            model,
            CancellationToken.None
        );

        await AssertNotTrivialAsync(verdict).ConfigureAwait(false);
    }

    private static EquivalenceVerdict ClassifyRegex(string originalPattern, string mutatedPattern)
    {
        var source = Wrap(originalPattern);
        var (_, model, tree) = CompilationFactory.CreateWithModel(source);
        var original = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        var replacement = CreateStringLiteral(mutatedPattern);

        return EquivalenceClassifier.Classify(CreateMutation(original, replacement), model, CancellationToken.None);
    }

    private static LiteralExpressionSyntax CreateStringLiteral(string value) =>
        SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value));

    private static Mutation CreateMutation(SyntaxNode original, SyntaxNode replacement) =>
        new Mutation(MutationKind.RegexQuantifier, "fixture.mutation", "fixture mutation", original, replacement);

    private static async Task AssertTrivialAsync(EquivalenceVerdict verdict, string expectedReason)
    {
        _ = await Assert.That(verdict.IsTrivial).IsTrue();
        _ = await Assert.That(verdict.Reason).IsEqualTo(expectedReason);
    }

    private static async Task AssertNotTrivialAsync(EquivalenceVerdict verdict)
    {
        _ = await Assert.That(verdict.Reason).IsNull();
        _ = await Assert.That(verdict.IsTrivial).IsFalse();
    }

    /// <summary>
    /// Wraps a pattern into a marked <c>Regex</c> constructor call, escaping it as an ordinary C# string
    /// literal so that the literal's value is exactly <paramref name="pattern" />.
    /// </summary>
    /// <param name="pattern">The pattern the marked literal has to denote.</param>
    /// <returns>The fixture source.</returns>
    private static string Wrap(string pattern)
    {
        var escaped = pattern.Replace("\\", "\\\\", StringComparison.Ordinal);

        return $$"""
            namespace Fixtures;

            using System.Text.RegularExpressions;

            internal static class Patterns
            {
                internal static Regex Create() => new Regex(/*!*/"{{escaped}}");
            }
            """;
    }
}
