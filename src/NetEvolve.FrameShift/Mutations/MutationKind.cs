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
    /// Logical and conditional operators, such as <c>&amp;&amp;</c>, <c>||</c>, <c>&amp;</c>, <c>|</c> and
    /// boolean <c>^</c>.
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
    /// Literals of a nullable value type (<c>bool?</c>, a nullable numeric type or <c>char?</c>), moved
    /// between the written value, <see langword="null" /> and the default value of the underlying type.
    /// </summary>
    NullableLiteral,

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
    /// The <see langword="checked"/> and <see langword="unchecked"/> keyword of a checked expression or statement, swapped for
    /// its counterpart. The keyword only decides how arithmetic overflow is handled at run time, so a
    /// test suite that never distinguishes the two cannot tell whether the choice is actually enforced.
    /// </summary>
    CheckedContext,

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
    /// Calls to well known <see cref="string" /> methods, such as <c>StartsWith</c> / <c>EndsWith</c>,
    /// <c>Trim</c> / <c>TrimStart</c> / <c>TrimEnd</c> and <c>IsNullOrEmpty</c> /
    /// <c>IsNullOrWhiteSpace</c>, replaced by a counterpart with a matching overload. A test suite that
    /// never distinguishes a prefix from a suffix check, trims the wrong side, or treats an empty
    /// string the same as a whitespace-only one, cannot tell the mutant from the original.
    /// </summary>
    StringMethod,

    /// <summary>
    /// <c>System.Text.RegularExpressions.RegexOptions</c> arguments, with a flag such as
    /// <c>IgnoreCase</c>, <c>Multiline</c> or <c>Singleline</c> added or removed. The flags change the
    /// pattern grammar and the matching semantics, so an untested flag hides matches the code relies on.
    /// </summary>
    RegexOptions,

    /// <summary>
    /// The zero width assertions of a regular expression pattern, with <c>^</c>, <c>$</c>, <c>\A</c>,
    /// <c>\z</c> and <c>\Z</c> removed and <c>\b</c> swapped for <c>\B</c>. A pattern that is only ever
    /// fed input the anchors already fit matches just as well without them, which is what makes the
    /// anchor untested.
    /// </summary>
    RegexAnchor,

    /// <summary>
    /// The repetition suffixes of a regular expression pattern, with <c>*</c> swapped for <c>+</c>, an
    /// optional <c>?</c> removed, greediness flipped and the bounds of <c>{n,m}</c> shifted. The
    /// boundaries of a repetition are exactly where a test suite tends to supply only the comfortable
    /// input length.
    /// </summary>
    RegexQuantifier,

    /// <summary>
    /// The groups of a regular expression pattern, with a capturing group turned into the non-capturing
    /// <c>(?:</c> and back. Code that reads a group by number or by name breaks when the group stops
    /// capturing, so a test that never looks at the captures cannot tell the two apart.
    /// </summary>
    RegexGroup,

    /// <summary>
    /// The alternations of a regular expression pattern, with one branch removed and the order of two
    /// branches swapped. A branch no test exercises is indistinguishable from a branch that is not there,
    /// and the order of the branches decides which one wins on input both of them match.
    /// </summary>
    RegexAlternation,

    /// <summary>
    /// The character classes of a regular expression pattern: the shorthand classes <c>\d</c>, <c>\w</c>
    /// and <c>\s</c> swapped among themselves and with their negations, a class negated or un-negated,
    /// a range widened by one at either end, a member removed from a set, and <c>.</c> swapped with the
    /// equivalent explicit class <c>[\s\S]</c>. A test suite that only supplies input already inside the
    /// intended class cannot tell it apart from a neighbouring or a wider one.
    /// </summary>
    RegexCharacterClass,

    /// <summary>
    /// The character escapes of a regular expression pattern, with the literal <c>\.</c> turned into the
    /// unescaped <c>.</c>. A literal dot becoming "matches any character" is a real and common defect that
    /// a test suite exercising only dot-shaped input never notices.
    /// </summary>
    RegexEscape,

    /// <summary>
    /// The lookaround assertions of a regular expression pattern, with a positive lookahead or lookbehind
    /// swapped for its negative counterpart and back. The two assert opposite things about the same
    /// position, so a test suite that never supplies input failing the assertion cannot tell them apart.
    /// </summary>
    RegexLookaround,

    /// <summary>
    /// The numbered backreferences of a regular expression pattern, with the referenced capture group
    /// shifted to a neighbouring one. A test suite that never distinguishes the captures cannot tell a
    /// backreference from a mistakenly renumbered one.
    /// </summary>
    RegexBackreference,

    /// <summary>
    /// Well known <c>System.Linq.Enumerable</c> method calls, such as <c>All</c> / <c>Any</c>,
    /// <c>First</c> / <c>FirstOrDefault</c> and <c>OrderBy</c> / <c>OrderByDescending</c>, replaced by
    /// their counterpart. A query that decides correctness through one of these choices has no mutant
    /// pinning it down until the call is renamed.
    /// </summary>
    LinqMethod,

    /// <summary>
    /// Calls to well-known <c>System.Math</c> static methods, replaced by a related method such as
    /// <c>Sin</c> by <c>Cos</c>, <c>Min</c> by <c>Max</c> or <c>Floor</c> by <c>Ceiling</c>, or with
    /// <c>Math.Abs</c> dropped entirely. Picking the wrong function of the same shape is a defect no test
    /// suite exercising only one branch of the computation notices.
    /// </summary>
    MathMethod,

    /// <summary>
    /// The element list of an array or collection initializer and of a collection expression, emptied
    /// when it carries elements and, where provably safe, filled with a single <see langword="default"/> element
    /// when it is empty. An emptied lookup table, default argument list or seed collection is otherwise
    /// indistinguishable from the original to a test suite that never inspects its contents.
    /// </summary>
    CollectionInitializer,

    /// <summary>
    /// Whole statements removed outright: a bare <c>return;</c> inside a <see langword="void" />
    /// returning member, a loop <see langword="break"/> or <see langword="continue"/>, a <see langword="throw"/> statement, and a
    /// standalone invocation of a <see langword="void" /> returning method with no <see langword="ref"/> or
    /// <see langword="out"/> arguments. A test suite that never distinguishes the statement being there from it
    /// being gone cannot tell an early return, a changed loop exit, a silently skipped guard or a
    /// discarded side-effecting call apart from the code that no longer has it.
    /// </summary>
    StatementRemoval,

    /// <summary>
    /// The trailing <c>System.StringComparer</c>, <c>IComparer&lt;T&gt;</c> or
    /// <c>IEqualityComparer&lt;T&gt;</c> argument of an object creation, removed so that the call resolves
    /// to a same-type overload with no comparer at all. A test suite that never asserts on comparer
    /// sensitive behaviour cannot tell whether a non-default comparer was needed in the first place.
    /// </summary>
    OptionalArgumentRemoval,

    /// <summary>
    /// A parenthesized additive sub-expression (<c>+</c>, <c>-</c>) reassociated across an enclosing
    /// multiplicative expression (<c>*</c>, <c>/</c>, <c>%</c>), such as <c>(a + b) * c</c> becoming
    /// <c>a + b * c</c>. Parentheses that override the default operator precedence are exactly the
    /// kind of thing a developer gets wrong once and a test suite never notices, because the
    /// unparenthesized reading of the same operators is a different, equally plausible-looking
    /// expression.
    /// </summary>
    Parenthesization,
}
