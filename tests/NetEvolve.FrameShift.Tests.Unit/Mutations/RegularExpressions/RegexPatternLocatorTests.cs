namespace NetEvolve.FrameShift.Tests.Unit.Mutations.RegularExpressions;

using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
/// the same code paths wherever they do run. The <c>[GeneratedRegex]</c> overload taking a culture name
/// arrived one release later still and is guarded on .NET 8, and the static <c>Count</c> and
/// <c>EnumerateMatches</c> methods are guarded on .NET 7 for the same reason.
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

    /// <summary>
    /// The options placeholder of <see cref="OptionsTemplate" />. It is spelled in capitals so that
    /// substituting it cannot touch the identifier <c>RegexOptions</c> anywhere in the fixture.
    /// </summary>
    private const string OptionsPlaceholder = "OPTIONS";

    /// <summary>
    /// The fixture the options expression is written into. It offers one specimen of every kind of
    /// expression the resolution has to tell apart: a parameter, a local, a <see langword="readonly" />
    /// field, a <see langword="const" /> field and a method call. Only the <see langword="const" /> one is
    /// a constant expression, and that is the whole distinction the locator has to make.
    /// </summary>
    private const string OptionsTemplate = """
        using System.Text.RegularExpressions;

        public class Sample
        {
            private const RegexOptions ConstantOptions = RegexOptions.IgnoreCase;

            private static readonly RegexOptions _readOnlyOptions = RegexOptions.Multiline;

            public Regex Create(RegexOptions parameterOptions)
            {
                var localOptions = RegexOptions.Singleline;

                return new Regex(/*!*/"a+", OPTIONS);
            }

            private static RegexOptions GetOptions() => RegexOptions.ECMAScript;
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
    /// A class deriving from <c>Regex</c> with a constructor of its own. The literal is handed to
    /// <c>Sample</c>, whose constructor is not the framework's, so it is no site - what the derived
    /// constructor does with the value is unknown, and the <c>base(...)</c> call it forwards to takes a
    /// parameter and no literal.
    /// </summary>
    private const string DerivedRegexConstructorSource = """
        using System.Text.RegularExpressions;

        public sealed class Derived : Regex
        {
            public Derived(string pattern)
                : base(pattern)
            {
            }
        }

        public sealed class Sample
        {
            public Derived Create() => new Derived(/*!*/"a+");
        }
        """;

    /// <summary>
    /// A late bound call. The argument list has a parent, but that parent binds to no symbol at all, so
    /// there is nothing to classify.
    /// </summary>
    private const string DynamicInvocationSource = """
        public class Sample
        {
            public void Run(dynamic target) => target.IsMatch(/*!*/"a+");
        }
        """;

    /// <summary>
    /// An indexer access. Its arguments are <see cref="ArgumentSyntax" /> just as a call's are, but they
    /// sit in a <see cref="BracketedArgumentListSyntax" />, which is no argument list of a call.
    /// </summary>
    private const string ElementAccessSource = """
        using System.Collections.Generic;

        public class Sample
        {
            public int Get(Dictionary<string, int> map) => map[/*!*/"a+"];
        }
        """;

    /// <summary>
    /// A tuple expression, whose elements are <see cref="ArgumentSyntax" /> without any argument list
    /// around them at all.
    /// </summary>
    private const string TupleElementSource = """
        public class Sample
        {
            public (string, int) Get() => (/*!*/"a+", 2);
        }
        """;

    /// <summary>
    /// An attribute that is neither of the two known ones, carrying a string in its constructor and one in
    /// a property. Both positions look exactly like a pattern and neither is one.
    /// </summary>
    private const string UnrelatedAttributeSource = """
        using System;

        public sealed class MarkerAttribute : Attribute
        {
            public MarkerAttribute(string pattern) => Pattern = pattern;

            public string Pattern { get; }

            public string Note { get; set; } = string.Empty;
        }

        [Marker(/*!*/"a+", Note = "unrelated")]
        public sealed class Sample
        {
        }
        """;

    private const string UnrelatedAttributePropertySource = """
        using System;

        public sealed class MarkerAttribute : Attribute
        {
            public MarkerAttribute(string pattern) => Pattern = pattern;

            public string Pattern { get; }

            public string Note { get; set; } = string.Empty;
        }

        [Marker("a+", Note = /*!*/"unrelated")]
        public sealed class Sample
        {
        }
        """;

    /// <summary>
    /// A hand-written <c>GeneratedRegexAttribute</c> of an unrelated namespace, with the signature of the
    /// framework's one. The attribute type is resolved by its exact metadata name, so this declaration
    /// never answers - not even on a target framework that has no real one.
    /// </summary>
    private const string UnrelatedGeneratedRegexAttributeSource = """
        namespace Unrelated
        {
            using System;

            public sealed class GeneratedRegexAttribute : Attribute
            {
                public GeneratedRegexAttribute(string pattern) => Pattern = pattern;

                public string Pattern { get; }
            }

            public static class Sample
            {
                [GeneratedRegex(/*!*/"a+")]
                public static void Create()
                {
                }
            }
        }
        """;

    /// <summary>
    /// A parenthesized pattern literal. The literal is then the expression of the parenthesis and not of
    /// the argument, so the locator does not see it. That is a site it misses, never a wrong one, and it is
    /// pinned here because the shape of the decision - a switch over the literal's parent - is what makes
    /// it so.
    /// </summary>
    private const string ParenthesizedPatternSource = """
        using System.Text.RegularExpressions;

        public class Sample
        {
            public Regex Create() => new Regex((/*!*/"a+"));
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
    /// <summary>
    /// A <c>u8</c>-suffixed string literal sitting at the second-argument position of a call shaped exactly
    /// like a static <c>Regex</c> method, where the pattern would otherwise be looked for. The parameter it
    /// binds to is typed <see cref="ReadOnlySpan{T}" /> of <see cref="byte" />, which a <see cref="string" />
    /// pattern parameter never is; a <c>ReadOnlySpan&lt;byte&gt;</c> parameter needs a reference no .NET
    /// Framework target of this suite carries, which is why the fixture is guarded the same way
    /// <see cref="RegularExpressionSource" /> is.
    /// </summary>
    private const string Utf8LiteralArgumentSource = """
        public static class Sample
        {
            public static bool IsMatch(string input, System.ReadOnlySpan<byte> pattern) => false;

            public static bool Check(string input) => IsMatch(input, /*!*/"a+"u8);
        }
        """;

    private const string RegularExpressionSource = """
        using System.ComponentModel.DataAnnotations;

        public class Model
        {
            [RegularExpression(/*!*/"a+")]
            public string? Name { get; set; }
        }
        """;

    /// <summary>
    /// The pattern of the DataAnnotations attribute followed by an <c>ErrorMessage</c> property, which is
    /// the only real attribute of the two that has a settable property at all. The property argument must
    /// not shift the position of the constructor arguments in front of it.
    /// </summary>
    private const string RegularExpressionWithErrorMessageSource = """
        using System.ComponentModel.DataAnnotations;

        public class Model
        {
            [RegularExpression(/*!*/"a+", ErrorMessage = "no match")]
            public string? Name { get; set; }
        }
        """;

    /// <summary>
    /// The <c>ErrorMessage</c> of the DataAnnotations attribute, a string literal in the very attribute
    /// that carries a pattern. It initializes a property and can never be the pattern.
    /// </summary>
    private const string RegularExpressionErrorMessageSource = """
        using System.ComponentModel.DataAnnotations;

        public class Model
        {
            [RegularExpression("a+", ErrorMessage = /*!*/"no match")]
            public string? Name { get; set; }
        }
        """;
#endif

    [Test]
    public async Task TryLocate_RegexConstructorWithoutOptions_ReportsTheConstructorOriginAndNoOptions()
    {
        var site = LocateInCall("_ = new Regex(/*!*/\"a+\");");

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.RegexConstructor);
            _ = await Assert.That(site.Pattern).IsEqualTo("a+");
            _ = await Assert.That(site.AreOptionsKnown).IsTrue();
            _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.None);
            _ = await Assert.That(site.OptionsExpression).IsNull();
            _ = await Assert.That(site.AttributeArgument).IsNull();
            _ = await Assert.That(site.PatternLiteral.Token.ValueText).IsEqualTo("a+");
        }
    }

    [Test]
    public async Task TryLocate_RegexConstructorWithOptions_ResolvesTheOptions()
    {
        var site = LocateInCall("_ = new Regex(/*!*/\"a+\", RegexOptions.IgnoreCase);");

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.RegexConstructor);
            _ = await Assert.That(site.AreOptionsKnown).IsTrue();
            _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.IgnoreCase);
            _ = await Assert.That(site.OptionsExpression).IsNotNull();
            _ = await Assert.That(site.OptionsExpression!.ToString()).IsEqualTo("RegexOptions.IgnoreCase");
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.AreOptionsKnown).IsTrue();
            _ = await Assert
                .That(site.Options!.Value)
                .IsEqualTo(RegexOptions.IgnorePatternWhitespace | RegexOptions.Multiline);
        }
    }

    [Test]
    public async Task TryLocate_RegexConstructorWithNamedArguments_ResolvesPatternAndOptions()
    {
        var site = LocateInCall("_ = new Regex(options: RegexOptions.Multiline, pattern: /*!*/\"a+\");");

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.RegexConstructor);
            _ = await Assert.That(site.Pattern).IsEqualTo("a+");
            _ = await Assert.That(site.AreOptionsKnown).IsTrue();
            _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.Multiline);
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.RegexConstructor);
            _ = await Assert.That(site.Pattern).IsEqualTo("a+");
            _ = await Assert.That(site.AreOptionsKnown).IsFalse();
            _ = await Assert.That(site.Options.HasValue).IsFalse();
            _ = await Assert.That(site.OptionsExpression).IsNotNull();
            _ = await Assert.That(site.OptionsExpression!.ToString()).IsEqualTo("runtimeOptions");
        }
    }

    [Test]
    public async Task TryLocate_ConstantOptionsField_ResolvesTheOptions()
    {
        var site = LocateSite(ConstantOptionsSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.AreOptionsKnown).IsTrue();
            _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.IgnoreCase | RegexOptions.Multiline);
        }
    }

    /// <summary>
    /// The named argument stands at the position of the parameter it names, the C# 7.2 form, and a
    /// positional argument follows it. Both have to bind to the ordinal they occupy.
    /// </summary>
    [Test]
    public async Task TryLocate_NamedPatternArgumentAtItsOwnPosition_ResolvesPatternAndOptions()
    {
        var site = LocateInCall("_ = new Regex(pattern: /*!*/\"a+\", RegexOptions.Multiline);");

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.RegexConstructor);
            _ = await Assert.That(site.Pattern).IsEqualTo("a+");
            _ = await Assert.That(site.AreOptionsKnown).IsTrue();
            _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.Multiline);
        }
    }

    /// <summary>
    /// The three-argument constructor with every argument named and the pattern written last, which is the
    /// furthest a call can get from the declared order.
    /// </summary>
    [Test]
    public async Task TryLocate_AllConstructorArgumentsNamedAndReordered_ResolvesPatternAndOptions()
    {
        var site = LocateInCall(
            "_ = new Regex(matchTimeout: TimeSpan.FromSeconds(1), options: RegexOptions.Multiline, "
                + "pattern: /*!*/\"a+\");"
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.RegexConstructor);
            _ = await Assert.That(site.Pattern).IsEqualTo("a+");
            _ = await Assert.That(site.AreOptionsKnown).IsTrue();
            _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.Multiline);
        }
    }

    /// <summary>
    /// The target-typed <c>new(...)</c> names no type at all, and it is recognised because the resolved
    /// member is asked for, not the syntax kind.
    /// </summary>
    [Test]
    public async Task TryLocate_TargetTypedNew_ReportsTheConstructorOriginAndResolvesTheOptions()
    {
        var site = LocateInCall("Regex regex = new(/*!*/\"a+\", RegexOptions.IgnoreCase);");

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.RegexConstructor);
            _ = await Assert.That(site.Pattern).IsEqualTo("a+");
            _ = await Assert.That(site.AreOptionsKnown).IsTrue();
            _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.IgnoreCase);
        }
    }

    /// <summary>
    /// Every options expression the compiler folds, whatever it is spelled as. The cast and the
    /// <see langword="default" /> form are constant expressions of the enum type, so their folded value is
    /// as much a fact as a named flag is.
    /// </summary>
    /// <param name="options">The options expression written into the fixture.</param>
    /// <param name="expected">The options the locator has to report.</param>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    [Arguments("ConstantOptions", RegexOptions.IgnoreCase)]
    [Arguments("RegexOptions.None", RegexOptions.None)]
    [Arguments("default(RegexOptions)", RegexOptions.None)]
    [Arguments("(RegexOptions)3", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    public async Task TryLocate_ConstantOptionsExpression_ResolvesTheFoldedValue(string options, RegexOptions expected)
    {
        var site = LocateSite(CreateOptionsSource(options));

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.AreOptionsKnown).IsTrue();
            _ = await Assert.That(site.Options!.Value).IsEqualTo(expected);
            _ = await Assert.That(site.OptionsExpression!.ToString()).IsEqualTo(options);
        }
    }

    /// <summary>
    /// Every options expression the compiler cannot fold, including one that mixes a named flag with a
    /// non-constant term. Reporting <c>RegexOptions.None</c> for any of them would hand a later rewriter a
    /// grammar that the engine may never use, so the answer has to stay unknown, and the expression the
    /// answer was read from is reported all the same.
    /// </summary>
    /// <param name="options">The options expression written into the fixture.</param>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    [Arguments("parameterOptions")]
    [Arguments("localOptions")]
    [Arguments("_readOnlyOptions")]
    [Arguments("GetOptions()")]
    [Arguments("RegexOptions.IgnoreCase | parameterOptions")]
    [Arguments("RegexOptions.IgnoreCase | GetOptions()")]
    public async Task TryLocate_NonConstantOptionsExpression_ReportsTheOptionsAsUnknown(string options)
    {
        var site = LocateSite(CreateOptionsSource(options));

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.RegexConstructor);
            _ = await Assert.That(site.Pattern).IsEqualTo("a+");
            _ = await Assert.That(site.AreOptionsKnown).IsFalse();
            _ = await Assert.That(site.Options.HasValue).IsFalse();
            _ = await Assert.That(site.OptionsExpression!.ToString()).IsEqualTo(options);
        }
    }

    /// <summary>
    /// A <c>base(...)</c> initializer reaching a <c>Regex</c> constructor is a constructor call like any
    /// other, which follows from asking the parent of the argument list instead of testing syntax kinds.
    /// </summary>
    [Test]
    public async Task TryLocate_BaseConstructorInitializer_ReportsTheConstructorOrigin()
    {
        var site = LocateSite(BaseInitializerSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.RegexConstructor);
            _ = await Assert.That(site.Pattern).IsEqualTo("a+");
        }
    }

    [Test]
    [Arguments("_ = Regex.IsMatch(input, /*!*/\"a+\");")]
    [Arguments("_ = Regex.Match(input, /*!*/\"a+\");")]
    [Arguments("_ = Regex.Matches(input, /*!*/\"a+\");")]
    [Arguments("_ = Regex.Replace(input, /*!*/\"a+\", \"b\");")]
    [Arguments("_ = Regex.Replace(input, /*!*/\"a+\", match => \"b\");")]
    [Arguments("_ = Regex.Split(input, /*!*/\"a+\");")]
