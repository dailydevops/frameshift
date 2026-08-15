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
/// Covers <see cref="StringMethodMutator" />, which mutates calls to well known <see cref="string" />
/// methods along the pairs <c>StartsWith</c> / <c>EndsWith</c>, <c>Trim</c> / <c>TrimStart</c> /
/// <c>TrimEnd</c> and the static <c>IsNullOrEmpty</c> / <c>IsNullOrWhiteSpace</c>.
/// </summary>
public class StringMethodMutatorTests
{
    private const string PlainOverloadTemplate = """
        internal static class Checks
        {
            public static bool Check(string value, string other) => value.METHOD(other);
        }
        """;

    private const string ComparisonOverloadTemplate = """
        using System;

        internal static class Checks
        {
            public static bool Check(string value, string other, StringComparison comparison) =>
                value.METHOD(other, comparison);
        }
        """;

    private const string CultureOverloadTemplate = """
        namespace Fixtures;

        using System.Globalization;

        internal static class Checks
        {
            public static bool Check(string value, string other, CultureInfo culture) =>
                value.METHOD(other, true, culture);
        }
        """;

    private const string TrimTemplate = """
        internal static class Trims
        {
            public static string Trim(string value) => value.METHOD();
        }
        """;

    private const string IsNullOrTemplate = """
        internal static class Checks
        {
            public static bool Check(string value) => string.METHOD(value);
        }
        """;

    private const string UserDefinedStartsWithSource = """
        internal sealed class Label
        {
            public bool StartsWith(string value) => true;

            public bool EndsWith(string value) => true;
        }

        internal static class Checks
        {
            public static bool Check(Label label) => /*!*/label.StartsWith("x");
        }
        """;

    private const string UserDefinedTrimSource = """
        internal sealed class Label
        {
            public string Trim() => "x";

            public string TrimStart() => "x";

            public string TrimEnd() => "x";
        }

        internal static class Checks
        {
            public static string Check(Label label) => /*!*/label.Trim();
        }
        """;

    private const string UserDefinedStaticTemplate = """
        internal static class MyString
        {
            public static bool IsNullOrEmpty(string value) => true;

            public static bool IsNullOrWhiteSpace(string value) => true;
        }

        internal static class Checks
        {
            public static bool Check(string value) => /*!*/MyString.IsNullOrEmpty(value);
        }
        """;

    /// <summary>
    /// An extension method named exactly like one of the covered pairs is never mistaken for the real
    /// instance method: the bound symbol is an extension method, which the operator rejects before it
    /// ever looks at the containing type.
    /// </summary>
    private const string ExtensionMethodMatchingNameSource = """
        internal sealed class Label
        {
            public string Value => "x";
        }

        internal static class LabelExtensions
        {
            public static bool StartsWith(this Label label, string value) => label.Value == value;
        }

        internal static class Checks
        {
            public static bool Check(Label label) => /*!*/label.StartsWith("x");
        }
        """;

    /// <summary>
    /// A call the semantic model cannot resolve to any method at all - here, an ambiguous overload
    /// resolution failure - binds to no <see cref="IMethodSymbol" />, which the operator has to reject
    /// rather than crash on.
    /// </summary>
    private const string UnresolvedCallSource = """
        using NamespaceA;
        using NamespaceB;

        namespace NamespaceA
        {
            internal static class Ext
            {
                internal static void Handle(this string value) { }
            }
        }

        namespace NamespaceB
        {
            internal static class Ext
            {
                internal static void Handle(this string value) { }
            }
        }

        internal static class Checks
        {
            public static void Check(string value) => /*!*/value.Handle();
        }
        """;

    /// <summary>
    /// A <see cref="string" /> method outside the seven covered names reaches the operator like any
    /// other invocation, and is rejected once its name does not match any of the known pairs.
    /// </summary>
    private const string UnrelatedStringMethodSource = """
        internal static class Checks
        {
            public static string Check(string value) => /*!*/value.Substring(1);
        }
        """;

