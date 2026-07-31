namespace NetEvolve.FrameShift.Mutations.RegularExpressions;

using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// An immutable description of one located regular expression pattern: the literal that spells it out,
/// the pattern text that literal denotes, the options the pattern is parsed with, and the construct it
/// was found in.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Pattern" /> is deliberately derived from the literal token instead of being passed in, so
/// that a caller cannot pair a literal with a text that does not belong to it. It is the token's
/// <em>value</em>, not its source text: a verbatim literal such as <c>@"\d+"</c> and a raw literal such
/// as <c>"""\d+"""</c> both denote the two characters <c>\d</c> followed by <c>+</c>, while their source
/// text carries the quoting and, in the ordinary form, doubled backslashes. Every consumer of a pattern
/// - the tokenizer above all - has to see the value, because that is what the regular expression engine
/// receives.
/// </para>
/// <para>
/// <see cref="Options" /> is nullable, and <see langword="null" /> means <em>not statically
/// determinable</em>, never <c>RegexOptions.None</c>. That distinction is the reason the type exists in
/// this shape. The pattern grammar itself depends on the options: with
/// <c>RegexOptions.IgnorePatternWhitespace</c> unescaped whitespace is insignificant and <c>#</c> starts
/// a comment that runs to the end of the line, so the very same characters tokenize into different
/// constructs. Silently substituting <c>None</c> for options that could not be resolved would therefore
/// make a later rewriter mis-parse the pattern and produce a mutant that is not the mutation it claims to
/// be. A caller that cannot deal with unknown options has to skip the site, which
/// <see cref="AreOptionsKnown" /> makes easy to say.
/// </para>
/// </remarks>
internal sealed class RegexPatternSite
{
    private const string UnknownOptionsText = "options not statically determinable";

    /// <summary>
    /// Initializes a new instance of the <see cref="RegexPatternSite" /> class.
    /// </summary>
    /// <param name="patternLiteral">The string literal holding the pattern.</param>
    /// <param name="origin">The construct the pattern was found in.</param>
    /// <param name="options">
    /// The resolved options, or <see langword="null" /> when they are not statically determinable.
    /// </param>
    /// <param name="optionsExpression">
    /// The expression the options were read from, or <see langword="null" /> when the located overload has
    /// no options parameter at all.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="patternLiteral" /> is <see langword="null" />.</exception>
    public RegexPatternSite(
        LiteralExpressionSyntax patternLiteral,
        RegexPatternOrigin origin,
        RegexOptions? options,
        ExpressionSyntax? optionsExpression
    )
    {
        if (patternLiteral is null)
        {
            throw new ArgumentNullException(nameof(patternLiteral));
        }

        PatternLiteral = patternLiteral;
        Pattern = patternLiteral.Token.ValueText;
        Origin = origin;
        Options = options;
        OptionsExpression = optionsExpression;
    }

    /// <summary>
    /// Gets the string literal holding the pattern, which is the node a rewriter replaces.
    /// </summary>
    public LiteralExpressionSyntax PatternLiteral { get; }

    /// <summary>
    /// Gets the pattern text the literal denotes, meaning the token's value with all C# quoting and
    /// escaping already resolved.
    /// </summary>
    public string Pattern { get; }

    /// <summary>
    /// Gets the construct the pattern was found in.
    /// </summary>
    public RegexPatternOrigin Origin { get; }

    /// <summary>
    /// Gets the options the pattern is parsed with, or <see langword="null" /> when they are not
    /// statically determinable. See the remarks on <see cref="RegexPatternSite" /> for why an unresolved
    /// value is never reported as <c>RegexOptions.None</c>.
    /// </summary>
    public RegexOptions? Options { get; }

    /// <summary>
    /// Gets a value indicating whether <see cref="Options" /> carries a resolved value.
    /// </summary>
    public bool AreOptionsKnown => Options.HasValue;

    /// <summary>
    /// Gets the expression the options were read from, or <see langword="null" /> when the located
    /// overload has no options parameter at all and the options are therefore <c>RegexOptions.None</c> by
    /// definition of the API. Together with <see cref="AreOptionsKnown" /> this separates the three
    /// possible states: no options parameter, a resolved one, and one that could not be resolved.
    /// </summary>
    public ExpressionSyntax? OptionsExpression { get; }

    /// <summary>
    /// Gets the attribute argument the pattern sits in, or <see langword="null" /> when the pattern was
    /// found in a call. An attribute argument only accepts a compile time constant, so a rewriter has to
    /// know that this is where it is working.
    /// </summary>
    public AttributeArgumentSyntax? AttributeArgument => PatternLiteral.Parent as AttributeArgumentSyntax;

    /// <inheritdoc />
    public override string ToString()
    {
        var origin = Origin.ToString();
        var options = Options.HasValue ? Options.Value.ToString() : UnknownOptionsText;

        return $"{origin}: \"{Pattern}\" [{options}]";
    }
}
