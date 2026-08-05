namespace NetEvolve.FrameShift.Tests.Unit.Mutations.Operators;

using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Mutations;
using NetEvolve.FrameShift.Mutations.Operators;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers <see cref="CaseConversionMutator" />, which mutates the case conversions of
/// <see cref="string" /> along the four pairs <c>ToUpper</c> / <c>ToUpperInvariant</c>,
/// <c>ToLower</c> / <c>ToLowerInvariant</c>, <c>ToUpper</c> / <c>ToLower</c> and
/// <c>ToUpperInvariant</c> / <c>ToLowerInvariant</c>.
/// </summary>
public class CaseConversionMutatorTests
{
    private const string MethodPlaceholder = "METHOD";

    private const string ConversionTemplate = """
        internal static class Conversions
        {
            public static string Convert(string value) => value.METHOD();
        }
        """;

    private const string TriviaSource = """
        internal static class Conversions
        {
            // a comment above the conversion
            public static string Convert(string value)
            {
                return value /* between */.ToUpper(); // a comment behind the conversion
            }
        }
        """;

    private const string CultureArgumentSource = """
        namespace Fixtures;

        using System.Globalization;

        internal static class Conversions
        {
            public static string Convert(string value, CultureInfo culture) => value.ToUpper(culture);
        }
        """;

    private const string ChainedSource = """
        internal static class Conversions
        {
            public static string Convert(string value) => value.Trim().ToUpper().Trim();
        }
        """;

    private const string InterpolationSource = """
        internal static class Conversions
        {
            public static string Describe(string value) => $"[{value.ToUpper()}]";
        }
        """;

    private const string ConditionalAccessSource = """
        internal static class Conversions
        {
            public static string? Convert(string? value) => value?.ToUpper();
        }
        """;

    private const string MethodGroupSource = """
        internal static class Conversions
        {
            public static System.Func<string> Select(string value) => /*!*/value.ToUpper;
        }
        """;

    private const string ExtensionMethodSource = """
        internal static class CaseExtensions
        {
            public static string ToUpper(this string value, int repeat) => value;
        }

        internal static class Conversions
        {
            public static string Convert(string value) => value.ToUpper(2);
        }
        """;

    private const string UserDefinedTemplate = """
        internal sealed class Label
        {
            public string ToUpper() => "U";

            public string ToLower() => "l";

            public string ToUpperInvariant() => "U";

            public string ToLowerInvariant() => "l";
        }

        internal static class Conversions
        {
            public static string Convert(Label label) => label.METHOD();
        }
        """;

    private const string StaticUserDefinedSource = """
        internal static class Label
        {
            public static string ToUpper(string value) => value;
        }

        internal static class Conversions
        {
            public static string Convert(string value) => Label.ToUpper(value);
        }
        """;

    private const string CharSource = """
        internal static class Conversions
        {
            public static char Convert(string value) => char.ToUpper(value[0]);
        }
        """;

    private const string TextInfoSource = """
        namespace Fixtures;

        using System.Globalization;

        internal static class Conversions
        {
            public static string Convert(string value, CultureInfo culture) => culture.TextInfo.ToUpper(value);
        }
        """;

    private const string AttributeArgumentSource = """
        namespace Fixtures;

        internal sealed class NameAttribute : System.Attribute
        {
            public NameAttribute(string value) => Value = value;

            public string Value { get; }
        }

        internal static class Conversions
        {
            [Name("x".ToUpper())]
            public static string Convert(string value) => value;
        }
        """;

    private const string ConstFieldSource = """
        internal static class Conversions
        {
            private const string Upper = "x".ToUpper();

            public static string Convert() => Upper;
        }
        """;

    private const string ConstLocalSource = """
        internal static class Conversions
        {
            public static string Convert(string value)
            {
                const string upper = "x".ToUpper();

                return upper + value;
            }
        }
        """;

    private const string DefaultParameterSource = """
        internal static class Conversions
        {
            public static string Convert(string value = "x".ToUpper()) => value;
        }
        """;

