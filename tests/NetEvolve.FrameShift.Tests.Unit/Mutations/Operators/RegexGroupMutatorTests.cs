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
/// Covers the <c>regex.group</c> operator: the exact set of produced mutations - identifier, display name
/// and order - for a literal holding all four opening forms the operator answers for; the opening forms it
/// deliberately leaves alone; a parenthesis inside a character class, which opens no group at all; the
/// rewrites that would leave a numbered or a named reference without its group and are therefore discarded
/// by the base class; and the rewritten source, which changes the opening only.
/// </summary>
public class RegexGroupMutatorTests
{
    private const string OperatorIdPrefix = "regex.group.";

    /// <summary>
    /// All four openings the operator answers for in one literal: the plain capturing group, the
    /// non-capturing group and both named forms. None of them is referenced anywhere in the pattern, so no
    /// rewrite can orphan a reference and every candidate survives.
    /// </summary>
    private const string MixedFormsSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"(a)(?:b)(?<y>c)(?'z'd)");
        }
        """;

    /// <summary>
    /// Inside a character class a <c>(</c> is an ordinary member, so <c>[()]</c> holds no group opening.
    /// </summary>
    private const string CharacterClassSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"[()]");
        }
        """;

    /// <summary>
    /// The fixture the data driven tests build their source from. The literal is verbatim, so that a pattern
    /// carrying a backslash - a backreference, for instance - needs no escaping of its own.
    /// </summary>
    private const string PatternTemplate = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/@"PATTERN");
        }
        """;

    /// <summary>
    /// The placeholder <see cref="PatternTemplate" /> carries the pattern in.
    /// </summary>
    private const string PatternPlaceholder = "PATTERN";

    private static readonly string[] _fixtures = [MixedFormsSource, CharacterClassSource];

    /// <summary>
    /// A generous timeout for the <see cref="Regex" /> instances a test constructs only to learn whether a
    /// mutated pattern is legal. None of them is ever matched against an input.
    /// </summary>
    private static readonly TimeSpan _parseTimeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task Metadata_Operator_ExposesIdKindAndSupportedKinds()
    {
        var mutator = new RegexGroupMutator();

        _ = await Assert.That(mutator.Id).IsEqualTo("regex.group");
        _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.RegexGroup);
        _ = await Assert
            .That(mutator.SupportedSyntaxKinds)
            .IsEquivalentTo(new[] { SyntaxKind.StringLiteralExpression });
    }

    /// <summary>
    /// A fixture that does not compile makes every expectation built on it meaningless, so all of them are
    /// bound once. The fixtures the data driven tests generate are bound by those tests themselves.
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
        var (_, mutations) = Mutate(MixedFormsSource);
        var offenders = mutations
            .Where(mutation => !mutation.OperatorId.StartsWith(OperatorIdPrefix, StringComparison.Ordinal))
            .Select(mutation => mutation.OperatorId);

        _ = await Assert.That(offenders).IsEmpty();
        _ = await Assert
            .That(mutations.Select(mutation => mutation.Kind).Distinct())
            .IsEquivalentTo(new[] { MutationKind.RegexGroup });
    }

    /// <summary>
    /// Every opening is answered for exactly once, in pattern order: the plain <c>(</c> and both named forms
    /// lose their capture, and <c>(?:</c> gains one. Only two identifiers exist for the whole family, so the
    /// three mutations that take a capture away share theirs - the display name is what tells them apart,
    /// and it always shows the whole pattern before and after the rewrite.
    /// </summary>
    [Test]
    public async Task CreateMutations_AllFourOpeningForms_ProducesTheExactSet()
    {
        var (_, mutations) = Mutate(MixedFormsSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                """
                regex.group.capturing-to-non-capturing | pattern '(a)(?:b)(?<y>c)(?'z'd)' => '(?:a)(?:b)(?<y>c)(?'z'd)'
                regex.group.non-capturing-to-capturing | pattern '(a)(?:b)(?<y>c)(?'z'd)' => '(a)(b)(?<y>c)(?'z'd)'
                regex.group.capturing-to-non-capturing | pattern '(a)(?:b)(?<y>c)(?'z'd)' => '(a)(?:b)(?:c)(?'z'd)'
                regex.group.capturing-to-non-capturing | pattern '(a)(?:b)(?<y>c)(?'z'd)' => '(a)(?:b)(?<y>c)(?:d)'
                """
            );
    }

    /// <summary>
    /// A mutant that the <see cref="Regex" /> constructor rejects would be killed by construction rather
    /// than by an assertion, so every produced pattern is handed to the real parser.
    /// </summary>
    [Test]
    public async Task CreateMutations_EveryMutant_IsAValidPattern()
    {
        var (_, mutations) = Mutate(MixedFormsSource);
        var offenders = mutations.Select(MutatedPattern).Where(pattern => !IsAcceptedByRegex(pattern));

        _ = await Assert.That(mutations).Count().IsEqualTo(4);
        _ = await Assert.That(offenders).IsEmpty();
    }

    /// <summary>
    /// Only the opening of a group is rewritten: the <c>)</c> that closes a non-capturing group is the same
    /// character that closes a capturing one, so the first mutation of the fixture turns <c>(a)</c> into
    /// <c>(?:a)</c> and leaves every other character of the pattern - the closing parenthesis included -
    /// exactly where it was. The whole literal is replaced by an ordinary C# string literal, and everything
    /// around it, the marker comment included, stays untouched.
    /// </summary>
    [Test]
    public async Task CreateMutations_AllFourOpeningForms_RewritesTheOpeningOnly()
    {
        var (tree, mutations) = Mutate(MixedFormsSource);
        var mutated = Rewrite(tree, mutations[0]);

        _ = await Assert
            .That(mutated)
            .IsEqualTo(
                MixedFormsSource.Replace(
                    "\"(a)(?:b)(?<y>c)(?'z'd)\"",
                    "\"(?:a)(?:b)(?<y>c)(?'z'd)\"",
                    StringComparison.Ordinal
                )
            );
        _ = await Assert.That(CompilationFactory.GetCompileErrors(CompilationFactory.Create(mutated))).IsEmpty();
    }

    /// <summary>
    /// Inside a character class a parenthesis is an ordinary member and opens nothing, which the tokenizer
    /// already says by handing it over as class content rather than as a group opening. The operator
    /// therefore needs no look at what surrounds a token and produces nothing here.
    /// </summary>
    [Test]
    public async Task CreateMutations_ParenthesisInsideACharacterClass_ReturnsEmpty()
    {
        var (_, mutations) = Mutate(CharacterClassSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// Every opening form outside the four the operator answers for is left alone, each for a reason of its
    /// own: the atomic group changes backtracking instead of capturing, the scoped inline options belong to
    /// the <c>RegexOptions</c> family, and the conditional - whose opening token is exactly <c>(?</c>,
    /// because the parenthesis of its condition is shared with the construct - captures nothing that could
    /// be taken away. The four lookarounds never even arrive as a group opening.
    /// </summary>
    /// <remarks>
    /// The two balancing forms are named without a group to pop from, so they are refused one step earlier:
    /// the pattern is not a legal regular expression at all and the base class skips the site. That the
    /// operator refuses a balancing opening on its own - by the <c>-</c> inside its name - is pinned by
    /// <see cref="CreateMutations_RewriteWithoutItsReference_IsDiscarded(string)" />, whose fixture defines
    /// the popped group.
    /// </remarks>
    /// <param name="pattern">The pattern of the generated fixture.</param>
    [Test]
    [Arguments("(?>a)")]
    [Arguments("(?i:b)")]
    [Arguments("(?=c)")]
    [Arguments("(?!d)")]
    [Arguments("(?<=c)")]
    [Arguments("(?<!d)")]
    [Arguments("(?(?=a)b|c)")]
    [Arguments("(?<c-o>x)")]
    [Arguments("(?<-o>x)")]
    public async Task CreateMutations_OpeningFormTheOperatorLeavesAlone_ReturnsEmpty(string pattern)
    {
        var (compilation, mutations) = MutatePattern(pattern);

        _ = await Assert.That(CompilationFactory.GetCompileErrors(compilation)).IsEmpty();
        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// Renumbering is not tracked, so a rewrite may leave a reference without the group it points at - and
    /// the base class then discards it, because such a mutant would throw in every test that reaches it
    /// instead of failing an assertion. Each of the three patterns is legal and offers exactly one
    /// candidate, and each candidate is dropped, so nothing at all is produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In <c>(?&lt;y&gt;a)\k&lt;y&gt;</c> the only opening is a named capture; turning it into <c>(?:</c>
    /// leaves <c>\k&lt;y&gt;</c> undefined.
    /// </para>
    /// <para>
    /// In <c>(?&lt;o&gt;a)(?&lt;c-o&gt;x)</c> the balancing opening carries a <c>-</c> and is skipped by the
    /// operator, so the named capture is the only candidate - and it is the group the balancing construct
    /// pops from, which the rewrite would take away.
    /// </para>
    /// <para>
    /// In <c>(?&lt;c-o&gt;x)(a)\1</c> the balancing opening is skipped as well and the plain <c>(</c> is the
    /// only candidate; turning it into <c>(?:</c> leaves <c>\1</c> without a first group. That the pattern
    /// pops from a group it never defines does not change the outcome either, because the base class then
    /// refuses the site outright.
    /// </para>
    /// </remarks>
    /// <param name="pattern">The pattern of the generated fixture.</param>
    [Test]
    [Arguments(@"(?<y>a)\k<y>")]
    [Arguments(@"(?<o>a)(?<c-o>x)")]
    [Arguments(@"(?<c-o>x)(a)\1")]
    public async Task CreateMutations_RewriteWithoutItsReference_IsDiscarded(string pattern)
    {
        var (compilation, mutations) = MutatePattern(pattern);

        _ = await Assert.That(CompilationFactory.GetCompileErrors(compilation)).IsEmpty();
        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_CancellationRequested_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(MixedFormsSource);
        var mutator = new RegexGroupMutator();
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        using var cancellation = new CancellationTokenSource();

        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = mutator.CreateMutations(node, semanticModel, cancellation.Token).ToArray()
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    private static (SyntaxTree Tree, Mutation[] Mutations) Mutate(string source)
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        var mutator = new RegexGroupMutator();

        return (tree, [.. mutator.CreateMutations(node, semanticModel, CancellationToken.None)]);
    }

    /// <summary>
    /// Mutates the pattern of a generated fixture and hands the compilation back as well, so that a data
    /// driven test can bind its own fixture before trusting its expectation.
    /// </summary>
    /// <param name="pattern">The pattern the fixture carries.</param>
    /// <returns>The compilation of the fixture and the produced mutations.</returns>
    private static (CSharpCompilation Compilation, Mutation[] Mutations) MutatePattern(string pattern)
    {
        var source = PatternTemplate.Replace(PatternPlaceholder, pattern, StringComparison.Ordinal);
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        var mutator = new RegexGroupMutator();

        return (compilation, [.. mutator.CreateMutations(node, semanticModel, CancellationToken.None)]);
    }

    private static string Rewrite(SyntaxTree tree, Mutation mutation) => mutation.ApplyTo(tree).ToString();

    /// <summary>
    /// Renders the produced mutations as one string of <c>id | display name</c> lines, so that a failing
    /// expectation shows the whole set at once instead of the first difference only.
    /// </summary>
    /// <param name="mutations">The produced mutations, in order.</param>
    /// <returns>The joined lines.</returns>
    private static string Lines(Mutation[] mutations) =>
        string.Join("\n", mutations.Select(mutation => mutation.OperatorId + " | " + mutation.DisplayName));

    /// <summary>
    /// Reads the rewritten pattern out of the replacement literal, whose value is the whole pattern the
    /// mutant would carry.
    /// </summary>
    /// <param name="mutation">The mutation to read.</param>
    /// <returns>The rewritten pattern.</returns>
    private static string MutatedPattern(Mutation mutation) =>
        ((LiteralExpressionSyntax)mutation.Replacement).Token.ValueText;

    /// <summary>
    /// Asks the real parser whether a pattern is legal.
    /// </summary>
    /// <param name="pattern">The pattern to compile.</param>
    /// <returns><see langword="true" /> if <see cref="Regex" /> accepts the pattern.</returns>
    private static bool IsAcceptedByRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, _parseTimeout);

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
