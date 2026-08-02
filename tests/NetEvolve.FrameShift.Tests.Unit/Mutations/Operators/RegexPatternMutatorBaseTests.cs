namespace NetEvolve.FrameShift.Tests.Unit.Mutations.Operators;

using System.Collections.Immutable;
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
/// Covers everything the operators of the regular expression pattern family inherit from their base
/// class, once instead of once per operator: which literals are a pattern site at all, the guard that refuses a
/// site whose options are not statically determinable, the two viability filters every offered rewrite
/// passes through, the shape of the produced mutation - operator identifier, display name, kind, location
/// and the replacement literal - and the two splice helpers a derived operator rewrites a construct with.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RegexAnchorMutator" /> is only the vehicle. It is the simplest operator of the family and is
/// used wherever a concrete operator is needed to reach the inherited behaviour; nothing asserted here is
/// a claim about anchors, and the operator's own decisions are pinned in its own test class.
/// </para>
/// <para>
/// The behaviour that no shipping operator exhibits - offering a rewrite that reproduces the pattern, one
/// that does not parse, or being asked at all when the options are unknown - is reached through a test
/// double, because the base class is abstract and its <c>Replace</c> and <c>Splice</c> helpers are
/// protected. The double returns exactly the rewrites a test hands it and records what it was asked.
/// </para>
/// </remarks>
public class RegexPatternMutatorBaseTests
{
    private const string ArgumentsPlaceholder = "ARGUMENTS";

    private const string ProbeId = "probe";

    private const string Separator = " ; ";

    /// <summary>
    /// The pattern every fixture built from <see cref="CallTemplate" /> carries, unless the argument list
    /// says otherwise. It holds two anchors, so the vehicle operator produces two mutations for it.
    /// </summary>
    private const string LineAnchoredPattern = "^a$";

    private const string ExpectedLineAnchorMutations =
        "regex.anchor.remove-caret | pattern '^a$' => 'a$'"
        + Separator
        + "regex.anchor.remove-dollar | pattern '^a$' => '^a'";

    private const string ExpectedStringAnchorMutations =
        @"regex.anchor.remove-string-start | pattern '\Aa\z' => 'a\z'"
        + Separator
        + @"regex.anchor.remove-string-end | pattern '\Aa\z' => '\Aa'";

    /// <summary>
    /// The fixture every call form is written into. The <c>RegexOptions</c> parameter is the simplest
    /// options expression the compiler cannot fold and is unused by most of the argument lists.
    /// </summary>
    private const string CallTemplate = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create(RegexOptions runtimeOptions) => new Regex(ARGUMENTS);
        }
        """;

    // The argument lists are spelled with escapes rather than as raw literals, because every one of them
    // ends with a quote and would collide with the closing delimiter of a raw literal.

    /// <summary>The overload without an options parameter, whose options are <c>None</c> by definition.</summary>
    private const string PlainArguments = "/*!*/\"^a$\"";

    private const string CompiledArguments = "/*!*/\"^a$\", RegexOptions.Compiled";

    private const string RuntimeOptionsArguments = "/*!*/\"^a$\", runtimeOptions";

    /// <summary>A verbatim literal, whose value carries the backslashes its source text spells once.</summary>
    private const string VerbatimArguments = "/*!*/@\"\\Aa\\z\"";

    /// <summary>A raw literal, which denotes the very same pattern as <see cref="VerbatimArguments" />.</summary>
    private const string RawArguments = "/*!*/\"\"\"\\Aa\\z\"\"\"";

    /// <summary>An unterminated character class, which is malformed lexically and semantically alike.</summary>
    private const string MalformedPatternArguments = "/*!*/\"[a-\"";

    /// <summary>A backreference to a group that does not exist, which tokenizes but is no legal regex.</summary>
    private const string UndefinedBackreferenceArguments = "/*!*/@\"\\1\"";

    /// <summary>A character class range in reverse order, which tokenizes but is no legal regex.</summary>
    private const string ReversedRangeArguments = "/*!*/\"[z-a]\"";

    private const string NonPatternArgumentSource = """
        namespace Fixtures;

        using System;

        internal static class Patterns
        {
            internal static void Write() => Console.WriteLine(/*!*/"^a$");
        }
        """;

    private const string LocalVariableSource = """
        namespace Fixtures;

        internal static class Patterns
        {
            internal static string Create()
            {
                var pattern = /*!*/"^a$";

                return pattern;
            }
        }
        """;

#if NET7_0_OR_GREATER
    private const string GeneratedRegexSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static partial class Patterns
        {
            [GeneratedRegex(/*!*/"^a$")]
            internal static partial Regex Pattern();
        }
        """;
