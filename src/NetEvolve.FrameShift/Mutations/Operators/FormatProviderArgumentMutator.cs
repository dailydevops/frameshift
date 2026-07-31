namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Drops the <c>System.IFormatProvider</c> argument of a formatting or parsing call, so that
/// <c>value.ToString(CultureInfo.InvariantCulture)</c> runs as <c>value.ToString()</c> and silently falls
/// back to the ambient culture of the executing thread.
/// </summary>
/// <remarks>
/// <para>
/// This is the one operator of the culture-sensitivity family that mutates the argument <em>list</em>
/// instead of a value. The removal is exactly the defect worth surfacing: the explicit provider is what
/// makes formatting and parsing independent of the machine it runs on, and a suite that never asserts the
/// produced text under a second locale cannot tell the two apart. The reported location is therefore the
/// removed argument, not the whole argument list, so that a report points at the provider that vanished.
/// </para>
/// <para>
/// Whether a provider-less overload exists at all is deliberately <em>not</em> checked. Overload
/// resolution of the mutated call is the compiler's job, and reimplementing it here would mean modelling
/// optional parameters, extension methods, generic inference and accessibility a second time, with a
/// different answer than the compiler's whenever the model is wrong. Where no overload accepts the
/// remaining arguments, the mutant does not compile and <see cref="MutantCompiler" /> discards it before
/// it can ever be reported. The absent check is that decision, not a missing guard.
/// </para>
/// <para>
/// Adding a provider to a call that has none is out of scope. It would have to invent both the provider
/// and the overload to route the call to, and neither choice is the code's own: where the invariant and
/// the current culture agree - which is the normal state of a build machine - the added argument changes
/// nothing observable, and where they disagree the mutant reports the same missing assertion the
/// <see cref="CultureInfoMutator" /> already reports for every provider that <em>is</em> written down. A
/// wrong guess at the target overload produces a mutant that never compiles, so the addition would cost
/// a compilation per call site and buy either noise or a duplicate.
/// </para>
/// <para>
/// The provider is resolved semantically, never by the spelling of a type or an argument: the invoked
/// symbol is bound, the first parameter whose type is or implements the <c>System.IFormatProvider</c> of
/// this very compilation is selected, and only then is the matching argument located. A same-named
/// interface from another namespace therefore never matches, and a parameter typed
/// <c>CultureInfo</c> - which implements the interface - does.
/// </para>
/// <para>
/// The argument is located by name for a named argument and by position for a positional one, which
/// covers arguments written out of order and optional parameters left out entirely. Positional matching
/// stays exact even next to a named argument, because C# only allows a positional argument to follow a
/// named one when that named one already sits in its own declared position. A <c>params</c> parameter is
/// never treated as the provider parameter: its argument is one element of an expanded collection, and
/// removing an element does not remove the provider the call formats with.
/// </para>
/// </remarks>
internal sealed class FormatProviderArgumentMutator : MutationOperatorBase
{
    /// <summary>
    /// The metadata name the provider interface is resolved by, so that a same-named interface from
    /// another namespace can never match.
    /// </summary>
    private const string FormatProviderMetadataName = "System.IFormatProvider";

    /// <summary>
    /// The suffix of the only mutation this operator creates.
    /// </summary>
    private const string RemoveSuffix = "remove";

    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds = [SyntaxKind.InvocationExpression];

    /// <summary>
    /// Initializes a new instance of the <see cref="FormatProviderArgumentMutator" /> class.
    /// </summary>
    public FormatProviderArgumentMutator()
        : base("culture.format-provider", MutationKind.FormatProvider, _supportedSyntaxKinds) { }

    /// <inheritdoc />
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // MutationOperatorBase.CreateMutations only hands over nodes of one of the SupportedSyntaxKinds, and
        // SyntaxKind.InvocationExpression is always an invocation expression, so the cast cannot fail and no
        // type test is needed here.
        var invocation = (InvocationExpressionSyntax)node;
        var argumentList = invocation.ArgumentList;

        if (argumentList.Arguments.Count == 0 || IsConstantRequired(invocation))
        {
            return [];
        }

        var provider = semanticModel.Compilation.GetTypeByMetadataName(FormatProviderMetadataName);

        if (provider is null)
        {
            return [];
        }

        if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
        {
            return [];
        }

        var parameterIndex = FindProviderParameter(method, provider);

        if (parameterIndex < 0)
        {
            return [];
        }

        var argumentIndex = FindArgument(argumentList, method.Parameters[parameterIndex].Name, parameterIndex);

        if (argumentIndex < 0)
        {
            return [];
        }

