namespace NetEvolve.FrameShift.Tests.Unit.Mutations.Operators;

using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Mutations;
using NetEvolve.FrameShift.Mutations.Operators;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the anchor operator of the regular expression pattern family: the exact set of mutations for the
/// two line anchors, the three string anchors and the two word boundary assertions, the source the mutated
/// literal is rewritten to, the three C# spellings of one and the same pattern, the constructs the operator
/// deliberately leaves alone - a character class member above all - and the option dependent tokenization.
/// </summary>
/// <remarks>
/// <para>
/// A mutation of this family replaces the whole pattern literal, so a test that only pins the display name
/// would not notice a replacement literal whose <em>value</em> is not the pattern the name promises. The
/// tests therefore assert three things about a mutation: the operator identifier, the display name, and the
/// pattern the replacement literal denotes - and, for the fixtures whose pattern needs a backslash, the
/// rewritten source text as well.
/// </para>
/// <para>
/// A fixture whose pattern contains a backslash is spelled with doubled backslashes in the ordinary form or
/// as a verbatim literal, so that the <em>value</em> of the literal is the pattern the expectation names.
/// The constants next to the fixtures spell out the exact source text a replacement produces, because the
/// replacement is always an ordinary literal whichever form the original used.
/// </para>
/// </remarks>
public class RegexAnchorMutatorTests
{
    private const string OperatorIdPrefix = "regex.anchor.";

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

