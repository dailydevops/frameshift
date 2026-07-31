namespace NetEvolve.FrameShift.Mutations.RegularExpressions;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>
/// Splits a .NET regular expression pattern into <see cref="RegexToken" /> instances, so that a mutation
/// operator can rewrite a single construct instead of doing string surgery on the raw pattern.
/// </summary>
/// <remarks>
/// <para>
/// The tokenizer is deliberately free of Roslyn: pattern text plus <see cref="RegexOptions" /> go in,
/// tokens come out. It is a lexer, which means it checks the structure of the pattern - balanced groups
/// and classes, complete escapes, well formed quantifiers - and it reports a malformed pattern. It
/// deliberately does not resolve the meaning of a construct, so a pattern whose only problem is semantic
/// still tokenizes: an undefined backreference such as <c>\1</c> without a first group, an unknown
/// Unicode property such as <c>\p{Nope}</c> and a reversed range such as <c>[z-a]</c> all produce tokens
/// even though <see cref="Regex" /> rejects them. Deciding whether a pattern is a legal regular
/// expression is the job of a validity check, not of the lexer.
/// </para>
/// <para>
/// The one place where the lexer has to look beyond the current construct is a numbered digit escape,
/// because <c>\10</c> is group ten in a pattern with ten groups but the single character U+0008 in a
/// pattern with one group. .NET reads the whole digit run and takes it as a backreference when it names a
/// group the pattern defines; otherwise it re-reads the run as an octal escape of at most three octal
/// digits and leaves any further digit a literal. A run of one digit is always a backreference. The
/// decision therefore happens once the whole pattern has been scanned and the captures are known.
/// </para>
/// <para>
/// The options are honoured, including their scope:
/// <see cref="RegexOptions.IgnorePatternWhitespace" /> turns unescaped whitespace outside a character
/// class into <see cref="RegexTokenKind.WhitespaceIgnored" /> and an unescaped <c>#</c> into a
/// <see cref="RegexTokenKind.Comment" />, an inline <c>(?x)</c> switches that grammar on for the
/// remainder of the enclosing group, and <c>(?-x:...)</c> switches it off for that group alone.
/// </para>
/// </remarks>
internal static class RegexPatternTokenizer
{
    /// <summary>
    /// Tokenizes <paramref name="pattern" />.
    /// </summary>
    /// <param name="pattern">The regular expression pattern, without any delimiters.</param>
    /// <param name="options">The options the pattern is compiled with, which change its grammar.</param>
    /// <returns>The tokens of the pattern, in order and covering it completely.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="pattern" /> is malformed.</exception>
    public static ImmutableArray<RegexToken> Tokenize(string pattern, RegexOptions options)
    {
        if (!TryTokenize(pattern, options, out var tokens, out var errorIndex, out var error))
        {
            throw new ArgumentException(
                $"The regular expression pattern is malformed at index {errorIndex}: {error}",
                nameof(pattern)
            );
        }

        return tokens;
    }

    /// <summary>
    /// Tries to tokenize <paramref name="pattern" /> and reports where and why a malformed pattern was
    /// rejected instead of throwing.
    /// </summary>
    /// <param name="pattern">The regular expression pattern, without any delimiters.</param>
    /// <param name="options">The options the pattern is compiled with, which change its grammar.</param>
    /// <param name="tokens">
    /// The tokens of the pattern on success, or an empty array if the pattern is malformed.
    /// </param>
    /// <param name="errorIndex">
    /// The zero based index of the construct that could not be tokenized, or <c>-1</c> on success. For an
    /// unclosed construct it is the index at which the construct starts, not the end of the pattern.
    /// </param>
    /// <param name="error">
    /// The reason the pattern was rejected, or <see langword="null" /> on success.
    /// </param>
    /// <returns><see langword="true" /> if the pattern was tokenized; otherwise <see langword="false" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern" /> is <see langword="null" />.</exception>
    public static bool TryTokenize(
        string pattern,
        RegexOptions options,
        out ImmutableArray<RegexToken> tokens,
        out int errorIndex,
        out string? error
    )
    {
        if (pattern is null)
        {
            throw new ArgumentNullException(nameof(pattern));
        }

        var scanner = new Scanner(pattern, options);
        if (!scanner.Run())
        {
            tokens = ImmutableArray<RegexToken>.Empty;
            errorIndex = scanner.ErrorIndex;
            error = scanner.Error;

            return false;
        }

        tokens = scanner.Tokens;
        errorIndex = -1;
        error = null;

        return true;
    }

    /// <summary>
    /// The mutable state of a single tokenizer run. One instance is created per call, which is what keeps
    /// the tokenizer itself stateless and therefore usable from an analyzer.
    /// </summary>
    private sealed class Scanner
    {
        private readonly string _pattern;
        private readonly ImmutableArray<RegexToken>.Builder _tokens;
        private readonly Stack<GroupFrame> _groups;
        private readonly Stack<ClassFrame> _classes;
        private readonly HashSet<string> _captureNames;
        private readonly HashSet<int> _captureNumbers;
        private int _unnamedCaptureCount;
        private int _conditionalOpenIndex;
        private int _index;
        private int _errorIndex;
        private string? _error;

        /// <summary>
        /// Initializes a new instance of the <see cref="Scanner" /> class.
        /// </summary>
        /// <param name="pattern">The pattern to tokenize.</param>
        /// <param name="options">The options the pattern is compiled with.</param>
        public Scanner(string pattern, RegexOptions options)
        {
            _pattern = pattern;
            _tokens = ImmutableArray.CreateBuilder<RegexToken>();
            _groups = new Stack<GroupFrame>();
            _classes = new Stack<ClassFrame>();
            _captureNames = new HashSet<string>(StringComparer.Ordinal);
            _captureNumbers = new HashSet<int>();
            _conditionalOpenIndex = -1;
            _errorIndex = -1;

            _groups.Push(new GroupFrame(options, openIndex: -1));
        }

        /// <summary>
        /// Gets the index the run failed at, or <c>-1</c> if it succeeded.
        /// </summary>
        public int ErrorIndex => _errorIndex;