#endif

#if !NETFRAMEWORK
    private const string RegularExpressionSource = """
        namespace Fixtures;

        using System.ComponentModel.DataAnnotations;

        internal sealed class Model
        {
            [RegularExpression(/*!*/"^a$")]
            internal string? Name { get; set; }
        }
        """;
#endif

    private static readonly string[] _fixtures =
    [
        CreateCallSource(PlainArguments),
        CreateCallSource(CompiledArguments),
        CreateCallSource(RuntimeOptionsArguments),
        CreateCallSource(VerbatimArguments),
        CreateCallSource(RawArguments),
        CreateCallSource(MalformedPatternArguments),
        CreateCallSource(UndefinedBackreferenceArguments),
        CreateCallSource(ReversedRangeArguments),
        NonPatternArgumentSource,
        LocalVariableSource,
#if !NETFRAMEWORK
        RegularExpressionSource,
#endif
    ];

    /// <summary>
    /// A fixture that does not compile makes every expectation built on it meaningless, above all the ones
    /// stating that a literal produces nothing. The <c>[GeneratedRegex]</c> fixture is deliberately not
    /// part of the list; see the test using it.
    /// </summary>
    [Test]
    public async Task Fixture_EveryFixture_Compiles()
    {
        var errors = _fixtures
            .SelectMany(source => CompilationFactory.GetCompileErrors(CompilationFactory.Create(source)))
            .Select(diagnostic => diagnostic.Id);

        _ = await Assert.That(errors).IsEmpty();
    }

    /// <summary>
    /// A pattern is always spelled out as a string literal, so the family claims that one syntax kind and
    /// nothing else. The kind covers the ordinary, the verbatim and the raw form alike.
    /// </summary>
    [Test]
    public async Task SupportedSyntaxKinds_FamilyOperator_IsTheStringLiteralKindOnly()
    {
        var mutator = new ProbePatternMutator(static _ => []);

        _ = await Assert.That(mutator.SupportedSyntaxKinds).IsEquivalentTo([SyntaxKind.StringLiteralExpression]);
        _ = await Assert.That(mutator.Id).IsEqualTo(ProbeId);
        _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.RegexAnchor);
    }

    [Test]
    public async Task CreateMutations_OverloadWithoutOptions_IsMutated()
    {
        var (_, mutations) = Mutate(CreateCallSource(PlainArguments));

        _ = await Assert.That(Describe(mutations)).IsEqualTo(ExpectedLineAnchorMutations);
    }

    /// <summary>
    /// Every mutation of the family replaces the whole pattern literal by an ordinary C# string literal
    /// whose value is the rewritten pattern. That is what makes the replacement a compile time constant
    /// regardless of which of the three literal forms the original used.
    /// </summary>
    [Test]
    public async Task CreateMutations_Replacement_IsAnOrdinaryStringLiteralHoldingTheRewrittenPattern()
    {
        var (_, mutations) = Mutate(CreateCallSource(PlainArguments));
        var replacement = (LiteralExpressionSyntax)mutations[0].Replacement;

        _ = await Assert.That(replacement.IsKind(SyntaxKind.StringLiteralExpression)).IsTrue();
        _ = await Assert.That(replacement.Token.ValueText).IsEqualTo("a$");
        _ = await Assert.That(replacement.ToString()).IsEqualTo("\"a$\"");
    }

    /// <summary>
    /// The mutation replaces the literal itself and is reported there, which is the position a report has
    /// to point a reader at.
    /// </summary>
    [Test]
    public async Task CreateMutations_Mutation_CarriesTheKindAndTheLocationOfThePatternLiteral()
    {
        var (tree, mutations) = Mutate(CreateCallSource(PlainArguments));
        var literal = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        _ = await Assert
            .That(mutations.Select(mutation => mutation.Kind).Distinct())
            .IsEquivalentTo([MutationKind.RegexAnchor]);
        _ = await Assert.That(mutations[0].Original.Span).IsEqualTo(literal.Span);
        _ = await Assert.That(mutations[0].Original.ToString()).IsEqualTo("\"^a$\"");
        _ = await Assert.That(mutations[0].Location.SourceSpan).IsEqualTo(literal.Span);
        _ = await Assert.That(mutations[0].Location.SourceTree).IsEqualTo(tree);
    }

    /// <summary>
    /// The pattern the operators see is the <em>value</em> of the literal, so a verbatim and a raw literal
    /// denoting the same characters are mutated identically and the display name shows the pattern as the
    /// regular expression engine receives it rather than as C# spells it.
    /// </summary>
    [Test]
    [Arguments(VerbatimArguments)]
    [Arguments(RawArguments)]
    public async Task CreateMutations_VerbatimAndRawLiteral_AreMutatedByTheirValue(string arguments)
    {
        var (_, mutations) = Mutate(CreateCallSource(arguments));
        var replacement = (LiteralExpressionSyntax)mutations[0].Replacement;

        _ = await Assert.That(Describe(mutations)).IsEqualTo(ExpectedStringAnchorMutations);
        _ = await Assert.That(replacement.Token.ValueText).IsEqualTo(@"a\z");
        _ = await Assert.That(replacement.Token.Text.StartsWith('@', StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>
    /// <c>RegexOptions.Compiled</c> only decides how the engine is built and is dropped before the pattern
    /// is validated, so a site carrying it produces its mutations like any other. That the flag is really
    /// dropped rather than tolerated cannot be observed from outside - no IL is emitted for a
    /// <see cref="System.Text.RegularExpressions.Regex" /> that is thrown away again - and the intent is
    /// documented on <c>ToParseOptions</c>.
    /// </summary>
    [Test]
    public async Task CreateMutations_CompiledOption_StillProducesTheMutations()
    {
        var (_, mutations) = Mutate(CreateCallSource(CompiledArguments));

        _ = await Assert.That(Describe(mutations)).IsEqualTo(ExpectedLineAnchorMutations);
    }

    /// <summary>
    /// The options decide the grammar of the pattern, so a site whose options are only known at run time is
    /// skipped instead of being parsed under guessed options. The operator is not even asked.
    /// </summary>
    [Test]
    public async Task CreateMutations_OptionsNotStaticallyDeterminable_ReturnsEmptyWithoutAskingTheOperator()
    {
        var mutator = new ProbePatternMutator(static pattern => [new RegexPatternRewrite(pattern + "b", "any")]);

        var (_, mutations) = Mutate(CreateCallSource(RuntimeOptionsArguments), mutator);

        _ = await Assert.That(mutations).IsEmpty();
        _ = await Assert.That(mutator.RewriteInvocations).IsEqualTo(0);
    }

    /// <summary>
    /// A string literal that is no pattern at all - an ordinary argument of an unrelated call and the
    /// initializer of a local variable - is not a site, so the whole family stays away from it.
    /// </summary>
    [Test]
    [Arguments(NonPatternArgumentSource)]
    [Arguments(LocalVariableSource)]
    public async Task CreateMutations_LiteralThatIsNoPattern_ReturnsEmpty(string source)
    {
        var (_, mutations) = Mutate(source);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// A pattern that is already broken produces no mutation, whether it fails the tokenizer or only the
    /// <see cref="System.Text.RegularExpressions.Regex" /> constructor. Which of the two checks rejects it
    /// is not observable from outside, and it does not matter: code carrying a broken pattern is broken
    /// with or without a mutant, so there is nothing a mutation of it could report about a test suite.
    /// </summary>
    [Test]
    [Arguments(MalformedPatternArguments)]
    [Arguments(UndefinedBackreferenceArguments)]
    [Arguments(ReversedRangeArguments)]
    public async Task CreateMutations_PatternThatIsNoLegalRegex_ReturnsEmpty(string arguments)
    {
        var (_, mutations) = Mutate(CreateCallSource(arguments));

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// The operator is handed the pattern the literal denotes together with tokens that tile it completely,
    /// which is what lets it splice into a token span without ever searching the pattern itself.
    /// </summary>
    [Test]
    public async Task CreateMutations_Operator_IsAskedWithThePatternAndTokensThatTileIt()
    {
        var mutator = new ProbePatternMutator(static _ => []);

        var (_, mutations) = Mutate(CreateCallSource(PlainArguments), mutator);

        _ = await Assert.That(mutations).IsEmpty();
        _ = await Assert.That(mutator.RewriteInvocations).IsEqualTo(1);
        _ = await Assert.That(mutator.ObservedPattern).IsEqualTo(LineAnchoredPattern);
        _ = await Assert.That(mutator.ObservedTokenText).IsEqualTo(LineAnchoredPattern);
    }

    /// <summary>
    /// Of the three rewrites the double offers, only one survives: the one reproducing the pattern is no
    /// mutation at all, and the one that does not parse would be killed by construction in every test that
    /// reaches it. Discarding both here is what allows an operator to offer a rewrite without proving
    /// first that it changes something and that the result is legal.
    /// </summary>
    [Test]
    public async Task CreateMutations_UnchangedAndInvalidRewrites_AreDiscarded()
    {
        var mutator = new ProbePatternMutator(static pattern =>
            [
                new RegexPatternRewrite(pattern, "unchanged"),
                new RegexPatternRewrite("[a-", "invalid"),
                new RegexPatternRewrite("a$", "good"),
            ]
        );

        var (_, mutations) = Mutate(CreateCallSource(PlainArguments), mutator);

        _ = await Assert.That(Describe(mutations)).IsEqualTo("probe.good | pattern '^a$' => 'a$'");
    }

#if NET7_0_OR_GREATER
    /// <summary>
    /// The pattern of <c>[GeneratedRegex]</c> is an attribute argument and therefore has to stay a compile
    /// time constant, which it does: the replacement is an ordinary string literal like the original. The
    /// family needs no constant context guard because of that.
    /// </summary>
    /// <remarks>
    /// The fixture uses the partial declaration a consumer writes. Without the regex source generator
    /// running there is no implementing part, so the fixture does not compile - which does not matter here,
    /// because binding the attribute argument does not depend on it. The attribute arrived with .NET 7 and
    /// a hand-written look-alike would prove nothing, the locator resolving the type by its metadata name.
    /// </remarks>
    [Test]
    public async Task CreateMutations_GeneratedRegexAttributePattern_IsMutatedIntoAStringLiteral()
    {
        var (tree, mutations) = Mutate(GeneratedRegexSource);
        var replacement = (LiteralExpressionSyntax)mutations[0].Replacement;

        _ = await Assert.That(Describe(mutations)).IsEqualTo(ExpectedLineAnchorMutations);
        _ = await Assert.That(replacement.IsKind(SyntaxKind.StringLiteralExpression)).IsTrue();
        _ = await Assert
            .That(mutations[0].ApplyTo(tree).ToString())
            .IsEqualTo(GeneratedRegexSource.Replace("\"^a$\"", "\"a$\"", StringComparison.Ordinal));
    }
#endif

#if !NETFRAMEWORK
    /// <summary>
    /// The same statement for the DataAnnotations <c>[RegularExpression]</c> attribute, whose pattern is
    /// always parsed with <c>RegexOptions.None</c>. Here the mutant is compiled as well, which is the
    /// direct proof that the replacement is still a constant expression in an attribute argument.
    /// </summary>
    /// <remarks>
    /// The attribute lives in an assembly the .NET Framework reference set of the harness does not carry,
    /// so the test is guarded rather than written against a look-alike declaration.
    /// </remarks>
    [Test]
    public async Task CreateMutations_RegularExpressionAttributePattern_IsMutatedAndTheMutantCompiles()
    {
        var (tree, mutations) = Mutate(RegularExpressionSource);
        var mutant = mutations[0].ApplyTo(tree).ToString();

        _ = await Assert.That(Describe(mutations)).IsEqualTo(ExpectedLineAnchorMutations);
        _ = await Assert.That(CompilationFactory.GetCompileErrors(CompilationFactory.Create(mutant))).IsEmpty();
    }
#endif

    [Test]
    public async Task CreateMutations_CancellationRequested_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(CreateCallSource(PlainArguments));
        var mutator = new RegexAnchorMutator();
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        using var cancellation = new CancellationTokenSource();

        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = mutator.CreateMutations(node, semanticModel, cancellation.Token).ToArray()
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    /// <summary>
    /// The rewrites are turned into mutations one by one and cancellation is observed between them, so a
    /// pattern offering many rewrites does not have to be finished once the analysis is abandoned.
    /// </summary>
    [Test]
    public async Task CreateMutations_CancellationBetweenRewrites_StopsProducingMutations()
    {
        using var cancellation = new CancellationTokenSource();
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(CreateCallSource(PlainArguments));
        var mutator = new ProbePatternMutator(_ => CancelAfterFirstRewrite(cancellation));
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = mutator.CreateMutations(node, semanticModel, cancellation.Token).ToArray()
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    /// <summary>
    /// The cache-aware overload <see cref="MutantGenerator" /> uses is not part of
    /// <see cref="IMutationOperator" />, but every operator of the family inherits it from
    /// <see cref="RegexPatternMutatorBase" />. It has to produce exactly the mutations the interface method
    /// does for the same node, and it has to reuse whatever a shared <see cref="RegexPatternCache" /> already
    /// resolved for that node instead of resolving it again.
    /// </summary>
    [Test]
    public async Task CreateMutations_WithCache_ProducesTheSameMutationsAsWithoutIt()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(CreateCallSource(PlainArguments));
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        var cache = new RegexPatternCache();

        var withoutCache = new RegexAnchorMutator().CreateMutations(node, semanticModel, CancellationToken.None);
        var withCache = new RegexAnchorMutator().CreateMutations(node, semanticModel, cache, CancellationToken.None);

        _ = await Assert.That(Describe([.. withCache])).IsEqualTo(Describe([.. withoutCache]));
    }

    /// <summary>
    /// Two operators of the family asked about the very same node through the cache-aware overload share
    /// one resolution: the site, the pattern text and therefore every mutation each of them offers stay
    /// exactly what they would be without the cache, proving the shared answer is not just reused but reused
    /// correctly.
    /// </summary>
    [Test]
    public async Task CreateMutations_WithCache_SharedBetweenTwoOperators_BothProduceTheirOwnMutations()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(CreateCallSource(PlainArguments));
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        var cache = new RegexPatternCache();

        var first = new RegexAnchorMutator().CreateMutations(node, semanticModel, cache, CancellationToken.None);
        var second = new RegexAnchorMutator().CreateMutations(node, semanticModel, cache, CancellationToken.None);

        _ = await Assert.That(Describe([.. second])).IsEqualTo(Describe([.. first]));
        _ = await Assert.That(Describe([.. first])).IsEqualTo(ExpectedLineAnchorMutations);
    }

    /// <summary>
    /// A node the cache has never seen before is resolved on demand, exactly like the interface method
    /// would, so the very first caller through the cache-aware overload is not a special case.
    /// </summary>
    [Test]
    public async Task CreateMutations_WithCache_FreshCache_StillResolvesTheNode()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(CreateCallSource(PlainArguments));
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        var mutations = new RegexAnchorMutator().CreateMutations(
            node,
            semanticModel,
            new RegexPatternCache(),
            CancellationToken.None
        );

        _ = await Assert.That(Describe([.. mutations])).IsEqualTo(ExpectedLineAnchorMutations);
    }

    /// <summary>
    /// A node of a syntax kind outside <see cref="MutationOperatorBase.SupportedSyntaxKinds" /> is
    /// rejected before the cache is ever consulted, exactly like the interface method rejects it before
    /// resolving anything.
    /// </summary>
    [Test]
    public async Task CreateMutations_WithCache_UnsupportedSyntaxKind_ReturnsEmpty()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(CreateCallSource(PlainArguments));
        var node = SyntaxNodeLocator.FindFirst<ParameterSyntax>(tree);

        var mutations = new RegexAnchorMutator().CreateMutations(
            node,
            semanticModel,
            new RegexPatternCache(),
            CancellationToken.None
        );

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_WithCache_NodeIsNull_ThrowsArgumentNullException()
    {
        var (_, semanticModel, _) = CompilationFactory.CreateWithModel(CreateCallSource(PlainArguments));

        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = new RegexAnchorMutator()
                .CreateMutations(null!, semanticModel, new RegexPatternCache(), CancellationToken.None)
                .ToArray()
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("node");
    }

    [Test]
    public async Task CreateMutations_WithCache_SemanticModelIsNull_ThrowsArgumentNullException()
    {
        var (_, _, tree) = CompilationFactory.CreateWithModel(CreateCallSource(PlainArguments));
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = new RegexAnchorMutator()
                .CreateMutations(node, null!, new RegexPatternCache(), CancellationToken.None)
                .ToArray()
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("semanticModel");
    }

    [Test]
    public async Task CreateMutations_WithCache_CacheIsNull_ThrowsArgumentNullException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(CreateCallSource(PlainArguments));
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = new RegexAnchorMutator().CreateMutations(node, semanticModel, null!, CancellationToken.None).ToArray()
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("cache");
    }

    [Test]
    public async Task CreateMutations_WithCache_CancellationRequested_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(CreateCallSource(PlainArguments));
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        using var cancellation = new CancellationTokenSource();

        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = new RegexAnchorMutator()
                .CreateMutations(node, semanticModel, new RegexPatternCache(), cancellation.Token)
                .ToArray()
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    [Test]
    public async Task Replace_TokenSpan_IsReplacedByTheText()
    {
        var anchor = new RegexToken(RegexTokenKind.Anchor, 0, "^");
        var literal = new RegexToken(RegexTokenKind.Literal, 1, "a");

        _ = await Assert
            .That(ProbePatternMutator.ReplaceToken(LineAnchoredPattern, anchor, string.Empty))
            .IsEqualTo("a$");
        _ = await Assert.That(ProbePatternMutator.ReplaceToken(LineAnchoredPattern, literal, "bc")).IsEqualTo("^bc$");
    }

    [Test]
    public async Task Replace_PatternIsNull_ThrowsArgumentNullException()
    {
        var token = new RegexToken(RegexTokenKind.Anchor, 0, "^");

        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = ProbePatternMutator.ReplaceToken(null!, token, string.Empty)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("pattern");
    }

    [Test]
    public async Task Replace_TokenIsNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = ProbePatternMutator.ReplaceToken(LineAnchoredPattern, null!, string.Empty)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("token");
    }

    [Test]
    public async Task Replace_ReplacementIsNull_ThrowsArgumentNullException()
    {
        var token = new RegexToken(RegexTokenKind.Anchor, 0, "^");

        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = ProbePatternMutator.ReplaceToken(LineAnchoredPattern, token, null!)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("replacement");
    }

    /// <summary>
    /// The three shapes of a splice: an empty replacement removes the range, an empty range inserts, and a
    /// non-empty replacement of a non-empty range substitutes.
    /// </summary>
    [Test]
    [Arguments("^a$", 0, 1, "", "a$")]
    [Arguments("^a$", 0, 3, "", "")]
    [Arguments("^a$", 1, 1, "b", "^ba$")]
    [Arguments("^a$", 3, 3, "b", "^a$b")]
    [Arguments("^a$", 0, 0, "b", "b^a$")]
    [Arguments("^a$", 1, 2, "bc", "^bc$")]
    public async Task Splice_HalfOpenRange_IsReplacedByTheText(
        string pattern,
        int start,
        int end,
        string replacement,
        string expected
    )
    {
        var spliced = ProbePatternMutator.SpliceRange(pattern, start, end, replacement);

        _ = await Assert.That(spliced).IsEqualTo(expected);
    }

    [Test]
    public async Task Splice_PatternIsNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = ProbePatternMutator.SpliceRange(null!, 0, 0, string.Empty)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("pattern");
    }

    [Test]
    public async Task Splice_ReplacementIsNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = ProbePatternMutator.SpliceRange(LineAnchoredPattern, 0, 0, null!)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("replacement");
    }

    [Test]
    [Arguments(-1, 0)]
    [Arguments(4, 4)]
    public async Task Splice_StartOutsideThePattern_ThrowsArgumentOutOfRangeException(int start, int end)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            _ = ProbePatternMutator.SpliceRange(LineAnchoredPattern, start, end, string.Empty)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("start");
        _ = await Assert.That(exception.Message).Contains("The start index must lie in the pattern.");
    }

    [Test]
    [Arguments(2, 1)]
    [Arguments(0, 4)]
    public async Task Splice_EndOutsideTheRange_ThrowsArgumentOutOfRangeException(int start, int end)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            _ = ProbePatternMutator.SpliceRange(LineAnchoredPattern, start, end, string.Empty)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("end");
        _ = await Assert.That(exception.Message).Contains("The end index must lie behind the start index.");
    }

    private static string CreateCallSource(string arguments) =>
        CallTemplate.Replace(ArgumentsPlaceholder, arguments, StringComparison.Ordinal);

    private static (SyntaxTree Tree, Mutation[] Mutations) Mutate(string source) =>
        Mutate(source, new RegexAnchorMutator());

    private static (SyntaxTree Tree, Mutation[] Mutations) Mutate(string source, RegexPatternMutatorBase mutator)
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        return (tree, [.. mutator.CreateMutations(node, semanticModel, CancellationToken.None)]);
    }

    /// <summary>
    /// Renders the produced mutations as one string, so that a failing assertion shows the whole set at
    /// once instead of the first difference of a collection comparison.
    /// </summary>
    /// <param name="mutations">The produced mutations.</param>
    /// <returns>One <c>identifier | display name</c> line per mutation, in order.</returns>
    private static string Describe(Mutation[] mutations) =>
        string.Join(Separator, mutations.Select(mutation => mutation.OperatorId + " | " + mutation.DisplayName));

    /// <summary>
    /// Offers one rewrite, then cancels, then offers a second one. The base class observes the token
    /// between two rewrites, so the second one is never turned into a mutation.
    /// </summary>
    /// <param name="cancellation">The source cancelled after the first rewrite.</param>
    /// <returns>The two rewrites, produced lazily.</returns>
    private static IEnumerable<RegexPatternRewrite> CancelAfterFirstRewrite(CancellationTokenSource cancellation)
    {
        yield return new RegexPatternRewrite("a$", "first");

        cancellation.Cancel();

        yield return new RegexPatternRewrite("^a", "second");
    }

    /// <summary>
    /// A minimal <see cref="RegexPatternMutatorBase" /> implementation that offers exactly the rewrites a
    /// test hands it, records what the base class asked it, and makes the two protected splice helpers
    /// reachable from a test.
    /// </summary>
    private sealed class ProbePatternMutator : RegexPatternMutatorBase
    {
        private readonly Func<string, IEnumerable<RegexPatternRewrite>> _rewrites;

        public ProbePatternMutator(Func<string, IEnumerable<RegexPatternRewrite>> rewrites)
            : base(ProbeId, MutationKind.RegexAnchor) => _rewrites = rewrites;

        /// <summary>
        /// Gets the number of times the base class asked for the rewrites of a pattern.
        /// </summary>
        public int RewriteInvocations { get; private set; }

        /// <summary>
        /// Gets the pattern text of the last request, which is the value of the literal.
        /// </summary>
        public string? ObservedPattern { get; private set; }

        /// <summary>
        /// Gets the concatenated text of the tokens of the last request, which reproduces the pattern
        /// exactly when the tokens tile it without a gap and without an overlap.
        /// </summary>
        public string ObservedTokenText { get; private set; } = string.Empty;

        public static string ReplaceToken(string pattern, RegexToken token, string replacement) =>
            Replace(pattern, token, replacement);

        public static string SpliceRange(string pattern, int start, int end, string replacement) =>
            Splice(pattern, start, end, replacement);

        protected override IEnumerable<RegexPatternRewrite> CreateRewrites(
            string pattern,
            ImmutableArray<RegexToken> tokens,
            CancellationToken cancellationToken
        )
        {
            RewriteInvocations++;
            ObservedPattern = pattern;
            ObservedTokenText = string.Concat(tokens.Select(token => token.Text));

            return _rewrites(pattern);
        }
    }
}