#if NET7_0_OR_GREATER
    [Arguments("_ = Regex.Count(input, /*!*/\"a+\");")]
    [Arguments("foreach (var match in Regex.EnumerateMatches(input, /*!*/\"a+\")) { }")]
#endif
    public async Task TryLocate_StaticMethodSecondArgument_IsThePattern(string statement)
    {
        var site = LocateInCall(statement);

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.RegexStaticMethod);
            _ = await Assert.That(site.Pattern).IsEqualTo("a+");
            _ = await Assert.That(site.AreOptionsKnown).IsTrue();
            _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.None);
            _ = await Assert.That(site.OptionsExpression).IsNull();
        }
    }

    [Test]
    [Arguments("_ = Regex.IsMatch(input, /*!*/\"a+\", RegexOptions.Singleline);")]
    [Arguments("_ = Regex.IsMatch(input, /*!*/\"a+\", RegexOptions.Singleline, TimeSpan.FromSeconds(1));")]
    [Arguments("_ = Regex.Match(input, /*!*/\"a+\", RegexOptions.Singleline);")]
    [Arguments("_ = Regex.Matches(input, /*!*/\"a+\", RegexOptions.Singleline);")]
    [Arguments("_ = Regex.Replace(input, /*!*/\"a+\", \"b\", RegexOptions.Singleline);")]
    [Arguments("_ = Regex.Replace(input, /*!*/\"a+\", \"b\", RegexOptions.Singleline, TimeSpan.FromSeconds(1));")]
    [Arguments("_ = Regex.Replace(input, /*!*/\"a+\", match => \"b\", RegexOptions.Singleline);")]
    [Arguments("_ = Regex.Split(input, /*!*/\"a+\", RegexOptions.Singleline);")]
    [Arguments("_ = Regex.Split(input, /*!*/\"a+\", RegexOptions.Singleline, TimeSpan.FromSeconds(1));")]