        /// <summary>
        /// Gets the reason the run failed, or <see langword="null" /> if it succeeded.
        /// </summary>
        public string? Error => _error;

        /// <summary>
        /// Gets the tokens collected by a successful run.
        /// </summary>
        public ImmutableArray<RegexToken> Tokens => _tokens.ToImmutable();

        private GroupFrame Group => _groups.Peek();

        private ClassFrame Class => _classes.Peek();

        private bool InClass => _classes.Count > 0;

        private bool IgnoreWhitespace => (Group.Options & RegexOptions.IgnorePatternWhitespace) != RegexOptions.None;

        private char Current => _pattern[_index];

        private int Remaining => _pattern.Length - _index;

        /// <summary>
        /// Tokenizes the whole pattern.
        /// </summary>
        /// <returns>
        /// <see langword="true" /> if the pattern is well formed; otherwise <see langword="false" />.
        /// </returns>
        public bool Run()
        {
            while (_index < _pattern.Length)
            {
                var scanned = InClass ? ScanInsideClass() : ScanOutsideClass();
                if (!scanned)
                {
                    return false;
                }
            }

            return Complete();
        }

        /// <summary>
        /// Verifies that no construct is left open and resolves the numbered backreferences.
        /// </summary>
        /// <returns><see langword="true" /> if every construct is closed; otherwise <see langword="false" />.</returns>
        private bool Complete()
        {
            if (InClass)
            {
                return Fail(Class.OpenIndex, "The character class opened here is never closed by ']'.");
            }

            if (_groups.Count > 1)
            {
                return Fail(Group.OpenIndex, "The group opened here is never closed by ')'.");
            }

            ResolveNumberedBackreferences();

            return true;
        }

        /// <summary>
        /// Scans one construct outside a character class.
        /// </summary>
        /// <returns>
        /// <see langword="true" /> if the construct is well formed; otherwise <see langword="false" />.
        /// </returns>
        private bool ScanOutsideClass()
        {
            var current = Current;
            switch (current)
            {
                case '\\':
                    return ScanBackslash();
                case '[':
                    return ScanCharacterClassOpen();
                case '(':
                    return ScanGroupOpen();
                case ')':
                    return ScanGroupClose();
                case '|':
                    return ScanAlternation();
                case '^':
                case '$':
                    return AddAtom(RegexTokenKind.Anchor, 1);
                case '*':
                case '+':
                case '?':
                    return ScanSimpleQuantifier();
                case '{':
                    return ScanBraceQuantifier();
                case '#' when IgnoreWhitespace:
                    return ScanLineComment();
                default:
                    if (IgnoreWhitespace && IsIgnorableWhitespace(current))
                    {
                        return ScanIgnoredWhitespace();
                    }

                    return AddAtom(RegexTokenKind.Literal, 1);
            }
        }

        /// <summary>
        /// Scans one member of a character class, its nested subtraction class or its closing bracket.
        /// </summary>
        /// <returns>
        /// <see langword="true" /> if the construct is well formed; otherwise <see langword="false" />.
        /// </returns>
        private bool ScanInsideClass()
        {
            if (Class.SubtractionApplied && Current != ']')
            {
                return Fail(
                    _index,
                    "A character class subtraction has to be the last element of its class, so nothing may "
                        + "follow it but ']'."
                );
            }

            switch (Current)
            {
                case '\\':
                    return ScanBackslash();
                case ']':
                    return ScanCharacterClassClose();
                case '-':
                    return ScanCharacterClassDash();
                default:
                    // Inside a class '[' is an ordinary member, which is why '[[a]' matches '[' and 'a', and
                    // whitespace as well as '#' stay members even under IgnorePatternWhitespace.
                    return AddClassMember(RegexTokenKind.CharacterClassContent, 1);
            }
        }

        /// <summary>
        /// Scans the alternation operator, which starts a new branch and therefore a new repeatable unit.
        /// </summary>
        /// <returns>Always <see langword="true" />.</returns>
        private bool ScanAlternation()
        {
            Add(RegexTokenKind.Alternation, 1);

            var group = Group;
            group.AtomPresent = false;
            group.QuantifierApplied = false;

            return true;
        }

        /// <summary>
        /// Scans <c>*</c>, <c>+</c> or <c>?</c> together with a lazy <c>?</c> that follows it, or the lazy
        /// <c>?</c> of a quantifier that only blanks separate it from.
        /// </summary>
        /// <returns>
        /// <see langword="true" /> if the quantifier has something to repeat; otherwise <see langword="false" />.
        /// </returns>
        /// <remarks>
        /// .NET runs its blank skipper between a quantifier and its lazy <c>?</c>, so <c>a*(?#c)?</c> is legal
        /// under every option and <c>a * ?</c> is legal under
        /// <see cref="RegexOptions.IgnorePatternWhitespace" />. A second real quantifier behind the same blanks
        /// stays a nested quantifier and is still rejected.
        /// </remarks>
        private bool ScanSimpleQuantifier()
        {
            var current = Current;
            if (current == '?' && Group.LazyMarkerAvailable)
            {
                Add(RegexTokenKind.Quantifier, 1);

                return true;
            }

            if (!CanQuantify(current))
            {
                return false;
            }

            var length = Remaining > 1 && _pattern[_index + 1] == '?' ? 2 : 1;
            Add(RegexTokenKind.Quantifier, length);

            var group = Group;
            group.QuantifierApplied = true;
            group.LazyMarkerAvailable = length == 1;

            return true;
        }

