namespace NetEvolve.FrameShift.Tests.Unit.Mutations.RegularExpressions;

using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Mutations.RegularExpressions;
using NetEvolve.FrameShift.Tests.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers <see cref="RegexPatternLocator" /> and the <see cref="RegexPatternSite" /> it produces: every
/// detection form, the second-argument positioning of the static <c>Regex</c> methods, named arguments,
/// the resolution of composed and of non-constant options, and every position that looks like a pattern
/// but is none.
/// </summary>
/// <remarks>
/// <para>
/// Each fixture is compiled and proven free of compile errors before it is inspected, see
/// <see cref="CreateFixture" />. A fixture that does not bind would make the semantic checks of the
/// locator vacuous, and every one of the "returns null" expectations below would then pass for the wrong
/// reason.
/// </para>
/// <para>
/// Two detection forms are not available on every target framework of this suite, and both are guarded
/// instead of being replaced by a hand-written look-alike: the locator resolves the attribute types by
/// their exact metadata name, so a look-alike would only prove that a declaration of that name matches
/// and would say nothing about the real type. <c>GeneratedRegexAttribute</c> arrived with .NET 7, and the
/// DataAnnotations <c>RegularExpressionAttribute</c> lives in an assembly the .NET Framework reference set
/// of the harness does not contain. The locator itself is framework-neutral, so the guarded tests cover
/// the same code paths wherever they do run.
/// </para>
/// </remarks>
public class RegexPatternLocatorTests
{
    private const string StatementPlaceholder = "STATEMENT";

    /// <summary>
    /// The fixture every call form is written into. It offers an input string and a <c>RegexOptions</c>
    /// parameter, the latter being the simplest options expression the compiler cannot fold.
    /// </summary>
    private const string CallTemplate = """
        using System;
        using System.Text.RegularExpressions;

        public class Sample
        {
            public void Run(string input, RegexOptions runtimeOptions)
            {
                STATEMENT
            }
        }
        """;

    private const string ConstantOptionsSource = """
        using System.Text.RegularExpressions;

        public class Sample
        {
            private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Multiline;

            public Regex Create() => new Regex(/*!*/"a+", Options);
        }
        """;

    private const string BaseInitializerSource = """
        using System.Text.RegularExpressions;

        public sealed class Sample : Regex
        {
            public Sample()
                : base(/*!*/"a+")
            {
            }
        }
        """;

    private const string PlainLiteralSource = """
        public class Sample
        {
            public string Get() => /*!*/"a+";
        }
        """;

    private const string VariablePatternSource = """
        using System.Text.RegularExpressions;

        public class Sample
        {
            public Regex Create()
            {
                var pattern = /*!*/"a+";

                return new Regex(pattern);
            }
        }
        """;

    private const string InterpolatedPatternSource = """
        using System.Text.RegularExpressions;

        public class Sample
        {
            public Regex Create(string prefix) => new Regex(/*!*/$"{prefix}a+");
        }
        """;

    private const string IndirectConstantSource = """
        using System.Text.RegularExpressions;

        public class Sample
        {
            private const string Pattern = /*!*/"a+";

            public Regex Create() => new Regex(Pattern);
        }
        """;

    private const string UnrelatedRegexConstructorSource = """
        namespace Unrelated
        {
            public sealed class Regex
            {
                public Regex(string pattern) => Pattern = pattern;

                public string Pattern { get; }

                public static bool IsMatch(string input, string pattern) => false;
            }

            public sealed class Sample
            {
                public Regex Create() => new Regex(/*!*/"a+");
            }
        }
        """;

    private const string UnrelatedRegexStaticMethodSource = """
        namespace Unrelated
        {
            public sealed class Regex
            {
                public Regex(string pattern) => Pattern = pattern;

                public string Pattern { get; }

                public static bool IsMatch(string input, string pattern) => false;
            }

            public sealed class Sample
            {
                public bool Check(string input) => Regex.IsMatch(input, /*!*/"a+");
            }
        }
        """;

