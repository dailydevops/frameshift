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
}