#if NET7_0_OR_GREATER
    [Arguments("_ = Regex.Count(input, /*!*/\"a+\", RegexOptions.Singleline);")]
    [Arguments("foreach (var match in Regex.EnumerateMatches(input, /*!*/\"a+\", RegexOptions.Singleline)) { }")]
#endif
    public async Task TryLocate_StaticMethodWithOptions_ResolvesTheOptions(string statement)
    {
        var site = LocateInCall(statement);

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.RegexStaticMethod);
            _ = await Assert.That(site.AreOptionsKnown).IsTrue();
            _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.Singleline);
        }
    }

    [Test]
    public async Task TryLocate_StaticMethodWithNamedArguments_ResolvesPatternAndOptions()
    {
        var site = LocateInCall(
            "_ = Regex.IsMatch(options: RegexOptions.RightToLeft, pattern: /*!*/\"a+\", input: input);"
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.RegexStaticMethod);
            _ = await Assert.That(site.Pattern).IsEqualTo("a+");
            _ = await Assert.That(site.AreOptionsKnown).IsTrue();
            _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.RightToLeft);
        }
    }

    /// <summary>
    /// The input of a static <c>Regex</c> method is a string literal just as often as the pattern is, and
    /// it sits in the parameter before it. It must never be mistaken for a pattern.
    /// </summary>
    [Test]
    [Arguments("_ = Regex.IsMatch(/*!*/\"input\", \"a+\");")]
    [Arguments("_ = Regex.IsMatch(/*!*/\"input\", \"a+\", RegexOptions.Singleline);")]
    [Arguments("_ = Regex.Replace(input, \"a+\", /*!*/\"b\");")]
    [Arguments("_ = Regex.Replace(input, \"a+\", /*!*/\"b\", RegexOptions.Singleline);")]
    [Arguments("_ = Regex.Escape(/*!*/\"a+\");")]
    [Arguments("_ = Regex.Unescape(/*!*/\"a+\");")]
    [Arguments("_ = new Regex(\"a+\").IsMatch(/*!*/\"input\");")]
    [Arguments("_ = new Regex(\"a+\").IsMatch(/*!*/\"input\", 0);")]
    [Arguments("_ = new Regex(\"a+\").Match(/*!*/\"input\", 0);")]
    [Arguments("_ = new Regex(\"a+\").Split(/*!*/\"input\", 2);")]
    [Arguments("_ = new Regex(\"a+\").Replace(input, /*!*/\"b\");")]
    [Arguments("_ = new Regex(\"a+\").Replace(input, /*!*/\"b\", 1);")]
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

    /// <summary>
    /// A type deriving from <c>Regex</c> declares constructors of its own, and their parameters are not the
    /// framework's. Only the constructor whose containing type is <c>Regex</c> itself is a site, which is
    /// why the <c>base(...)</c> form is recognised and this one is not.
    /// </summary>
    [Test]
    public async Task TryLocate_ConstructorOfADerivedRegexType_ReturnsNull()
    {
        var site = Locate(DerivedRegexConstructorSource);

        _ = await Assert.That(site).IsNull();
    }

    /// <summary>
    /// A late bound call resolves to no symbol at all, so there is no member to classify and no parameter
    /// to bind the literal to.
    /// </summary>
    [Test]
    public async Task TryLocate_LateBoundInvocation_ReturnsNull()
    {
        var (semanticModel, tree) = CreateFixture(DynamicInvocationSource);
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        var invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();

        var site = RegexPatternLocator.TryLocate(node, semanticModel, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(semanticModel.GetSymbolInfo(invocation!).Symbol).IsNull();
            _ = await Assert.That(site).IsNull();
        }
    }

    /// <summary>
    /// An indexer argument and a tuple element are both an <see cref="ArgumentSyntax" />, and neither one
    /// belongs to the argument list of a call.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ArgumentWithoutCallSources))]
    public async Task TryLocate_ArgumentThatBelongsToNoCall_ReturnsNull(string source)
    {
        var (semanticModel, tree) = CreateFixture(source);
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        var site = RegexPatternLocator.TryLocate(node, semanticModel, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(node.Parent is ArgumentSyntax).IsTrue();
            _ = await Assert.That(node.Parent!.Parent is ArgumentListSyntax).IsFalse();
            _ = await Assert.That(site).IsNull();
        }
    }

    /// <summary>
    /// An argument list that belongs to nothing, built with the syntax factory. There is no call to ask the
    /// semantic model about, and the locator has to answer without ever asking.
    /// </summary>
    [Test]
    public async Task TryLocate_DetachedArgumentList_ReturnsNull()
    {
        var (semanticModel, _) = CreateFixture(PlainLiteralSource);
        var list = SyntaxFactory.ArgumentList(
            SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(CreateDetachedLiteral()))
        );
        var literal = (LiteralExpressionSyntax)list.Arguments[0].Expression;

        var site = RegexPatternLocator.TryLocate(literal, semanticModel, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(literal.Parent is ArgumentSyntax).IsTrue();
            _ = await Assert.That(list.Parent).IsNull();
            _ = await Assert.That(site).IsNull();
        }
    }

    /// <summary>
    /// An attribute argument list that belongs to no attribute, built with the syntax factory. The same
    /// answer as for the detached argument list, reached through the attribute half of the locator.
    /// </summary>
    [Test]
    public async Task TryLocate_DetachedAttributeArgumentList_ReturnsNull()
    {
        var (semanticModel, _) = CreateFixture(PlainLiteralSource);
        var list = SyntaxFactory.AttributeArgumentList(
            SyntaxFactory.SingletonSeparatedList(SyntaxFactory.AttributeArgument(CreateDetachedLiteral()))
        );
        var literal = (LiteralExpressionSyntax)list.Arguments[0].Expression;

        var site = RegexPatternLocator.TryLocate(literal, semanticModel, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(literal.Parent is AttributeArgumentSyntax).IsTrue();
            _ = await Assert.That(list.Parent).IsNull();
            _ = await Assert.That(site).IsNull();
        }
    }

    /// <summary>
    /// An attribute that is none of the two known ones, in its constructor argument and in a property
    /// initializer alike, and a hand-written <c>GeneratedRegexAttribute</c> of an unrelated namespace.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(UnrelatedAttributeSources))]
    public async Task TryLocate_LiteralInAnUnrelatedAttribute_ReturnsNull(string source)
    {
        var site = Locate(source);

        _ = await Assert.That(site).IsNull();
    }

    /// <summary>
    /// A parenthesized pattern literal is the expression of the parenthesis, not of the argument, and the
    /// locator therefore does not recognise it. The expectation is pinned so that the limitation is a
    /// decision and not an accident; missing a site costs a mutant, while reporting a wrong one would cost
    /// the correctness of every mutant built on it.
    /// </summary>
    [Test]
    public async Task TryLocate_ParenthesizedPatternLiteral_ReturnsNull()
    {
        var site = Locate(ParenthesizedPatternSource);

        _ = await Assert.That(site).IsNull();
    }

    [Test]
    public async Task TryLocate_VerbatimLiteral_ReportsTheTokenValueAndNotItsText()
    {
        var site = LocateSite(VerbatimPatternSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.Pattern).IsEqualTo("\\d+\\s");
            _ = await Assert.That(site.PatternLiteral.Token.Text).IsEqualTo("@\"\\d+\\s\"");
            _ = await Assert.That(site.PatternLiteral.Token.Text).IsNotEqualTo(site.Pattern);
        }
    }

    [Test]
    public async Task TryLocate_RawStringLiteral_ReportsTheTokenValueAndNotItsText()
    {
        var site = LocateSite(RawStringPatternSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.Pattern).IsEqualTo("\\d+\\s");
            _ = await Assert.That(site.PatternLiteral.Token.Text).IsEqualTo("\"\"\"\\d+\\s\"\"\"");
            _ = await Assert.That(site.PatternLiteral.Token.Text).IsNotEqualTo(site.Pattern);
        }
    }