        /// <summary>
        /// Scans a <c>{n}</c>, <c>{n,}</c> or <c>{n,m}</c> quantifier. A brace that starts no quantifier is
        /// a literal, which is why <c>a{</c>, <c>a{}</c> and <c>a{x}</c> are legal patterns.
        /// </summary>
        /// <returns>
        /// <see langword="true" /> if the quantifier is well formed; otherwise <see langword="false" />.
        /// </returns>
        private bool ScanBraceQuantifier()
        {
            if (!TryMeasureBraceQuantifier(out var length, out var minimum, out var maximum))
            {
                return AddAtom(RegexTokenKind.Literal, 1);
            }

            if (!CanQuantify('{'))
            {
                return false;
            }

            if (minimum > int.MaxValue || maximum > int.MaxValue)
            {
                return Fail(_index, "A quantifier bound must not exceed Int32.MaxValue.");
            }

            if (maximum >= 0 && maximum < minimum)
            {
                return Fail(_index, "The quantifier specifies a maximum that is smaller than its minimum.");
            }

            var lazyConsumed = _pattern[_index + length - 1] == '?';
            Add(RegexTokenKind.Quantifier, length);

            var group = Group;
            group.QuantifierApplied = true;
            group.LazyMarkerAvailable = !lazyConsumed;

            return true;
        }

        /// <summary>
        /// Measures a brace quantifier at the current index.
        /// </summary>
        /// <param name="length">The length of the quantifier including a lazy <c>?</c>.</param>
        /// <param name="minimum">The lower bound of the quantifier.</param>
        /// <param name="maximum">The upper bound, or <c>-1</c> if the quantifier is unbounded.</param>
        /// <returns><see langword="true" /> if a quantifier starts here; otherwise <see langword="false" />.</returns>
        private bool TryMeasureBraceQuantifier(out int length, out long minimum, out long maximum)
        {
            length = 0;
            maximum = -1;
            var position = _index + 1;

            if (!TryReadDecimal(ref position, out minimum))
            {
                return false;
            }

            if (position < _pattern.Length && _pattern[position] == ',')
            {
                position++;
                if (
                    position < _pattern.Length
                    && _pattern[position] != '}'
                    && !TryReadDecimal(ref position, out maximum)
                )
                {
                    return false;
                }
            }

            if (position >= _pattern.Length || _pattern[position] != '}')
            {
                return false;
            }

            position++;
            if (position < _pattern.Length && _pattern[position] == '?')
            {
                position++;
            }

            length = position - _index;

            return true;
        }

        /// <summary>
        /// Reads a decimal number, clamping the result just above <see cref="int.MaxValue" /> so that an
        /// absurdly long run of digits reports an out of range bound instead of overflowing.
        /// </summary>
        /// <param name="position">The index to read at, advanced past the digits.</param>
        /// <param name="value">The number that was read.</param>
        /// <returns>
        /// <see langword="true" /> if at least one digit was read; otherwise <see langword="false" />.
        /// </returns>
        private bool TryReadDecimal(ref int position, out long value)
        {
            var start = position;
            value = 0;

            while (position < _pattern.Length && _pattern[position] is >= '0' and <= '9')
            {
                if (value <= int.MaxValue)
                {
                    value = (value * 10) + (_pattern[position] - '0');
                }

                position++;
            }

            return position > start;
        }

        /// <summary>
        /// Verifies that a quantifier may appear at the current index.
        /// </summary>
        /// <param name="quantifier">The character the quantifier starts with, used in the reported reason.</param>
        /// <returns>
        /// <see langword="true" /> if a quantifier is allowed here; otherwise <see langword="false" />.
        /// </returns>
        private bool CanQuantify(char quantifier)
        {
            var group = Group;
            if (group.QuantifierApplied)
            {
                return Fail(
                    _index,
                    $"The quantifier '{quantifier}' follows another quantifier, which is a nested quantifier."
                );
            }

            if (!group.AtomPresent)
            {
                return Fail(_index, $"The quantifier '{quantifier}' follows nothing that could be repeated.");
            }

            return true;
        }

        /// <summary>
        /// Scans a run of whitespace that <see cref="RegexOptions.IgnorePatternWhitespace" /> removes.
        /// </summary>
        /// <returns>Always <see langword="true" />.</returns>
        private bool ScanIgnoredWhitespace()
        {
            var length = 1;
            while (length < Remaining && IsIgnorableWhitespace(_pattern[_index + length]))
            {
                length++;
            }

            Add(RegexTokenKind.WhitespaceIgnored, length);

            return true;
        }

        /// <summary>
        /// Decides whether <see cref="RegexOptions.IgnorePatternWhitespace" /> removes a character.
        /// </summary>
        /// <param name="candidate">The character to classify.</param>
        /// <returns><see langword="true" /> if the character is removed from the pattern.</returns>
        /// <remarks>
        /// .NET drops exactly five characters - tab, line feed, form feed, carriage return and space - and
        /// nothing else. <see cref="char.IsWhiteSpace(char)" /> is a strictly wider set that also holds the
        /// vertical tab and the Unicode separators, and every character it adds is one the pattern really has
        /// to match, so asking it would silently drop a member of the language the pattern describes.
        /// </remarks>
        private static bool IsIgnorableWhitespace(char candidate) => candidate is '\t' or '\n' or '\f' or '\r' or ' ';

        /// <summary>
        /// Scans a <c>#</c> comment, which under <see cref="RegexOptions.IgnorePatternWhitespace" /> runs to
        /// the end of the line and therefore swallows every construct on it.
        /// </summary>
        /// <returns>Always <see langword="true" />.</returns>
        private bool ScanLineComment()
        {
            var length = 1;
            while (length < Remaining && _pattern[_index + length] != '\n')
            {
                length++;
            }

            Add(RegexTokenKind.Comment, length);

            return true;
        }

        /// <summary>
        /// Scans <c>[</c> or <c>[^</c>, including the leading <c>]</c> that is a member rather than the end
        /// of the class, as in <c>[]]</c> and <c>[^]]</c>.
        /// </summary>
        /// <returns>Always <see langword="true" />.</returns>
        private bool ScanCharacterClassOpen()
        {
            var openIndex = _index;
            var length = Remaining > 1 && _pattern[_index + 1] == '^' ? 2 : 1;

            Add(RegexTokenKind.CharacterClassOpen, length);
            _classes.Push(new ClassFrame(openIndex));

            if (_index < _pattern.Length && Current == ']')
            {
                return AddClassMember(RegexTokenKind.CharacterClassContent, 1);
            }

            return true;
        }