    private const string LineAnchorSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"^a$");
        }
        """;

    private const string StringAnchorSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"\\Aa\\z");
        }
        """;

    /// <summary>
    /// The same pattern as <see cref="StringAnchorSource" />, spelled as a verbatim literal.
    /// </summary>
    private const string VerbatimStringAnchorSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/@"\Aa\z");
        }
        """;

    /// <summary>
    /// The same pattern as <see cref="LineAnchorSource" />, spelled as a raw string literal. The fixture
    /// itself needs four quotes as its delimiter, because its content holds a run of three.
    /// </summary>
    private const string RawLineAnchorSource = """"
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"""^a$""");
        }
        """";

    /// <summary>
    /// The third string anchor, which is not <c>\z</c>: it also matches in front of a final newline, so it
    /// carries a suffix of its own.
    /// </summary>
    private const string FinalNewlineAnchorSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"a\\Z");
        }
        """;

    private const string WordBoundarySource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"\\Ba\\b");
        }
        """;

    /// <summary>
    /// The two line anchors and the word boundary assertion as members of a character class, where none of
    /// them is an assertion at all: <c>\b</c> is the backspace character, and <c>^</c> as well as <c>$</c>
    /// are ordinary members.
    /// </summary>
    private const string CharacterClassSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"[\\b^$]");
        }
        """;

    private const string MatchStartSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"\\G");
        }
        """;

    /// <summary>
    /// The same anchor twice, which is two mutation points rather than one.
    /// </summary>
    private const string RepeatedAnchorSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"\\ba\\b");
        }
        """;

    private const string OnlyCaretSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"^");
        }
        """;

    /// <summary>
    /// Under <c>RegexOptions.IgnorePatternWhitespace</c> the blanks are insignificant and <c>#</c> starts a
    /// comment, so the very same characters tokenize into different constructs than they would otherwise.
    /// </summary>
    private const string IgnorePatternWhitespaceSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() =>
                new Regex(/*!*/"^a $ #x", RegexOptions.IgnorePatternWhitespace);
        }
        """;

    /// <summary>
    /// The source text of the literal of <see cref="LineAnchorSource" />, meaning <c>"^a$"</c>.
    /// </summary>
    private const string LineAnchorLiteralText = @"""^a$""";

    /// <summary>
    /// The source text of the literal of <see cref="StringAnchorSource" />, meaning <c>"\\Aa\\z"</c>.
    /// </summary>
    private const string StringAnchorLiteralText = @"""\\Aa\\z""";

    /// <summary>
    /// The source text of the literal of <see cref="VerbatimStringAnchorSource" />, meaning
    /// <c>@"\Aa\z"</c>.
    /// </summary>
    private const string VerbatimStringAnchorLiteralText = @"@""\Aa\z""";

    /// <summary>
    /// The literal the removal of <c>\A</c> produces, meaning <c>"a\\z"</c>.
    /// </summary>
    private const string StringStartRemovedLiteralText = @"""a\\z""";

    /// <summary>
    /// The literal the removal of <c>\z</c> produces, meaning <c>"\\Aa"</c>.
    /// </summary>
    private const string StringEndRemovedLiteralText = @"""\\Aa""";

    private static readonly string[] _fixtures =
    [
        LineAnchorSource,
        StringAnchorSource,
        VerbatimStringAnchorSource,
        FinalNewlineAnchorSource,
        RawLineAnchorSource,
        WordBoundarySource,
        CharacterClassSource,
        MatchStartSource,
        RepeatedAnchorSource,
        OnlyCaretSource,
        IgnorePatternWhitespaceSource,
    ];

    [Test]
    public async Task Metadata_Operator_ExposesIdKindAndSupportedKinds()
    {
        var mutator = new RegexAnchorMutator();

        _ = await Assert.That(mutator.Id).IsEqualTo("regex.anchor");
        _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.RegexAnchor);
        _ = await Assert.That(mutator.SupportedSyntaxKinds).IsEquivalentTo([SyntaxKind.StringLiteralExpression]);
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
        var (_, mutations) = Mutate(LineAnchorSource);
        var offenders = mutations
            .Where(mutation => !mutation.OperatorId.StartsWith(OperatorIdPrefix, StringComparison.Ordinal))
            .Select(mutation => mutation.OperatorId);

        _ = await Assert.That(offenders).IsEmpty();
        _ = await Assert
            .That(mutations.Select(mutation => mutation.Kind).Distinct())
            .IsEquivalentTo([MutationKind.RegexAnchor]);
    }

    /// <summary>
    /// The two line anchors, whose meaning depends on <c>RegexOptions.Multiline</c> and which are therefore
    /// reported apart from the string anchors.
    /// </summary>
    [Test]
    public async Task CreateMutations_LineAnchors_RemovesEachOfThem()
    {
        var (_, mutations) = Mutate(LineAnchorSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                "regex.anchor.remove-caret | pattern '^a$' => 'a$'"
                    + LineSeparator
                    + "regex.anchor.remove-dollar | pattern '^a$' => '^a'"
            );
        _ = await Assert.That(ReplacementPattern(mutations, "remove-caret")).IsEqualTo("a$");
        _ = await Assert.That(ReplacementPattern(mutations, "remove-dollar")).IsEqualTo("^a");
    }

    /// <summary>
    /// The three string anchors are removed as well, and <c>\Z</c> is kept apart from <c>\z</c>, because
    /// the two describe different positions and a surviving mutant of either names a different test.
    /// </summary>
    [Test]
    public async Task CreateMutations_StringAnchors_RemovesEachOfThem()
    {
        var (_, mutations) = Mutate(StringAnchorSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                @"regex.anchor.remove-string-start | pattern '\Aa\z' => 'a\z'"
                    + LineSeparator
                    + @"regex.anchor.remove-string-end | pattern '\Aa\z' => '\Aa'"
            );
        _ = await Assert.That(ReplacementPattern(mutations, "remove-string-start")).IsEqualTo(@"a\z");
        _ = await Assert.That(ReplacementPattern(mutations, "remove-string-end")).IsEqualTo(@"\Aa");
    }

    /// <summary>
    /// <c>\Z</c> is removed too, under a suffix of its own: it matches at the end of the input and in front
    /// of a final newline, so a mutant of it names a different missing test than a mutant of <c>\z</c>.
    /// </summary>
    [Test]
    public async Task CreateMutations_FinalNewlineAnchor_CarriesItsOwnSuffix()
    {
        var (_, mutations) = Mutate(FinalNewlineAnchorSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(@"regex.anchor.remove-string-end-before-final-newline | pattern 'a\Z' => 'a'");
        _ = await Assert.That(ReplacementPattern(mutations, "remove-string-end-before-final-newline")).IsEqualTo("a");
    }

    /// <summary>
    /// A word boundary is negated rather than removed, in both directions: the negation inverts the
    /// assertion for every input, while a removal would only be observable for the inputs sitting right at
    /// a boundary.
    /// </summary>
    [Test]
    public async Task CreateMutations_WordBoundaries_NegatesThemInBothDirections()
    {
        var (_, mutations) = Mutate(WordBoundarySource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                @"regex.anchor.non-word-boundary-to-word-boundary | pattern '\Ba\b' => '\ba\b'"
                    + LineSeparator
                    + @"regex.anchor.word-boundary-to-non-word-boundary | pattern '\Ba\b' => '\Ba\B'"
            );
        _ = await Assert.That(ReplacementPattern(mutations, "non-word-boundary-to-word-boundary")).IsEqualTo(@"\ba\b");
        _ = await Assert.That(ReplacementPattern(mutations, "word-boundary-to-non-word-boundary")).IsEqualTo(@"\Ba\B");
    }

    /// <summary>
    /// The mutated source, not only the display name: the replacement is an ordinary C# literal, so its
    /// escaping is part of the contract.
    /// </summary>
    [Test]
    public async Task CreateMutations_LineAnchors_RewriteTheSource()
    {
        var (tree, mutations) = Mutate(LineAnchorSource);

        _ = await Assert
            .That(Rewrite(tree, Single(mutations, "remove-caret")))
            .IsEqualTo(LineAnchorSource.Replace(LineAnchorLiteralText, @"""a$""", StringComparison.Ordinal));
        _ = await Assert
            .That(Rewrite(tree, Single(mutations, "remove-dollar")))
            .IsEqualTo(LineAnchorSource.Replace(LineAnchorLiteralText, @"""^a""", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same for a pattern that needs a backslash, which is the case a display name alone cannot pin: a
    /// replacement literal spelling <c>"a\z"</c> instead of <c>"a\\z"</c> would not even compile, and one
    /// spelling <c>"a\\\\z"</c> would compile and denote a different pattern.
    /// </summary>
    [Test]
    public async Task CreateMutations_StringAnchors_RewriteTheSourceWithEscapedBackslashes()
    {
        var (tree, mutations) = Mutate(StringAnchorSource);

        _ = await Assert
            .That(Rewrite(tree, Single(mutations, "remove-string-start")))
            .IsEqualTo(
                StringAnchorSource.Replace(
                    StringAnchorLiteralText,
                    StringStartRemovedLiteralText,
                    StringComparison.Ordinal
                )
            );
        _ = await Assert
            .That(Rewrite(tree, Single(mutations, "remove-string-end")))
            .IsEqualTo(
                StringAnchorSource.Replace(
                    StringAnchorLiteralText,
                    StringEndRemovedLiteralText,
                    StringComparison.Ordinal
                )
            );
        _ = await Assert
            .That(CompilationFactory.GetCompileErrors(CompilationFactory.Create(Rewrite(tree, mutations[0]))))
            .IsEmpty();
    }

    /// <summary>
    /// A verbatim literal denotes the very same pattern as the doubled backslash form, so it produces the
    /// very same mutations. This is what makes the pattern being the <em>value</em> of the token rather
    /// than its source text observable, and the replacement is an ordinary literal either way.
    /// </summary>
    [Test]
    public async Task CreateMutations_VerbatimLiteral_ProducesTheSameMutations()
    {
        var (tree, mutations) = Mutate(VerbatimStringAnchorSource);
        var (_, ordinary) = Mutate(StringAnchorSource);

        _ = await Assert.That(Lines(mutations)).IsEqualTo(Lines(ordinary));
        _ = await Assert
            .That(Rewrite(tree, Single(mutations, "remove-string-start")))
            .IsEqualTo(
                VerbatimStringAnchorSource.Replace(
                    VerbatimStringAnchorLiteralText,
                    StringStartRemovedLiteralText,
                    StringComparison.Ordinal
                )
            );
    }

    /// <summary>
    /// A raw string literal is recognised as well, and its mutant is an ordinary literal.
    /// </summary>
    [Test]
    public async Task CreateMutations_RawStringLiteral_ProducesTheSameMutations()
    {
        var (tree, mutations) = Mutate(RawLineAnchorSource);
        var (_, ordinary) = Mutate(LineAnchorSource);

        _ = await Assert.That(Lines(mutations)).IsEqualTo(Lines(ordinary));
        _ = await Assert
            .That(Rewrite(tree, Single(mutations, "remove-caret")))
            .IsEqualTo(RawLineAnchorSource.Replace(@"""""""^a$""""""", @"""a$""", StringComparison.Ordinal));
    }

    /// <summary>
    /// The acceptance criterion of the issue: inside a character class none of these characters is an
    /// assertion. <c>\b</c> is the backspace character, the <c>^</c> and the <c>$</c> are ordinary members,
    /// and the operator therefore produces nothing at all - which it achieves by answering only for anchor
    /// tokens, without looking at the characters around them.
    /// </summary>
    [Test]
    public async Task CreateMutations_AnchorCharactersInsideACharacterClass_ReturnsEmpty()
    {
        var (_, mutations) = Mutate(CharacterClassSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// <c>\G</c> pins the start of the match to the end of the previous one rather than describing a
    /// position inside the input, so it is out of scope and produces nothing.
    /// </summary>
    [Test]
    public async Task CreateMutations_MatchStartAnchor_ReturnsEmpty()
    {
        var (_, mutations) = Mutate(MatchStartSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// Every occurrence of an anchor is a mutation point of its own, so the same anchor twice yields two
    /// mutations that carry the same suffix and each rewrite only their own occurrence.
    /// </summary>
    [Test]
    public async Task CreateMutations_SameAnchorTwice_ProducesOneMutationPerOccurrence()
    {
        var (_, mutations) = Mutate(RepeatedAnchorSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                @"regex.anchor.word-boundary-to-non-word-boundary | pattern '\ba\b' => '\Ba\b'"
                    + LineSeparator
                    + @"regex.anchor.word-boundary-to-non-word-boundary | pattern '\ba\b' => '\ba\B'"
            );
        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(ReplacementPattern(mutations[0])).IsEqualTo(@"\Ba\b");
        _ = await Assert.That(ReplacementPattern(mutations[1])).IsEqualTo(@"\ba\B");
    }

    /// <summary>
    /// Removing the only anchor of a pattern leaves the empty pattern, which is a legal regular expression
    /// and therefore a legitimate mutant: it is produced rather than discarded.
    /// </summary>
    [Test]
    public async Task CreateMutations_OnlyAnchor_ProducesTheEmptyPattern()
    {
        var (tree, mutations) = Mutate(OnlyCaretSource);

        _ = await Assert.That(Lines(mutations)).IsEqualTo("regex.anchor.remove-caret | pattern '^' => ''");
        _ = await Assert.That(ReplacementPattern(mutations, "remove-caret")).IsEqualTo(string.Empty);
        _ = await Assert
            .That(Rewrite(tree, Single(mutations, "remove-caret")))
            .IsEqualTo(OnlyCaretSource.Replace(@"""^""", @"""""", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every mutant of the family has to be a legal regular expression under the options of its site, so
    /// that it is killed by an assertion rather than by the <see cref="Regex" /> constructor. The real
    /// parser is the oracle; a test may construct a <see cref="Regex" />, the analyzer may not.
    /// </summary>
    [Test]
    public async Task CreateMutations_EveryMutant_IsAValidPattern()
    {
        var (_, mutations) = Mutate(LineAnchorSource);
        var (_, boundaries) = Mutate(WordBoundarySource);
        var (_, empty) = Mutate(OnlyCaretSource);
        var offenders = mutations
            .Concat(boundaries)
            .Concat(empty)
            .Where(mutation => !IsAcceptedByRegex(ReplacementPattern(mutation), RegexOptions.None))
            .Select(mutation => mutation.DisplayName);

        _ = await Assert.That(offenders).IsEmpty();
        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(boundaries).Count().IsEqualTo(2);
        _ = await Assert.That(empty).Count().IsEqualTo(1);
    }

    /// <summary>
    /// Under <c>RegexOptions.IgnorePatternWhitespace</c> the trailing <c>#x</c> is a comment and the blanks
    /// are insignificant, yet both are part of the pattern text and are carried over untouched: removing
    /// the <c>$</c> leaves the two blanks that surrounded it next to each other. That the operator finds
    /// the <c>$</c> as an anchor at all is what proves it sees option dependent tokens.
    /// </summary>
    [Test]
    public async Task CreateMutations_IgnorePatternWhitespace_MutatesTheAnchorsOfTheOptionDependentTokens()
    {
        var (_, mutations) = Mutate(IgnorePatternWhitespaceSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                "regex.anchor.remove-caret | pattern '^a $ #x' => 'a $ #x'"
                    + LineSeparator
                    + "regex.anchor.remove-dollar | pattern '^a $ #x' => '^a  #x'"
            );
        _ = await Assert.That(ReplacementPattern(mutations, "remove-caret")).IsEqualTo("a $ #x");
        _ = await Assert.That(ReplacementPattern(mutations, "remove-dollar")).IsEqualTo("^a  #x");
    }

    [Test]
    public async Task CreateMutations_UnsupportedSyntaxKind_ReturnsEmpty()
    {
        var (_, mutations) = Mutate(LineAnchorSource, SyntaxNodeLocator.FindFirst<ObjectCreationExpressionSyntax>);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_NodeNull_ThrowsArgumentNullException()
    {
        var (_, semanticModel, _) = CompilationFactory.CreateWithModel(LineAnchorSource);
        var mutator = new RegexAnchorMutator();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(null!, semanticModel, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("node");
    }

    [Test]
    public async Task CreateMutations_SemanticModelNull_ThrowsArgumentNullException()
    {
        var (_, _, tree) = CompilationFactory.CreateWithModel(LineAnchorSource);
        var mutator = new RegexAnchorMutator();
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(node, null!, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("semanticModel");
    }

    [Test]
    public async Task CreateMutations_CancellationRequested_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(LineAnchorSource);
        var mutator = new RegexAnchorMutator();
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        using var cancellation = new CancellationTokenSource();

        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = mutator.CreateMutations(node, semanticModel, cancellation.Token).ToArray()
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    private static (SyntaxTree Tree, Mutation[] Mutations) Mutate(string source) =>
        Mutate(source, SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>);

    private static (SyntaxTree Tree, Mutation[] Mutations) Mutate(string source, Func<SyntaxTree, SyntaxNode> select)
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var mutator = new RegexAnchorMutator();

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
