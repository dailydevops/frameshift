namespace NetEvolve.FrameShift.Tests.Unit.Mutations.Operators;

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
/// Covers the backreference operator of the regular expression pattern family: the increase and decrease
/// rewrites for a numbered backreference, the boundary at group <c>1</c> where a decrease is never offered,
/// the discarding of an increase that would reference a group the pattern does not define, and the named
/// backreference form that is deliberately left alone entirely.
/// </summary>
/// <remarks>
/// A mutation of this family replaces the whole pattern literal, so a test that only pins the display name
/// would not notice a replacement literal whose <em>value</em> is not the pattern the name promises. The
/// tests therefore assert the operator identifier, the display name and the pattern the replacement literal
/// denotes, and for the fixture whose pattern needs a backslash, the rewritten source text as well.
/// </remarks>
public class RegexBackreferenceMutatorTests
{
    private const string OperatorIdPrefix = "regex.backreference.";

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
    /// Three capturing groups and a backreference to the middle one, so that both the increase (to
    /// <c>\3</c>) and the decrease (to <c>\1</c>) are legal patterns.
    /// </summary>
    private const string MiddleBackreferenceSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"(a)(b)(c)\\2");
        }
        """;

    /// <summary>
    /// Two capturing groups with a backreference to the first one. The decrease is never offered here,
    /// because <c>\0</c> would not reference group <c>0</c> - it is the octal escape for NUL, a different
    /// kind of construct entirely - and the operator only offers a decrease starting at <c>\2</c>. The
    /// second group exists so that the increase, to <c>\2</c>, is itself a legal pattern rather than one
    /// discarded by the base class as invalid.
    /// </summary>
    private const string OnlyGroupBackreferenceSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"(a)(b)\\1");
        }
        """;

    /// <summary>
    /// A named backreference, whose sibling group name is not available at the token-stream level this
    /// operator works at, so it is left alone entirely.
    /// </summary>
    private const string NamedBackreferenceSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"(?<year>\\d+)\\k<year>");
        }
        """;

    /// <summary>
    /// A backreference whose increase points one past the last group the pattern defines, so the increase
    /// is offered but discarded by <see cref="RegexPatternMutatorBase" /> as an invalid pattern, while the
    /// decrease survives.
    /// </summary>
    private const string LastGroupBackreferenceSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"(a)(b)\\2");
        }
        """;

    /// <summary>
    /// A backreference whose digit run overflows <see cref="int" />. The XML remarks of the operator
    /// promise that such a run "is never observed in a pattern that could ever match, but it is not
    /// rejected upstream either, so this operator yields no rewrite for it rather than throwing" - this
    /// fixture is the one no existing test constructed to actually exercise that guard.
    /// </summary>
    private const string OverflowingBackreferenceSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"(a)\\99999999999999999999");
        }
        """;

    /// <summary>
    /// The source text of the literal of <see cref="MiddleBackreferenceSource" />, meaning
    /// <c>"(a)(b)(c)\\2"</c>.
    /// </summary>
    private const string MiddleBackreferenceLiteralText = @"""(a)(b)(c)\\2""";

    /// <summary>
    /// The literal the increase of <c>\2</c> produces, meaning <c>"(a)(b)(c)\\3"</c>.
    /// </summary>
    private const string MiddleIncreasedLiteralText = @"""(a)(b)(c)\\3""";

    /// <summary>
    /// The literal the decrease of <c>\2</c> produces, meaning <c>"(a)(b)(c)\\1"</c>.
    /// </summary>
    private const string MiddleDecreasedLiteralText = @"""(a)(b)(c)\\1""";

    /// <summary>
    /// A backreference whose number is explicitly registered as a capture via <c>(?&lt;n&gt;...)</c> at
    /// <see cref="int.MaxValue" /> itself, the largest number the pattern grammar accepts at all - one more
    /// and the real regular expression engine rejects the pattern outright, per
    /// <c>"Quantifier and capture group numbers must be less than or equal to Int32.MaxValue."</c>. A
    /// backreference this large can only be defined through the explicit numbering form: no pattern could
    /// ever spell out two billion capturing parentheses. It exists to reach the exact boundary at which the
    /// increase path's <c>number + 1</c> arithmetic would overflow <see cref="int" />.
    /// </summary>
    private const string MaxInt32BackreferenceSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"(?<2147483647>a)\\2147483647");
        }
        """;

    private static readonly string[] _fixtures =
    [
        MiddleBackreferenceSource,
        OnlyGroupBackreferenceSource,
        NamedBackreferenceSource,
        LastGroupBackreferenceSource,
        MaxInt32BackreferenceSource,
        OverflowingBackreferenceSource,
    ];

    [Test]
    public async Task Metadata_Operator_ExposesIdKindAndSupportedKinds()
    {
        var mutator = new RegexBackreferenceMutator();

        _ = await Assert.That(mutator.Id).IsEqualTo("regex.backreference");
        _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.RegexBackreference);
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
        var (_, mutations) = Mutate(MiddleBackreferenceSource);
        var offenders = mutations
            .Where(mutation => !mutation.OperatorId.StartsWith(OperatorIdPrefix, StringComparison.Ordinal))
            .Select(mutation => mutation.OperatorId);

        _ = await Assert.That(offenders).IsEmpty();
        _ = await Assert
            .That(mutations.Select(mutation => mutation.Kind).Distinct())
            .IsEquivalentTo([MutationKind.RegexBackreference]);
    }

    /// <summary>
    /// A backreference to a middle group offers both rewrites: increasing to the following group and
    /// decreasing to the preceding one, because the referenced number is neither <c>1</c> nor undefined
    /// one step further out.
    /// </summary>
    [Test]
    public async Task CreateMutations_MiddleGroupBackreference_OffersBothRewrites()
    {
        var (_, mutations) = Mutate(MiddleBackreferenceSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                @"regex.backreference.increase-referenced-group | pattern '(a)(b)(c)\2' => '(a)(b)(c)\3'"
                    + LineSeparator
                    + @"regex.backreference.decrease-referenced-group | pattern '(a)(b)(c)\2' => '(a)(b)(c)\1'"
            );
        _ = await Assert.That(ReplacementPattern(mutations, "increase-referenced-group")).IsEqualTo(@"(a)(b)(c)\3");
        _ = await Assert.That(ReplacementPattern(mutations, "decrease-referenced-group")).IsEqualTo(@"(a)(b)(c)\1");
    }

    /// <summary>
    /// The mutated source, not only the display name: the replacement is an ordinary C# literal, so its
    /// escaping is part of the contract.
    /// </summary>
    [Test]
    public async Task CreateMutations_MiddleGroupBackreference_RewritesTheSourceWithEscapedBackslashes()
    {
        var (tree, mutations) = Mutate(MiddleBackreferenceSource);

        _ = await Assert
            .That(Rewrite(tree, Single(mutations, "increase-referenced-group")))
            .IsEqualTo(
                MiddleBackreferenceSource.Replace(
                    MiddleBackreferenceLiteralText,
                    MiddleIncreasedLiteralText,
                    StringComparison.Ordinal
                )
            );
        _ = await Assert
            .That(Rewrite(tree, Single(mutations, "decrease-referenced-group")))
            .IsEqualTo(
                MiddleBackreferenceSource.Replace(
                    MiddleBackreferenceLiteralText,
                    MiddleDecreasedLiteralText,
                    StringComparison.Ordinal
                )
            );
    }

    /// <summary>
    /// A backreference to the first group offers only the increase: a decrease would rewrite <c>\1</c> to
    /// <c>\0</c>, which is not a smaller reference but a different kind of construct entirely - the octal
    /// escape for NUL - so it is never offered in the first place, not merely discarded as invalid.
    /// </summary>
    [Test]
    public async Task CreateMutations_FirstGroupBackreference_OffersOnlyTheIncrease()
    {
        var (_, mutations) = Mutate(OnlyGroupBackreferenceSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(@"regex.backreference.increase-referenced-group | pattern '(a)(b)\1' => '(a)(b)\2'");
        _ = await Assert.That(mutations).Count().IsEqualTo(1);
    }

    /// <summary>
    /// A backreference to the last defined group offers the increase as a candidate rewrite, but the
    /// resulting pattern references a group the pattern does not define, so the base class discards it as
    /// invalid and only the decrease survives.
    /// </summary>
    [Test]
    public async Task CreateMutations_LastGroupBackreference_DiscardsTheInvalidIncrease()
    {
        var (_, mutations) = Mutate(LastGroupBackreferenceSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(@"regex.backreference.decrease-referenced-group | pattern '(a)(b)\2' => '(a)(b)\1'");
        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(IsAcceptedByRegex(@"(a)(b)\3", RegexOptions.None)).IsFalse();
    }

    /// <summary>
    /// A backreference number at <see cref="int.MaxValue" /> must not make the increase path's
    /// <c>number + 1</c> arithmetic wrap around to <see cref="int.MinValue" />. Before the overflow guard,
    /// the wrapped value was rendered as the replacement text <c>\-2147483648</c>, which the real regular
    /// expression engine does not reject as a malformed backreference - it reads the leading backslash as
    /// an escape of the literal <c>-</c> and the digits that follow as ordinary text, so the base class's
    /// validity filter would not have caught it either. The fixed operator instead offers no increase at
    /// all once the number cannot be incremented without overflowing, leaving only the decrease.
    /// </summary>
    [Test]
    public async Task CreateMutations_MaxInt32Backreference_OffersOnlyTheDecrease()
    {
        var (_, mutations) = Mutate(MaxInt32BackreferenceSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                @"regex.backreference.decrease-referenced-group | pattern '(?<2147483647>a)\2147483647' => '(?<2147483647>a)\2147483646'"
            );
        _ = await Assert.That(mutations).Count().IsEqualTo(1);
    }

    /// <summary>
    /// The acceptance criterion of the issue: a named backreference is left alone entirely, because its
    /// sibling group name is not available at the token-stream level this operator works at.
    /// </summary>
    [Test]
    public async Task CreateMutations_NamedBackreference_ReturnsEmpty()
    {
        var (_, mutations) = Mutate(NamedBackreferenceSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// The digit run of the backreference is long enough that parsing it as an <see cref="int" /> fails,
    /// which is exactly the documented, deliberate no-throw path described by the operator's XML remarks:
    /// no rewrite is offered, and no exception escapes the call.
    /// </summary>
    [Test]
    public async Task CreateMutations_OverflowingDigitRunBackreference_ReturnsEmptyWithoutThrowing()
    {
        Mutation[] mutations = [];

        _ = await Assert.That(() => mutations = Mutate(OverflowingBackreferenceSource).Mutations).ThrowsNothing();

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// Every surviving mutant of the family has to be a legal regular expression under the options of its
    /// site. The real parser is the strongest available oracle, and it especially matters here: it proves
    /// that the increase which points at an undefined group was actually discarded rather than merely not
    /// asserted on, by pinning the exact surviving count.
    /// </summary>
    [Test]
    public async Task CreateMutations_EveryMutant_IsAValidPattern()
    {
        var (_, middle) = Mutate(MiddleBackreferenceSource);
        var (_, onlyGroup) = Mutate(OnlyGroupBackreferenceSource);
        var (_, lastGroup) = Mutate(LastGroupBackreferenceSource);
        var offenders = middle
            .Concat(onlyGroup)
            .Concat(lastGroup)
            .Where(mutation => !IsAcceptedByRegex(ReplacementPattern(mutation), RegexOptions.None))
            .Select(mutation => mutation.DisplayName);

        _ = await Assert.That(offenders).IsEmpty();
        _ = await Assert.That(middle).Count().IsEqualTo(2);
        _ = await Assert.That(onlyGroup).Count().IsEqualTo(1);
        _ = await Assert.That(lastGroup).Count().IsEqualTo(1);
    }

    [Test]
    public async Task CreateMutations_UnsupportedSyntaxKind_ReturnsEmpty()
    {
        var (_, mutations) = Mutate(
            MiddleBackreferenceSource,
            SyntaxNodeLocator.FindFirst<ObjectCreationExpressionSyntax>
        );

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_NodeNull_ThrowsArgumentNullException()
    {
        var (_, semanticModel, _) = CompilationFactory.CreateWithModel(MiddleBackreferenceSource);
        var mutator = new RegexBackreferenceMutator();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(null!, semanticModel, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("node");
    }

    [Test]
    public async Task CreateMutations_SemanticModelNull_ThrowsArgumentNullException()
    {
        var (_, _, tree) = CompilationFactory.CreateWithModel(MiddleBackreferenceSource);
        var mutator = new RegexBackreferenceMutator();
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(node, null!, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("semanticModel");
    }

    [Test]
    public async Task CreateMutations_CancellationRequested_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(MiddleBackreferenceSource);
        var mutator = new RegexBackreferenceMutator();
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
        var mutator = new RegexBackreferenceMutator();

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
