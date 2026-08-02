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
/// Covers the quantifier operator: the exact set of produced mutations - identifier, display name and
/// order - for the simple <c>*</c>, <c>+</c> and <c>?</c> quantifiers in their greedy and their lazy
/// spelling, for the counted <c>{n}</c>, <c>{n,}</c> and <c>{n,m}</c> forms, the bounds it refuses to
/// shift, the rewrites the base class discards because they cross their own bounds, the exact count that
/// is never made lazy, the constructs the operator never touches, and the rewritten source.
/// </summary>
public class RegexQuantifierMutatorTests
{
    private const string OperatorIdPrefix = "regex.quantifier.";

    /// <summary>
    /// The separator between the <c>identifier | display name</c> lines an expectation is built from.
    /// A single joined string is asserted instead of a collection, so that a failure shows every produced
    /// mutation at once instead of only the first difference.
    /// </summary>
    private const string LineSeparator = "\n";

    private const string StarPlusOptionalSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"a*b+c?");
        }
        """;

    private const string LazyStarPlusOptionalSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"a*?b+?c??");
        }
        """;

    private const string ExactSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/@"\d{4}");
        }
        """;

    private const string LazyExactSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/@"\d{4}?");
        }
        """;

    private const string OpenEndedSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/@"\d{2,}");
        }
        """;

    private const string RangeSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/@"\d{2,3}");
        }
        """;

    private const string LazyRangeSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/@"\d{2,3}?");
        }
        """;

    private const string ZeroExactSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"a{0}");
        }
        """;

    private const string ZeroRangeSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"a{0,0}");
        }
        """;

    private const string EqualBoundsSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"a{3,3}");
        }
        """;