        /// <summary>
        /// Scans the <c>]</c> that closes a character class or its nested subtraction class.
        /// </summary>
        /// <returns>Always <see langword="true" />.</returns>
        private bool ScanCharacterClassClose()
        {
            _ = _classes.Pop();
            Add(RegexTokenKind.CharacterClassClose, 1);

            if (!InClass)
            {
                MarkAtom();

                return true;
            }

            var characterClass = Class;
            characterClass.MemberPending = false;
            characterClass.AwaitingRangeEnd = false;

            // A class only ever nests as the operand of a subtraction, so landing back inside an enclosing
            // class means the subtraction of that class has just been read and nothing may follow it.
            characterClass.SubtractionApplied = true;

            return true;
        }

        /// <summary>
        /// Decides which of the three roles a <c>-</c> inside a character class plays: it subtracts a nested
        /// class, it separates the two ends of a range, or it is an ordinary member.
        /// </summary>
        /// <returns>
        /// <see langword="true" /> if the construct is well formed; otherwise <see langword="false" />.
        /// </returns>
        /// <remarks>
        /// A <c>-[</c> only subtracts when the class already holds a member to subtract from, and neither the
        /// opening bracket nor the negating <c>^</c> counts as one. That is why <c>[-[a]</c> is a complete class
        /// of <c>-</c>, <c>[</c> and <c>a</c> rather than an unterminated subtraction, while <c>[]-[a]]</c> and
        /// <c>[^]-[a]]</c> are subtractions: their leading <c>]</c> is a member.
        /// </remarks>
        private bool ScanCharacterClassDash()
        {
            var characterClass = Class;
            if (characterClass.MemberSeen && Remaining > 1 && _pattern[_index + 1] == '[')
            {
                Add(RegexTokenKind.CharacterClassSubtraction, 1);
                characterClass.MemberPending = false;
                characterClass.AwaitingRangeEnd = false;

                return ScanCharacterClassOpen();
            }

            if (characterClass.MemberPending && Remaining > 1 && _pattern[_index + 1] != ']')
            {
                Add(RegexTokenKind.CharacterClassRange, 1);
                characterClass.MemberPending = false;
                characterClass.AwaitingRangeEnd = true;

                return true;
            }

            return AddClassMember(RegexTokenKind.CharacterClassContent, 1);
        }

        /// <summary>
        /// Scans a backslash escape, honouring the character class rules: <c>\b</c> is the backspace instead
        /// of a word boundary, a digit escape is octal instead of a backreference, and the anchors as well
        /// as <c>\k</c> are not allowed at all.
        /// </summary>
        /// <returns><see langword="true" /> if the escape is well formed; otherwise <see langword="false" />.</returns>
        private bool ScanBackslash()
        {
            var start = _index;
            if (Remaining < 2)
            {
                return Fail(start, "The pattern ends with a single backslash, so its escape sequence is incomplete.");
            }

            var escaped = _pattern[start + 1];
            switch (escaped)
            {
                case 'd':
                case 'D':
                case 'w':
                case 'W':
                case 's':
                case 'S':
                case 'a':
                case 'e':
                case 'f':
                case 'n':
                case 'r':
                case 't':
                case 'v':
                    return AddEscape(2);
                case 'b':
                    return InClass ? AddEscape(2) : AddAtom(RegexTokenKind.Anchor, 2);
                case 'A':
                case 'z':
                case 'Z':
                case 'G':
                case 'B':
                    return InClass ? FailClassEscape(escaped) : AddAtom(RegexTokenKind.Anchor, 2);
                case 'p':
                case 'P':
                    return ScanUnicodeCategoryEscape();
                case 'x':
                    return ScanHexadecimalEscape(2);
                case 'u':
                    return ScanHexadecimalEscape(4);
                case 'c':
                    return ScanControlEscape();
                case 'k':
                    return InClass ? FailClassEscape('k') : ScanNamedBackreference();
                default:
                    return ScanOtherEscape(escaped);
            }
        }

        /// <summary>
        /// Scans an escape that is neither an anchor, nor a class shorthand, nor a character escape with its
        /// own syntax: a digit escape, or a single escaped character such as <c>\-</c> and <c>\\</c>.
        /// </summary>
        /// <param name="escaped">The character behind the backslash.</param>
        /// <returns><see langword="true" /> if the escape is well formed; otherwise <see langword="false" />.</returns>
        private bool ScanOtherEscape(char escaped)
        {
            if (escaped is >= '0' and <= '9')
            {
                return ScanDigitEscape(escaped);
            }

            if (IsWordCharacter(escaped))
            {
                // .NET only allows an escaped word character when it spells one of its own escapes, so this
                // rejects '\Q' and '\E' as well: neither has a meaning in a .NET pattern.
                return Fail(_index, $"The escape sequence '\\{escaped}' is not recognized by .NET.");
            }

            return AddEscape(2);
        }

        /// <summary>
        /// Scans <c>\p{...}</c> or <c>\P{...}</c>. Whether the property exists is a semantic question and
        /// therefore not decided here.
        /// </summary>
        /// <returns><see langword="true" /> if the escape is well formed; otherwise <see langword="false" />.</returns>
        private bool ScanUnicodeCategoryEscape()
        {
            var start = _index;
            var escaped = _pattern[start + 1];

            if (Remaining < 3 || _pattern[start + 2] != '{')
            {
                return Fail(
                    start,
                    $"The '\\{escaped}' escape requires a property name in braces, e.g. '\\{escaped}{{Lu}}'."
                );
            }

            var close = _pattern.IndexOf('}', start + 3);
            if (close < 0)
            {
                return Fail(start, $"The '\\{escaped}{{' escape is never closed by '}}'.");
            }

            return AddEscape(close - start + 1);
        }

        /// <summary>
        /// Scans <c>\xNN</c> or <c>\uNNNN</c>, both of which require exactly as many hexadecimal digits as
        /// their form prescribes.
        /// </summary>
        /// <param name="digits">The number of hexadecimal digits the escape requires.</param>
        /// <returns><see langword="true" /> if the escape is well formed; otherwise <see langword="false" />.</returns>
        private bool ScanHexadecimalEscape(int digits)
        {
            var start = _index;
            var escaped = _pattern[start + 1];

            if (Remaining < digits + 2 || !AreHexadecimal(start + 2, digits))
            {
                return Fail(start, $"The '\\{escaped}' escape requires exactly {digits} hexadecimal digits.");
            }

            return AddEscape(digits + 2);
        }