#if NET7_0_OR_GREATER
    [Test]
    public async Task TryLocate_GeneratedRegexWithoutOptions_ReportsTheAttributeOriginAndNoOptions()
    {
        var site = LocateSite(CreateGeneratedRegexSource("[GeneratedRegex(/*!*/\"a+\")]"));

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.GeneratedRegex);
            _ = await Assert.That(site.Pattern).IsEqualTo("a+");
            _ = await Assert.That(site.AreOptionsKnown).IsTrue();
            _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.None);
            _ = await Assert.That(site.OptionsExpression).IsNull();
            _ = await Assert.That(site.AttributeArgument).IsNotNull();
            _ = await Assert.That(ReferenceEquals(site.AttributeArgument!.Expression, site.PatternLiteral)).IsTrue();
        }
    }

    [Test]
    public async Task TryLocate_GeneratedRegexWithComposedOptions_CombinesTheFlags()
    {
        var site = LocateSite(
            CreateGeneratedRegexSource(
                "[GeneratedRegex(/*!*/\"a+\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]"
            )
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.GeneratedRegex);
            _ = await Assert.That(site.AreOptionsKnown).IsTrue();
            _ = await Assert
                .That(site.Options!.Value)
                .IsEqualTo(RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    [Test]
    public async Task TryLocate_GeneratedRegexWithNamedArguments_ResolvesPatternAndOptions()
    {
        var site = LocateSite(
            CreateGeneratedRegexSource("[GeneratedRegex(options: RegexOptions.Multiline, pattern: /*!*/\"a+\")]")
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.GeneratedRegex);
            _ = await Assert.That(site.Pattern).IsEqualTo("a+");
            _ = await Assert.That(site.AreOptionsKnown).IsTrue();
            _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.Multiline);
        }
    }

    [Test]
    public async Task TryLocate_GeneratedRegexWithMatchTimeout_StillResolvesTheOptions()
    {
        var site = LocateSite(
            CreateGeneratedRegexSource("[GeneratedRegex(/*!*/\"a+\", RegexOptions.Singleline, 250)]")
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.AreOptionsKnown).IsTrue();
            _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.Singleline);
        }
    }

    /// <summary>
    /// The match timeout of <c>[GeneratedRegex]</c> is an <see cref="int" /> literal sitting in the very
    /// same attribute. It is rejected for not being a string literal at all, which happens before any
    /// binding; the position rule itself is proven by the culture name below, which <em>is</em> a string.
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