        return [CreateRemoval(argumentList, argumentIndex)];
    }

    /// <summary>
    /// Builds the mutation that removes the argument at <paramref name="argumentIndex" />, keeping the
    /// remaining arguments, their separators and all trivia between them exactly as they were written.
    /// </summary>
    /// <param name="argumentList">The argument list of the invocation.</param>
    /// <param name="argumentIndex">The index of the provider argument inside that list.</param>
    /// <returns>The created mutation, located at the removed argument.</returns>
    /// <remarks>
    /// The mutation is created directly instead of through the <c>CreateMutation</c> helper of the base
    /// class, because the rewritten node is the argument list while the interesting location is the
    /// argument that disappears from it, and only the full constructor takes a location.
    /// </remarks>
    private Mutation CreateRemoval(ArgumentListSyntax argumentList, int argumentIndex)
    {
        var argument = argumentList.Arguments[argumentIndex];
        var replacement = argumentList.WithArguments(argumentList.Arguments.RemoveAt(argumentIndex));
        var removed = argument.ToString();

        return new Mutation(
            Kind,
            $"{Id}.{RemoveSuffix}",
            $"{removed} => (removed)",
            argumentList,
            replacement,
            argument.GetLocation()
        );
    }

    /// <summary>
    /// Finds the first parameter of <paramref name="method" /> whose type is or implements
    /// <paramref name="provider" />, skipping a <c>params</c> parameter.
    /// </summary>
    /// <param name="method">The bound symbol of the invocation.</param>
    /// <param name="provider">The <c>System.IFormatProvider</c> of the analysed compilation.</param>
    /// <returns>The index of that parameter, or <c>-1</c> when the method takes no provider.</returns>
    private static int FindProviderParameter(IMethodSymbol method, INamedTypeSymbol provider)
    {
        var parameters = method.Parameters;

        for (var index = 0; index < parameters.Length; index++)
        {
            if (!parameters[index].IsParams && IsFormatProvider(parameters[index].Type, provider))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Determines whether <paramref name="type" /> is <paramref name="provider" /> itself or implements it.
    /// </summary>
    /// <param name="type">The declared type of a parameter.</param>
    /// <param name="provider">The <c>System.IFormatProvider</c> of the analysed compilation.</param>
    /// <returns><see langword="true" /> if a value of that type is a format provider.</returns>
    private static bool IsFormatProvider(ITypeSymbol type, INamedTypeSymbol provider) =>
        SymbolEqualityComparer.Default.Equals(type, provider)
        || type.AllInterfaces.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, provider));

    /// <summary>
    /// Finds the argument that supplies the parameter <paramref name="parameterName" /> at
    /// <paramref name="parameterIndex" />, by name for a named argument and by position otherwise.
    /// </summary>
    /// <param name="argumentList">The argument list of the invocation.</param>
    /// <param name="parameterName">The name of the provider parameter.</param>
    /// <param name="parameterIndex">The index of the provider parameter in the bound symbol.</param>
    /// <returns>
    /// The index of that argument, or <c>-1</c> when the parameter is optional and was left out.
    /// </returns>
    private static int FindArgument(ArgumentListSyntax argumentList, string parameterName, int parameterIndex)
    {
        var arguments = argumentList.Arguments;

        for (var index = 0; index < arguments.Count; index++)
        {
            var nameColon = arguments[index].NameColon;

            if (nameColon is null)
            {
                if (index == parameterIndex)
                {
                    return index;
                }
            }
            else if (string.Equals(nameColon.Name.Identifier.ValueText, parameterName, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Determines whether <paramref name="node" /> sits in a position that only accepts a compile
    /// time constant, such as an attribute argument, a <see langword="const" /> initializer, a default
    /// parameter value, a <c>case</c> label, a <c>goto case</c> statement or a constant pattern.
    /// </summary>
    /// <param name="node">The node to inspect.</param>
    /// <returns><see langword="true" /> if the node must stay a constant expression.</returns>
    private static bool IsConstantRequired(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case AttributeSyntax:
                case AttributeArgumentSyntax:
                case CaseSwitchLabelSyntax:
                case ConstantPatternSyntax:
                case RelationalPatternSyntax:
                case ParameterSyntax:
                case EnumMemberDeclarationSyntax:
                    return true;

                case GotoStatementSyntax gotoStatement when gotoStatement.IsKind(SyntaxKind.GotoCaseStatement):
                case FieldDeclarationSyntax field when field.Modifiers.Any(SyntaxKind.ConstKeyword):
                case LocalDeclarationStatementSyntax local when local.Modifiers.Any(SyntaxKind.ConstKeyword):
                    return true;

                case MemberDeclarationSyntax:
                case CompilationUnitSyntax:
                    return false;

                default:
                    continue;
            }
        }

        return false;
    }
}
