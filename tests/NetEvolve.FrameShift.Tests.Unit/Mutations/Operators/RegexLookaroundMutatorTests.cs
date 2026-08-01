namespace NetEvolve.FrameShift.Tests.Unit.Mutations.Operators;

using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Mutations;
using NetEvolve.FrameShift.Mutations.Operators;
using NetEvolve.FrameShift.Mutations.RegularExpressions;
using NetEvolve.FrameShift.Tests.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the lookaround operator of the regular expression pattern family: the exact set of mutations for
/// the four lookaround openings, the source the mutated literal is rewritten to, the constructs the operator
/// deliberately leaves alone - every other <c>(?...</c> form above all - and the option independent
/// tokenization of the construct.
/// </summary>
/// <remarks>
/// A mutation of this family replaces the whole pattern literal, so a test that only pins the display name
/// would not notice a replacement literal whose <em>value</em> is not the pattern the name promises. The
/// tests therefore assert three things about a mutation: the operator identifier, the display name, and the
/// pattern the replacement literal denotes - and, in addition, the rewritten source text of at least one
/// mutation.
/// </remarks>
public class RegexLookaroundMutatorTests
{
    private const string OperatorIdPrefix = "regex.lookaround.";

    /// <summary>
    /// The separator between the reported lines. One joined string per expectation makes a failing
    /// assertion show the whole difference at once instead of the first deviating element.
    /// </summary>
    private const string LineSeparator = "\n";

