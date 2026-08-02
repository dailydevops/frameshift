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
/// Covers the escape operator of the regular expression pattern family: the single rewrite from an escaped
/// literal dot to an unescaped dot, the source the mutated literal is rewritten to, and the character class
/// member the operator deliberately leaves alone because unescaping it there would be a silent no-op.
/// </summary>
/// <remarks>
/// <para>
/// A mutation of this family replaces the whole pattern literal, so a test that only pins the display name
/// would not notice a replacement literal whose <em>value</em> is not the pattern the name promises. The
/// tests therefore assert three things about a mutation: the operator identifier, the display name, and the
/// pattern the replacement literal denotes - and, for the fixtures whose pattern needs a backslash, the
/// rewritten source text as well.
/// </para>
/// </remarks>
public class RegexEscapeMutatorTests
{
    private const string OperatorIdPrefix = "regex.escape.";

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

    private const string EscapedDotSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"a\\.b");
        }
        """;

    /// <summary>
    /// A version-number-shaped pattern containing two escaped dots, each its own mutation point.
    /// </summary>
    private const string RepeatedEscapedDotSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"\\d+\\.\\d+\\.\\d+");
        }
        """;

    /// <summary>
    /// The escaped dot as a member of a character class, where it already means the literal dot and an
    /// unescaped dot there means exactly the same thing, so no defect is modelled and no mutation is
    /// offered.
    /// </summary>
    private const string CharacterClassSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"[\\.]");
        }
        """;

    /// <summary>
    /// An unescaped dot, which already means any character and carries nothing left for this operator.
    /// </summary>
    private const string UnescapedDotSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex(/*!*/"a.b");
        }
        """;

    /// <summary>
    /// The source text of the literal of <see cref="EscapedDotSource" />, meaning <c>"a\\.b"</c>.
    /// </summary>
    private const string EscapedDotLiteralText = @"""a\\.b""";

    /// <summary>
    /// The literal the unescaping of the dot produces, meaning <c>"a.b"</c>.
    /// </summary>
    private const string UnescapedDotLiteralText = @"""a.b""";

    private static readonly string[] _fixtures =
    [
        EscapedDotSource,
        RepeatedEscapedDotSource,
        CharacterClassSource,
        UnescapedDotSource,
    ];

    [Test]
    public async Task Metadata_Operator_ExposesIdKindAndSupportedKinds()
    {
        var mutator = new RegexEscapeMutator();

        _ = await Assert.That(mutator.Id).IsEqualTo("regex.escape");
        _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.RegexEscape);
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
        var (_, mutations) = Mutate(EscapedDotSource);
        var offenders = mutations
            .Where(mutation => !mutation.OperatorId.StartsWith(OperatorIdPrefix, StringComparison.Ordinal))
            .Select(mutation => mutation.OperatorId);

        _ = await Assert.That(offenders).IsEmpty();
        _ = await Assert
            .That(mutations.Select(mutation => mutation.Kind).Distinct())
            .IsEquivalentTo([MutationKind.RegexEscape]);
    }

    /// <summary>
    /// The escaped dot outside a character class is unescaped into any character, which turns "match a
    /// literal dot" into "match any character".
    /// </summary>
    [Test]
    public async Task CreateMutations_EscapedDot_UnescapesIt()
    {
        var (_, mutations) = Mutate(EscapedDotSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(@"regex.escape.literal-dot-to-any-character | pattern 'a\.b' => 'a.b'");
        _ = await Assert.That(ReplacementPattern(mutations, "literal-dot-to-any-character")).IsEqualTo("a.b");
    }

    /// <summary>
    /// The mutated source, not only the display name: the replacement is an ordinary C# literal, so its
    /// escaping is part of the contract. A replacement literal spelling <c>"a.b"</c> is what the value
    /// <c>a.b</c> requires; a source spelling <c>"a\.b"</c> would denote a different pattern (the very one
    /// being mutated away from).
    /// </summary>
    [Test]
    public async Task CreateMutations_EscapedDot_RewritesTheSourceWithoutTheBackslash()
    {
        var (tree, mutations) = Mutate(EscapedDotSource);

        _ = await Assert
            .That(Rewrite(tree, Single(mutations, "literal-dot-to-any-character")))
            .IsEqualTo(
                EscapedDotSource.Replace(EscapedDotLiteralText, UnescapedDotLiteralText, StringComparison.Ordinal)
            );
    }

    /// <summary>
    /// Every occurrence of an escaped dot is a mutation point of its own, so a pattern with two of them - the
    /// shape of a version number - yields two mutations, each rewriting only its own occurrence.
    /// </summary>
    [Test]
    public async Task CreateMutations_RepeatedEscapedDot_ProducesOneMutationPerOccurrence()
    {
        var (_, mutations) = Mutate(RepeatedEscapedDotSource);

        _ = await Assert
            .That(Lines(mutations))
            .IsEqualTo(
                @"regex.escape.literal-dot-to-any-character | pattern '\d+\.\d+\.\d+' => '\d+.\d+\.\d+'"
                    + LineSeparator
                    + @"regex.escape.literal-dot-to-any-character | pattern '\d+\.\d+\.\d+' => '\d+\.\d+.\d+'"
            );
        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(ReplacementPattern(mutations[0])).IsEqualTo(@"\d+.\d+\.\d+");
        _ = await Assert.That(ReplacementPattern(mutations[1])).IsEqualTo(@"\d+\.\d+.\d+");
    }

    /// <summary>
    /// The acceptance criterion of the issue: inside a character class <c>\.</c> and <c>.</c> already mean
    /// the same thing, a class member is never "any character", so unescaping it models no defect and the
    /// operator produces nothing at all.
    /// </summary>
    [Test]
    public async Task CreateMutations_EscapedDotInsideACharacterClass_ReturnsEmpty()
    {
        var (_, mutations) = Mutate(CharacterClassSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// An already unescaped dot carries no escape token for this operator to look at, so it produces
    /// nothing.
    /// </summary>
    [Test]
    public async Task CreateMutations_UnescapedDot_ReturnsEmpty()
    {
        var (_, mutations) = Mutate(UnescapedDotSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// Every mutant of the family has to be a legal regular expression under the options of its site, so
    /// that it is killed by an assertion rather than by the <see cref="Regex" /> constructor. The real
    /// parser is the oracle; a test may construct a <see cref="Regex" />, the analyzer may not.
    /// </summary>
    [Test]
    public async Task CreateMutations_EveryMutant_IsAValidPattern()
    {
        var (_, mutations) = Mutate(EscapedDotSource);
        var (_, repeated) = Mutate(RepeatedEscapedDotSource);
        var offenders = mutations
            .Concat(repeated)
            .Where(mutation => !IsAcceptedByRegex(ReplacementPattern(mutation), RegexOptions.None))
            .Select(mutation => mutation.DisplayName);

        _ = await Assert.That(offenders).IsEmpty();
        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(repeated).Count().IsEqualTo(2);
    }

    [Test]
    public async Task CreateMutations_UnsupportedSyntaxKind_ReturnsEmpty()
    {
        var (_, mutations) = Mutate(EscapedDotSource, SyntaxNodeLocator.FindFirst<ObjectCreationExpressionSyntax>);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_NodeNull_ThrowsArgumentNullException()
    {
        var (_, semanticModel, _) = CompilationFactory.CreateWithModel(EscapedDotSource);
        var mutator = new RegexEscapeMutator();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(null!, semanticModel, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("node");
    }

    [Test]
    public async Task CreateMutations_SemanticModelNull_ThrowsArgumentNullException()
    {
        var (_, _, tree) = CompilationFactory.CreateWithModel(EscapedDotSource);
        var mutator = new RegexEscapeMutator();
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(node, null!, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("semanticModel");
    }

    [Test]
    public async Task CreateMutations_CancellationRequested_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(EscapedDotSource);
        var mutator = new RegexEscapeMutator();
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
        var mutator = new RegexEscapeMutator();

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