    /// <summary>
    /// A conversion reached through a pointer, which is a member access of the pointer kind and not a simple
    /// one. The fixture deliberately does not compile - the test compilations forbid unsafe code - because
    /// the pointer form is the only member access kind the guard has to refuse.
    /// </summary>
    private const string PointerAccessSource = """
        internal struct Box
        {
            public string ToUpper() => "U";
        }

        internal static class Conversions
        {
            public static unsafe string Convert(Box* box) => /*!*/box->ToUpper();
        }
        """;

    /// <summary>
    /// A call to <c>ToUpper</c> that no overload accepts, so the invocation binds to nothing at all. The
    /// fixture deliberately does not compile, because an unbound invocation is what the guard is about.
    /// </summary>
    private const string UnresolvedOverloadSource = """
        internal static class Conversions
        {
            public static string Convert(string value) => /*!*/value.ToUpper(1, 2, 3);
        }
        """;

    private const string DelegateInvokeSource = """
        internal static class Conversions
        {
            public static string Convert(System.Func<string> factory) => /*!*/factory.Invoke();
        }
        """;

    private const string OtherStringMethodSource = """
        internal static class Conversions
        {
            public static string Convert(string value) => /*!*/value.Trim();
        }
        """;

    private const string UnexpectedParametersTemplate = """
        internal sealed class Label
        {
            public string ToUpperInvariant(int repeat) => "U";

            public string ToUpper(int first, int second) => "U";
        }

        internal static class Conversions
        {
            public static string Convert(Label label) => /*!*/label.METHOD;
        }
        """;

    private const string NonConstantFieldSource = """
        internal sealed class Conversions
        {
            private readonly string _upper = /*!*/"x".ToUpper();

            public string Convert() => _upper;
        }
        """;

    private const string NonConstantLocalSource = """
        internal static class Conversions
        {
            public static string Convert(string value)
            {
                var upper = /*!*/value.ToUpper();

                return upper;
            }
        }
        """;

    private static readonly CaseConversionMutator _mutator = new CaseConversionMutator();