        /// <summary>
        /// Scans a <c>\cX</c> control character escape.
        /// </summary>
        /// <returns><see langword="true" /> if the escape is well formed; otherwise <see langword="false" />.</returns>
        private bool ScanControlEscape()
        {
            var start = _index;
            if (Remaining < 3 || !IsControlCharacter(_pattern[start + 2]))
            {
                return Fail(start, "The '\\c' escape requires a control character, e.g. '\\cA'.");
            }

            return AddEscape(3);
        }

        /// <summary>
        /// Scans a digit escape, which is octal inside a character class and a numbered backreference
        /// outside one.
        /// </summary>
        /// <param name="escaped">The digit behind the backslash.</param>
        /// <returns><see langword="true" /> if the escape is well formed; otherwise <see langword="false" />.</returns>
        private bool ScanDigitEscape(char escaped)
        {
            if (InClass && escaped > '7')
            {
                return Fail(
                    _index,
                    $"The digit escape '\\{escaped}' is not octal and therefore not allowed inside a character class."
                );
            }

            if (InClass || escaped == '0')
            {
                var length = 2;
                while (length < 4 && length < Remaining && _pattern[_index + length] is >= '0' and <= '7')
                {
                    length++;
                }

                return AddEscape(length);
            }

            var digits = 2;
            while (digits < Remaining && _pattern[_index + digits] is >= '0' and <= '9')
            {
                digits++;
            }

            return AddAtom(RegexTokenKind.Backreference, digits);
        }

        /// <summary>
        /// Scans a named backreference, either <c>\k&lt;name&gt;</c> or <c>\k'name'</c>.
        /// </summary>
        /// <returns>
        /// <see langword="true" /> if the backreference is well formed; otherwise <see langword="false" />.
        /// </returns>
        private bool ScanNamedBackreference()
        {
            var start = _index;
            if (!TryMeasureBracketedName(start + 2, out var close, out var name))
            {
                return Fail(start, "The '\\k' backreference requires a group name in '<...>' or in \"'...'\".");
            }

            if (!IsGroupName(name))
            {
                return Fail(start + 3, $"'{name}' is not a valid group name.");
            }

            return AddAtom(RegexTokenKind.Backreference, close - start + 1);
        }

        /// <summary>
        /// Scans an opening parenthesis and everything that decides what it opens.
        /// </summary>
        /// <returns>
        /// <see langword="true" /> if the construct is well formed; otherwise <see langword="false" />.
        /// </returns>
        private bool ScanGroupOpen()
        {
            var start = _index;
            if (Remaining < 2 || _pattern[start + 1] != '?')
            {
                return ScanCapturingGroup();
            }

            var marker = Remaining > 2 ? _pattern[start + 2] : '\0';
            switch (marker)
            {
                case '#':
                    return ScanInlineComment();
                case ':':
                case '>':
                    return OpenGroup(RegexTokenKind.GroupOpen, 3, Group.Options);
                case '=':
                case '!':
                    return OpenGroup(RegexTokenKind.Lookaround, 3, Group.Options);
                case '\'':
                    return ScanNamedGroup('\'');
                case '<':
                    return ScanAngleGroup();
                case '(':
                    // A conditional '(?(...)yes|no)': the '(' of the condition is shared with the construct,
                    // so '(?' opens the conditional and the condition is tokenized as the group that follows.
                    _conditionalOpenIndex = start;

                    return OpenGroup(RegexTokenKind.GroupOpen, 2, Group.Options);
                default:
                    return ScanOptions();
            }
        }

        /// <summary>
        /// Scans a plain <c>(</c>, which captures unless <see cref="RegexOptions.ExplicitCapture" /> is in
        /// effect or the group is the condition of a conditional construct.
        /// </summary>
        /// <returns>Always <see langword="true" />.</returns>
        private bool ScanCapturingGroup()
        {
            var isCondition = _conditionalOpenIndex == _index - 2;
            if (!isCondition && (Group.Options & RegexOptions.ExplicitCapture) == RegexOptions.None)
            {
                _unnamedCaptureCount++;
            }

            return OpenGroup(RegexTokenKind.GroupOpen, 1, Group.Options);
        }

        /// <summary>
        /// Scans a construct that starts with <c>(?&lt;</c>, which is either a lookbehind or a named group.
        /// </summary>
        /// <returns>
        /// <see langword="true" /> if the construct is well formed; otherwise <see langword="false" />.
        /// </returns>
        private bool ScanAngleGroup()
        {
            var marker = Remaining > 3 ? _pattern[_index + 3] : '\0';
            if (marker is '=' or '!')
            {
                return OpenGroup(RegexTokenKind.Lookaround, 4, Group.Options);
            }

            return ScanNamedGroup('<');
        }

        /// <summary>
        /// Scans a named group <c>(?&lt;name&gt;</c> or <c>(?'name'</c>, including the balancing forms
        /// <c>(?&lt;close-open&gt;</c> and <c>(?&lt;-open&gt;</c>.
        /// </summary>
        /// <param name="opener">The character that opens the name.</param>
        /// <returns><see langword="true" /> if the group is well formed; otherwise <see langword="false" />.</returns>
        private bool ScanNamedGroup(char opener)
        {
            var start = _index;
            var closer = opener == '<' ? '>' : '\'';
            var close = _pattern.IndexOf(closer, start + 3);

            if (close < 0)
            {
                var expected = closer == '>' ? "'>'" : "a single quote";

                return Fail(start, $"The named group opened here is never closed by {expected}.");
            }

            var name = _pattern.Substring(start + 3, close - start - 3);
            if (!TryRegisterGroupName(name, start + 3))
            {
                return false;
            }

            return OpenGroup(RegexTokenKind.GroupOpen, close - start + 1, Group.Options);
        }