#if NET8_0_OR_GREATER
    /// <summary>
    /// The overload taking a culture name, whose third parameter is a <see cref="string" /> and whose
    /// options still have to be resolved from the second one.
    /// </summary>
    [Test]
    public async Task TryLocate_GeneratedRegexWithCultureName_ResolvesTheOptions()
    {
        var site = LocateSite(
            CreateGeneratedRegexSource("[GeneratedRegex(/*!*/\"a+\", RegexOptions.IgnoreCase, \"en-US\")]")
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.GeneratedRegex);
            _ = await Assert.That(site.Pattern).IsEqualTo("a+");
            _ = await Assert.That(site.AreOptionsKnown).IsTrue();
            _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.IgnoreCase);
        }
    }

    /// <summary>
    /// The culture name of <c>[GeneratedRegex]</c> is a <em>string</em> literal in the very attribute that
    /// carries the pattern, and it is the only such position either regular expression attribute has. It is
    /// what proves that the pattern is decided by the parameter the literal binds to: a literal of another
    /// type, such as the match timeout, is already rejected for not being a string at all.
    /// </summary>
    [Test]
    public async Task TryLocate_GeneratedRegexCultureName_ReturnsNull()
    {
        var (semanticModel, tree) = CreateFixture(
            CreateGeneratedRegexSource("[GeneratedRegex(\"a+\", RegexOptions.IgnoreCase, /*!*/\"en-US\")]")
        );
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        var site = RegexPatternLocator.TryLocate(node, semanticModel, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(node.Token.Value is string).IsTrue();
            _ = await Assert.That(site).IsNull();
        }
    }