    [Test]
    public async Task SupportedSyntaxKinds_Always_IsTheInvocationKind()
    {
        SyntaxKind[] expected = [SyntaxKind.InvocationExpression];

        _ = await Assert.That(_mutator.SupportedSyntaxKinds.ToArray()).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Metadata_Always_IdentifiesTheCaseConversionFamily()
    {
        using (Assert.Multiple())
        {
            _ = await Assert.That(_mutator.Id).IsEqualTo("culture.case-conversion");
            _ = await Assert.That(_mutator.Kind).IsEqualTo(MutationKind.CaseConversion);
        }
    }

    /// <summary>
    /// Every one of the four conversions is offered its culture counterpart and its direction
    /// counterpart, which together are the eight directions of the four pairs.
    /// </summary>
    /// <param name="source">The called conversion.</param>
    /// <param name="firstTarget">The culture counterpart.</param>
    /// <param name="secondTarget">The direction counterpart.</param>
    /// <param name="firstId">The operator id of the culture counterpart.</param>
    /// <param name="secondId">The operator id of the direction counterpart.</param>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    [Arguments(
        "ToUpper",
        "ToUpperInvariant",
        "ToLower",
        "culture.case-conversion.upper-to-upper-invariant",
        "culture.case-conversion.upper-to-lower"
    )]
    [Arguments(
        "ToLower",
        "ToLowerInvariant",
        "ToUpper",
        "culture.case-conversion.lower-to-lower-invariant",
        "culture.case-conversion.lower-to-upper"
    )]
    [Arguments(
        "ToUpperInvariant",
        "ToUpper",
        "ToLowerInvariant",
        "culture.case-conversion.upper-invariant-to-upper",
        "culture.case-conversion.upper-invariant-to-lower-invariant"
    )]
    [Arguments(
        "ToLowerInvariant",
        "ToLower",
        "ToUpperInvariant",
        "culture.case-conversion.lower-invariant-to-lower",
        "culture.case-conversion.lower-invariant-to-upper-invariant"
    )]
    public async Task CreateMutations_ParameterlessConversion_ProducesBothCounterparts(
        string source,
        string firstTarget,
        string secondTarget,
        string firstId,
        string secondId
    )
    {
        ArgumentNullException.ThrowIfNull(source);

        string[] expectedNames = [$"{source} => {firstTarget}", $"{source} => {secondTarget}"];
        string[] expectedIds = [firstId, secondId];
        var (mutations, node, _, _, errors) = MutateCall(CreateSource(source), source);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(Join(DisplayNames(mutations))).IsEqualTo(Join(expectedNames));
            _ = await Assert.That(Join(OperatorIds(mutations))).IsEqualTo(Join(expectedIds));
            _ = await Assert.That(Join(Kinds(mutations))).IsEqualTo("CaseConversion, CaseConversion");
            _ = await Assert.That(mutations[0].Original.ToString()).IsEqualTo($"value.{source}()");
            _ = await Assert.That(mutations[0].Location.SourceSpan).IsEqualTo(node.Span);
        }
    }

    /// <summary>
    /// The rewrite of a parameterless conversion is the renamed call and nothing else, and every one of
    /// the eight mutants compiles - including the ones leaving the invariant form, which keep the empty
    /// argument list and therefore bind to the parameterless culture aware overload.
    /// </summary>
    /// <param name="source">The called conversion.</param>
    /// <param name="target">The conversion the mutant calls.</param>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    [Arguments("ToUpper", "ToUpperInvariant")]
    [Arguments("ToUpper", "ToLower")]
    [Arguments("ToLower", "ToLowerInvariant")]
    [Arguments("ToLower", "ToUpper")]
    [Arguments("ToUpperInvariant", "ToUpper")]
    [Arguments("ToUpperInvariant", "ToLowerInvariant")]
    [Arguments("ToLowerInvariant", "ToLower")]
    [Arguments("ToLowerInvariant", "ToUpperInvariant")]
    public async Task ApplyTo_ParameterlessConversion_ProducesTheRenamedCall(string source, string target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var (mutations, _, tree, _, errors) = MutateCall(CreateSource(source), source);
        var mutated = Pick(mutations, source, target).ApplyTo(tree).ToString();
        var compilation = CompilationFactory.Create(mutated);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(mutated).IsEqualTo(CreateSource(target));
            _ = await Assert.That(Describe(CompilationFactory.GetCompileErrors(compilation))).IsEqualTo(string.Empty);
        }
    }

    [Test]
    public async Task ApplyTo_UpperToUpperInvariant_KeepsTheSurroundingTrivia()
    {
        var expected = TriviaSource.Replace("ToUpper()", "ToUpperInvariant()", StringComparison.Ordinal);
        var (mutations, _, tree, _, errors) = MutateCall(TriviaSource, "ToUpper");
        var mutated = Pick(mutations, "ToUpper", "ToUpperInvariant").ApplyTo(tree).ToString();

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(mutated).IsEqualTo(expected);
        }
    }

    /// <summary>
    /// <c>ToUpper(culture)</c> and <c>ToUpperInvariant()</c> are different overloads, so the mutant has to
    /// drop the argument along with the rename, while the mutant that stays culture aware keeps it.
    /// </summary>
    [Test]
    public async Task CreateMutations_ConversionWithACulture_ProducesBothCounterparts()
    {
        string[] expected = ["ToUpper => ToUpperInvariant", "ToUpper => ToLower"];
        var (mutations, node, _, model, errors) = MutateCall(CultureArgumentSource, "ToUpper");
        var method = model.GetSymbolInfo(node).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(method?.Parameters.Length).IsEqualTo(1);
            _ = await Assert.That(method?.Parameters[0].Type.Name).IsEqualTo("CultureInfo");
            _ = await Assert.That(Join(DisplayNames(mutations))).IsEqualTo(Join(expected));
        }
    }

    [Test]
    public async Task ApplyTo_ConversionWithACulture_DropsTheArgumentForTheInvariantForm()
    {
        var expected = CultureArgumentSource.Replace(
            "value.ToUpper(culture)",
            "value.ToUpperInvariant()",
            StringComparison.Ordinal
        );
        var (mutations, _, tree, _, errors) = MutateCall(CultureArgumentSource, "ToUpper");
        var mutated = Pick(mutations, "ToUpper", "ToUpperInvariant").ApplyTo(tree).ToString();
        var compilation = CompilationFactory.Create(mutated);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(mutated).IsEqualTo(expected);
            _ = await Assert.That(Describe(CompilationFactory.GetCompileErrors(compilation))).IsEqualTo(string.Empty);
        }
    }

    [Test]
    public async Task ApplyTo_ConversionWithACulture_KeepsTheArgumentForTheCultureAwareForm()
    {
        var expected = CultureArgumentSource.Replace(
            "value.ToUpper(culture)",
            "value.ToLower(culture)",
            StringComparison.Ordinal
        );
        var (mutations, _, tree, _, errors) = MutateCall(CultureArgumentSource, "ToUpper");
        var mutated = Pick(mutations, "ToUpper", "ToLower").ApplyTo(tree).ToString();
        var compilation = CompilationFactory.Create(mutated);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(mutated).IsEqualTo(expected);
            _ = await Assert.That(Describe(CompilationFactory.GetCompileErrors(compilation))).IsEqualTo(string.Empty);
        }
    }

    [Test]
    public async Task ApplyTo_ConversionInsideAChain_RewritesOnlyTheConversion()
    {
        string[] expected = ["ToUpper => ToUpperInvariant", "ToUpper => ToLower"];
        var expectedSource = ChainedSource.Replace("ToUpper()", "ToLower()", StringComparison.Ordinal);
        var (mutations, node, tree, _, errors) = MutateCall(ChainedSource, "ToUpper");
        var mutated = Pick(mutations, "ToUpper", "ToLower").ApplyTo(tree).ToString();

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(node.ToString()).IsEqualTo("value.Trim().ToUpper()");
            _ = await Assert.That(Join(DisplayNames(mutations))).IsEqualTo(Join(expected));
            _ = await Assert.That(mutated).IsEqualTo(expectedSource);
        }
    }

    [Test]
    public async Task ApplyTo_ConversionInsideAnInterpolation_RewritesTheCall()
    {
        string[] expected = ["ToUpper => ToUpperInvariant", "ToUpper => ToLower"];
        var expectedSource = InterpolationSource.Replace("ToUpper()", "ToUpperInvariant()", StringComparison.Ordinal);
        var (mutations, _, tree, _, errors) = MutateCall(InterpolationSource, "ToUpper");
        var mutated = Pick(mutations, "ToUpper", "ToUpperInvariant").ApplyTo(tree).ToString();
        var compilation = CompilationFactory.Create(mutated);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(Join(DisplayNames(mutations))).IsEqualTo(Join(expected));
            _ = await Assert.That(mutated).IsEqualTo(expectedSource);
            _ = await Assert.That(Describe(CompilationFactory.GetCompileErrors(compilation))).IsEqualTo(string.Empty);
        }
    }

    /// <summary>
    /// A null-conditional call has no member access to rename, its callee is a member binding, so it is
    /// left untouched even though the bound method is the one of <see cref="string" />.
    /// </summary>
    [Test]
    public async Task CreateMutations_NullConditionalConversion_ReturnsEmpty()
    {
        var (mutations, node, _, model, errors) = Mutate(
            ConditionalAccessSource,
            SyntaxNodeLocator.FindFirst<InvocationExpressionSyntax>
        );
        var invocation = (InvocationExpressionSyntax)node;
        var method = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(invocation.Expression.Kind()).IsEqualTo(SyntaxKind.MemberBindingExpression);
            _ = await Assert.That(method?.ContainingType.SpecialType).IsEqualTo(SpecialType.System_String);
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// A conversion used as a method group is no invocation at all, so the syntax kind filter of the base
    /// class already keeps it away from the operator.
    /// </summary>
    [Test]
    public async Task CreateMutations_ConversionUsedAsAMethodGroup_ReturnsEmpty()
    {
        var (mutations, node, _, _, errors) = Mutate(MethodGroupSource, SyntaxNodeLocator.FindMarked);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(node.Kind()).IsEqualTo(SyntaxKind.SimpleMemberAccessExpression);
            _ = await Assert
                .That(_mutator.SupportedSyntaxKinds.Contains(SyntaxKind.SimpleMemberAccessExpression))
                .IsFalse();
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// An extension method may well be called <c>ToUpper</c> and may well extend <see cref="string" />,
    /// but it is declared by another type, so it is none of the conversions this operator knows.
    /// </summary>
    [Test]
    public async Task CreateMutations_ExtensionMethodNamedLikeAConversion_ReturnsEmpty()
    {
        var (mutations, node, _, model, errors) = MutateCall(ExtensionMethodSource, "ToUpper");
        var method = model.GetSymbolInfo(node).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(method?.IsExtensionMethod).IsTrue();
            _ = await Assert.That(method?.ContainingType.Name).IsEqualTo("CaseExtensions");
            _ = await Assert.That(method?.ContainingType.SpecialType).IsEqualTo(SpecialType.None);
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// A user-defined type may declare all four names; none of them is a conversion of
    /// <see cref="string" />, so none of them is mutated.
    /// </summary>
    /// <param name="methodName">The called conversion of the user-defined type.</param>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    [Arguments("ToUpper")]
    [Arguments("ToLower")]
    [Arguments("ToUpperInvariant")]
    [Arguments("ToLowerInvariant")]
    public async Task CreateMutations_ConversionOnAnotherType_ReturnsEmpty(string methodName)
    {
        ArgumentNullException.ThrowIfNull(methodName);

        var source = UserDefinedTemplate.Replace(MethodPlaceholder, methodName, StringComparison.Ordinal);
        var (mutations, node, _, model, errors) = MutateCall(source, methodName);
        var method = model.GetSymbolInfo(node).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(method?.Name).IsEqualTo(methodName);
            _ = await Assert.That(method?.ContainingType.Name).IsEqualTo("Label");
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// A user-defined <see langword="static" /> method named like a conversion is otherwise
    /// indistinguishable from a valid call: its <see cref="MethodKind" /> is <see cref="MethodKind.Ordinary" />,
    /// it is not an extension method, its name is one of the four well-known ones and its single
    /// <see cref="string" /> parameter matches <c>HasExpectedParameters</c>. Only the <c>IsStatic</c>
    /// guard keeps it from being treated as a conversion of <see cref="string" />, so this fixture pins
    /// that guard down in isolation instead of it being incidentally covered by a fixture whose containing
    /// type would have failed the later check regardless.
    /// </summary>
    [Test]
    public async Task CreateMutations_StaticConversionOnAnotherType_ReturnsEmpty()
    {
        var (mutations, node, _, model, errors) = MutateCall(StaticUserDefinedSource, "ToUpper");
        var method = model.GetSymbolInfo(node).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(method?.Name).IsEqualTo("ToUpper");
            _ = await Assert.That(method?.ContainingType.Name).IsEqualTo("Label");
            _ = await Assert.That(method?.MethodKind).IsEqualTo(MethodKind.Ordinary);
            _ = await Assert.That(method?.IsExtensionMethod).IsFalse();
            _ = await Assert.That(method?.IsStatic).IsTrue();
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// <c>char.ToUpper</c> is a static conversion of another type and <c>TextInfo.ToUpper</c> an instance
    /// conversion of another type; neither is a conversion of <see cref="string" />.
    /// </summary>
    /// <param name="source">The fixture source.</param>
    /// <param name="expectedTypeName">The type the call is declared on.</param>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    [Arguments(CharSource, "Char")]
    [Arguments(TextInfoSource, "TextInfo")]
    public async Task CreateMutations_ConversionOfAnotherBclType_ReturnsEmpty(string source, string expectedTypeName)
    {
        ArgumentNullException.ThrowIfNull(source);

        var (mutations, node, _, model, errors) = MutateCall(source, "ToUpper");
        var method = model.GetSymbolInfo(node).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(method?.ContainingType.Name).IsEqualTo(expectedTypeName);
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// A case conversion is never a compile time constant, so every one of these fixtures deliberately
    /// does not compile. They are still the only way to place the call in a position that demands a
    /// constant, which is exactly what the operator has to skip; the symbol assertions therefore pin that
    /// the call binds to the conversion of <see cref="string" /> and that only the position kept the
    /// mutations away.
    /// </summary>
    /// <param name="source">The fixture source.</param>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    [Arguments(AttributeArgumentSource)]
    [Arguments(ConstFieldSource)]
    [Arguments(ConstLocalSource)]
    [Arguments(DefaultParameterSource)]
    public async Task CreateMutations_ConversionInAConstantContext_ReturnsEmpty(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var (mutations, node, _, model, _) = MutateCall(source, "ToUpper");
        var method = model.GetSymbolInfo(node).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(method?.Name).IsEqualTo("ToUpper");
            _ = await Assert.That(method?.ContainingType.SpecialType).IsEqualTo(SpecialType.System_String);
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// A pointer member access is a <see cref="MemberAccessExpressionSyntax" /> as well, so the type test
    /// alone would let it through; the kind test is what refuses it.
    /// </summary>
    [Test]
    public async Task CreateMutations_PointerMemberAccess_ReturnsEmpty()
    {
        var (mutations, node, _, _, _) = MutateMarked(PointerAccessSource);
        var invocation = (InvocationExpressionSyntax)node;

        using (Assert.Multiple())
        {
            _ = await Assert.That(invocation.Expression is MemberAccessExpressionSyntax).IsTrue();
            _ = await Assert.That(invocation.Expression.Kind()).IsEqualTo(SyntaxKind.PointerMemberAccessExpression);
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// Without a bound method symbol there is nothing to compare against <see cref="string" />, so the
    /// operator stays silent instead of renaming a call it does not understand.
    /// </summary>
    [Test]
    public async Task CreateMutations_UnresolvableConversion_ReturnsEmpty()
    {
        var (mutations, node, _, model, _) = MutateMarked(UnresolvedOverloadSource);
        var info = model.GetSymbolInfo(node);

        using (Assert.Multiple())
        {
            _ = await Assert.That(info.Symbol).IsNull();
            _ = await Assert.That(info.CandidateReason).IsEqualTo(CandidateReason.OverloadResolutionFailure);
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// The invoked symbol of a delegate call is the <c>Invoke</c> of the delegate type, whose method kind is
    /// not an ordinary one. That kind is the first thing the operator looks at.
    /// </summary>
    [Test]
    public async Task CreateMutations_DelegateInvocation_ReturnsEmpty()
    {
        var (mutations, node, _, model, errors) = MutateMarked(DelegateInvokeSource);
        var method = model.GetSymbolInfo(node).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(method?.MethodKind).IsEqualTo(MethodKind.DelegateInvoke);
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// An ordinary instance method of <see cref="string" /> that is none of the four conversions has no
    /// counterpart to offer, which is the other side of the name lookup.
    /// </summary>
    [Test]
    public async Task CreateMutations_OtherInstanceMethodOfString_ReturnsEmpty()
    {
        var (mutations, node, _, model, errors) = MutateMarked(OtherStringMethodSource);
        var method = model.GetSymbolInfo(node).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(method?.Name).IsEqualTo("Trim");
            _ = await Assert.That(method?.ContainingType.SpecialType).IsEqualTo(SpecialType.System_String);
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// The parameter list is checked before the containing type, so a method carrying one of the four names
    /// with a parameter list <see cref="string" /> never declares is refused by that check. An invariant
    /// conversion takes no argument at all, and none of the four takes two.
    /// </summary>
    /// <param name="call">The call the fixture contains.</param>
    /// <param name="expectedName">The name of the called method.</param>
    /// <param name="expectedParameterCount">The number of parameters that method declares.</param>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    [Arguments("ToUpperInvariant(2)", "ToUpperInvariant", 1)]
    [Arguments("ToUpper(1, 2)", "ToUpper", 2)]
    public async Task CreateMutations_ConversionNameWithAnUnexpectedParameterList_ReturnsEmpty(
        string call,
        string expectedName,
        int expectedParameterCount
    )
    {
        ArgumentNullException.ThrowIfNull(call);

        var source = UnexpectedParametersTemplate.Replace(MethodPlaceholder, call, StringComparison.Ordinal);
        var (mutations, node, _, model, errors) = MutateMarked(source);
        var method = model.GetSymbolInfo(node).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(method?.Name).IsEqualTo(expectedName);
            _ = await Assert.That(method?.Parameters.Length).IsEqualTo(expectedParameterCount);
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// A field and a local declaration without the <see langword="const" /> modifier are ordinary
    /// initializers, so the walk up the parent chain continues past them instead of treating them as a
    /// position that requires a constant.
    /// </summary>
    /// <param name="source">The fixture source.</param>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    [Arguments(NonConstantFieldSource)]
    [Arguments(NonConstantLocalSource)]
    public async Task CreateMutations_ConversionInANonConstantInitializer_ProducesBothCounterparts(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        string[] expected = ["ToUpper => ToUpperInvariant", "ToUpper => ToLower"];
        string[] expectedIds =
        [
            "culture.case-conversion.upper-to-upper-invariant",
            "culture.case-conversion.upper-to-lower",
        ];
        var (mutations, _, _, _, errors) = MutateMarked(source);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(Join(DisplayNames(mutations))).IsEqualTo(Join(expected));
            _ = await Assert.That(Join(OperatorIds(mutations))).IsEqualTo(Join(expectedIds));
        }
    }

    private static string CreateSource(string methodName) =>
        ConversionTemplate.Replace(MethodPlaceholder, methodName, StringComparison.Ordinal);

    private static Mutation Pick(ImmutableArray<Mutation> mutations, string source, string target)
    {
        var displayName = $"{source} => {target}";

        return mutations.Single(mutation => string.Equals(mutation.DisplayName, displayName, StringComparison.Ordinal));
    }

    private static InvocationExpressionSyntax FindCall(SyntaxTree tree, string methodName) =>
        SyntaxNodeLocator.FindFirst<InvocationExpressionSyntax>(
            tree,
            invocation =>
                invocation.Expression is MemberAccessExpressionSyntax access
                && string.Equals(access.Name.Identifier.ValueText, methodName, StringComparison.Ordinal)
        );

    private static string[] DisplayNames(ImmutableArray<Mutation> mutations) =>
        [.. mutations.Select(mutation => mutation.DisplayName)];

    private static string[] OperatorIds(ImmutableArray<Mutation> mutations) =>
        [.. mutations.Select(mutation => mutation.OperatorId)];

    private static string[] Kinds(ImmutableArray<Mutation> mutations) =>
        [.. mutations.Select(mutation => mutation.Kind.ToString())];

    private static string Join(IEnumerable<string> values) => string.Join(", ", values);

    private static (
        ImmutableArray<Mutation> Mutations,
        SyntaxNode Node,
        SyntaxTree Tree,
        SemanticModel Model,
        string Errors
    ) MutateCall(string source, string methodName) => Mutate(source, syntaxTree => FindCall(syntaxTree, methodName));

    private static (
        ImmutableArray<Mutation> Mutations,
        SyntaxNode Node,
        SyntaxTree Tree,
        SemanticModel Model,
        string Errors
    ) MutateMarked(string source) => Mutate(source, SyntaxNodeLocator.FindMarked<InvocationExpressionSyntax>);

    private static (
        ImmutableArray<Mutation> Mutations,
        SyntaxNode Node,
        SyntaxTree Tree,
        SemanticModel Model,
        string Errors
    ) Mutate(string source, Func<SyntaxTree, SyntaxNode> selector)
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var node = selector(tree);
        var created = _mutator.CreateMutations(node, semanticModel, CancellationToken.None);
        ImmutableArray<Mutation> mutations = [.. created];

        return (mutations, node, tree, semanticModel, Describe(CompilationFactory.GetCompileErrors(compilation)));
    }

    private static string Describe(ImmutableArray<Diagnostic> errors) =>
        string.Join(", ", errors.Select(error => error.GetMessage(CultureInfo.InvariantCulture)));
}
