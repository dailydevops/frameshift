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
/// Covers <see cref="FormatProviderArgumentMutator" />, which drops the <c>System.IFormatProvider</c>
/// argument of a formatting or parsing call so that the call falls back to the ambient culture.
/// </summary>
public class FormatProviderArgumentMutatorTests
{
    private const string ExpressionPlaceholder = "EXPRESSION";

    private const string OperatorId = "culture.format-provider";

    private const string RemoveId = "culture.format-provider.remove";

    /// <summary>
    /// The fixture every table driven case is built from. The locals give the expressions a receiver of
    /// every relevant type, and the parameter gives them a provider that is not a member access, so that
    /// the operator is exercised with a provider passed as a variable as well.
    /// </summary>
    private const string InvocationTemplate = """
        using System;
        using System.Globalization;

        namespace Fixtures
        {
            internal static class Formats
            {
                public static object? Render(IFormatProvider provider)
                {
                    var value = 42;
                    var number = 1.5;
                    var moment = DateTime.UtcNow;
                    var text = "42";

                    return /*!*/EXPRESSION;
                }
            }
        }
        """;

    /// <summary>
    /// A call whose provider is supplied by name while the optional parameter in front of it is left out
    /// entirely, which is the case a purely positional argument lookup would miss.
    /// </summary>
    private const string OptionalParameterSource = """
        using System;
        using System.Globalization;

        namespace Fixtures
        {
            internal static class Formats
            {
                public static string Render(int value, string format = "D", IFormatProvider? provider = null) =>
                    value.ToString(format, provider);

                public static string Use(int value) => /*!*/Render(value, provider: CultureInfo.InvariantCulture);
            }
        }
        """;

    /// <summary>
    /// The same call with the provider left out as well, so that nothing can be removed.
    /// </summary>
    private const string OmittedProviderSource = """
        using System;
        using System.Globalization;

        namespace Fixtures
        {
            internal static class Formats
            {
                public static string Render(int value, string format = "D", IFormatProvider? provider = null) =>
                    value.ToString(format, provider);

                public static string Use(int value) => /*!*/Render(value, "N0");
            }
        }
        """;

    /// <summary>
    /// An extension method invoked in reduced form, whose bound symbol therefore does not carry the
    /// receiver as its first parameter.
    /// </summary>
    private const string ExtensionSource = """
        using System;
        using System.Globalization;

        namespace Fixtures
        {
            internal static class FormatExtensions
            {
                public static string Render(this int value) => value.ToString(CultureInfo.CurrentCulture);

                public static string Render(this int value, IFormatProvider provider) => value.ToString(provider);
            }

            internal static class Formats
            {
                public static string Use(int value) => /*!*/value.Render(CultureInfo.InvariantCulture);
            }
        }
        """;

    /// <summary>
    /// A parameter typed <c>CultureInfo</c>, which is no format provider itself but implements one.
    /// </summary>
    private const string DerivedProviderSource = """
        using System.Globalization;

        namespace Fixtures
        {
            internal static class Formats
            {
                public static string Render(int value) => value.ToString(CultureInfo.CurrentCulture);

                public static string Render(int value, CultureInfo culture) => value.ToString(culture);

                public static string Use(int value) => /*!*/Render(value, CultureInfo.InvariantCulture);
            }
        }
        """;

    /// <summary>
    /// An interface named exactly like the provider interface but declared in another namespace. Resolving
    /// the parameter type by name alone would match it, which is what this fixture forbids.
    /// </summary>
    private const string ForeignProviderSource = """
        namespace Fake
        {
            internal interface IFormatProvider
            {
            }
        }

        namespace Fixtures
        {
            internal static class Formats
            {
                public static string Render(int value, Fake.IFormatProvider provider) => value.ToString();

                public static string Use(int value, Fake.IFormatProvider provider) => /*!*/Render(value, provider);
            }
        }
        """;