        /// <summary>
        /// Validates the name of a named or balancing group and records the capture it defines.
        /// </summary>
        /// <param name="name">The text between the name delimiters.</param>
        /// <param name="index">The index the name starts at, used in the reported reason.</param>
        /// <returns><see langword="true" /> if the name is valid; otherwise <see langword="false" />.</returns>
        private bool TryRegisterGroupName(string name, int index)
        {
            var separator = name.IndexOf('-');
            var first = separator < 0 ? name : name.Substring(0, separator);
            var second = separator < 0 ? null : name.Substring(separator + 1);

            if (separator == 0)
            {
                // '(?<-open>)' only pops a capture, so it defines no name of its own.
                first = string.Empty;
            }

            if ((first.Length > 0 && !IsGroupName(first)) || (second is not null && !IsGroupName(second)))
            {
                return Fail(index, $"'{name}' is not a valid group name.");
            }

            if (first.Length == 0 && second is null)
            {
                return Fail(index, "A named group requires a name.");
            }

            if (first.Length > 0)
            {
                RegisterCapture(first);
            }

            return true;
        }

        /// <summary>
        /// Records a capture the pattern defines, which is what resolves a numbered backreference later on.
        /// </summary>
        /// <param name="name">The valid name of the capture.</param>
        private void RegisterCapture(string name)
        {
            if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                _ = _captureNumbers.Add(number);

                return;
            }

            _ = _captureNames.Add(name);
        }

        /// <summary>
        /// Scans a <c>(?#...)</c> comment group.
        /// </summary>
        /// <returns><see langword="true" /> if the comment is closed; otherwise <see langword="false" />.</returns>
        private bool ScanInlineComment()
        {
            var start = _index;
            var close = _pattern.IndexOf(')', start + 3);

            if (close < 0)
            {
                return Fail(start, "The '(?#' comment is never closed by ')'.");
            }

            Add(RegexTokenKind.Comment, close - start + 1);

            return true;
        }

        /// <summary>
        /// Scans an inline options construct, either the standalone <c>(?imnsx-imnsx)</c> that changes the
        /// options for the remainder of the enclosing group or the scoped <c>(?imnsx-imnsx:</c> that changes
        /// them for the group it opens.
        /// </summary>
        /// <returns>
        /// <see langword="true" /> if the construct is well formed; otherwise <see langword="false" />.
        /// </returns>
        private bool ScanOptions()
        {
            var start = _index;
            var position = start + 2;
            var options = Group.Options;
            var negated = false;

            while (position < _pattern.Length && TryApplyOption(_pattern[position], ref negated, ref options))
            {
                position++;
            }

            if (position == start + 2 || position >= _pattern.Length)
            {
                return Fail(start, "The '(?' is followed by an unrecognized grouping construct.");
            }

            if (_pattern[position] == ':')
            {
                return OpenGroup(RegexTokenKind.GroupOpen, position - start + 1, options);
            }

            if (_pattern[position] != ')')
            {
                return Fail(start, "The '(?' is followed by an unrecognized grouping construct.");
            }

            Add(RegexTokenKind.InlineOptions, position - start + 1);

            var group = Group;
            group.Options = options;
            group.AtomPresent = false;
            group.QuantifierApplied = false;

            return true;
        }

        /// <summary>
        /// Applies a single inline option character.
        /// </summary>
        /// <param name="candidate">The character to apply.</param>
        /// <param name="negated">Whether the options behind a <c>-</c> are being cleared.</param>
        /// <param name="options">The options being built.</param>
        /// <returns>
        /// <see langword="true" /> if the character is part of the construct; otherwise <see langword="false" />.
        /// </returns>
        /// <remarks>
        /// A mode switch run is a sequence, not a pair: <c>-</c> and <c>+</c> may each occur any number of
        /// times and every one of them only decides the direction of the letters that follow it, so
        /// <c>(?i-s-x)</c>, <c>(?-i+s)</c> and even a trailing <c>(?i+)</c> are all legal. The last switch that
        /// mentions a letter is the one that wins, which is why the direction is a flag rather than a latch.
        /// </remarks>
        private static bool TryApplyOption(char candidate, ref bool negated, ref RegexOptions options)
        {
            if (candidate is '-' or '+')
            {
                negated = candidate == '-';

                return true;
            }

            var option = MapOption(candidate);
            if (option == RegexOptions.None)
            {
                return false;
            }

            options = negated ? options & ~option : options | option;

            return true;
        }

        /// <summary>
        /// Maps an inline option character to its <see cref="RegexOptions" /> flag.
        /// </summary>
        /// <param name="candidate">The character to map.</param>
        /// <returns>The flag, or <see cref="RegexOptions.None" /> if the character is no option.</returns>
        private static RegexOptions MapOption(char candidate) =>
            candidate switch
            {
                'i' => RegexOptions.IgnoreCase,
                'm' => RegexOptions.Multiline,
                'n' => RegexOptions.ExplicitCapture,
                's' => RegexOptions.Singleline,
                'x' => RegexOptions.IgnorePatternWhitespace,
                _ => RegexOptions.None,
            };

        /// <summary>
        /// Emits the token that opens a group and pushes the state the group scopes.
        /// </summary>
        /// <param name="kind">The kind of the opening token.</param>
        /// <param name="length">The length of the opening token.</param>
        /// <param name="options">The options that apply inside the group.</param>
        /// <returns>Always <see langword="true" />.</returns>
        private bool OpenGroup(RegexTokenKind kind, int length, RegexOptions options)
        {
            var openIndex = _index;
            Add(kind, length);
            _groups.Push(new GroupFrame(options, openIndex));

            return true;
        }

        /// <summary>
        /// Scans the <c>)</c> that closes a group, which turns the whole group into a repeatable unit.
        /// </summary>
        /// <returns><see langword="true" /> if a group is open; otherwise <see langword="false" />.</returns>
        private bool ScanGroupClose()
        {
            if (_groups.Count == 1)
            {
                return Fail(_index, "The ')' closes a group that was never opened.");
            }

            _ = _groups.Pop();
            Add(RegexTokenKind.GroupClose, 1);
            MarkAtom();

            return true;
        }