    /// <summary>
    /// A verbatim literal: its source text is <c>@"\d+"</c>, its value is the four characters
    /// <c>\d+</c> minus the quoting, so a locator reading the token text would hand a different pattern to
    /// the tokenizer than the one the engine receives.
    /// </summary>
    private const string VerbatimPatternSource = """
        using System.Text.RegularExpressions;

        public class Sample
        {
            public Regex Create() => new Regex(/*!*/@"\d+\s");
        }
        """;

    /// <summary>
    /// A raw string literal, whose source text carries three quotes on either side and whose value again
    /// is only <c>\d+\s</c>. Written with a four-quote delimiter so that the fixture can contain the
    /// three-quote one.
    /// </summary>
    private const string RawStringPatternSource = """"
        using System.Text.RegularExpressions;

        public class Sample
        {
            public Regex Create() => new Regex(/*!*/"""\d+\s""");
        }
        """";

#if NET7_0_OR_GREATER
    private const string AttributePlaceholder = "ATTRIBUTE";

    private const string GeneratedRegexTemplate = """
        using System.Text.RegularExpressions;

        public class Sample
        {
            ATTRIBUTE
            public static Regex Create() => null!;
        }
        """;
#endif

#if !NETFRAMEWORK
    private const string RegularExpressionSource = """
        using System.ComponentModel.DataAnnotations;

        public class Model
        {
            [RegularExpression(/*!*/"a+")]
            public string? Name { get; set; }
        }
        """;
#endif

    [Test]
    public async Task TryLocate_RegexConstructorWithoutOptions_ReportsTheConstructorOriginAndNoOptions()
    {
        var site = LocateInCall("_ = new Regex(/*!*/\"a+\");");

        _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.RegexConstructor);
        _ = await Assert.That(site.Pattern).IsEqualTo("a+");
        _ = await Assert.That(site.AreOptionsKnown).IsTrue();
        _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.None);
        _ = await Assert.That(site.OptionsExpression).IsNull();
        _ = await Assert.That(site.AttributeArgument).IsNull();
        _ = await Assert.That(site.PatternLiteral.Token.ValueText).IsEqualTo("a+");
    }

    [Test]
    public async Task TryLocate_RegexConstructorWithOptions_ResolvesTheOptions()
    {
        var site = LocateInCall("_ = new Regex(/*!*/\"a+\", RegexOptions.IgnoreCase);");

        _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.RegexConstructor);
        _ = await Assert.That(site.AreOptionsKnown).IsTrue();
        _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.IgnoreCase);
        _ = await Assert.That(site.OptionsExpression).IsNotNull();
        _ = await Assert.That(site.OptionsExpression!.ToString()).IsEqualTo("RegexOptions.IgnoreCase");
    }

    /// <summary>
    /// The three-argument overload, with the options written as a flag combination. Combining the flags is
    /// not optional: the tokenizer's grammar changes with <c>IgnorePatternWhitespace</c>, so a resolution
    /// that dropped one operand of the <c>|</c> would silently be a different grammar.
    /// </summary>
    [Test]
    public async Task TryLocate_RegexConstructorWithComposedOptionsAndTimeout_CombinesTheFlags()
    {
        var site = LocateInCall(
            "_ = new Regex(/*!*/\"a+\", RegexOptions.IgnorePatternWhitespace | RegexOptions.Multiline, "
                + "TimeSpan.FromSeconds(1));"
        );

        _ = await Assert.That(site.AreOptionsKnown).IsTrue();
        _ = await Assert
            .That(site.Options!.Value)
            .IsEqualTo(RegexOptions.IgnorePatternWhitespace | RegexOptions.Multiline);
    }

    [Test]
    public async Task TryLocate_RegexConstructorWithNamedArguments_ResolvesPatternAndOptions()
    {
        var site = LocateInCall("_ = new Regex(options: RegexOptions.Multiline, pattern: /*!*/\"a+\");");

        _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.RegexConstructor);
        _ = await Assert.That(site.Pattern).IsEqualTo("a+");
        _ = await Assert.That(site.AreOptionsKnown).IsTrue();
        _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.Multiline);
    }

    /// <summary>
    /// The options are a method parameter, so nothing about them is statically determinable. The result has
    /// to say exactly that; reporting <c>RegexOptions.None</c> would tell a later rewriter that the pattern
    /// may be parsed with the default grammar, which is an assumption and not a fact.
    /// </summary>
    [Test]
    public async Task TryLocate_RegexConstructorWithNonConstantOptions_ReportsTheOptionsAsUnknown()
    {
        var site = LocateInCall("_ = new Regex(/*!*/\"a+\", runtimeOptions);");

        _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.RegexConstructor);
        _ = await Assert.That(site.Pattern).IsEqualTo("a+");
        _ = await Assert.That(site.AreOptionsKnown).IsFalse();
        _ = await Assert.That(site.Options.HasValue).IsFalse();
        _ = await Assert.That(site.OptionsExpression).IsNotNull();
        _ = await Assert.That(site.OptionsExpression!.ToString()).IsEqualTo("runtimeOptions");
    }

    [Test]
    public async Task TryLocate_ConstantOptionsField_ResolvesTheOptions()
    {
        var site = LocateSite(ConstantOptionsSource);

        _ = await Assert.That(site.AreOptionsKnown).IsTrue();
        _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.IgnoreCase | RegexOptions.Multiline);
    }

    /// <summary>
    /// A <c>base(...)</c> initializer reaching a <c>Regex</c> constructor is a constructor call like any
    /// other, which follows from asking the parent of the argument list instead of testing syntax kinds.
    /// </summary>
    [Test]
    public async Task TryLocate_BaseConstructorInitializer_ReportsTheConstructorOrigin()
    {
        var site = LocateSite(BaseInitializerSource);

        _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.RegexConstructor);
        _ = await Assert.That(site.Pattern).IsEqualTo("a+");
    }

    [Test]
    [Arguments("_ = Regex.IsMatch(input, /*!*/\"a+\");")]
    [Arguments("_ = Regex.Match(input, /*!*/\"a+\");")]
    [Arguments("_ = Regex.Matches(input, /*!*/\"a+\");")]
    [Arguments("_ = Regex.Replace(input, /*!*/\"a+\", \"b\");")]
    [Arguments("_ = Regex.Split(input, /*!*/\"a+\");")]
