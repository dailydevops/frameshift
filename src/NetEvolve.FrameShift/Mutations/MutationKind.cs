namespace NetEvolve.FrameShift.Mutations;

/// <summary>
/// The families of mutation operators supported by FrameShift.
/// </summary>
internal enum MutationKind
{
    /// <summary>
    /// Binary arithmetic operators, such as <c>+</c>, <c>-</c>, <c>*</c>, <c>/</c> and <c>%</c>.
    /// </summary>
    ArithmeticOperator,

    /// <summary>
    /// Compound arithmetic assignments, such as <c>+=</c>, <c>-=</c>, <c>*=</c>, <c>/=</c> and <c>%=</c>.
    /// </summary>
    ArithmeticAssignment,

    /// <summary>
    /// Relational operators, such as <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c> and <c>&gt;=</c>.
    /// </summary>
    RelationalOperator,

    /// <summary>
    /// Equality operators, such as <c>==</c> and <c>!=</c>.
    /// </summary>
    EqualityOperator,

    /// <summary>
    /// Logical and conditional operators, such as <c>&amp;&amp;</c>, <c>||</c>, <c>&amp;</c> and <c>|</c>.
    /// </summary>
    LogicalOperator,

    /// <summary>
    /// The logical negation operator <c>!</c>, either added or removed.
    /// </summary>
    LogicalNegation,

    /// <summary>
    /// Boolean literals, replacing <see langword="true" /> with <see langword="false" /> and vice versa.
    /// </summary>
    BooleanLiteral,

    /// <summary>
    /// Literals of type <c>bool?</c>, moved between the three states of three valued logic, so that
    /// <see langword="true" /> and <see langword="false" /> become <see langword="null" /> and
    /// <see langword="null" /> becomes both of them.
    /// </summary>
    NullableBooleanLiteral,

    /// <summary>
    /// Numeric literals, replaced by a neighbouring or boundary value.
    /// </summary>
    NumericLiteral,

    /// <summary>
    /// String literals, replaced by a different string value.
    /// </summary>
    StringLiteral,

    /// <summary>
    /// The null-coalescing operators <c>??</c> and <c>??=</c>.
    /// </summary>
    NullCoalescing,

    /// <summary>
    /// Conditional expressions, such as the ternary <c>?:</c> operator.
    /// </summary>
    ConditionalExpression,

    /// <summary>
    /// Unary operators, such as the unary <c>-</c>, <c>+</c> and <c>~</c>.
    /// </summary>
    UnaryOperator,

    /// <summary>
    /// The increment operator <c>++</c>, in prefix and postfix form.
    /// </summary>
    Increment,

    /// <summary>
    /// The decrement operator <c>--</c>, in prefix and postfix form.
    /// </summary>
    Decrement,

    /// <summary>
    /// Bitwise operators, such as <c>&amp;</c>, <c>|</c> and <c>^</c>.
    /// </summary>
    BitwiseOperator,

    /// <summary>
    /// Shift operators, such as <c>&lt;&lt;</c> and <c>&gt;&gt;</c>.
    /// </summary>
    ShiftOperator,

    /// <summary>
    /// <c>System.StringComparison</c> arguments, replaced by a different member of the same enumeration,
    /// for example <c>Ordinal</c> by <c>OrdinalIgnoreCase</c>. A test suite that never distinguishes the
    /// members cannot tell a case sensitive comparison from a case insensitive or culture aware one.
    /// </summary>
    StringComparison,

    /// <summary>
    /// <c>System.StringComparer</c> instances, replaced by a different well known comparer, for example
    /// <c>StringComparer.Ordinal</c> by <c>StringComparer.OrdinalIgnoreCase</c>. Dictionaries, sets and
    /// sorts silently change their notion of key equality and ordering when the wrong comparer is used.
    /// </summary>
    StringComparer,

    /// <summary>
    /// <c>System.Globalization.CultureInfo</c> references, replaced by a different culture, for example
    /// <c>CultureInfo.InvariantCulture</c> by <c>CultureInfo.CurrentCulture</c>. Culture dependent parsing
    /// and comparison is a classic source of defects that only surface on a machine with another locale.
    /// </summary>
    CultureInfo,

    /// <summary>
    /// <c>System.IFormatProvider</c> arguments of formatting and parsing calls, replaced by another
    /// provider or omitted. Formatting without an explicit provider makes numbers, dates and money
    /// depend on ambient state, so the produced text differs between environments.
    /// </summary>
    FormatProvider,

    /// <summary>
    /// Case conversion calls, such as <c>ToUpper</c>, <c>ToLower</c>, <c>ToUpperInvariant</c> and
    /// <c>ToLowerInvariant</c>, replaced by their counterpart or by their invariant and culture aware
    /// form. Normalising with the wrong case or the wrong culture breaks lookups for specific inputs.
    /// </summary>
    CaseConversion,

    /// <summary>
    /// <c>System.Text.RegularExpressions.RegexOptions</c> arguments, with a flag such as
    /// <c>IgnoreCase</c>, <c>Multiline</c> or <c>Singleline</c> added or removed. The flags change the
    /// pattern grammar and the matching semantics, so an untested flag hides matches the code relies on.
    /// </summary>
    RegexOptions,
}
