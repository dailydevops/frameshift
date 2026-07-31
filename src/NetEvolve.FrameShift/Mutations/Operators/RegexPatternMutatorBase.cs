namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Mutations.RegularExpressions;

/// <summary>
/// Shared plumbing for the operators of the regular expression pattern family: locating the pattern
/// behind a string literal, tokenizing it, discarding every rewrite that is not a valid pattern, and
/// turning the surviving ones into mutations of the literal.
/// </summary>
/// <remarks>
/// <para>
/// A derived operator only answers the one question that makes it a distinct operator: given the tokens
/// of the pattern, which rewrites does it offer? It therefore never looks at Roslyn, never parses the
/// pattern a second time and never decides whether its own output is legal.
/// </para>
/// <para>
/// Every operator of this family claims <see cref="SyntaxKind.StringLiteralExpression" />, which covers
/// the ordinary, the verbatim and the raw form alike, and hands the node to
/// <see cref="RegexPatternLocator" /> - the only thing that decides whether a literal is a pattern at
/// all. A site whose options are not statically determinable is skipped, because the options change the
/// grammar of the pattern and a rewrite made under guessed options would not be the mutation it claims
/// to be.
/// </para>
/// <para>
/// A pattern the tokenizer rejects, and a pattern the <see cref="Regex" /> constructor rejects, produce
/// no mutation at all. The second check is not redundant: the tokenizer is a lexer and deliberately
/// accepts a pattern whose only problem is semantic. Code that already carries a broken pattern is
/// broken with or without a mutant, so there is nothing to demand a test for.
/// </para>
/// <para>
/// The replacement literal is always an ordinary C# string literal built by
/// <see cref="SyntaxFactory.Literal(string)" />, so its value is exactly the rewritten pattern no matter
/// which form the original literal used. It is a compile time constant like the original, which is why
/// the family needs no constant context guard and can mutate the pattern of a <c>[GeneratedRegex]</c> or
/// <c>[RegularExpression]</c> attribute as well.
/// </para>
/// </remarks>
internal abstract class RegexPatternMutatorBase : MutationOperatorBase
{
    /// <summary>
    /// The one syntax kind every operator of the family claims. A pattern is always spelled out as a
    /// string literal, in whichever of the three C# forms.
    /// </summary>
    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds = [SyntaxKind.StringLiteralExpression];

    /// <summary>
    /// Initializes a new instance of the <see cref="RegexPatternMutatorBase" /> class.
    /// </summary>
    /// <param name="id">The stable identifier prefix of the operator, e.g. <c>regex.anchor</c>.</param>
    /// <param name="kind">The operator family the operator belongs to.</param>
    protected RegexPatternMutatorBase(string id, MutationKind kind)
        : base(id, kind, _supportedSyntaxKinds) { }

    /// <inheritdoc />
    protected sealed override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var site = RegexPatternLocator.TryLocate(node, semanticModel, cancellationToken);

        if (site is null || !site.AreOptionsKnown)
        {
            return [];
        }

        var options = ToParseOptions(site.Options!.Value);

        if (!RegexPatternValidity.IsValid(site.Pattern, options, out _))
        {
            return [];
        }

        if (!RegexPatternTokenizer.TryTokenize(site.Pattern, options, out var tokens, out _, out _))
        {
            return [];
        }