    private const string AttributeArgumentSource = """
        namespace Fixtures;

        internal sealed class NameAttribute : System.Attribute
        {
            public NameAttribute(bool value) => Value = value;

            public bool Value { get; }
        }

        internal static class Checks
        {
            [Name("x".StartsWith("y"))]
            public static bool Check(string value) => value.Length > 0;
        }
        """;

    private const string ConstFieldSource = """
        internal static class Checks
        {
            private const bool StartsWithY = "x".StartsWith("y");

            public static bool Check() => StartsWithY;
        }
        """;

    private const string ConstLocalSource = """
        internal static class Checks
        {
            public static bool Check(string value)
            {
                const bool startsWithY = "x".StartsWith("y");

                return startsWithY;
            }
        }
        """;

    private const string DefaultParameterSource = """
        internal static class Checks
        {
            public static bool Check(bool value = "x".StartsWith("y")) => value;
        }
        """;

    private static readonly StringMethodMutator _mutator = new StringMethodMutator();

    [Test]
    public async Task SupportedSyntaxKinds_Always_IsTheInvocationKind()
    {
        SyntaxKind[] expected = [SyntaxKind.InvocationExpression];

        _ = await Assert.That(_mutator.SupportedSyntaxKinds.ToArray()).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Metadata_Always_IdentifiesTheStringMethodFamily()
    {
        using (Assert.Multiple())
        {
            _ = await Assert.That(_mutator.Id).IsEqualTo("string-method");
            _ = await Assert.That(_mutator.Kind).IsEqualTo(MutationKind.StringMethod);
        }
    }

    /// <summary>
    /// <c>StartsWith</c> and <c>EndsWith</c> are offered as each other's counterpart, whatever overload
    /// was called: the plain string overload, the one carrying a <c>StringComparison</c> and the one
    /// carrying a <see cref="bool"/> and a <c>CultureInfo</c>.
    /// </summary>
    [Test]
    [Arguments("StartsWith", "EndsWith", PlainOverloadTemplate)]
    [Arguments("EndsWith", "StartsWith", PlainOverloadTemplate)]
    [Arguments("StartsWith", "EndsWith", ComparisonOverloadTemplate)]
    [Arguments("EndsWith", "StartsWith", ComparisonOverloadTemplate)]
    [Arguments("StartsWith", "EndsWith", CultureOverloadTemplate)]
    [Arguments("EndsWith", "StartsWith", CultureOverloadTemplate)]
    public async Task CreateMutations_StartsWithOrEndsWith_ProducesTheCounterpart(
        string source,
        string target,
        string template
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(template);

        string[] expectedNames = [$"{source} => {target}"];
        var (mutations, _, tree, _, errors) = MutateCall(Fill(template, source), source);
        var mutated = mutations.Single().ApplyTo(tree).ToString();
        var compilation = CompilationFactory.Create(mutated);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(Join(DisplayNames(mutations))).IsEqualTo(Join(expectedNames));
            _ = await Assert.That(Join(Kinds(mutations))).IsEqualTo("StringMethod");
            _ = await Assert.That(mutated).IsEqualTo(Fill(template, target));
            _ = await Assert.That(Describe(CompilationFactory.GetCompileErrors(compilation))).IsEqualTo(string.Empty);
        }
    }