    /// <summary>
    /// A provider passed as one element of an expanded <see langword="params"/> array. The parameter the argument
    /// belongs to is the array, not the provider, so there is no provider argument to remove.
    /// </summary>
    private const string ParamsSource = """
        using System;
        using System.Globalization;

        namespace Fixtures
        {
            internal static class Formats
            {
                public static string Render(params object[] values) => string.Join(", ", values);

                public static string Use() => /*!*/Render(CultureInfo.InvariantCulture);
            }
        }
        """;

    /// <summary>
    /// A call to a method that does not exist. The fixture deliberately does not compile, because an
    /// invocation whose symbol cannot be bound is exactly what the guard is about.
    /// </summary>
    private const string UnresolvedSource = """
        using System.Globalization;

        namespace Fixtures
        {
            internal static class Formats
            {
                public static string Use() => /*!*/Render(CultureInfo.InvariantCulture);
            }
        }
        """;

    /// <summary>
    /// A <see langword="const" /> initializer holding an invocation. The fixture deliberately does not
    /// compile - a method call is never a compile time constant - because that is the only way to place a
    /// bindable invocation in a constant context, which is what the guard has to skip.
    /// </summary>
    private const string ConstantFieldSource = """
        using System;
        using System.Globalization;

        namespace Fixtures
        {
            internal static class Formats
            {
                private const string Rendered = /*!*/string.Format(CultureInfo.InvariantCulture, "{0}", 42);

                public static string Use() => Rendered;
            }
        }
        """;

    /// <summary>
    /// An attribute argument holding an invocation, with the same deliberate compile error as
    /// <see cref="ConstantFieldSource" /> and for the same reason.
    /// </summary>
    private const string AttributeArgumentSource = """
        using System;
        using System.Globalization;

        namespace Fixtures
        {
            internal static class Formats
            {
                [Obsolete(/*!*/string.Format(CultureInfo.InvariantCulture, "{0}", 42))]
                public static string Use() => string.Empty;
            }
        }
        """;

    /// <summary>
    /// A default parameter value holding an invocation, with the same deliberate compile error as
    /// <see cref="ConstantFieldSource" /> and for the same reason.
    /// </summary>
    private const string DefaultParameterSource = """
        using System;
        using System.Globalization;

        internal static class Formats
        {
            public static string Use(string text = /*!*/string.Format(CultureInfo.InvariantCulture, "{0}", 1)) =>
                text;
        }
        """;

    /// <summary>
    /// A field initializer without the <see langword="const" /> modifier, which is an ordinary initializer
    /// and therefore no constant context at all.
    /// </summary>
    private const string NonConstantFieldSource = """
        using System;
        using System.Globalization;

        internal static class Formats
        {
            private static readonly string Text = /*!*/string.Format(CultureInfo.InvariantCulture, "{0}", 1);

            public static string Use() => Text;
        }
        """;

    /// <summary>
    /// A local declaration without the <see langword="const" /> modifier, the other half of the
    /// <see langword="const" /> guard on the walk up the parent chain.
    /// </summary>
    private const string NonConstantLocalSource = """
        using System;
        using System.Globalization;

        internal static class Formats
        {
            public static string Use()
            {
                var text = /*!*/string.Format(CultureInfo.InvariantCulture, "{0}", 1);

                return text;
            }
        }
        """;

    private const string TriviaSource = """
        using System;
        using System.Globalization;

        namespace Fixtures
        {
            internal static class Formats
            {
                // a comment above the invocation
                public static string Render(int first, int second)
                {
                    return /*!*/string.Format(
                        CultureInfo.InvariantCulture,
                        // the format string
                        "{0}-{1}",
                        first /* the first value */,
                        second
                    ); // a comment behind the invocation
                }
            }
        }
        """;

    private const string TriviaExpected = """
        using System;
        using System.Globalization;

        namespace Fixtures
        {
            internal static class Formats
            {
                // a comment above the invocation
                public static string Render(int first, int second)
                {
                    return /*!*/string.Format(
                        // the format string
                        "{0}-{1}",
                        first /* the first value */,
                        second
                    ); // a comment behind the invocation
                }
            }
        }
        """;