#endif
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.DataAnnotationsRegularExpression);
            _ = await Assert.That(site.Pattern).IsEqualTo("a+");
            _ = await Assert.That(site.AreOptionsKnown).IsTrue();
            _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.None);
            _ = await Assert.That(site.OptionsExpression).IsNull();
            _ = await Assert.That(site.AttributeArgument).IsNotNull();
        }
    }

    /// <summary>
    /// The pattern of the DataAnnotations attribute stays the first constructor argument when a
    /// <c>Name = value</c> argument follows it, which is the only place a real regular expression attribute
    /// can carry one.
    /// </summary>
    [Test]
    public async Task TryLocate_DataAnnotationsRegularExpressionWithErrorMessage_ReportsThePattern()
    {
        var site = LocateSite(RegularExpressionWithErrorMessageSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(site.Origin).IsEqualTo(RegexPatternOrigin.DataAnnotationsRegularExpression);
            _ = await Assert.That(site.Pattern).IsEqualTo("a+");
            _ = await Assert.That(site.AreOptionsKnown).IsTrue();
            _ = await Assert.That(site.Options!.Value).IsEqualTo(RegexOptions.None);
        }
    }

    /// <summary>
    /// The <c>ErrorMessage</c> of the very attribute that carries a pattern initializes a property, so it
    /// binds to no constructor parameter and is no pattern.
    /// </summary>
    [Test]
    public async Task TryLocate_DataAnnotationsErrorMessage_ReturnsNull()
    {
        var site = Locate(RegularExpressionErrorMessageSource);

        _ = await Assert.That(site).IsNull();
    }