    /// <summary>
    /// Each of <c>Trim</c>, <c>TrimStart</c> and <c>TrimEnd</c> offers the other two as its mutations,
    /// the same "rotate N into each other" shape <see cref="ArithmeticAssignmentMutator" /> uses for the
    /// compound arithmetic assignments.
    /// </summary>
    /// <remarks>
    /// The classic .NET Framework targets only declare a parameterless overload for <c>Trim</c>, not for
    /// <c>TrimStart</c>/<c>TrimEnd</c> (those only take a <c>params char[]</c> there); a parameterless
    /// <c>Trim()</c> call therefore has no matching-overload counterpart to rename to on those targets,
    /// which a separate, classic-framework-only test covers instead. A parameterless
    /// <c>TrimStart()</c>/<c>TrimEnd()</c> call, on the other hand, still
    /// binds to the one-parameter <c>params char[]</c> overload there, which <c>Trim(char[])</c> matches
    /// just fine, so those two directions are unaffected and stay in this parameterised test.
    /// </remarks>
    [Test]
#if NET6_0_OR_GREATER
    [Arguments("Trim", "TrimStart", "TrimEnd")]
#endif
    [Arguments("TrimStart", "Trim", "TrimEnd")]
    [Arguments("TrimEnd", "Trim", "TrimStart")]
    public async Task CreateMutations_TrimFamily_ProducesTheOtherTwo(
        string source,
        string firstTarget,
        string secondTarget
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(firstTarget);
        ArgumentNullException.ThrowIfNull(secondTarget);

        string[] expected = [$"{source} => {firstTarget}", $"{source} => {secondTarget}"];
        var (mutations, _, tree, _, errors) = MutateCall(Fill(TrimTemplate, source), source);
        var mutatedFirst = Pick(mutations, source, firstTarget).ApplyTo(tree).ToString();
        var mutatedSecond = Pick(mutations, source, secondTarget).ApplyTo(tree).ToString();

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(Join(DisplayNames(mutations))).IsEqualTo(Join(expected));
            _ = await Assert.That(mutatedFirst).IsEqualTo(Fill(TrimTemplate, firstTarget));
            _ = await Assert.That(mutatedSecond).IsEqualTo(Fill(TrimTemplate, secondTarget));
            _ = await Assert
                .That(Describe(CompilationFactory.GetCompileErrors(CompilationFactory.Create(mutatedFirst))))
                .IsEqualTo(string.Empty);
            _ = await Assert
                .That(Describe(CompilationFactory.GetCompileErrors(CompilationFactory.Create(mutatedSecond))))
                .IsEqualTo(string.Empty);
        }
    }