#if NET7_0_OR_GREATER
    [Arguments("_ = Regex.Count(input, /*!*/\"a+\");")]
    [Arguments("foreach (var match in Regex.EnumerateMatches(input, /*!*/\"a+\")) { }")]
#endif
    public async Task TryLocate_StaticMethodSecondArgument_IsThePattern(string statement)
    {
        var site = LocateInCall(statement);

        _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.RegexStaticMethod);
        _ = await Assert.That(site.Pattern).IsEqualTo("a+");
        _ = await Assert.That(site.AreOptionsKnown).IsTrue();
        _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.None);
        _ = await Assert.That(site.OptionsExpression).IsNull();
    }

    [Test]
    [Arguments("_ = Regex.IsMatch(input, /*!*/\"a+\", RegexOptions.Singleline);")]
    [Arguments("_ = Regex.Replace(input, /*!*/\"a+\", \"b\", RegexOptions.Singleline);")]
    [Arguments("_ = Regex.Split(input, /*!*/\"a+\", RegexOptions.Singleline);")]
    public async Task TryLocate_StaticMethodWithOptions_ResolvesTheOptions(string statement)
    {
        var site = LocateInCall(statement);

        _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.RegexStaticMethod);
        _ = await Assert.That(site.AreOptionsKnown).IsTrue();
        _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.Singleline);
    }

    [Test]
    public async Task TryLocate_StaticMethodWithNamedArguments_ResolvesPatternAndOptions()
    {
        var site = LocateInCall(
            "_ = Regex.IsMatch(options: RegexOptions.RightToLeft, pattern: /*!*/\"a+\", input: input);"
        );

        _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.RegexStaticMethod);
        _ = await Assert.That(site.Pattern).IsEqualTo("a+");
        _ = await Assert.That(site.AreOptionsKnown).IsTrue();
        _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.RightToLeft);
    }

    /// <summary>
    /// The input of a static <c>Regex</c> method is a string literal just as often as the pattern is, and
    /// it sits in the parameter before it. It must never be mistaken for a pattern.
    /// </summary>
    [Test]
    [Arguments("_ = Regex.IsMatch(/*!*/\"input\", \"a+\");")]
    [Arguments("_ = Regex.Replace(input, \"a+\", /*!*/\"b\");")]
    [Arguments("_ = Regex.Escape(/*!*/\"a+\");")]
    [Arguments("_ = Regex.Unescape(/*!*/\"a+\");")]
    [Arguments("_ = new Regex(\"a+\").IsMatch(/*!*/\"input\");")]
    [Arguments("_ = new Regex(\"a+\", RegexOptions.None, new TimeSpan(/*!*/100));")]
    [Arguments("_ = input.Replace(/*!*/\"a+\", \"b\");")]
    public async Task TryLocate_LiteralThatIsNoPattern_ReturnsNull(string statement)
    {
        var site = Locate(CreateCallSource(statement));

        _ = await Assert.That(site).IsNull();
    }

    [Test]
    public async Task TryLocate_StandaloneStringLiteral_ReturnsNull()
    {
        var site = Locate(PlainLiteralSource);

        _ = await Assert.That(site).IsNull();
    }

    /// <summary>
    /// The pattern reaches the constructor through a local, so the literal at the declaration is no site:
    /// nothing at that position says the value is ever used as a pattern, and the argument itself is no
    /// literal a rewriter could replace.
    /// </summary>
    [Test]
    public async Task TryLocate_PatternPassedAsVariable_ReturnsNull()
    {
        var site = Locate(VariablePatternSource);

        _ = await Assert.That(site).IsNull();
    }

    [Test]
    public async Task TryLocate_InterpolatedString_ReturnsNull()
    {
        var (semanticModel, tree) = CreateFixture(InterpolatedPatternSource);
        var node = SyntaxNodeLocator.FindMarked<InterpolatedStringExpressionSyntax>(tree);

        var site = RegexPatternLocator.TryLocate(node, semanticModel, CancellationToken.None);

        _ = await Assert.That(site).IsNull();
    }

    [Test]
    public async Task TryLocate_ConstantReferencedIndirectly_ReturnsNull()
    {
        var site = Locate(IndirectConstantSource);

        _ = await Assert.That(site).IsNull();
    }

    /// <summary>
    /// A class named <c>Regex</c> in an unrelated namespace, with a constructor and a static method whose
    /// signatures match the framework's, must not match. The type is resolved through
    /// <c>GetTypeByMetadataName</c> and compared with <c>SymbolEqualityComparer</c>, so only the real
    /// <c>System.Text.RegularExpressions.Regex</c> ever answers.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(UnrelatedRegexSources))]
    public async Task TryLocate_SameNamedRegexTypeOfAnotherNamespace_ReturnsNull(string source)
    {
        var site = Locate(source);

        _ = await Assert.That(site).IsNull();
    }

    [Test]
    public async Task TryLocate_VerbatimLiteral_ReportsTheTokenValueAndNotItsText()
    {
        var site = LocateSite(VerbatimPatternSource);

        _ = await Assert.That(site.Pattern).IsEqualTo("\\d+\\s");
        _ = await Assert.That(site.PatternLiteral.Token.Text).IsEqualTo("@\"\\d+\\s\"");
        _ = await Assert.That(site.PatternLiteral.Token.Text).IsNotEqualTo(site.Pattern);
    }

    [Test]
    public async Task TryLocate_RawStringLiteral_ReportsTheTokenValueAndNotItsText()
    {
        var site = LocateSite(RawStringPatternSource);

        _ = await Assert.That(site.Pattern).IsEqualTo("\\d+\\s");
        _ = await Assert.That(site.PatternLiteral.Token.Text).IsEqualTo("\"\"\"\\d+\\s\"\"\"");
        _ = await Assert.That(site.PatternLiteral.Token.Text).IsNotEqualTo(site.Pattern);
    }