        return CreateViableMutations(site, options, tokens, cancellationToken);
    }

    /// <summary>
    /// Creates the rewrites this operator offers for <paramref name="pattern" />.
    /// </summary>
    /// <param name="pattern">The pattern text the tokens tile completely.</param>
    /// <param name="tokens">The tokens of the pattern, in order.</param>
    /// <param name="cancellationToken">A token to observe while producing the rewrites.</param>
    /// <returns>
    /// The candidate rewrites, in the order the mutations are produced in. A rewrite that equals
    /// <paramref name="pattern" /> and a rewrite that is not a valid pattern are both discarded by the
    /// base class, so an implementation may offer either without checking.
    /// </returns>
    protected abstract IEnumerable<RegexPatternRewrite> CreateRewrites(
        string pattern,
        ImmutableArray<RegexToken> tokens,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Replaces the span <paramref name="token" /> covers by <paramref name="replacement" />, which is
    /// how every operator of this family rewrites a construct.
    /// </summary>
    /// <param name="pattern">The pattern to rewrite.</param>
    /// <param name="token">The token whose span is replaced.</param>
    /// <param name="replacement">The text taking its place, possibly empty to remove the construct.</param>
    /// <returns>The rewritten pattern.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="pattern" />, <paramref name="token" /> or <paramref name="replacement" /> is
    /// <see langword="null" />.
    /// </exception>
    protected static string Replace(string pattern, RegexToken token, string replacement)
    {
        if (token is null)
        {
            throw new ArgumentNullException(nameof(token));
        }

        return Splice(pattern, token.Start, token.End, replacement);
    }

    /// <summary>
    /// Replaces the half open range <paramref name="start" />..<paramref name="end" /> of
    /// <paramref name="pattern" /> by <paramref name="replacement" />.
    /// </summary>
    /// <param name="pattern">The pattern to rewrite.</param>
    /// <param name="start">The first index of the replaced range.</param>
    /// <param name="end">The index one past the last replaced character.</param>
    /// <param name="replacement">The text taking its place, possibly empty.</param>
    /// <returns>The rewritten pattern.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="pattern" /> or <paramref name="replacement" /> is <see langword="null" />.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The range does not lie inside <paramref name="pattern" />.
    /// </exception>
    protected static string Splice(string pattern, int start, int end, string replacement)
    {
        if (pattern is null)
        {
            throw new ArgumentNullException(nameof(pattern));
        }

        if (replacement is null)
        {
            throw new ArgumentNullException(nameof(replacement));
        }

        if (start < 0 || start > pattern.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, "The start index must lie in the pattern.");
        }

        if (end < start || end > pattern.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(end), end, "The end index must lie behind the start index.");
        }

        return pattern.Substring(0, start) + replacement + pattern.Substring(end);
    }

    /// <summary>
    /// Turns the resolved options of a site into the options the validity check is allowed to construct a
    /// <see cref="Regex" /> with.
    /// </summary>
    /// <param name="options">The options the site resolved.</param>
    /// <returns>The options without <see cref="RegexOptions.Compiled" />.</returns>
    /// <remarks>
    /// Only <see cref="RegexOptions.Compiled" /> is dropped, and it has to be: it makes the constructor
    /// emit IL for an object that is thrown away immediately, which an analyzer running inside the
    /// compiler must not do. It cannot change the answer either, because it selects how the engine is
    /// built and not which patterns are legal. Every other flag is kept, including the ones that change
    /// the grammar and the ones that shrink it, because dropping those would answer a different
    /// question than the one the site asks.
    /// </remarks>
    private static RegexOptions ToParseOptions(RegexOptions options) => options & ~RegexOptions.Compiled;

    /// <summary>
    /// Turns the surviving rewrites into mutations of the pattern literal.
    /// </summary>
    /// <param name="site">The located pattern site.</param>
    /// <param name="options">The options the rewrites are validated under.</param>
    /// <param name="tokens">The tokens of the original pattern.</param>
    /// <param name="cancellationToken">A token to observe while producing the mutations.</param>
    /// <returns>The viable mutations, possibly none.</returns>
    private IEnumerable<Mutation> CreateViableMutations(
        RegexPatternSite site,
        RegexOptions options,
        ImmutableArray<RegexToken> tokens,
        CancellationToken cancellationToken
    )
    {
        foreach (var rewrite in CreateRewrites(site.Pattern, tokens, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.Equals(rewrite.Pattern, site.Pattern, StringComparison.Ordinal))
            {
                // A rewrite that reproduces the pattern is no mutation. Discarding it here means an
                // operator may offer a rewrite without proving first that it changes anything.
                continue;
            }

            if (!RegexPatternValidity.IsValid(rewrite.Pattern, options, out _))
            {
                // A mutant that throws in every test that reaches it is killed by construction rather
                // than by an assertion, so it carries no information about the test suite.
                continue;
            }

            yield return CreateMutation(
                site.PatternLiteral,
                CreatePatternLiteral(rewrite.Pattern),
                rewrite.OperatorSuffix,
                Describe(site.Pattern, rewrite.Pattern)
            );
        }
    }

    /// <summary>
    /// Spells out <paramref name="pattern" /> as an ordinary C# string literal.
    /// </summary>
    /// <param name="pattern">The pattern the literal has to denote.</param>
    /// <returns>The created literal.</returns>
    private static LiteralExpressionSyntax CreatePatternLiteral(string pattern) =>
        SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(pattern));

    /// <summary>
    /// Composes the human readable description of a pattern mutation.
    /// </summary>
    /// <param name="original">The original pattern.</param>
    /// <param name="mutated">The rewritten pattern.</param>
    /// <returns>The description, e.g. <c>pattern '^a$' =&gt; 'a$'</c>.</returns>
    /// <remarks>
    /// Both patterns are shown in full and unescaped, meaning as the regular expression engine sees them
    /// rather than as the C# literal spells them. A reader comparing the two has to see the exact
    /// characters that changed, and an abbreviation would hide precisely the construct the mutation is
    /// about.
    /// </remarks>
    private static string Describe(string original, string mutated) => $"pattern '{original}' => '{mutated}'";
}