#if !NET6_0_OR_GREATER
    /// <summary>
    /// On the classic .NET Framework targets, a parameterless <c>Trim()</c> call binds to the one real
    /// zero-parameter overload <see cref="string" /> declares there, but <c>TrimStart</c>/<c>TrimEnd</c>
    /// only declare a <c>params char[]</c> overload on those targets - no zero-parameter overload at
    /// all - so neither is a matching-overload counterpart and the call is left untouched.
    /// </summary>
    [Test]
    public async Task CreateMutations_TrimWithNoMatchingClassicOverload_ReturnsEmpty()
    {
        var (mutations, _, _, _, errors) = MutateCall(Fill(TrimTemplate, "Trim"), "Trim");

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }
#endif

    /// <summary>
    /// The static <c>IsNullOrEmpty</c> and <c>IsNullOrWhiteSpace</c> are offered as each other's
    /// counterpart, called as <c>string.METHOD(value)</c> rather than as an instance call.
    /// </summary>
    [Test]
    [Arguments("IsNullOrEmpty", "IsNullOrWhiteSpace")]
    [Arguments("IsNullOrWhiteSpace", "IsNullOrEmpty")]
    public async Task CreateMutations_IsNullOrEmptyOrIsNullOrWhiteSpace_ProducesTheCounterpart(
        string source,
        string target
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        string[] expected = [$"{source} => {target}"];
        var (mutations, _, tree, model, errors) = MutateCall(Fill(IsNullOrTemplate, source), source);
        var method = model.GetSymbolInfo(FindCall(tree, source)).Symbol as IMethodSymbol;
        var mutated = mutations.Single().ApplyTo(tree).ToString();

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(method?.IsStatic).IsTrue();
            _ = await Assert.That(Join(DisplayNames(mutations))).IsEqualTo(Join(expected));
            _ = await Assert.That(mutated).IsEqualTo(Fill(IsNullOrTemplate, target));
            _ = await Assert
                .That(Describe(CompilationFactory.GetCompileErrors(CompilationFactory.Create(mutated))))
                .IsEqualTo(string.Empty);
        }
    }

    /// <summary>
    /// A user-defined type may declare methods named exactly like the covered pairs, with a matching
    /// shape; none of them is a method of <see cref="string" /> itself, so none of them is mutated.
    /// </summary>
    [Test]
    [Arguments(UserDefinedStartsWithSource)]
    [Arguments(UserDefinedTrimSource)]
    public async Task CreateMutations_MethodOnAnotherType_ReturnsEmpty(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var (mutations, node, _, model, errors) = MutateMarked(source);
        var invocation = (InvocationExpressionSyntax)node;
        var method = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(method?.ContainingType.Name).IsEqualTo("Label");
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// A user-defined type may also declare static methods named exactly like
    /// <c>IsNullOrEmpty</c>/<c>IsNullOrWhiteSpace</c>; they are not declared on <see cref="string" />, so
    /// they are left untouched.
    /// </summary>
    [Test]
    public async Task CreateMutations_StaticMethodOnAnotherType_ReturnsEmpty()
    {
        var (mutations, node, _, model, errors) = MutateMarked(UserDefinedStaticTemplate);
        var invocation = (InvocationExpressionSyntax)node;
        var method = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(method?.ContainingType.Name).IsEqualTo("MyString");
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// An extension method sharing the name of a covered pair is never mistaken for the real
    /// <see cref="string" /> instance method: <see cref="IMethodSymbol.IsExtensionMethod" /> is checked
    /// before the containing type even matters.
    /// </summary>
    [Test]
    public async Task CreateMutations_ExtensionMethodWithMatchingName_ReturnsEmpty()
    {
        var (mutations, node, _, model, errors) = MutateMarked(ExtensionMethodMatchingNameSource);
        var invocation = (InvocationExpressionSyntax)node;
        var method = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(method?.IsExtensionMethod).IsTrue();
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// A call the semantic model cannot resolve to a single method - here, an ambiguous overload - binds
    /// to no <see cref="IMethodSymbol" /> at all, which the operator has to reject rather than crash on.
    /// </summary>
    [Test]
    public async Task CreateMutations_UnresolvedInvocation_ReturnsEmpty()
    {
        var (mutations, node, _, model, errors) = MutateMarked(UnresolvedCallSource);
        var invocation = (InvocationExpressionSyntax)node;
        var symbol = model.GetSymbolInfo(invocation).Symbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsNotEqualTo(string.Empty);
            _ = await Assert.That(symbol as IMethodSymbol).IsNull();
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// A <see cref="string" /> method outside the seven covered names is rejected once its name does not
    /// match any of the known pairs, regardless of it being declared on <see cref="string" /> itself.
    /// </summary>
    [Test]
    public async Task CreateMutations_UnrelatedStringMethod_ReturnsEmpty()
    {
        var (mutations, node, _, model, errors) = MutateMarked(UnrelatedStringMethodSource);
        var invocation = (InvocationExpressionSyntax)node;
        var method = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(method?.Name).IsEqualTo("Substring");
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// A string method call is never a compile time constant, so every one of these fixtures deliberately
    /// does not compile. They are still the only way to place the call in a position that demands a
    /// constant, which is exactly what the operator has to skip; the symbol assertions therefore pin that
    /// the call binds to <c>StartsWith</c> of <see cref="string" /> and that only the position kept the
    /// mutations away.
    /// </summary>
    /// <param name="source">The fixture source.</param>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    [Arguments(AttributeArgumentSource)]
    [Arguments(ConstFieldSource)]
    [Arguments(ConstLocalSource)]
    [Arguments(DefaultParameterSource)]
    public async Task CreateMutations_StringMethodInAConstantContext_ReturnsEmpty(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var (mutations, node, _, model, _) = MutateCall(source, "StartsWith");
        var method = model.GetSymbolInfo(node).Symbol as IMethodSymbol;

        using (Assert.Multiple())
        {
            _ = await Assert.That(method?.Name).IsEqualTo("StartsWith");
            _ = await Assert.That(method?.ContainingType.SpecialType).IsEqualTo(SpecialType.System_String);
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    private static string Fill(string template, string methodName) =>
        template.Replace("METHOD", methodName, StringComparison.Ordinal);

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