    /// <summary>
    /// The timeout handed to every <see cref="Regex" /> a test constructs. Nothing is ever matched, so the
    /// timeout can never elapse; it is passed because the analyzers of this repository require one.
    /// </summary>
    private static readonly TimeSpan _parseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// All four lookaround forms in one pattern, in the order lookbehind, lookahead, negative lookahead,
    /// negative lookbehind - deliberately not the order the mutations are named in, so that the test proves
    /// the operator follows token order rather than any order of its own.
    /// </summary>
    private const string AllFormsSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"(?<=c)(?=a)(?!b)(?<!d)");
        }
        """;

    /// <summary>
    /// Every other opening that starts with <c>(?</c>: an ordinary non-capturing group, an atomic group, a
    /// named group, a scoped inline-options group and a standalone inline-options construct. None of them is
    /// a lookaround, and the operator therefore produces nothing at all for this fixture.
    /// </summary>
    private const string NonLookaroundFormsSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"(?:a)(?>b)(?<name>c)(?i:d)(?i)e");
        }
        """;

    /// <summary>
    /// The same lookahead twice, which is two mutation points rather than one.
    /// </summary>
    private const string RepeatedLookaheadSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"(?=a)(?=b)");
        }
        """;

    /// <summary>
    /// The source text of the literal of <see cref="AllFormsSource" />.
    /// </summary>
    private const string AllFormsLiteralText = """
        "(?<=c)(?=a)(?!b)(?<!d)"
        """;

    private static readonly string[] _fixtures = [AllFormsSource, NonLookaroundFormsSource, RepeatedLookaheadSource];

    [Test]
    public async Task Metadata_Operator_ExposesIdKindAndSupportedKinds()
    {
        var mutator = new RegexLookaroundMutator();

        _ = await Assert.That(mutator.Id).IsEqualTo("regex.lookaround");
        _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.RegexLookaround);
        _ = await Assert
            .That(mutator.SupportedSyntaxKinds)
            .IsEquivalentTo(new[] { SyntaxKind.StringLiteralExpression });
    }

    /// <summary>
    /// A fixture that does not compile makes every expectation built on it meaningless, so all of them are
    /// bound once.
    /// </summary>
    [Test]
    public async Task Fixture_EveryFixture_Compiles()
    {
        var errors = _fixtures
            .SelectMany(source => CompilationFactory.GetCompileErrors(CompilationFactory.Create(source)))
            .Select(diagnostic => diagnostic.Id);

        _ = await Assert.That(errors).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_EveryMutation_UsesTheOperatorIdPrefixAndFamilyKind()
    {
        var (_, mutations) = Mutate(AllFormsSource);
        var offenders = mutations
            .Where(mutation => !mutation.OperatorId.StartsWith(OperatorIdPrefix, StringComparison.Ordinal))
            .Select(mutation => mutation.OperatorId);

        _ = await Assert.That(offenders).IsEmpty();
        _ = await Assert
            .That(mutations.Select(mutation => mutation.Kind).Distinct())
            .IsEquivalentTo(new[] { MutationKind.RegexLookaround });
    }

    /// <summary>
    /// The four lookaround forms are each negated in place, keeping their direction: a lookbehind stays a
    /// lookbehind and a lookahead stays a lookahead, only the polarity flips.
    /// </summary>
    [Test]
    public async Task CreateMutations_AllFourForms_NegatesEachOfThemKeepingDirection()
    {
        var (_, mutations) = Mutate(AllFormsSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                "regex.lookaround.lookbehind-to-negative-lookbehind | pattern '(?<=c)(?=a)(?!b)(?<!d)' => '(?<!c)(?=a)(?!b)(?<!d)'"
                    + LineSeparator
                    + "regex.lookaround.lookahead-to-negative-lookahead | pattern '(?<=c)(?=a)(?!b)(?<!d)' => '(?<=c)(?!a)(?!b)(?<!d)'"
                    + LineSeparator
                    + "regex.lookaround.negative-lookahead-to-lookahead | pattern '(?<=c)(?=a)(?!b)(?<!d)' => '(?<=c)(?=a)(?=b)(?<!d)'"
                    + LineSeparator
                    + "regex.lookaround.negative-lookbehind-to-lookbehind | pattern '(?<=c)(?=a)(?!b)(?<!d)' => '(?<=c)(?=a)(?!b)(?<=d)'"
            );
        _ = await Assert
            .That(ReplacementPattern(mutations, "lookbehind-to-negative-lookbehind"))
            .IsEqualTo("(?<!c)(?=a)(?!b)(?<!d)");
        _ = await Assert
            .That(ReplacementPattern(mutations, "lookahead-to-negative-lookahead"))
            .IsEqualTo("(?<=c)(?!a)(?!b)(?<!d)");
        _ = await Assert
            .That(ReplacementPattern(mutations, "negative-lookahead-to-lookahead"))
            .IsEqualTo("(?<=c)(?=a)(?=b)(?<!d)");
        _ = await Assert
            .That(ReplacementPattern(mutations, "negative-lookbehind-to-lookbehind"))
            .IsEqualTo("(?<=c)(?=a)(?!b)(?<=d)");
    }

    /// <summary>
    /// The mutated source, not only the display name: the replacement is an ordinary C# literal that denotes
    /// the negated pattern.
    /// </summary>
    [Test]
    public async Task CreateMutations_AllFourForms_RewriteTheSource()
    {
        var (tree, mutations) = Mutate(AllFormsSource);

        _ = await Assert
            .That(Rewrite(tree, Single(mutations, "lookbehind-to-negative-lookbehind")))
            .IsEqualTo(
                AllFormsSource.Replace(
                    AllFormsLiteralText,
                    """
                    "(?<!c)(?=a)(?!b)(?<!d)"
                    """,
                    StringComparison.Ordinal
                )
            );
        _ = await Assert
            .That(Rewrite(tree, Single(mutations, "negative-lookahead-to-lookahead")))
            .IsEqualTo(
                AllFormsSource.Replace(
                    AllFormsLiteralText,
                    """
                    "(?<=c)(?=a)(?=b)(?<!d)"
                    """,
                    StringComparison.Ordinal
                )
            );
    }

    /// <summary>
    /// Every other opening that starts with <c>(?</c> - an ordinary group, an atomic group, a named group, a
    /// scoped inline-options group and a standalone inline-options construct - is tokenized as
    /// <see cref="NetEvolve.FrameShift.Mutations.RegularExpressions.RegexTokenKind.GroupOpen" /> or
    /// <see cref="NetEvolve.FrameShift.Mutations.RegularExpressions.RegexTokenKind.InlineOptions" /> rather
    /// than as a lookaround, so the operator produces nothing at all for this fixture.
    /// </summary>
    [Test]
    public async Task CreateMutations_EveryOtherOpeningForm_ReturnsEmpty()
    {
        var (_, mutations) = Mutate(NonLookaroundFormsSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// Every occurrence of a lookaround is a mutation point of its own, so the same form twice yields two
    /// mutations that carry the same suffix and each rewrite only their own occurrence.
    /// </summary>
    [Test]
    public async Task CreateMutations_SameFormTwice_ProducesOneMutationPerOccurrence()
    {
        var (_, mutations) = Mutate(RepeatedLookaheadSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                "regex.lookaround.lookahead-to-negative-lookahead | pattern '(?=a)(?=b)' => '(?!a)(?=b)'"
                    + LineSeparator
                    + "regex.lookaround.lookahead-to-negative-lookahead | pattern '(?=a)(?=b)' => '(?=a)(?!b)'"
            );
        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(ReplacementPattern(mutations[0])).IsEqualTo("(?!a)(?=b)");
        _ = await Assert.That(ReplacementPattern(mutations[1])).IsEqualTo("(?=a)(?!b)");
    }

    /// <summary>
    /// Every mutant of the family has to be a legal regular expression under the options of its site, so
    /// that it is killed by an assertion rather than by the <see cref="Regex" /> constructor. The real
    /// parser is the oracle; a test may construct a <see cref="Regex" />, the analyzer may not.
    /// </summary>
    [Test]
    public async Task CreateMutations_EveryMutant_IsAValidPattern()
    {
        var (_, mutations) = Mutate(AllFormsSource);
        var (_, repeated) = Mutate(RepeatedLookaheadSource);
        var offenders = mutations
            .Concat(repeated)
            .Where(mutation => !IsAcceptedByRegex(ReplacementPattern(mutation), RegexOptions.None))
            .Select(mutation => mutation.DisplayName);

        _ = await Assert.That(offenders).IsEmpty();
        _ = await Assert.That(mutations).Count().IsEqualTo(4);
        _ = await Assert.That(repeated).Count().IsEqualTo(2);
    }

    [Test]
    public async Task CreateMutations_UnsupportedSyntaxKind_ReturnsEmpty()
    {
        var (_, mutations) = Mutate(AllFormsSource, SyntaxNodeLocator.FindFirst<ObjectCreationExpressionSyntax>);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_NodeNull_ThrowsArgumentNullException()
    {
        var (_, semanticModel, _) = CompilationFactory.CreateWithModel(AllFormsSource);
        var mutator = new RegexLookaroundMutator();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(null!, semanticModel, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("node");
    }

    [Test]
    public async Task CreateMutations_SemanticModelNull_ThrowsArgumentNullException()
    {
        var (_, _, tree) = CompilationFactory.CreateWithModel(AllFormsSource);
        var mutator = new RegexLookaroundMutator();
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(node, null!, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("semanticModel");
    }

    [Test]
    public async Task CreateMutations_CancellationRequested_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(AllFormsSource);
        var mutator = new RegexLookaroundMutator();
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        using var cancellation = new CancellationTokenSource();

        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = mutator.CreateMutations(node, semanticModel, cancellation.Token).ToArray()
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    /// <summary>
    /// <c>GetNegation</c> falls back to <c>(null, null)</c> for a lookaround opening none of the four known
    /// spellings match, and <c>TryCreateRewrite</c> turns that into a <see langword="null" /> rewrite instead
    /// of throwing. The tokenizer is asserted to never produce such a token for
    /// <see cref="RegexTokenKind.Lookaround" />, so this path is unreachable through the public
    /// <c>CreateMutations</c> entry point; it is exercised here directly through the private static methods,
    /// the same way <c>ArithmeticOperatorMutatorTests</c> reaches the unreachable default arms of its own
    /// mapping tables.
    /// </summary>
    [Test]
    public async Task GetNegation_UnknownOpening_ReturnsNullReplacementAndSuffix()
    {
        var (replacement, suffix) = InvokeGetNegation("(?#");

        using (Assert.Multiple())
        {
            _ = await Assert.That(replacement).IsNull();
            _ = await Assert.That(suffix).IsNull();
        }
    }

    /// <summary>
    /// <c>TryCreateRewrite</c> is handed a <see cref="RegexToken" /> of kind
    /// <see cref="RegexTokenKind.Lookaround" /> whose text is not one of the four spellings the tokenizer
    /// ever produces for that kind. No test can reach this through the tokenizer, since the tokenizer itself
    /// guarantees the invariant; the token is constructed directly instead.
    /// </summary>
    [Test]
    public async Task TryCreateRewrite_UnknownOpeningToken_ReturnsNullWithoutThrowing()
    {
        var token = new RegexToken(RegexTokenKind.Lookaround, 0, "(?#");

        var rewrite = InvokeTryCreateRewrite("(?#x)", token);

        _ = await Assert.That(rewrite).IsNull();
    }

    /// <summary>
    /// Invokes the private static <c>GetNegation</c> mapping through reflection, which is the only way to
    /// reach its defensive default arm: the tokenizer never produces the token text that would drive
    /// <c>CreateMutations</c> there.
    /// </summary>
    /// <param name="opening">The token text to negate.</param>
    /// <returns>The replacement text and the suffix, both possibly <see langword="null" />.</returns>
    /// <exception cref="InvalidOperationException">The mapping method no longer exists.</exception>
    private static (string? Replacement, string? Suffix) InvokeGetNegation(string opening)
    {
        var method =
            typeof(RegexLookaroundMutator).GetMethod("GetNegation", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The mapping method 'GetNegation' no longer exists.");

        var result = method.Invoke(null, [opening]);
        var resultType = result!.GetType();
        var replacement = (string?)resultType.GetField("Item1")!.GetValue(result);
        var suffix = (string?)resultType.GetField("Item2")!.GetValue(result);

        return (replacement, suffix);
    }

    /// <summary>
    /// Invokes the private static <c>TryCreateRewrite</c> method through reflection, so that the null
    /// fallback for an unexpected lookaround token can be asserted directly, without a tokenizer bug to
    /// produce it.
    /// </summary>
    /// <param name="pattern">The pattern the token belongs to.</param>
    /// <param name="token">The lookaround token to hand to the method.</param>
    /// <returns>The produced rewrite, or <see langword="null" />.</returns>
    /// <exception cref="InvalidOperationException">The method no longer exists.</exception>
    private static RegexPatternRewrite? InvokeTryCreateRewrite(string pattern, RegexToken token)
    {
        var method =
            typeof(RegexLookaroundMutator).GetMethod("TryCreateRewrite", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The method 'TryCreateRewrite' no longer exists.");

        return (RegexPatternRewrite?)method.Invoke(null, [pattern, token]);
    }

    private static (SyntaxTree Tree, Mutation[] Mutations) Mutate(string source) =>
        Mutate(source, SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>);

    private static (SyntaxTree Tree, Mutation[] Mutations) Mutate(string source, Func<SyntaxTree, SyntaxNode> select)
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var mutator = new RegexLookaroundMutator();

        return (tree, [.. mutator.CreateMutations(select(tree), semanticModel, CancellationToken.None)]);
    }

    /// <summary>
    /// Builds the one string an expectation is compared against: the operator identifier and the display
    /// name of every mutation, in the order the operator produced them.
    /// </summary>
    /// <param name="mutations">The produced mutations.</param>
    /// <returns>The joined report.</returns>
    private static string Lines(Mutation[] mutations) =>
        string.Join(LineSeparator, mutations.Select(mutation => mutation.OperatorId + " | " + mutation.DisplayName));

    private static string Rewrite(SyntaxTree tree, Mutation mutation) => mutation.ApplyTo(tree).ToString();

    private static Mutation Single(Mutation[] mutations, string suffix) =>
        mutations.Single(mutation =>
            string.Equals(mutation.OperatorId, OperatorIdPrefix + suffix, StringComparison.Ordinal)
        );

    private static string ReplacementPattern(Mutation[] mutations, string suffix) =>
        ReplacementPattern(Single(mutations, suffix));

    /// <summary>
    /// Reads the pattern the replacement literal denotes, which is the value of its token and therefore
    /// exactly what the regular expression engine would receive.
    /// </summary>
    /// <param name="mutation">The mutation to inspect.</param>
    /// <returns>The pattern of the replacement literal.</returns>
    private static string ReplacementPattern(Mutation mutation) =>
        ((LiteralExpressionSyntax)mutation.Replacement).Token.ValueText;

    /// <summary>
    /// Asks the real parser whether a pattern is legal, which is the strongest available oracle for the
    /// claim that no mutant of this operator throws before an assertion can kill it.
    /// </summary>
    /// <param name="pattern">The pattern to compile.</param>
    /// <param name="options">The options of the site the pattern came from.</param>
    /// <returns><see langword="true" /> if <see cref="Regex" /> accepts the pattern.</returns>
    private static bool IsAcceptedByRegex(string pattern, RegexOptions options)
    {
        try
        {
            _ = new Regex(pattern, options, _parseTimeout);

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