#if NET7_0_OR_GREATER
    [Test]
    public async Task TryLocate_GeneratedRegexWithoutOptions_ReportsTheAttributeOriginAndNoOptions()
    {
        var site = LocateSite(CreateGeneratedRegexSource("[GeneratedRegex(/*!*/\"a+\")]"));

        _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.GeneratedRegex);
        _ = await Assert.That(site.Pattern).IsEqualTo("a+");
        _ = await Assert.That(site.AreOptionsKnown).IsTrue();
        _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.None);
        _ = await Assert.That(site.OptionsExpression).IsNull();
        _ = await Assert.That(site.AttributeArgument).IsNotNull();
        _ = await Assert.That(ReferenceEquals(site.AttributeArgument!.Expression, site.PatternLiteral)).IsTrue();
    }

    [Test]
    public async Task TryLocate_GeneratedRegexWithComposedOptions_CombinesTheFlags()
    {
        var site = LocateSite(
            CreateGeneratedRegexSource(
                "[GeneratedRegex(/*!*/\"a+\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]"
            )
        );

        _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.GeneratedRegex);
        _ = await Assert.That(site.AreOptionsKnown).IsTrue();
        _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    [Test]
    public async Task TryLocate_GeneratedRegexWithNamedArguments_ResolvesPatternAndOptions()
    {
        var site = LocateSite(
            CreateGeneratedRegexSource("[GeneratedRegex(options: RegexOptions.Multiline, pattern: /*!*/\"a+\")]")
        );

        _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.GeneratedRegex);
        _ = await Assert.That(site.Pattern).IsEqualTo("a+");
        _ = await Assert.That(site.AreOptionsKnown).IsTrue();
        _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.Multiline);
    }

    [Test]
    public async Task TryLocate_GeneratedRegexWithMatchTimeout_StillResolvesTheOptions()
    {
        var site = LocateSite(
            CreateGeneratedRegexSource("[GeneratedRegex(/*!*/\"a+\", RegexOptions.Singleline, 250)]")
        );

        _ = await Assert.That(site.AreOptionsKnown).IsTrue();
        _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.Singleline);
    }

    /// <summary>
    /// The match timeout of <c>[GeneratedRegex]</c> is an <see cref="int" /> literal sitting in the very
    /// same attribute, so it proves that the pattern is recognised by the parameter it binds to.
    /// </summary>
    [Test]
    public async Task TryLocate_GeneratedRegexMatchTimeout_ReturnsNull()
    {
        var (semanticModel, tree) = CreateFixture(
            CreateGeneratedRegexSource("[GeneratedRegex(\"a+\", RegexOptions.Singleline, /*!*/250)]")
        );
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        var site = RegexPatternLocator.TryLocate(node, semanticModel, CancellationToken.None);

        _ = await Assert.That(site).IsNull();
    }