        /// <summary>
        /// Decides for every numbered digit escape whether it is a backreference or an octal character escape,
        /// which can only be answered once the pattern's capture groups are all known.
        /// </summary>
        /// <remarks>
        /// <para>
        /// .NET reads the <em>whole</em> digit run and asks whether it names a capture group the pattern
        /// defines. If it does, the run is a backreference. If it does not, .NET does not shorten the
        /// backreference - it re-reads the run from the start as an octal escape of at most three octal digits
        /// and leaves every further digit an ordinary literal. So a pattern with one group reads <c>\10</c> as
        /// the single character U+0008 rather than as group one followed by <c>'0'</c>, and <c>\18</c> as
        /// U+0001 followed by the literal <c>'8'</c>.
        /// </para>
        /// <para>
        /// A run of a single digit is never re-read: <c>\1</c> and <c>\8</c> are backreferences whatever the
        /// pattern captures, and an undefined one is lexically well formed and only semantically invalid, which
        /// is the boundary this tokenizer draws everywhere. The same holds for a longer run that begins with no
        /// octal digit at all. Getting this wrong is not cosmetic - a rewriter that renumbers what it believes
        /// to be a backreference would silently corrupt a character escape.
        /// </para>
        /// </remarks>
        private void ResolveNumberedBackreferences()
        {
            var captureCount = _unnamedCaptureCount + _captureNames.Count;

            for (var index = 0; index < _tokens.Count; index++)
            {
                var token = _tokens[index];
                if (token.Kind != RegexTokenKind.Backreference || token.Length < 3 || token.Text[1] == 'k')
                {
                    continue;
                }

                var digits = token.Text.Substring(1);
                if (IsDefinedCapture(digits, captureCount))
                {
                    continue;
                }

                var octal = CountLeadingOctalDigits(digits);
                if (octal == 0)
                {
                    continue;
                }

                _tokens[index] = new RegexToken(RegexTokenKind.Escape, token.Start, "\\" + digits.Substring(0, octal));

                for (var offset = octal; offset < digits.Length; offset++)
                {
                    _tokens.Insert(
                        index + offset - octal + 1,
                        new RegexToken(RegexTokenKind.Literal, token.Start + offset + 1, digits[offset].ToString())
                    );
                }
            }
        }

        /// <summary>
        /// Counts the octal digits an octal escape may consume from the start of a digit run.
        /// </summary>
        /// <param name="digits">The digit run behind the backslash.</param>
        /// <returns>The number of leading octal digits, at most three.</returns>
        private static int CountLeadingOctalDigits(string digits)
        {
            var count = 0;
            while (count < 3 && count < digits.Length && digits[count] is >= '0' and <= '7')
            {
                count++;
            }

            return count;
        }

        /// <summary>
        /// Decides whether the pattern defines the capture group a backreference names.
        /// </summary>
        /// <param name="digits">The digits of the backreference.</param>
        /// <param name="captureCount">The number of capture groups the pattern defines implicitly.</param>
        /// <returns><see langword="true" /> if the group exists; otherwise <see langword="false" />.</returns>
        private bool IsDefinedCapture(string digits, int captureCount)
        {
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return false;
            }

