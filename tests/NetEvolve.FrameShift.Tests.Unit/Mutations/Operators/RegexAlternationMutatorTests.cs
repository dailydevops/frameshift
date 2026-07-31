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
/// Covers the <c>regex.alternation</c> operator: the exact set of produced mutations - identifier, display
/// name and order - for a two branch, a three branch and a prefix alternation, for a pattern that
/// alternates in two scopes, and for an alternation with an empty branch; the constructs that are not an
/// alternation at all and therefore produce nothing; the scoping of the conditional exclusion; and the
/// rewritten source.
/// </summary>
public class RegexAlternationMutatorTests
{
    private const string OperatorIdPrefix = "regex.alternation.";

    private const string TwoBranchSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"a|b");
        }
        """;

    private const string ThreeBranchSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"a|b|c");
        }
        """;

    /// <summary>
    /// The textbook case of the swap mutation: .NET's alternation is leftmost-first, so <c>(a|ab)</c>
    /// matching <c>ab</c> captures <c>a</c> while the swapped pattern captures <c>ab</c>.
    /// </summary>
    private const string PrefixBranchSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"(a|ab)c");
        }
        """;

    /// <summary>
    /// Two independent alternation scopes inside one literal, which is what makes the position part of the
    /// identifier suffix necessary.
    /// </summary>
    private const string TwoScopeSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"(a|b)(c|d)");
        }
        """;

    /// <summary>
    /// An alternation whose second branch is empty, which is legal and needs no special handling.
    /// </summary>
    private const string EmptyBranchSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"a|");
        }
        """;

    /// <summary>
    /// A <c>|</c> inside a character class is an ordinary member of the class rather than an alternation.
    /// </summary>
    private const string CharacterClassSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"[a|b]");
        }
        """;

    /// <summary>
    /// A conditional <c>(?(...)yes|no)</c>, whose bar separates the yes part from the no part rather than
    /// two alternatives of one alternation.
    /// </summary>
    private const string ConditionalSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"(?(?=a)b|c)");
        }
        """;

    /// <summary>
    /// A real alternation nested inside the yes part of a conditional, which proves the exclusion covers the
    /// conditional itself and not everything below it.
    /// </summary>
    private const string ConditionalWithNestedAlternationSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"(?(?=a)(b|c)|d)");
        }
        """;

    /// <summary>
    /// An alternation inside a lookahead. A lookaround opens a scope of its own, so its branches are
    /// mutated like the branches of any other group.
    /// </summary>
    private const string LookaroundSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"(?=a|b)c");
        }
        """;

    private static readonly string[] _fixtures =
    [
        TwoBranchSource,
        ThreeBranchSource,
        PrefixBranchSource,
        TwoScopeSource,
        EmptyBranchSource,
        CharacterClassSource,
        ConditionalSource,
        ConditionalWithNestedAlternationSource,
        LookaroundSource,
    ];

    /// <summary>
    /// A generous timeout for the <see cref="Regex" /> instances a test constructs only to learn whether a
    /// mutated pattern is legal. None of them is ever matched against an input.
    /// </summary>
    private static readonly TimeSpan _parseTimeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task Metadata_Operator_ExposesIdKindAndSupportedKinds()
    {
        var mutator = new RegexAlternationMutator();

        _ = await Assert.That(mutator.Id).IsEqualTo("regex.alternation");
        _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.RegexAlternation);
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
        var (_, mutations) = Mutate(TwoBranchSource);
        var offenders = mutations
            .Where(mutation => !mutation.OperatorId.StartsWith(OperatorIdPrefix, StringComparison.Ordinal))
            .Select(mutation => mutation.OperatorId);

        _ = await Assert.That(offenders).IsEmpty();
        _ = await Assert
            .That(mutations.Select(mutation => mutation.Kind).Distinct())
            .IsEquivalentTo(new[] { MutationKind.RegexAlternation });
    }

    /// <summary>
    /// Two branches produce two removals and one swap, the removals first and both groups in ascending
    /// branch index.
    /// </summary>
    [Test]
    public async Task CreateMutations_TwoBranches_RemovesEachBranchAndSwapsThePair()
    {
        var (_, mutations) = Mutate(TwoBranchSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                """
                regex.alternation.remove-branch-1-at-0 | pattern 'a|b' => 'b'
                regex.alternation.remove-branch-2-at-0 | pattern 'a|b' => 'a'
                regex.alternation.swap-branches-1-2-at-0 | pattern 'a|b' => 'b|a'
                """
            );
    }

    /// <summary>
    /// Three branches produce three removals and two swaps: only <em>adjacent</em> pairs are swapped, so
    /// there is no mutation exchanging the first and the third branch.
    /// </summary>
    [Test]
    public async Task CreateMutations_ThreeBranches_SwapsOnlyAdjacentPairs()
    {
        var (_, mutations) = Mutate(ThreeBranchSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                """
                regex.alternation.remove-branch-1-at-0 | pattern 'a|b|c' => 'b|c'
                regex.alternation.remove-branch-2-at-0 | pattern 'a|b|c' => 'a|c'
                regex.alternation.remove-branch-3-at-0 | pattern 'a|b|c' => 'a|b'
                regex.alternation.swap-branches-1-2-at-0 | pattern 'a|b|c' => 'b|a|c'
                regex.alternation.swap-branches-2-3-at-0 | pattern 'a|b|c' => 'a|c|b'
                """
            );
    }

    /// <summary>
    /// The alternation of a group starts behind the opening parenthesis, which is why the suffix names
    /// index one rather than index zero, and the parentheses themselves are never touched.
    /// </summary>
    [Test]
    public async Task CreateMutations_AlternationInsideAGroup_MutatesTheGroupContentOnly()
    {
        var (_, mutations) = Mutate(PrefixBranchSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                """
                regex.alternation.remove-branch-1-at-1 | pattern '(a|ab)c' => '(ab)c'
                regex.alternation.remove-branch-2-at-1 | pattern '(a|ab)c' => '(a)c'
                regex.alternation.swap-branches-1-2-at-1 | pattern '(a|ab)c' => '(ab|a)c'
                """
            );
    }

    /// <summary>
    /// Both scopes of <c>(a|b)(c|d)</c> hold two branches, so the branch indices alone would collide inside
    /// the very same literal. The <c>-at-&lt;index&gt;</c> part of the suffix is what keeps the six
    /// identifiers apart, and it names a position in the pattern rather than in the source file.
    /// </summary>
    [Test]
    public async Task CreateMutations_TwoAlternationScopes_KeepsTheIdentifiersUniqueByPosition()
    {
        var (_, mutations) = Mutate(TwoScopeSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                """
                regex.alternation.remove-branch-1-at-1 | pattern '(a|b)(c|d)' => '(b)(c|d)'
                regex.alternation.remove-branch-2-at-1 | pattern '(a|b)(c|d)' => '(a)(c|d)'
                regex.alternation.swap-branches-1-2-at-1 | pattern '(a|b)(c|d)' => '(b|a)(c|d)'
                regex.alternation.remove-branch-1-at-6 | pattern '(a|b)(c|d)' => '(a|b)(d)'
                regex.alternation.remove-branch-2-at-6 | pattern '(a|b)(c|d)' => '(a|b)(c)'
                regex.alternation.swap-branches-1-2-at-6 | pattern '(a|b)(c|d)' => '(a|b)(d|c)'
                """
            );
        _ = await Assert
            .That(mutations.Select(mutation => mutation.OperatorId).Distinct(StringComparer.Ordinal))
            .Count()
            .IsEqualTo(6);
    }

    /// <summary>
    /// An empty branch is spliced like any other range. Removing the second branch of <c>a|</c> together
    /// with the bar in front of it leaves <c>a</c>, and removing the first branch together with the bar
    /// behind it leaves the empty pattern - which is a legal pattern and therefore a real mutant.
    /// </summary>
    [Test]
    public async Task CreateMutations_EmptyBranch_IsMutatedLikeAnyOtherBranch()
    {
        var (_, mutations) = Mutate(EmptyBranchSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                """
                regex.alternation.remove-branch-1-at-0 | pattern 'a|' => ''
                regex.alternation.remove-branch-2-at-0 | pattern 'a|' => 'a'
                regex.alternation.swap-branches-1-2-at-0 | pattern 'a|' => '|a'
                """
            );
    }

    /// <summary>
    /// Inside a character class a <c>|</c> is an ordinary member, so <c>[a|b]</c> holds no alternation and
    /// nothing is offered.
    /// </summary>
    [Test]
    public async Task CreateMutations_BarInsideACharacterClass_ReturnsEmpty()
    {
        var (_, mutations) = Mutate(CharacterClassSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// The bar of a conditional separates its yes part from its no part. Its opening token is exactly
    /// <c>(?</c>, because the parenthesis of the condition is shared with the construct, so the condition
    /// itself sits inside what a naive reading would call the first branch. Removing or reordering those
    /// pseudo-branches would move the condition rather than an alternative, which is why the scope is
    /// excluded and this fixture produces nothing at all.
    /// </summary>
    [Test]
    public async Task CreateMutations_Conditional_ReturnsEmpty()
    {
        var (_, mutations) = Mutate(ConditionalSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// The exclusion is scoped to the conditional itself. The group inside its yes part is an ordinary
    /// alternation scope, so its two branches are removed and swapped exactly as anywhere else, while the
    /// bar of the conditional at index 12 is still left alone - no suffix names that position.
    /// </summary>
    [Test]
    public async Task CreateMutations_AlternationNestedInsideAConditional_IsMutated()
    {
        var (_, mutations) = Mutate(ConditionalWithNestedAlternationSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                """
                regex.alternation.remove-branch-1-at-8 | pattern '(?(?=a)(b|c)|d)' => '(?(?=a)(c)|d)'
                regex.alternation.remove-branch-2-at-8 | pattern '(?(?=a)(b|c)|d)' => '(?(?=a)(b)|d)'
                regex.alternation.swap-branches-1-2-at-8 | pattern '(?(?=a)(b|c)|d)' => '(?(?=a)(c|b)|d)'
                """
            );
    }

    /// <summary>
    /// A lookaround opens a scope of its own, so the alternation it encloses is mutated and the lookaround
    /// itself stays intact.
    /// </summary>
    [Test]
    public async Task CreateMutations_AlternationInsideALookaround_IsMutated()
    {
        var (_, mutations) = Mutate(LookaroundSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                """
                regex.alternation.remove-branch-1-at-3 | pattern '(?=a|b)c' => '(?=b)c'
                regex.alternation.remove-branch-2-at-3 | pattern '(?=a|b)c' => '(?=a)c'
                regex.alternation.swap-branches-1-2-at-3 | pattern '(?=a|b)c' => '(?=b|a)c'
                """
            );
    }

    /// <summary>
    /// The whole pattern literal is replaced by an ordinary C# string literal holding the rewritten
    /// pattern, and everything around it - including the marker comment - stays untouched.
    /// </summary>
    [Test]
    public async Task CreateMutations_TwoBranches_RewritesTheSource()
    {
        var (tree, mutations) = Mutate(TwoBranchSource);
        var mutated = Rewrite(tree, Single(mutations, "remove-branch-1-at-0"));

        _ = await Assert.That(mutated).IsEqualTo(TwoBranchSource.Replace("\"a|b\"", "\"b\"", StringComparison.Ordinal));
        _ = await Assert.That(CompilationFactory.GetCompileErrors(CompilationFactory.Create(mutated))).IsEmpty();
    }

    /// <summary>
    /// A mutant that the <see cref="Regex" /> constructor rejects would be killed by construction rather
    /// than by an assertion, so every produced pattern is handed to the real parser.
    /// </summary>
    [Test]
    public async Task CreateMutations_EveryMutant_IsAValidPattern()
    {
        var (_, mutations) = Mutate(TwoScopeSource);
        var offenders = mutations.Select(MutatedPattern).Where(pattern => !IsAcceptedByRegex(pattern));

        _ = await Assert.That(mutations).Count().IsEqualTo(6);
        _ = await Assert.That(offenders).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_CancellationRequested_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(TwoScopeSource);
        var mutator = new RegexAlternationMutator();
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
        var mutator = new RegexAlternationMutator();

        return (tree, [.. mutator.CreateMutations(node, semanticModel, CancellationToken.None)]);
    }

    private static string Rewrite(SyntaxTree tree, Mutation mutation) => mutation.ApplyTo(tree).ToString();

    private static Mutation Single(Mutation[] mutations, string suffix) =>
        mutations.Single(mutation =>
            string.Equals(mutation.OperatorId, OperatorIdPrefix + suffix, StringComparison.Ordinal)
        );

    /// <summary>
    /// Renders the produced mutations as one string of <c>id | display name</c> lines, so that a failing
    /// expectation shows the whole set at once instead of the first difference only.
    /// </summary>
    /// <param name="mutations">The produced mutations, in order.</param>
    /// <returns>The joined lines.</returns>
    private static string Lines(Mutation[] mutations) =>
        string.Join("\n", mutations.Select(mutation => mutation.OperatorId + " | " + mutation.DisplayName));

    /// <summary>
    /// Reads the rewritten pattern back out of a display name, which spells it unescaped between the last
    /// <c>=&gt; '</c> and the closing quote.
    /// </summary>
    /// <param name="mutation">The mutation to read.</param>
    /// <returns>The rewritten pattern, possibly empty.</returns>
    private static string MutatedPattern(Mutation mutation)
    {
        const string Separator = " => '";

        var displayName = mutation.DisplayName;
        var start = displayName.LastIndexOf(Separator, StringComparison.Ordinal) + Separator.Length;

        return displayName.Substring(start, displayName.Length - start - 1);
    }

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