#if !NETFRAMEWORK
    private const string MaximumInt32Source = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"a{2147483647}");
        }
        """;
#endif

    private const string OverflowingBoundSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"a{99999999999}");
        }
        """;

    private const string LeadingZerosSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"a{007}");
        }
        """;

    private const string CharacterClassSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"[*+?{2}]");
        }
        """;

    private const string BraceLiteralSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"a{x}");
        }
        """;

    private static readonly string[] _fixtures =
    [
        StarPlusOptionalSource,
        LazyStarPlusOptionalSource,
        ExactSource,
        LazyExactSource,
        OpenEndedSource,
        RangeSource,
        LazyRangeSource,
        ZeroExactSource,
        ZeroRangeSource,
        EqualBoundsSource,
#if !NETFRAMEWORK
        // Excluded on .NET Framework: validating this pattern's astronomic exact count triggers an
        // OutOfMemoryException in the legacy regular expression engine, see the remarks on
        // CreateMutations_MaximumInt32Count_IsOnlyDecreased. The analyzer itself never runs on that
        // engine.
        MaximumInt32Source,
#endif
        OverflowingBoundSource,
        LeadingZerosSource,
        CharacterClassSource,
        BraceLiteralSource,
    ];

    [Test]
    public async Task Metadata_Operator_ExposesIdKindAndSupportedKinds()
    {
        var mutator = new RegexQuantifierMutator();
        var expectedKinds = new[] { SyntaxKind.StringLiteralExpression };

        _ = await Assert.That(mutator.Id).IsEqualTo("regex.quantifier");
        _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.RegexQuantifier);
        _ = await Assert.That(mutator.SupportedSyntaxKinds).IsEquivalentTo(expectedKinds);
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
        var (_, mutations) = Mutate(StarPlusOptionalSource);
        var offenders = mutations
            .Where(mutation => !mutation.OperatorId.StartsWith(OperatorIdPrefix, StringComparison.Ordinal))
            .Select(mutation => mutation.OperatorId);

        _ = await Assert.That(offenders).IsEmpty();
        _ = await Assert
            .That(mutations.Select(mutation => mutation.Kind).Distinct())
            .IsEquivalentTo([MutationKind.RegexQuantifier]);
    }

    /// <summary>
    /// The order is part of the contract: each token is rewritten in pattern order and, inside a token,
    /// the shape mutation comes first and the laziness toggle last.
    /// </summary>
    [Test]
    public async Task CreateMutations_GreedyQuantifiers_SwapsTheShapeThenTogglesLaziness()
    {
        var (_, mutations) = Mutate(StarPlusOptionalSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                Expected(
                    "regex.quantifier.star-to-plus | pattern 'a*b+c?' => 'a+b+c?'",
                    "regex.quantifier.greedy-to-lazy | pattern 'a*b+c?' => 'a*?b+c?'",
                    "regex.quantifier.plus-to-star | pattern 'a*b+c?' => 'a*b*c?'",
                    "regex.quantifier.greedy-to-lazy | pattern 'a*b+c?' => 'a*b+?c?'",
                    "regex.quantifier.remove-optional | pattern 'a*b+c?' => 'a*b+c'",
                    "regex.quantifier.greedy-to-lazy | pattern 'a*b+c?' => 'a*b+c??'"
                )
            );
    }

    /// <summary>
    /// A shape mutation keeps the laziness of the token it rewrites, and the toggle of a lazy token drops
    /// the marker instead of adding one. Removing the optional <c>??</c> deletes core and marker together,
    /// which is the one rewrite that removes the whole token.
    /// </summary>
    [Test]
    public async Task CreateMutations_LazyQuantifiers_KeepTheMarkerAndToggleToGreedy()
    {
        var (_, mutations) = Mutate(LazyStarPlusOptionalSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                Expected(
                    "regex.quantifier.star-to-plus | pattern 'a*?b+?c??' => 'a+?b+?c??'",
                    "regex.quantifier.lazy-to-greedy | pattern 'a*?b+?c??' => 'a*b+?c??'",
                    "regex.quantifier.plus-to-star | pattern 'a*?b+?c??' => 'a*?b*?c??'",
                    "regex.quantifier.lazy-to-greedy | pattern 'a*?b+?c??' => 'a*?b+c??'",
                    "regex.quantifier.remove-optional | pattern 'a*?b+?c??' => 'a*?b+?c'",
                    "regex.quantifier.lazy-to-greedy | pattern 'a*?b+?c??' => 'a*?b+?c?'"
                )
            );
    }

    [Test]
    public async Task CreateMutations_ExactCount_ShiftsTheCountInBothDirections()
    {
        var (_, mutations) = Mutate(ExactSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                Expected(
                    @"regex.quantifier.decrease-exact | pattern '\d{4}' => '\d{3}'",
                    @"regex.quantifier.increase-exact | pattern '\d{4}' => '\d{5}'"
                )
            );
    }

    [Test]
    public async Task CreateMutations_OpenEndedCount_ShiftsTheLowerBoundAndTogglesLaziness()
    {
        var (_, mutations) = Mutate(OpenEndedSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                Expected(
                    @"regex.quantifier.decrease-minimum | pattern '\d{2,}' => '\d{1,}'",
                    @"regex.quantifier.increase-minimum | pattern '\d{2,}' => '\d{3,}'",
                    @"regex.quantifier.greedy-to-lazy | pattern '\d{2,}' => '\d{2,}?'"
                )
            );
    }

    [Test]
    public async Task CreateMutations_CountRange_ShiftsBothBoundsAndTogglesLaziness()
    {
        var (_, mutations) = Mutate(RangeSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                Expected(
                    @"regex.quantifier.decrease-minimum | pattern '\d{2,3}' => '\d{1,3}'",
                    @"regex.quantifier.increase-minimum | pattern '\d{2,3}' => '\d{3,3}'",
                    @"regex.quantifier.decrease-maximum | pattern '\d{2,3}' => '\d{2,2}'",
                    @"regex.quantifier.increase-maximum | pattern '\d{2,3}' => '\d{2,4}'",
                    @"regex.quantifier.greedy-to-lazy | pattern '\d{2,3}' => '\d{2,3}?'"
                )
            );
    }

    /// <summary>
    /// The lazy marker of a counted quantifier sits behind the closing brace, so every bound rewrite has
    /// to re-emit it; only the toggle itself drops it.
    /// </summary>
    [Test]
    public async Task CreateMutations_LazyCountRange_KeepsTheMarkerOnEveryBoundRewrite()
    {
        var (_, mutations) = Mutate(LazyRangeSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                Expected(
                    @"regex.quantifier.decrease-minimum | pattern '\d{2,3}?' => '\d{1,3}?'",
                    @"regex.quantifier.increase-minimum | pattern '\d{2,3}?' => '\d{3,3}?'",
                    @"regex.quantifier.decrease-maximum | pattern '\d{2,3}?' => '\d{2,2}?'",
                    @"regex.quantifier.increase-maximum | pattern '\d{2,3}?' => '\d{2,4}?'",
                    @"regex.quantifier.lazy-to-greedy | pattern '\d{2,3}?' => '\d{2,3}'"
                )
            );
    }

    /// <summary>
    /// An exact repetition count leaves the engine no choice about how often to repeat, so its marker
    /// cannot change a single match and the mutant would be equivalent to the original by construction.
    /// The toggle is therefore missing in both directions: the greedy <c>{4}</c> is never made lazy, and
    /// the lazy <c>{4}?</c> is never made greedy.
    /// </summary>
    [Test]
    public async Task CreateMutations_ExactCount_IsNeverMadeLazyOrGreedy()
    {
        var (_, greedy) = Mutate(ExactSource);
        var (_, lazy) = Mutate(LazyExactSource);

        _ = await Assert.That(Mentioning(greedy, "lazy")).IsEmpty();
        _ = await Assert.That(Mentioning(lazy, "lazy")).IsEmpty();
        _ = await Assert
            .That(Lines(lazy))
            .IsEqualTo(
                Expected(
                    @"regex.quantifier.decrease-exact | pattern '\d{4}?' => '\d{3}?'",
                    @"regex.quantifier.increase-exact | pattern '\d{4}?' => '\d{5}?'"
                )
            );
    }

    /// <summary>
    /// A lower bound of zero is not decreased, because a negative bound is not a quantifier at all.
    /// </summary>
    [Test]
    public async Task CreateMutations_ZeroExactCount_IsOnlyIncreased()
    {
        var (_, mutations) = Mutate(ZeroExactSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(Expected("regex.quantifier.increase-exact | pattern 'a{0}' => 'a{1}'"));
    }

    /// <summary>
    /// Both bounds of <c>{0,0}</c> are at zero, so neither is decreased. Increasing the lower one would
    /// produce <c>{1,0}</c>, whose bounds cross, and that rewrite is discarded by the validity check of
    /// the base class - which leaves the increase of the upper bound and the laziness toggle.
    /// </summary>
    [Test]
    public async Task CreateMutations_ZeroBounds_ProduceOnlyTheViableRewrites()
    {
        var (_, mutations) = Mutate(ZeroRangeSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                Expected(
                    "regex.quantifier.increase-maximum | pattern 'a{0,0}' => 'a{0,1}'",
                    "regex.quantifier.greedy-to-lazy | pattern 'a{0,0}' => 'a{0,0}?'"
                )
            );
    }

    /// <summary>
    /// The operator offers all four bound shifts of <c>{3,3}</c> without an ordering check of its own, so
    /// it also offers <c>{4,3}</c> and <c>{3,2}</c>. Both are rejected by the regular expression engine,
    /// and the base class discards every rewrite that is not a valid pattern - which is why the increase
    /// of the minimum and the decrease of the maximum are absent while the other rewrites are reported.
    /// </summary>
    [Test]
    public async Task CreateMutations_CrossingBounds_AreDiscardedByTheValidityCheck()
    {
        var (_, mutations) = Mutate(EqualBoundsSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                Expected(
                    "regex.quantifier.decrease-minimum | pattern 'a{3,3}' => 'a{2,3}'",
                    "regex.quantifier.increase-maximum | pattern 'a{3,3}' => 'a{3,4}'",
                    "regex.quantifier.greedy-to-lazy | pattern 'a{3,3}' => 'a{3,3}?'"
                )
            );
    }

#if !NETFRAMEWORK
    /// <summary>
    /// A bound already at <see cref="int.MaxValue" /> is not increased, because there is no larger bound
    /// to write down, and the exact form gets no laziness toggle - so a single mutation remains.
    /// </summary>
    /// <remarks>
    /// Excluded on .NET Framework: its legacy regular expression engine eagerly precomputes a fixed
    /// length prefix for an exact quantifier and tries to allocate a string as wide as the repeat count,
    /// which turns validating <c>a{2147483647}</c> itself into an <see cref="OutOfMemoryException" />
    /// rather than a clean parse result. The analyzer never runs on that engine - Roslyn 5.6 requires a
    /// modern SDK - so the boundary is pinned on every runtime the analyzer actually executes under.
    /// </remarks>
    [Test]
    public async Task CreateMutations_MaximumInt32Count_IsOnlyDecreased()
    {
        var (_, mutations) = Mutate(MaximumInt32Source);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(Expected("regex.quantifier.decrease-exact | pattern 'a{2147483647}' => 'a{2147483646}'"));
    }
#endif

    /// <summary>
    /// A bound that does not fit into an <see cref="int" /> produces no mutation at all, and the reason is
    /// the earlier of the two possible ones: the regular expression engine rejects such a bound itself, so
    /// the whole site is already skipped by the validity check of the base class and the operator is never
    /// asked. The <c>TryParseBound</c> guard of the operator is the second line of defence behind it.
    /// </summary>
    [Test]
    public async Task CreateMutations_BoundBeyondInt32_ReturnsEmpty()
    {
        var (_, mutations) = Mutate(OverflowingBoundSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// A bound is parsed and re-emitted rather than edited in place, so leading zeros disappear from a
    /// token that is being changed anyway. That never alters what the mutant matches.
    /// </summary>
    [Test]
    public async Task CreateMutations_BoundWithLeadingZeros_IsNormalized()
    {
        var (_, mutations) = Mutate(LeadingZerosSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                Expected(
                    "regex.quantifier.decrease-exact | pattern 'a{007}' => 'a{6}'",
                    "regex.quantifier.increase-exact | pattern 'a{007}' => 'a{8}'"
                )
            );
    }

    /// <summary>
    /// A <c>*</c>, <c>+</c> or <c>?</c> inside a character class is a member matching itself, and so are
    /// the braces and the digit next to them, so the class holds no quantifier to rewrite.
    /// </summary>
    [Test]
    public async Task CreateMutations_QuantifierCharactersInsideACharacterClass_ReturnEmpty()
    {
        var (_, mutations) = Mutate(CharacterClassSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// A <c>{</c> that starts no quantifier is an ordinary literal, which is why <c>a{x}</c> is a legal
    /// pattern and holds nothing this operator answers for.
    /// </summary>
    [Test]
    public async Task CreateMutations_BraceThatStartsNoQuantifier_ReturnsEmpty()
    {
        var (_, mutations) = Mutate(BraceLiteralSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// The whole pattern literal is replaced by an ordinary C# string literal holding the rewritten
    /// pattern, whatever form the original literal used: the verbatim fixture becomes an ordinary literal
    /// whose backslash is escaped.
    /// </summary>
    [Test]
    public async Task CreateMutations_Mutation_RewritesTheWholePatternLiteral()
    {
        var (starTree, starMutations) = Mutate(StarPlusOptionalSource);
        var (exactTree, exactMutations) = Mutate(ExactSource);

        _ = await Assert
            .That(Rewrite(starTree, Single(starMutations, "star-to-plus")))
            .IsEqualTo(StarPlusOptionalSource.Replace("\"a*b+c?\"", "\"a+b+c?\"", StringComparison.Ordinal));

        // The fixture spells the pattern as @"\d{4}"; the replacement spells '\d{3}' as "\\d{3}".
        _ = await Assert
            .That(Rewrite(exactTree, Single(exactMutations, "decrease-exact")))
            .IsEqualTo(ExactSource.Replace("@\"\\d{4}\"", "\"\\\\d{3}\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every mutant of every fixture is itself a legal pattern. A mutant that throws in each test that
    /// reaches it would be killed by construction rather than by an assertion, and would therefore say
    /// nothing about the test suite. The total count is asserted as well, so that a fixture silently
    /// producing nothing cannot make the check pass by having nothing to check.
    /// </summary>
    /// <remarks>
    /// The expected count is one lower on .NET Framework, because <c>MaximumInt32Source</c> - which
    /// contributes exactly one mutation, <c>decrease-exact</c> - is excluded from <see cref="_fixtures" />
    /// there; see the remarks on <see cref="_fixtures" />.
    /// </remarks>
    [Test]
    public async Task CreateMutations_EveryMutant_IsAValidPattern()
    {
        var patterns = _fixtures.SelectMany(source => Mutate(source).Mutations).Select(MutatedPattern).ToArray();
        var offenders = patterns.Where(pattern => !IsValidPattern(pattern));
#if NETFRAMEWORK
        const int expectedCount = 37;
#else
        const int expectedCount = 38;
#endif

        _ = await Assert.That(patterns).Count().IsEqualTo(expectedCount);
        _ = await Assert.That(offenders).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_CancellationRequested_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(StarPlusOptionalSource);
        var mutator = new RegexQuantifierMutator();
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
        var mutator = new RegexQuantifierMutator();
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        return (tree, [.. mutator.CreateMutations(node, semanticModel, CancellationToken.None)]);
    }

    /// <summary>
    /// Renders the produced mutations as <c>identifier | display name</c> lines, in the order the operator
    /// produced them.
    /// </summary>
    /// <param name="mutations">The produced mutations.</param>
    /// <returns>The rendered lines, joined by <see cref="LineSeparator" />.</returns>
    private static string Lines(Mutation[] mutations) =>
        string.Join(LineSeparator, mutations.Select(mutation => mutation.OperatorId + " | " + mutation.DisplayName));

    /// <summary>
    /// Joins the expected lines the same way <see cref="Lines" /> joins the produced ones.
    /// </summary>
    /// <param name="lines">The expected lines, in the expected order.</param>
    /// <returns>The expectation as one string.</returns>
    private static string Expected(params string[] lines) => string.Join(LineSeparator, lines);

    /// <summary>
    /// Selects the mutations whose identifier names <paramref name="part" />, which is how a test pins that
    /// a rewrite is never offered.
    /// </summary>
    /// <param name="mutations">The produced mutations.</param>
    /// <param name="part">The identifier part that must not appear.</param>
    /// <returns>The offending identifiers, expected to be none.</returns>
    private static IEnumerable<string> Mentioning(Mutation[] mutations, string part) =>
        mutations
            .Where(mutation => mutation.OperatorId.Contains(part, StringComparison.Ordinal))
            .Select(mutation => mutation.OperatorId);

    /// <summary>
    /// Reads the pattern a mutation puts into the literal, meaning the value of the replacement literal
    /// rather than its C# spelling.
    /// </summary>
    /// <param name="mutation">The mutation to read.</param>
    /// <returns>The rewritten pattern.</returns>
    private static string MutatedPattern(Mutation mutation) =>
        ((LiteralExpressionSyntax)mutation.Replacement).Token.ValueText;

    /// <summary>
    /// Decides whether a rewritten pattern is a legal regular expression, by constructing the
    /// <see cref="Regex" /> a consumer of the mutant would construct.
    /// </summary>
    /// <param name="pattern">The pattern to check.</param>
    /// <returns><see langword="true" /> when the pattern is legal.</returns>
    /// <remarks>
    /// The fixtures pass no options, so the mutants are checked with <see cref="RegexOptions.None" />, and
    /// the match timeout is stated only because construction alone must never be able to block.
    /// </remarks>
    private static bool IsValidPattern(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Rewrite(SyntaxTree tree, Mutation mutation) => mutation.ApplyTo(tree).ToString();

    private static Mutation Single(Mutation[] mutations, string suffix) =>
        mutations.Single(mutation =>
            string.Equals(mutation.OperatorId, OperatorIdPrefix + suffix, StringComparison.Ordinal)
        );
}