            return (number >= 1 && number <= captureCount) || _captureNumbers.Contains(number);
        }

        /// <summary>
        /// Measures a name delimited by <c>&lt;...&gt;</c> or by <c>'...'</c>.
        /// </summary>
        /// <param name="start">The index of the opening delimiter.</param>
        /// <param name="close">The index of the closing delimiter.</param>
        /// <param name="name">The text between the delimiters.</param>
        /// <returns>
        /// <see langword="true" /> if a delimited name was found; otherwise <see langword="false" />.
        /// </returns>
        private bool TryMeasureBracketedName(int start, out int close, out string name)
        {
            close = -1;
            name = string.Empty;

            if (start >= _pattern.Length)
            {
                return false;
            }

            var opener = _pattern[start];
            if (opener is not '<' and not '\'')
            {
                return false;
            }

            close = _pattern.IndexOf(opener == '<' ? '>' : '\'', start + 1);
            if (close < 0)
            {
                return false;
            }

            name = _pattern.Substring(start + 1, close - start - 1);

            return true;
        }

        /// <summary>
        /// Emits a token at the current index and advances past it.
        /// </summary>
        /// <param name="kind">The kind of the token.</param>
        /// <param name="length">The length of the token.</param>
        /// <remarks>
        /// Every token but a blank ends the window in which a lazy <c>?</c> may still attach itself to the
        /// preceding quantifier. Clearing it here rather than in each scanner is what keeps the window open
        /// across a run of blanks of any length and closes it for everything else.
        /// </remarks>
        private void Add(RegexTokenKind kind, int length)
        {
            _tokens.Add(new RegexToken(kind, _index, _pattern.Substring(_index, length)));
            _index += length;

            if (kind is not RegexTokenKind.WhitespaceIgnored and not RegexTokenKind.Comment)
            {
                Group.LazyMarkerAvailable = false;
            }
        }

        /// <summary>
        /// Emits a token that is a repeatable unit, so that a quantifier may follow it.
        /// </summary>
        /// <param name="kind">The kind of the token.</param>
        /// <param name="length">The length of the token.</param>
        /// <returns>Always <see langword="true" />.</returns>
        private bool AddAtom(RegexTokenKind kind, int length)
        {
            Add(kind, length);
            MarkAtom();

            return true;
        }

        /// <summary>
        /// Emits an escape, which is a class member inside a character class and a repeatable unit outside.
        /// </summary>
        /// <param name="length">The length of the escape.</param>
        /// <returns>Always <see langword="true" />.</returns>
        private bool AddEscape(int length) =>
            InClass ? AddClassMember(RegexTokenKind.Escape, length) : AddAtom(RegexTokenKind.Escape, length);

        /// <summary>
        /// Emits a member of the current character class and tracks whether it can start a range.
        /// </summary>
        /// <param name="kind">The kind of the token.</param>
        /// <param name="length">The length of the token.</param>
        /// <returns>Always <see langword="true" />.</returns>
        private bool AddClassMember(RegexTokenKind kind, int length)
        {
            Add(kind, length);

            var characterClass = Class;
            characterClass.MemberSeen = true;

            if (characterClass.AwaitingRangeEnd)
            {
                characterClass.AwaitingRangeEnd = false;
                characterClass.MemberPending = false;

                return true;
            }

            characterClass.MemberPending = true;

            return true;
        }

        /// <summary>
        /// Records that the last token is a repeatable unit that no quantifier has been applied to yet.
        /// </summary>
        private void MarkAtom()
        {
            var group = Group;
            group.AtomPresent = true;
            group.QuantifierApplied = false;
        }

        /// <summary>
        /// Reports an escape that a character class does not allow.
        /// </summary>
        /// <param name="escaped">The character behind the backslash.</param>
        /// <returns>Always <see langword="false" />.</returns>
        private bool FailClassEscape(char escaped) =>
            Fail(_index, $"The escape sequence '\\{escaped}' is not allowed inside a character class.");

        /// <summary>
        /// Records the first failure of the run.
        /// </summary>
        /// <param name="index">The index of the offending construct.</param>
        /// <param name="reason">The reason the pattern is rejected.</param>
        /// <returns>Always <see langword="false" />.</returns>
        private bool Fail(int index, string reason)
        {
            _errorIndex = index;
            _error = reason;

            return false;
        }

        /// <summary>
        /// Decides whether the pattern holds the requested number of hexadecimal digits at an index.
        /// </summary>
        /// <param name="start">The index of the first digit.</param>
        /// <param name="digits">The number of digits required.</param>
        /// <returns>
        /// <see langword="true" /> if all digits are hexadecimal; otherwise <see langword="false" />.
        /// </returns>
        private bool AreHexadecimal(int start, int digits)
        {
            for (var offset = 0; offset < digits; offset++)
            {
                if (!IsHexadecimal(_pattern[start + offset]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Decides whether a character is a hexadecimal digit.
        /// </summary>
        /// <param name="candidate">The character to test.</param>
        /// <returns>
        /// <see langword="true" /> if the character is a hexadecimal digit; otherwise <see langword="false" />.
        /// </returns>
        private static bool IsHexadecimal(char candidate) =>
            candidate is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

        /// <summary>
        /// Decides whether a character may follow <c>\c</c>, using the set .NET accepts.
        /// </summary>
        /// <param name="candidate">The character to test.</param>
        /// <returns>
        /// <see langword="true" /> if the character names a control character; otherwise <see langword="false" />.
        /// </returns>
        private static bool IsControlCharacter(char candidate) =>
            candidate is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '@' or '[' or '\\' or ']' or '^' or '_';

        /// <summary>
        /// Decides whether a character is a word character, which is what .NET requires of a group name and
        /// what makes an unknown escape an error instead of a literal.
        /// </summary>
        /// <param name="candidate">The character to test.</param>
        /// <returns>
        /// <see langword="true" /> if the character is a word character; otherwise <see langword="false" />.
        /// </returns>
        private static bool IsWordCharacter(char candidate) => candidate == '_' || char.IsLetterOrDigit(candidate);

        /// <summary>
        /// Decides whether a name is a legal group name, meaning either a plain number or a run of word
        /// characters that does not start with a digit.
        /// </summary>
        /// <param name="name">The name to test.</param>
        /// <returns><see langword="true" /> if the name is legal; otherwise <see langword="false" />.</returns>
        private static bool IsGroupName(string name)
        {
            if (name.Length == 0)
            {
                return false;
            }

            if (name[0] is >= '0' and <= '9')
            {
                return int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out _);
            }

            return name.All(IsWordCharacter);
        }
    }

    /// <summary>
    /// The state a group scopes: the options in effect inside it and whether it already holds something a
    /// quantifier could repeat.
    /// </summary>
    private sealed class GroupFrame
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GroupFrame" /> class.
        /// </summary>
        /// <param name="options">The options in effect inside the group.</param>
        /// <param name="openIndex">The index the group is opened at, or <c>-1</c> for the whole pattern.</param>
        public GroupFrame(RegexOptions options, int openIndex)
        {
            Options = options;
            OpenIndex = openIndex;
        }

        /// <summary>
        /// Gets or sets the options in effect, which an inline <c>(?imnsx)</c> changes.
        /// </summary>
        public RegexOptions Options { get; set; }

        /// <summary>
        /// Gets the index the group is opened at, or <c>-1</c> for the whole pattern.
        /// </summary>
        public int OpenIndex { get; }

        /// <summary>
        /// Gets or sets a value indicating whether a quantifier would have something to repeat.
        /// </summary>
        public bool AtomPresent { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether that unit is already quantified, which makes a further
        /// quantifier a nested one.
        /// </summary>
        public bool QuantifierApplied { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether a quantifier without a lazy <c>?</c> was just read, so that
        /// a <c>?</c> reached across nothing but blanks still turns it lazy instead of nesting.
        /// </summary>
        public bool LazyMarkerAvailable { get; set; }
    }

    /// <summary>
    /// The state a character class scopes, which is what decides the role of a <c>-</c> inside it.
    /// </summary>
    private sealed class ClassFrame
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClassFrame" /> class.
        /// </summary>
        /// <param name="openIndex">The index the class is opened at.</param>
        public ClassFrame(int openIndex) => OpenIndex = openIndex;

        /// <summary>
        /// Gets the index the class is opened at.
        /// </summary>
        public int OpenIndex { get; }

        /// <summary>
        /// Gets or sets a value indicating whether a member is available as the lower end of a range.
        /// </summary>
        public bool MemberPending { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the next member closes a range that was opened by a
        /// <see cref="RegexTokenKind.CharacterClassRange" /> token.
        /// </summary>
        public bool AwaitingRangeEnd { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the class already holds at least one member, which is what
        /// a following <c>-[</c> needs in order to be a subtraction rather than an ordinary member.
        /// </summary>
        public bool MemberSeen { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the class has already been subtracted from, after which
        /// nothing but its closing <c>]</c> may follow.
        /// </summary>
        public bool SubtractionApplied { get; set; }
    }
}
