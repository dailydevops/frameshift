namespace NetEvolve.FrameShift.Mutations.RegularExpressions;

/// <summary>
/// The construct a regular expression pattern was found in, which is what
/// <see cref="RegexPatternLocator" /> records on every <see cref="RegexPatternSite" /> it produces.
/// </summary>
/// <remarks>
/// The origin is not decoration. It decides where the options of a pattern can come from at all - a
/// constructor and <c>[GeneratedRegex]</c> may carry them, the DataAnnotations attribute never does - and
/// it tells a later rewriter what kind of position the literal sits in, because an attribute argument
/// must stay a compile time constant while a call argument does not.
/// </remarks>
internal enum RegexPatternOrigin
{
    /// <summary>
    /// A constructor of <c>System.Text.RegularExpressions.Regex</c>, meaning <c>new Regex(pattern)</c> and
    /// its overloads taking options and a match timeout. The pattern is the first parameter.
    /// </summary>
    RegexConstructor = 0,

    /// <summary>
    /// A static method of <c>System.Text.RegularExpressions.Regex</c>, meaning <c>IsMatch</c>,
    /// <c>Match</c>, <c>Matches</c>, <c>Replace</c>, <c>Split</c>, <c>Count</c> and
    /// <c>EnumerateMatches</c>. The pattern is the <em>second</em> parameter there, behind the input.
    /// </summary>
    RegexStaticMethod,

    /// <summary>
    /// The <c>System.Text.RegularExpressions.GeneratedRegexAttribute</c>, whose first constructor
    /// parameter is the pattern and whose second one, when the chosen overload has it, is the options.
    /// </summary>
    GeneratedRegex,

    /// <summary>
    /// The <c>System.ComponentModel.DataAnnotations.RegularExpressionAttribute</c>, whose only
    /// constructor parameter is the pattern. It has no options in any overload, so its patterns are
    /// always parsed with <c>RegexOptions.None</c>.
    /// </summary>
    DataAnnotationsRegularExpression,
}
