namespace NetEvolve.FrameShift.Tests.Unit.Mutations.Operators;

using System;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Mutations;
using NetEvolve.FrameShift.Mutations.Operators;
using NetEvolve.FrameShift.Tests.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the character class operator of the regular expression pattern family: every shorthand swap
/// from one starting shorthand, the negation toggle in both directions, the independence of a nested
/// subtraction's class from the one enclosing it, range widening at both ends together with the guard
/// against producing a character that is special inside a class, member removal together with the case
/// that must never remove anything because the member is a range endpoint, the dot equivalence in both
/// directions, the source the mutated literal is rewritten to, and the standard boilerplate every
/// operator of the family shares.
/// </summary>
/// <remarks>
/// As in the anchor operator's tests, every expectation pins the operator identifier, the display name
/// and the pattern the replacement literal denotes, and the fixtures that need a backslash additionally
/// pin the rewritten source text, because the replacement is always an ordinary C# literal whichever
/// form the original used.
/// </remarks>
public class RegexCharacterClassMutatorTests
{
    private const string OperatorIdPrefix = "regex.character-class.";

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

    private const string ShorthandDigitSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"\\d");
        }
        """;

    private const string NegateOpenSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"[ab]");
        }
        """;

    private const string NegateClosedSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"[^ab]");
        }
        """;

    /// <summary>
    /// A class that subtracts a nested class, whose outer and nested opening brackets are each their own
    /// mutation point.
    /// </summary>
    private const string NestedSubtractionSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"[\\w-[\\d]]");
        }
        """;

    private const string RangeWideningSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"[b-y]");
        }
        """;

    /// <summary>
    /// A range whose lower bound would decrement into <c>^</c>, which is special inside a class and must
    /// never be produced, while the upper bound still widens normally.
    /// </summary>
    private const string RangeWideningGuardSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"[_-b]");
        }
        """;

    /// <summary>
    /// A range whose start sits at <see cref="char.MinValue" /> itself - the extreme the guard
    /// <c>startCharacter &gt; 0</c> exists to prevent underflowing past. The C# escape <c>\0</c> is resolved
    /// by the inner compilation before the tokenizer ever sees the pattern text, so the pattern is a single
    /// raw NUL character followed by <c>-b</c>, not the two-character regex escape <c>\0</c>; only a raw
    /// class member reaches <c>TryWiden</c> as a single-character range bound. The upper bound still widens
    /// normally.
    /// </summary>
    private const string RangeWideningMinBoundarySource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"[\0-b]");
        }
        """;

    /// <summary>
    /// A range whose end sits at <see cref="char.MaxValue" /> itself - the extreme the guard
    /// <c>endCharacter &lt; char.MaxValue</c> exists to prevent overflowing past. As with
    /// <see cref="RangeWideningMinBoundarySource" />, the C# escape resolves to a single raw character
    /// before the tokenizer runs, so the pattern is <c>a-</c> followed by the raw <c>U+FFFF</c> character.
    /// The lower bound still widens normally.
    /// </summary>
    private const string RangeWideningMaxBoundarySource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"[a-\uffff]");
        }
        """;

    private const string MemberRemovalSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"[abc]");
        }
        """;

    /// <summary>
    /// Both members of the class are range endpoints, so neither may be removed - only the range itself
    /// may widen.
    /// </summary>
    private const string RangeMemberNoRemovalSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"[a-z]");
        }
        """;

    private const string DotSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/".");
        }
        """;

    /// <summary>
    /// The dot equivalent spelled out as a class, as a verbatim literal so its value needs no doubled
    /// backslashes.
    /// </summary>
    private const string AnyCharacterClassSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/@"[\s\S]");
        }
        """;

    /// <summary>
    /// The source text of the literal of <see cref="ShorthandDigitSource" />, meaning <c>"\\d"</c>.
    /// </summary>
    private const string ShorthandDigitLiteralText = @"""\\d""";

    /// <summary>
    /// The source text of the literal of <see cref="DotSource" />, meaning <c>"."</c>.
    /// </summary>
    private const string DotLiteralText = @""".""";

    /// <summary>
    /// The literal the expansion of <c>.</c> produces, meaning <c>"[\\s\\S]"</c>.
    /// </summary>
    private const string AnyCharacterClassLiteralText = @"""[\\s\\S]""";

    private static readonly string[] _fixtures =
    [
        ShorthandDigitSource,
        NegateOpenSource,
        NegateClosedSource,
        NestedSubtractionSource,
        RangeWideningSource,
        RangeWideningGuardSource,
        RangeWideningMinBoundarySource,
        RangeWideningMaxBoundarySource,
        MemberRemovalSource,
        RangeMemberNoRemovalSource,
        DotSource,
        AnyCharacterClassSource,
    ];

    [Test]
    public async Task Metadata_Operator_ExposesIdKindAndSupportedKinds()
    {
        var mutator = new RegexCharacterClassMutator();

        _ = await Assert.That(mutator.Id).IsEqualTo("regex.character-class");
        _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.RegexCharacterClass);
        _ = await Assert
            .That(mutator.SupportedSyntaxKinds)
            .IsEquivalentTo([SyntaxKind.StringLiteralExpression]);
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
        var (_, mutations) = Mutate(ShorthandDigitSource);
        var offenders = mutations
            .Where(mutation => !mutation.OperatorId.StartsWith(OperatorIdPrefix, StringComparison.Ordinal))
            .Select(mutation => mutation.OperatorId);

        _ = await Assert.That(offenders).IsEmpty();
        _ = await Assert
            .That(mutations.Select(mutation => mutation.Kind).Distinct())
            .IsEquivalentTo([MutationKind.RegexCharacterClass]);
    }

    /// <summary>
    /// Every one of the five swaps <c>\d</c> offers, in the fixed shorthand order, whether the occurrence
    /// sits bare in the pattern or not.
    /// </summary>
    [Test]
    public async Task CreateMutations_ShorthandDigit_SwapsToEachOfTheOtherFive()
    {
        var (_, mutations) = Mutate(ShorthandDigitSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                string.Join(
                    LineSeparator,
                    @"regex.character-class.digit-to-non-digit | pattern '\d' => '\D'",
                    @"regex.character-class.digit-to-word | pattern '\d' => '\w'",
                    @"regex.character-class.digit-to-non-word | pattern '\d' => '\W'",
                    @"regex.character-class.digit-to-space | pattern '\d' => '\s'",
                    @"regex.character-class.digit-to-non-space | pattern '\d' => '\S'"
                )
            );
        _ = await Assert.That(ReplacementPattern(mutations, "digit-to-non-digit")).IsEqualTo(@"\D");
        _ = await Assert.That(ReplacementPattern(mutations, "digit-to-word")).IsEqualTo(@"\w");
        _ = await Assert.That(ReplacementPattern(mutations, "digit-to-non-word")).IsEqualTo(@"\W");
        _ = await Assert.That(ReplacementPattern(mutations, "digit-to-space")).IsEqualTo(@"\s");
        _ = await Assert.That(ReplacementPattern(mutations, "digit-to-non-space")).IsEqualTo(@"\S");
    }

    /// <summary>
    /// The source rewrite of a shorthand swap, which is the case a display name alone cannot pin because
    /// the pattern needs a backslash either way.
    /// </summary>
    [Test]
    public async Task CreateMutations_ShorthandDigit_RewritesTheSourceWithEscapedBackslashes()
    {
        var (tree, mutations) = Mutate(ShorthandDigitSource);

        _ = await Assert
            .That(Rewrite(tree, Single(mutations, "digit-to-word")))
            .IsEqualTo(ShorthandDigitSource.Replace(ShorthandDigitLiteralText, @"""\\w""", StringComparison.Ordinal));
    }

    /// <summary>
    /// A class open token of the plain <c>[</c> form is negated, and every standalone member of the same
    /// class is offered as a removal of its own.
    /// </summary>
    [Test]
    public async Task CreateMutations_PlainClass_NegatesAndRemovesEachMember()
    {
        var (_, mutations) = Mutate(NegateOpenSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                string.Join(
                    LineSeparator,
                    "regex.character-class.negate-class | pattern '[ab]' => '[^ab]'",
                    "regex.character-class.remove-member-at-1 | pattern '[ab]' => '[b]'",
                    "regex.character-class.remove-member-at-2 | pattern '[ab]' => '[a]'"
                )
            );
    }

    /// <summary>
    /// A class open token of the negated <c>[^</c> form is un-negated, the opposite direction of the same
    /// toggle.
    /// </summary>
    [Test]
    public async Task CreateMutations_NegatedClass_UnNegatesAndRemovesEachMember()
    {
        var (_, mutations) = Mutate(NegateClosedSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                string.Join(
                    LineSeparator,
                    "regex.character-class.un-negate-class | pattern '[^ab]' => '[ab]'",
                    "regex.character-class.remove-member-at-2 | pattern '[^ab]' => '[^b]'",
                    "regex.character-class.remove-member-at-3 | pattern '[^ab]' => '[^a]'"
                )
            );
    }

    /// <summary>
    /// A subtraction's outer class and its nested class are each their own mutation point: negating the
    /// outer bracket leaves the nested class untouched and vice versa.
    /// </summary>
    [Test]
    public async Task CreateMutations_NestedSubtraction_NegatesOuterAndNestedClassesIndependently()
    {
        var (_, mutations) = Mutate(NestedSubtractionSource);
        var toggles = Filter(mutations, "negate-class");

        _ = await Assert.That(mutations).Count().IsEqualTo(12);
        _ = await Assert.That(toggles).Count().IsEqualTo(2);
        _ = await Assert.That(ReplacementPattern(toggles[0])).IsEqualTo(@"[^\w-[\d]]");
        _ = await Assert.That(ReplacementPattern(toggles[1])).IsEqualTo(@"[\w-[^\d]]");
    }

    /// <summary>
    /// Both bounds of a plain range widen towards each other by exactly one character, in addition to the
    /// negation the class open token always offers.
    /// </summary>
    [Test]
    public async Task CreateMutations_PlainRange_WidensBothBounds()
    {
        var (_, mutations) = Mutate(RangeWideningSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                string.Join(
                    LineSeparator,
                    "regex.character-class.negate-class | pattern '[b-y]' => '[^b-y]'",
                    "regex.character-class.widen-range-start-at-1 | pattern '[b-y]' => '[a-y]'",
                    "regex.character-class.widen-range-end-at-3 | pattern '[b-y]' => '[b-z]'"
                )
            );
    }

    /// <summary>
    /// The lower bound of <c>[_-b]</c> would decrement into <c>^</c>, one of the four characters that are
    /// special inside a class, so it must never be produced; the upper bound still widens normally.
    /// </summary>
    [Test]
    public async Task CreateMutations_RangeWideningIntoASpecialCharacter_IsNeverProduced()
    {
        var (_, mutations) = Mutate(RangeWideningGuardSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                string.Join(
                    LineSeparator,
                    "regex.character-class.negate-class | pattern '[_-b]' => '[^_-b]'",
                    "regex.character-class.widen-range-end-at-3 | pattern '[_-b]' => '[_-c]'"
                )
            );
        _ = await Assert
            .That(
                mutations.Any(mutation =>
                    string.Equals(
                        mutation.OperatorId,
                        OperatorIdPrefix + "widen-range-start-at-1",
                        StringComparison.Ordinal
                    )
                )
            )
            .IsFalse();
    }

    /// <summary>
    /// The start of <c>[\0-b]</c> already sits at <see cref="char.MinValue" />, so the guard
    /// <c>startCharacter &gt; 0</c> must skip the mutation rather than let <c>TryWiden</c> underflow past
    /// it; the upper bound still widens normally.
    /// </summary>
    [Test]
    public async Task CreateMutations_RangeStartAtMinValue_SkipsStartWidening()
    {
        var (_, mutations) = Mutate(RangeWideningMinBoundarySource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                string.Join(
                    LineSeparator,
                    "regex.character-class.negate-class | pattern '[\0-b]' => '[^\0-b]'",
                    "regex.character-class.widen-range-end-at-3 | pattern '[\0-b]' => '[\0-c]'"
                )
            );
        _ = await Assert
            .That(
                mutations.Any(mutation =>
                    string.Equals(
                        mutation.OperatorId,
                        OperatorIdPrefix + "widen-range-start-at-1",
                        StringComparison.Ordinal
                    )
                )
            )
            .IsFalse();
    }

    /// <summary>
    /// The end of <c>[a-\uffff]</c> already sits at <see cref="char.MaxValue" />, so the guard
    /// <c>endCharacter &lt; char.MaxValue</c> must skip the mutation rather than let <c>TryWiden</c>
    /// overflow past it; the lower bound still widens normally.
    /// </summary>
    [Test]
    public async Task CreateMutations_RangeEndAtMaxValue_SkipsEndWidening()
    {
        var (_, mutations) = Mutate(RangeWideningMaxBoundarySource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                string.Join(
                    LineSeparator,
                    "regex.character-class.negate-class | pattern '[a-\uffff]' => '[^a-\uffff]'",
                    "regex.character-class.widen-range-start-at-1 | pattern '[a-\uffff]' => '[`-\uffff]'"
                )
            );
        _ = await Assert
            .That(
                mutations.Any(mutation =>
                    string.Equals(
                        mutation.OperatorId,
                        OperatorIdPrefix + "widen-range-end-at-3",
                        StringComparison.Ordinal
                    )
                )
            )
            .IsFalse();
    }

    /// <summary>
    /// Every standalone member of <c>[abc]</c> is offered as a removal of its own, one rewrite per member,
    /// each touching only its own span.
    /// </summary>
    [Test]
    public async Task CreateMutations_StandaloneMembers_AreEachRemovedOnTheirOwn()
    {
        var (_, mutations) = Mutate(MemberRemovalSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                string.Join(
                    LineSeparator,
                    "regex.character-class.negate-class | pattern '[abc]' => '[^abc]'",
                    "regex.character-class.remove-member-at-1 | pattern '[abc]' => '[bc]'",
                    "regex.character-class.remove-member-at-2 | pattern '[abc]' => '[ac]'",
                    "regex.character-class.remove-member-at-3 | pattern '[abc]' => '[ab]'"
                )
            );
    }

    /// <summary>
    /// Neither end of <c>[a-z]</c> is removed, because both are range endpoints rather than standalone
    /// members; only the range itself widens.
    /// </summary>
    [Test]
    public async Task CreateMutations_RangeEndpoints_AreNeverRemoved()
    {
        var (_, mutations) = Mutate(RangeMemberNoRemovalSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                string.Join(
                    LineSeparator,
                    "regex.character-class.negate-class | pattern '[a-z]' => '[^a-z]'",
                    "regex.character-class.widen-range-start-at-1 | pattern '[a-z]' => '[`-z]'",
                    "regex.character-class.widen-range-end-at-3 | pattern '[a-z]' => '[a-{]'"
                )
            );
    }

    /// <summary>
    /// A literal <c>.</c> expands to <c>[\s\S]</c>, the strongest form of the equivalence: it holds
    /// regardless of <c>RegexOptions.Singleline</c>, which changes only what <c>.</c> itself matches.
    /// </summary>
    [Test]
    public async Task CreateMutations_Dot_ExpandsToAnyCharacterClass()
    {
        var (tree, mutations) = Mutate(DotSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(@"regex.character-class.dot-to-any-character-class | pattern '.' => '[\s\S]'");
        _ = await Assert
            .That(Rewrite(tree, Single(mutations, "dot-to-any-character-class")))
            .IsEqualTo(DotSource.Replace(DotLiteralText, AnyCharacterClassLiteralText, StringComparison.Ordinal));
    }

    /// <summary>
    /// The exact four token run <c>[\s\S]</c> collapses back to <c>.</c>, proving the two directions are
    /// each other's mutation.
    /// </summary>
    [Test]
    public async Task CreateMutations_AnyCharacterClass_CollapsesToDot()
    {
        var (_, mutations) = Mutate(AnyCharacterClassSource);
        var collapse = Single(mutations, "any-character-class-to-dot");

        _ = await Assert.That(ReplacementPattern(collapse)).IsEqualTo(".");
    }

    /// <summary>
    /// Every mutant of the family has to be a legal regular expression under the options of its site, so
    /// that it is killed by an assertion rather than by the <see cref="Regex" /> constructor. The real
    /// parser is the oracle; a test may construct a <see cref="Regex" />, the analyzer may not.
    /// </summary>
    [Test]
    public async Task CreateMutations_EveryMutant_IsAValidPattern()
    {
        var (_, digit) = Mutate(ShorthandDigitSource);
        var (_, negateOpen) = Mutate(NegateOpenSource);
        var (_, negateClosed) = Mutate(NegateClosedSource);
        var (_, nested) = Mutate(NestedSubtractionSource);
        var (_, range) = Mutate(RangeWideningSource);
        var (_, guard) = Mutate(RangeWideningGuardSource);
        var (_, minBoundary) = Mutate(RangeWideningMinBoundarySource);
        var (_, maxBoundary) = Mutate(RangeWideningMaxBoundarySource);
        var (_, removal) = Mutate(MemberRemovalSource);
        var (_, noRemoval) = Mutate(RangeMemberNoRemovalSource);
        var (_, dot) = Mutate(DotSource);
        var (_, anyClass) = Mutate(AnyCharacterClassSource);

        var all = digit
            .Concat(negateOpen)
            .Concat(negateClosed)
            .Concat(nested)
            .Concat(range)
            .Concat(guard)
            .Concat(minBoundary)
            .Concat(maxBoundary)
            .Concat(removal)
            .Concat(noRemoval)
            .Concat(dot)
            .Concat(anyClass)
            .ToArray();
        var offenders = all.Where(mutation => !IsAcceptedByRegex(ReplacementPattern(mutation), RegexOptions.None))
            .Select(mutation => mutation.DisplayName);

        _ = await Assert.That(offenders).IsEmpty();
        _ = await Assert.That(all).Count().IsEqualTo(52);
    }

    [Test]
    public async Task CreateMutations_UnsupportedSyntaxKind_ReturnsEmpty()
    {
        var (_, mutations) = Mutate(ShorthandDigitSource, SyntaxNodeLocator.FindFirst<ObjectCreationExpressionSyntax>);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_NodeNull_ThrowsArgumentNullException()
    {
        var (_, semanticModel, _) = CompilationFactory.CreateWithModel(ShorthandDigitSource);
        var mutator = new RegexCharacterClassMutator();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(null!, semanticModel, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("node");
    }

    [Test]
    public async Task CreateMutations_SemanticModelNull_ThrowsArgumentNullException()
    {
        var (_, _, tree) = CompilationFactory.CreateWithModel(ShorthandDigitSource);
        var mutator = new RegexCharacterClassMutator();
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(node, null!, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("semanticModel");
    }

    [Test]
    public async Task CreateMutations_CancellationRequested_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(ShorthandDigitSource);
        var mutator = new RegexCharacterClassMutator();
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
        var mutator = new RegexCharacterClassMutator();

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

    /// <summary>
    /// Selects every mutation carrying the given suffix, in the order the operator produced them, for the
    /// cases where the same suffix is offered at more than one position of the pattern.
    /// </summary>
    /// <param name="mutations">The produced mutations.</param>
    /// <param name="suffix">The operator suffix to filter by.</param>
    /// <returns>The matching mutations, possibly more than one.</returns>
    private static Mutation[] Filter(Mutation[] mutations, string suffix) =>
        [
            .. mutations.Where(mutation =>
                string.Equals(mutation.OperatorId, OperatorIdPrefix + suffix, StringComparison.Ordinal)
            ),
        ];

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