#endif

#if !NETFRAMEWORK
    /// <summary>
    /// A <c>u8</c>-suffixed string literal is not rejected by the initial literal-value-type guard the way
    /// the inline comment above it describes: Roslyn stores the plain string value on the token itself -
    /// <c>literal.Token.Value is not string</c> is <see langword="false" /> for it, exactly as it is for an
    /// ordinary string literal - and the conversion to bytes happens later, when the compiler binds the
    /// literal against a target type. What actually keeps a <c>u8</c> literal out of this call is the
    /// parameter-type check further down: it can only ever bind to a parameter typed
    /// <see cref="ReadOnlySpan{T}" /> of <see cref="byte" /> or <see cref="byte" />[], never one of type
    /// <see cref="string" />, so <c>IsPatternSlot</c> - which requires
    /// <c>SpecialType.System_String</c> - rejects it regardless of the initial guard.
    /// </summary>
    [Test]
    public async Task TryLocate_Utf8StringLiteral_ReturnsNull()
    {
        var (semanticModel, tree) = CreateFixture(Utf8LiteralArgumentSource);
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        var site = RegexPatternLocator.TryLocate(node, semanticModel, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(node.Token.Value is string).IsTrue();
            _ = await Assert.That(site).IsNull();
        }
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
        ToSources(UnrelatedRegexConstructorSource, UnrelatedRegexStaticMethodSource);

    /// <summary>
    /// The sources of <see cref="TryLocate_ArgumentThatBelongsToNoCall_ReturnsNull" />.
    /// </summary>
    /// <returns>The two fixtures, one per argument list that is none of a call.</returns>
    public static IEnumerable<Func<string>> ArgumentWithoutCallSources() =>
        ToSources(ElementAccessSource, TupleElementSource);

    /// <summary>
    /// The sources of <see cref="TryLocate_LiteralInAnUnrelatedAttribute_ReturnsNull" />.
    /// </summary>
    /// <returns>The three fixtures, one per attribute position that looks like a pattern.</returns>
    public static IEnumerable<Func<string>> UnrelatedAttributeSources() =>
        ToSources(UnrelatedAttributeSource, UnrelatedAttributePropertySource, UnrelatedGeneratedRegexAttributeSource);

    /// <summary>
    /// Wraps fixture sources into the factories a <c>[MethodDataSource]</c> hands to a test.
    /// </summary>
    /// <param name="sources">The fixture sources.</param>
    /// <returns>One factory per source, in the given order.</returns>
    private static IEnumerable<Func<string>> ToSources(params string[] sources) =>
        sources.Select(source => (Func<string>)(() => source));

    /// <summary>
    /// Creates a string literal that is not part of any syntax tree, which is what the detached list tests
    /// build their argument around.
    /// </summary>
    /// <returns>The created literal, whose token value is the pattern <c>a+</c>.</returns>
    private static LiteralExpressionSyntax CreateDetachedLiteral() =>
        SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal("a+"));

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

    private static string CreateOptionsSource(string options) =>
        OptionsTemplate.Replace(OptionsPlaceholder, options, StringComparison.Ordinal);

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