#endif

#if !NETFRAMEWORK
    /// <summary>
    /// The DataAnnotations attribute has no options parameter in any overload, so its options are
    /// <c>RegexOptions.None</c> and that is a resolved value, not a fallback.
    /// </summary>
    [Test]
    public async Task TryLocate_DataAnnotationsRegularExpression_ReportsNoOptions()
    {
        var site = LocateSite(RegularExpressionSource);

        _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.DataAnnotationsRegularExpression);
        _ = await Assert.That(site.Pattern).IsEqualTo("a+");
        _ = await Assert.That(site.AreOptionsKnown).IsTrue();
        _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.None);
        _ = await Assert.That(site.OptionsExpression).IsNull();
        _ = await Assert.That(site.AttributeArgument).IsNotNull();
    }
#endif

    [Test]
    public async Task TryLocate_NodeNull_ThrowsArgumentNullException()
    {
        var (semanticModel, _) = CreateFixture(PlainLiteralSource);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = RegexPatternLocator.TryLocate(null!, semanticModel, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("node");
    }

    [Test]
    public async Task TryLocate_SemanticModelNull_ThrowsArgumentNullException()
    {
        var (_, tree) = CreateFixture(PlainLiteralSource);
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = RegexPatternLocator.TryLocate(node, null!, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("semanticModel");
    }

    [Test]
    public async Task TryLocate_CancelledToken_ThrowsOperationCanceledException()
    {
        var (semanticModel, tree) = CreateFixture(CreateCallSource("_ = new Regex(/*!*/\"a+\");"));
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = RegexPatternLocator.TryLocate(node, semanticModel, cancellation.Token)
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    [Test]
    public async Task RegexPatternSite_PatternLiteralNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = new RegexPatternSite(null!, RegexPatternOrigin.RegexConstructor, RegexOptions.None, null)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("patternLiteral");
    }

    [Test]
    public async Task ToString_ResolvedOptions_NamesOriginPatternAndOptions()
    {
        var site = LocateInCall("_ = new Regex(/*!*/\"a+\", RegexOptions.IgnoreCase);");

        _ = await Assert.That(site.ToString()).IsEqualTo("RegexConstructor: \"a+\" [IgnoreCase]");
    }

    [Test]
    public async Task ToString_UnknownOptions_SaysThatTheOptionsAreNotDeterminable()
    {
        var site = LocateInCall("_ = new Regex(/*!*/\"a+\", runtimeOptions);");

        _ = await Assert
            .That(site.ToString())
            .IsEqualTo("RegexConstructor: \"a+\" [options not statically determinable]");
    }

    /// <summary>
    /// The sources of <see cref="TryLocate_SameNamedRegexTypeOfAnotherNamespace_ReturnsNull" />.
    /// </summary>
    /// <returns>The two fixtures, one per detection form of the look-alike type.</returns>
    public static IEnumerable<Func<string>> UnrelatedRegexSources() =>
        new[] { UnrelatedRegexConstructorSource, UnrelatedRegexStaticMethodSource }.Select(source =>
            (Func<string>)(() => source)
        );

    private static RegexPatternSite LocateInCall(string statement) => LocateSite(CreateCallSource(statement));

    private static RegexPatternSite LocateSite(string source) =>
        Locate(source)
        ?? throw new InvalidOperationException("The locator found no pattern site in the fixture:\n" + source);

    private static RegexPatternSite? Locate(string source)
    {
        var (semanticModel, tree) = CreateFixture(source);
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        return RegexPatternLocator.TryLocate(node, semanticModel, CancellationToken.None);
    }

    private static string CreateCallSource(string statement) =>
        CallTemplate.Replace(StatementPlaceholder, statement, StringComparison.Ordinal);

#if NET7_0_OR_GREATER
    private static string CreateGeneratedRegexSource(string attribute) =>
        GeneratedRegexTemplate.Replace(AttributePlaceholder, attribute, StringComparison.Ordinal);
#endif

    /// <summary>
    /// Compiles a fixture and proves that it binds, because every expectation of this suite is about what
    /// the semantic model says and a fixture with a compile error says nothing at all.
    /// </summary>
    /// <param name="source">The fixture source.</param>
    /// <returns>The semantic model and the syntax tree of the fixture.</returns>
    /// <exception cref="InvalidOperationException">The fixture does not compile.</exception>
    private static (SemanticModel SemanticModel, SyntaxTree Tree) CreateFixture(string source)
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var errors = CompilationFactory.GetCompileErrors(compilation);

        if (!errors.IsEmpty)
        {
            throw new InvalidOperationException(
                "The fixture does not compile: " + string.Join("; ", errors) + "\n" + source
            );
        }

        return (semanticModel, tree);
    }
}