    /// <summary>
    /// A call whose only overload takes a provider, so that removing the argument cannot bind to anything.
    /// </summary>
    private const string ProviderOnlyOverloadSource = """
        using System;
        using System.Globalization;

        namespace Fixtures
        {
            internal static class Formats
            {
                public static string Render(int value, IFormatProvider provider) => value.ToString(provider);

                public static string Use(int value) => /*!*/Render(value, CultureInfo.InvariantCulture);
            }
        }
        """;

    private static readonly FormatProviderArgumentMutator _mutator = new FormatProviderArgumentMutator();

    [Test]
    public async Task SupportedSyntaxKinds_Always_IsTheInvocationKind()
    {
        SyntaxKind[] expected = [SyntaxKind.InvocationExpression];

        _ = await Assert.That(_mutator.SupportedSyntaxKinds.ToArray()).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Metadata_Always_IdentifiesTheFormatProviderFamily()
    {
        using (Assert.Multiple())
        {
            _ = await Assert.That(_mutator.Id).IsEqualTo(OperatorId);
            _ = await Assert.That(_mutator.Kind).IsEqualTo(MutationKind.FormatProvider);
        }
    }

    /// <summary>
    /// The provider argument is removed wherever it sits, whatever the receiver is and however the
    /// argument is written: positionally, by name, out of order, and as a variable instead of a member
    /// access. Every case pins the exact rewritten source, not only the number of mutations.
    /// </summary>
    /// <param name="expression">The invocation the fixture contains.</param>
    /// <param name="expected">The invocation the mutant must contain.</param>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    [Arguments("value.ToString(CultureInfo.InvariantCulture)", "value.ToString()")]
    [Arguments("value.ToString(\"D\", CultureInfo.InvariantCulture)", "value.ToString(\"D\")")]
    [Arguments("number.ToString(\"F2\", CultureInfo.InvariantCulture)", "number.ToString(\"F2\")")]
    [Arguments("moment.ToString(\"o\", CultureInfo.InvariantCulture)", "moment.ToString(\"o\")")]
    [Arguments("string.Format(CultureInfo.InvariantCulture, \"{0}\", value)", "string.Format(\"{0}\", value)")]
    [Arguments("Convert.ToString(value, CultureInfo.InvariantCulture)", "Convert.ToString(value)")]
    [Arguments("int.Parse(text, CultureInfo.InvariantCulture)", "int.Parse(text)")]
    [Arguments(
        "int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)",
        "int.TryParse(text, NumberStyles.Integer, out var parsed)"
    )]
    [Arguments("int.Parse(text, provider: CultureInfo.InvariantCulture)", "int.Parse(text)")]
    [Arguments("int.Parse(provider: CultureInfo.InvariantCulture, s: text)", "int.Parse(s: text)")]
    [Arguments("int.Parse(s: text, provider: CultureInfo.InvariantCulture)", "int.Parse(s: text)")]
    [Arguments("value.ToString(provider)", "value.ToString()")]
    [Arguments("string.Format(provider, \"{0}\", value)", "string.Format(\"{0}\", value)")]
    public async Task CreateMutations_ProviderArgument_RemovesIt(string expression, string expected)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(expected);

        string[] expectedIds = [RemoveId];
        var (mutations, _, tree, _, errors) = Mutate(CreateSource(expression));

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(OperatorIds(mutations)).IsEquivalentTo(expectedIds);
            _ = await Assert.That(mutations.Single().Kind).IsEqualTo(MutationKind.FormatProvider);
            _ = await Assert.That(mutations.Single().ApplyTo(tree).ToString()).IsEqualTo(CreateSource(expected));
        }
    }

    [Test]
    public async Task CreateMutations_ProviderArgument_ReportsTheRemovedArgumentAsTheLocation()
    {
        var source = CreateSource("value.ToString(CultureInfo.InvariantCulture)");
        var (mutations, _, tree, _, errors) = Mutate(source);
        var invocation = SyntaxNodeLocator.FindMarked<InvocationExpressionSyntax>(tree);
        var mutation = mutations.Single();

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(mutation.OperatorId).IsEqualTo(RemoveId);
            _ = await Assert.That(mutation.DisplayName).IsEqualTo("CultureInfo.InvariantCulture => (removed)");
            _ = await Assert.That(mutation.Location.SourceSpan).IsEqualTo(invocation.ArgumentList.Arguments[0].Span);
            _ = await Assert.That(mutation.Original.ToString()).IsEqualTo("(CultureInfo.InvariantCulture)");
            _ = await Assert.That(mutation.Replacement.ToString()).IsEqualTo("()");
        }
    }

    [Test]
    public async Task ApplyTo_ProviderRemoved_ProducesCompilableSource()
    {
        var (mutations, _, tree, _, _) = Mutate(CreateSource("value.ToString(CultureInfo.InvariantCulture)"));
        var mutated = mutations.Single().ApplyTo(tree).ToString();
        var compilation = CompilationFactory.Create(mutated);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutated).Contains("value.ToString()");
            _ = await Assert.That(Describe(CompilationFactory.GetCompileErrors(compilation))).IsEqualTo(string.Empty);
        }
    }

    /// <summary>
    /// The provider is named while the optional parameter in front of it is omitted, so the argument is
    /// found by name and the removal leaves a call that still binds.
    /// </summary>
    [Test]
    public async Task CreateMutations_NamedProviderBehindAnOmittedOptionalParameter_RemovesIt()
    {
        string[] expectedIds = [RemoveId];
        var (mutations, _, tree, model, errors) = Mutate(OptionalParameterSource);
        var expected = OptionalParameterSource.Replace(
            "Render(value, provider: CultureInfo.InvariantCulture)",
            "Render(value)",
            StringComparison.Ordinal
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(ParameterNames(tree, model)).IsEqualTo("value, format, provider");
            _ = await Assert.That(OperatorIds(mutations)).IsEquivalentTo(expectedIds);
            _ = await Assert.That(mutations.Single().ApplyTo(tree).ToString()).IsEqualTo(expected);
        }
    }

    /// <summary>
    /// The reduced symbol of an extension method does not carry the receiver as a parameter, so the
    /// provider sits at the very position the argument list uses for it.
    /// </summary>
    [Test]
    public async Task CreateMutations_ReducedExtensionMethod_RemovesTheProvider()
    {
        string[] expectedIds = [RemoveId];
        var (mutations, _, tree, model, errors) = Mutate(ExtensionSource);
        var expected = ExtensionSource.Replace(
            "value.Render(CultureInfo.InvariantCulture)",
            "value.Render()",
            StringComparison.Ordinal
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(MethodKindOf(tree, model)).IsEqualTo(MethodKind.ReducedExtension);
            _ = await Assert.That(ParameterNames(tree, model)).IsEqualTo("provider");
            _ = await Assert.That(OperatorIds(mutations)).IsEquivalentTo(expectedIds);
            _ = await Assert.That(mutations.Single().ApplyTo(tree).ToString()).IsEqualTo(expected);
        }
    }

    /// <summary>
    /// A parameter typed <c>CultureInfo</c> is no format provider itself, it implements one, and the
    /// operator has to accept it.
    /// </summary>
    [Test]
    public async Task CreateMutations_ParameterImplementsTheProviderInterface_RemovesIt()
    {
        string[] expectedIds = [RemoveId];
        var (mutations, _, tree, model, errors) = Mutate(DerivedProviderSource);
        var expected = DerivedProviderSource.Replace(
            "Render(value, CultureInfo.InvariantCulture)",
            "Render(value)",
            StringComparison.Ordinal
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(ParameterTypes(tree, model)).IsEqualTo("int, System.Globalization.CultureInfo");
            _ = await Assert.That(OperatorIds(mutations)).IsEquivalentTo(expectedIds);
            _ = await Assert.That(mutations.Single().ApplyTo(tree).ToString()).IsEqualTo(expected);
        }
    }

    /// <summary>
    /// The interface of the parameter is named <c>IFormatProvider</c> but is not the one of the framework,
    /// so nothing is removed. The assertion on the parameter type pins that the fixture really binds to
    /// the foreign interface.
    /// </summary>
    [Test]
    public async Task CreateMutations_ForeignInterfaceOfTheSameName_ReturnsEmpty()
    {
        var (mutations, _, tree, model, errors) = Mutate(ForeignProviderSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(ParameterTypes(tree, model)).IsEqualTo("int, Fake.IFormatProvider");
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// The provider is one element of an expanded <see langword="params"/> array. The parameter it belongs to is the
    /// array, which is no format provider, so no argument qualifies for removal.
    /// </summary>
    [Test]
    public async Task CreateMutations_ProviderInsideAParamsArray_ReturnsEmpty()
    {
        var (mutations, _, tree, model, errors) = Mutate(ParamsSource);
        var method = ResolveMethod(tree, model);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(method?.Parameters[0].IsParams).IsTrue();
            _ = await Assert.That(ParameterTypes(tree, model)).IsEqualTo("object[]");
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// A call without an <c>IFormatProvider</c> parameter offers nothing to remove, whatever else it takes.
    /// </summary>
    /// <param name="expression">The invocation the fixture contains.</param>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    [Arguments("value.ToString()")]
    [Arguments("value.ToString(\"D\")")]
    [Arguments("string.Format(\"{0}\", value)")]
    [Arguments("int.Parse(text)")]
    [Arguments("text.Substring(1)")]
    public async Task CreateMutations_WithoutAnyProviderParameter_ReturnsEmpty(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        var (mutations, _, _, _, errors) = Mutate(CreateSource(expression));

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(mutations.ToArray()).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_OptionalProviderLeftOut_ReturnsEmpty()
    {
        var (mutations, _, tree, model, errors) = Mutate(OmittedProviderSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(ParameterNames(tree, model)).IsEqualTo("value, format, provider");
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// The invocation binds to nothing at all, so there is no parameter list to inspect. The fixture
    /// deliberately does not compile; the symbol assertion pins its shape instead of its compile errors.
    /// </summary>
    [Test]
    public async Task CreateMutations_UnresolvableSymbol_ReturnsEmpty()
    {
        var (mutations, _, tree, model, _) = Mutate(UnresolvedSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(ResolveMethod(tree, model)).IsNull();
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// An invocation in a constant context is skipped although it binds. Both fixtures deliberately do not
    /// compile, because a constant context accepting a method call does not exist; the symbol assertion
    /// proves the invocation was bound and the empty result therefore comes from the guard.
    /// </summary>
    /// <param name="source">The fixture source.</param>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    [Arguments(ConstantFieldSource)]
    [Arguments(AttributeArgumentSource)]
    [Arguments(DefaultParameterSource)]
    public async Task CreateMutations_ConstantContext_ReturnsEmpty(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var (mutations, _, tree, model, _) = Mutate(source);

        using (Assert.Multiple())
        {
            _ = await Assert.That(ResolveMethod(tree, model)?.Name).IsEqualTo("Format");
            _ = await Assert.That(ParameterTypes(tree, model)).IsEqualTo("System.IFormatProvider, string, object");
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// A field and a local declaration without the <see langword="const" /> modifier are ordinary
    /// initializers, so the walk up the parent chain continues past them instead of treating them as a
    /// position that requires a constant, and the provider is removed as it is anywhere else.
    /// </summary>
    /// <param name="source">The fixture source.</param>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    [Arguments(NonConstantFieldSource)]
    [Arguments(NonConstantLocalSource)]
    public async Task CreateMutations_ProviderInANonConstantInitializer_RemovesIt(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        string[] expectedIds = [RemoveId];
        var expected = source.Replace(
            "string.Format(CultureInfo.InvariantCulture, \"{0}\", 1)",
            "string.Format(\"{0}\", 1)",
            StringComparison.Ordinal
        );
        var (mutations, _, tree, _, errors) = Mutate(source);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(OperatorIds(mutations)).IsEquivalentTo(expectedIds);
            _ = await Assert.That(mutations.Single().ApplyTo(tree).ToString()).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task ApplyTo_ProviderRemoved_KeepsTheTriviaOfTheRemainingArguments()
    {
        var (mutations, _, tree, _, errors) = Mutate(TriviaSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(mutations.Single().ApplyTo(tree).ToString()).IsEqualTo(TriviaExpected);
        }
    }

    /// <summary>
    /// Documents the interaction with mutant viability: the removal is offered even where no provider-less
    /// overload exists, because the operator deliberately does not reimplement overload resolution.
    /// <c>MutantCompiler</c> is what discards such a mutant, which this test proves by verifying it.
    /// </summary>
    [Test]
    public async Task CreateMutations_WithoutAProviderLessOverload_IsOfferedAndDiscardedByTheCompiler()
    {
        string[] expectedIds = [RemoveId];
        var (mutations, compilation, tree, _, errors) = Mutate(ProviderOnlyOverloadSource);
        var mutation = mutations.Single();
        var viability = new MutantCompiler(compilation).Verify(mutation, tree, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(OperatorIds(mutations)).IsEquivalentTo(expectedIds);
            _ = await Assert.That(mutation.ApplyTo(tree).ToString()).Contains("Render(value)");
            _ = await Assert.That(viability).IsEqualTo(MutantViability.DoesNotCompile);
        }
    }

    private static string CreateSource(string expression) =>
        InvocationTemplate.Replace(ExpressionPlaceholder, expression, StringComparison.Ordinal);

    private static string[] OperatorIds(ImmutableArray<Mutation> mutations) =>
        [.. mutations.Select(mutation => mutation.OperatorId)];

    private static IMethodSymbol? ResolveMethod(SyntaxTree tree, SemanticModel model)
    {
        var invocation = SyntaxNodeLocator.FindMarked<InvocationExpressionSyntax>(tree);

        return model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
    }

    private static MethodKind? MethodKindOf(SyntaxTree tree, SemanticModel model) =>
        ResolveMethod(tree, model)?.MethodKind;

    private static string ParameterNames(SyntaxTree tree, SemanticModel model) =>
        string.Join(", ", Parameters(tree, model).Select(parameter => parameter.Name));

    /// <summary>
    /// Renders the parameter types of the resolved call, with nullable reference annotations dropped: the
    /// reference assemblies of the modern targets annotate <c>string.Format</c> while the classic ones do
    /// not, and the annotation says nothing about the guard these assertions pin.
    /// </summary>
    /// <param name="tree">The tree the fixture was parsed into.</param>
    /// <param name="model">The semantic model of that tree.</param>
    /// <returns>The rendered parameter types.</returns>
    private static string ParameterTypes(SyntaxTree tree, SemanticModel model) =>
        string.Join(
            ", ",
            Parameters(tree, model)
                .Select(parameter => parameter.Type.WithNullableAnnotation(NullableAnnotation.None))
                .Select(type => type.ToDisplayString())
        );

    private static ImmutableArray<IParameterSymbol> Parameters(SyntaxTree tree, SemanticModel model)
    {
        var method = ResolveMethod(tree, model);

        return method is null ? [] : method.Parameters;
    }

    private static (
        ImmutableArray<Mutation> Mutations,
        CSharpCompilation Compilation,
        SyntaxTree Tree,
        SemanticModel Model,
        string Errors
    ) Mutate(string source)
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var node = SyntaxNodeLocator.FindMarked<InvocationExpressionSyntax>(tree);
        var created = _mutator.CreateMutations(node, semanticModel, CancellationToken.None);
        ImmutableArray<Mutation> mutations = [.. created];
        var errors = Describe(CompilationFactory.GetCompileErrors(compilation));

        return (mutations, compilation, tree, semanticModel, errors);
    }

    private static string Describe(ImmutableArray<Diagnostic> errors) =>
        string.Join(", ", errors.Select(error => error.GetMessage(CultureInfo.InvariantCulture)));
}
