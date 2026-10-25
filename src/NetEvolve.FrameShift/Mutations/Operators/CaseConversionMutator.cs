namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Mutates the case conversion calls of <see cref="string" /> into each other, along the four pairs
/// <c>ToUpper</c> / <c>ToUpperInvariant</c>, <c>ToLower</c> / <c>ToLowerInvariant</c>,
/// <c>ToUpper</c> / <c>ToLower</c> and <c>ToUpperInvariant</c> / <c>ToLowerInvariant</c>. Every call
/// therefore yields exactly two mutants: one that flips the culture the conversion uses and one that
/// flips its direction. Both are behaviour changes a test suite that normalises casing should notice.
/// </summary>
/// <remarks>
/// <para>
/// The two forms have different signatures: <c>ToUpper</c> and <c>ToLower</c> exist both parameterless
/// and with a <c>System.Globalization.CultureInfo</c>, while <c>ToUpperInvariant</c> and
/// <c>ToLowerInvariant</c> take no argument at all. Mutating <c>ToUpper(culture)</c> into
/// <c>ToUpperInvariant()</c> therefore drops the argument as well as renaming the method, and the
/// parentheses of the call - including whatever trivia they carry - are kept as they were.
/// </para>
/// <para>
/// The opposite direction is not symmetric, because there is no culture the mutator could invent: a
/// mutant leaving the invariant form keeps the argument list it found, which is the empty one, and
/// therefore calls the parameterless overload. <see cref="string" /> declares that overload on every
/// supported target framework, so the mutant binds; should a mutant nevertheless fail to bind,
/// <c>MutantCompiler</c> discards it, and no attempt is made here to predict that.
/// </para>
/// <para>
/// The bound method symbol decides whether a call is mutated at all, and its containing type has to be
/// <see cref="string" /> itself. A <c>ToUpper</c> declared on another type, an extension method of that
/// name, and the <see cref="char"/> and <c>System.Globalization.TextInfo</c> conversions are all left
/// untouched. So is a null-conditional call, whose receiver is not a plain member access, and a call in
/// a position that requires a compile time constant.
/// </para>
/// <para>
/// Whether a mutant actually behaves differently is not decided here - <c>"abc".ToUpper()</c> and
/// <c>"abc".ToUpperInvariant()</c> agree for that input - because proving equivalence is the job of the
/// equivalence classifier.
/// </para>
/// </remarks>
internal sealed class CaseConversionMutator : MutationOperatorBase
{
    private const string ToUpperName = "ToUpper";
    private const string ToLowerName = "ToLower";
    private const string ToUpperInvariantName = "ToUpperInvariant";
    private const string ToLowerInvariantName = "ToLowerInvariant";

    private const string StringMetadataName = "System.String";

    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds = [SyntaxKind.InvocationExpression];

    private static readonly ImmutableArray<string> _fromToUpper = [ToUpperInvariantName, ToLowerName];

    private static readonly ImmutableArray<string> _fromToLower = [ToLowerInvariantName, ToUpperName];

    private static readonly ImmutableArray<string> _fromToUpperInvariant = [ToUpperName, ToLowerInvariantName];

    private static readonly ImmutableArray<string> _fromToLowerInvariant = [ToLowerName, ToUpperInvariantName];

    /// <summary>
    /// Initializes a new instance of the <see cref="CaseConversionMutator" /> class.
    /// </summary>
    public CaseConversionMutator()
        : base("culture.case-conversion", MutationKind.CaseConversion, _supportedSyntaxKinds) { }

    /// <inheritdoc />
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // MutationOperatorBase.CreateMutations only hands over nodes of one of the SupportedSyntaxKinds, and
        // the only supported kind is the invocation expression, so the cast cannot fail.
        var invocation = (InvocationExpressionSyntax)node;

        if (
            invocation.Expression is not MemberAccessExpressionSyntax access
            || !access.IsKind(SyntaxKind.SimpleMemberAccessExpression)
            || ConstantContext.IsRequired(invocation)
        )
        {
            return [];
        }

        if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
        {
            return [];
        }

        if (!IsStringCaseConversion(method, semanticModel.Compilation))
        {
            return [];
        }

        return CreateRenames(invocation, access, method.Name, cancellationToken);
    }

    /// <summary>
    /// Yields one mutation per counterpart of <paramref name="sourceName" />, in the order the pairs are
    /// declared: the culture counterpart first, the direction counterpart second.
    /// </summary>
    /// <param name="invocation">The call that gets replaced.</param>
    /// <param name="access">The member access naming the called method.</param>
    /// <param name="sourceName">The name of the called case conversion method.</param>
    /// <param name="cancellationToken">A token to observe while creating the mutations.</param>
    /// <returns>The two mutations of the call.</returns>
    private IEnumerable<Mutation> CreateRenames(
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax access,
        string sourceName,
        CancellationToken cancellationToken
    )
    {
        foreach (var targetName in GetTargets(sourceName))
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return CreateMutation(
                invocation,
                Rewrite(invocation, access, targetName),
                $"{GetSuffix(sourceName)}-to-{GetSuffix(targetName)}",
                $"{sourceName} => {targetName}"
            );
        }
    }

    /// <summary>
    /// Renames the called method and, for a target that takes no argument, empties the argument list.
    /// Every other token of the call is reused, so the receiver, the dot, the parentheses and all of
    /// their trivia survive the rewrite unchanged.
    /// </summary>
    /// <param name="invocation">The call to rewrite.</param>
    /// <param name="access">The member access naming the called method.</param>
    /// <param name="targetName">The name the call gets.</param>
    /// <returns>The rewritten call.</returns>
    private static InvocationExpressionSyntax Rewrite(
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax access,
        string targetName
    )
    {
        var identifier = access.Name.Identifier;
        var name = SyntaxFactory.IdentifierName(
            SyntaxFactory.Identifier(identifier.LeadingTrivia, targetName, identifier.TrailingTrivia)
        );
        var argumentList = IsInvariant(targetName)
            ? invocation.ArgumentList.WithArguments(SyntaxFactory.SeparatedList<ArgumentSyntax>())
            : invocation.ArgumentList;

        return invocation.WithExpression(access.WithName(name)).WithArgumentList(argumentList);
    }

    /// <summary>
    /// Decides whether <paramref name="method" /> is one of the four case conversion methods declared by
    /// <see cref="string" /> itself.
    /// </summary>
    /// <param name="method">The bound method symbol of the call.</param>
    /// <param name="compilation">The compilation the call belongs to.</param>
    /// <returns><see langword="true" /> if the call converts the case of a <see cref="string" />.</returns>
    private static bool IsStringCaseConversion(IMethodSymbol method, Compilation compilation)
    {
        if (method.MethodKind != MethodKind.Ordinary || method.IsStatic || method.IsExtensionMethod)
        {
            return false;
        }

        if (GetTargets(method.Name).IsEmpty || !HasExpectedParameters(method))
        {
            return false;
        }

        var stringType = WellKnownTypeCache.GetType(compilation, StringMetadataName);

        return stringType is not null && SymbolEqualityComparer.Default.Equals(method.ContainingType, stringType);
    }

    /// <summary>
    /// Checks the parameter list of a candidate. <see cref="string" /> declares its case conversions
    /// parameterless and, for the culture aware pair only, with a single culture, so the parameter count
    /// alone identifies the overload once the containing type is known.
    /// </summary>
    /// <param name="method">The bound method symbol of the call.</param>
    /// <returns><see langword="true" /> if the parameter list is one of the expected ones.</returns>
    private static bool HasExpectedParameters(IMethodSymbol method) =>
        method.Parameters.Length == 0 || (method.Parameters.Length == 1 && !IsInvariant(method.Name));

    /// <summary>
    /// Gets the counterparts of a case conversion method, or an empty array if the name is none of the
    /// four.
    /// </summary>
    /// <param name="methodName">The name of the called method.</param>
    /// <returns>The counterparts, in the order they are offered.</returns>
    private static ImmutableArray<string> GetTargets(string methodName) =>
        methodName switch
        {
            ToUpperName => _fromToUpper,
            ToLowerName => _fromToLower,
            ToUpperInvariantName => _fromToUpperInvariant,
            ToLowerInvariantName => _fromToLowerInvariant,
            _ => [],
        };

    /// <summary>
    /// Gets the identifier fragment a method name contributes to the operator id.
    /// </summary>
    /// <param name="methodName">The name of one of the four case conversion methods.</param>
    /// <returns>The fragment, for example <c>upper-invariant</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="methodName" /> is none of the four.</exception>
    private static string GetSuffix(string methodName) =>
        methodName switch
        {
            ToUpperName => "upper",
            ToLowerName => "lower",
            ToUpperInvariantName => "upper-invariant",
            ToLowerInvariantName => "lower-invariant",
            _ => throw new ArgumentOutOfRangeException(
                nameof(methodName),
                methodName,
                "The method is not a case conversion of System.String."
            ),
        };

    /// <summary>
    /// Decides whether a method name is one of the two invariant conversions, which take no argument.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <returns><see langword="true" /> for <c>ToUpperInvariant</c> and <c>ToLowerInvariant</c>.</returns>
    private static bool IsInvariant(string methodName) =>
        string.Equals(methodName, ToUpperInvariantName, StringComparison.Ordinal)
        || string.Equals(methodName, ToLowerInvariantName, StringComparison.Ordinal);
}
