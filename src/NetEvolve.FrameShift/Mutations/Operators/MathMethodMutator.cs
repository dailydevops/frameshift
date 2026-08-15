namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Mutates calls to well-known <see cref="System.Math" /> static methods into a related method whose
/// choice a test suite has to distinguish: the co-functions <c>Sin</c> / <c>Cos</c>, <c>Asin</c> /
/// <c>Acos</c>, <c>Tan</c> / <c>Atan</c> and <c>Sinh</c> / <c>Cosh</c>, the extremes <c>Min</c> /
/// <c>Max</c>, the rounding directions <c>Floor</c> / <c>Ceiling</c>, and <c>Abs</c>, whose call is
/// dropped entirely so that <c>Math.Abs(x)</c> becomes plain <c>x</c>.
/// </summary>
/// <remarks>
/// <para>
/// The called method is resolved through the semantic model and has to be static and declared on
/// <see cref="System.Math" /> itself; a same-named method on another type - including a user-defined
/// one - is left untouched.
/// </para>
/// <para>
/// <see cref="System.Math" /> does not declare every member for every numeric type: the trigonometric
/// functions and <c>Floor</c> / <c>Ceiling</c> only have <see cref="double"/>, <see cref="float"/> and <see cref="decimal"/>
/// overloads, while <c>Min</c>, <c>Max</c> and <c>Abs</c> have one for every numeric type including the
/// integral ones. Rather than hard-coding that shape, the counterpart is only offered when
/// <see cref="System.Math" /> itself declares a static overload of that name whose parameters match the
/// called overload's parameter types exactly, so every produced mutant compiles.
/// </para>
/// <para>
/// Because the matching overload takes the same parameter types, the rewrite only ever renames the
/// called method; the argument list, its parentheses and all of their trivia survive unchanged. The one
/// exception is <c>Abs</c>, which has no counterpart at all: its mutation replaces the whole invocation
/// with its own single argument expression, keeping the argument's type and therefore the compiled type
/// of the expression exactly as it was.
/// </para>
/// </remarks>
internal sealed class MathMethodMutator : MutationOperatorBase
{
    private const string MathMetadataName = "System.Math";

    private const string SinName = "Sin";
    private const string CosName = "Cos";
    private const string AsinName = "Asin";
    private const string AcosName = "Acos";
    private const string TanName = "Tan";
    private const string AtanName = "Atan";
    private const string SinhName = "Sinh";
    private const string CoshName = "Cosh";
    private const string MinName = "Min";
    private const string MaxName = "Max";
    private const string FloorName = "Floor";
    private const string CeilingName = "Ceiling";
    private const string AbsName = "Abs";

    private const string RemoveSuffix = "abs.remove";

    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds = [SyntaxKind.InvocationExpression];

    private static readonly ImmutableDictionary<string, string> _counterparts = ImmutableDictionary
        .CreateBuilder<string, string>(StringComparer.Ordinal)
        .AddPair(SinName, CosName)
        .AddPair(AsinName, AcosName)
        .AddPair(TanName, AtanName)
        .AddPair(SinhName, CoshName)
        .AddPair(MinName, MaxName)
        .AddPair(FloorName, CeilingName)
        .ToImmutable();

    /// <summary>
    /// Initializes a new instance of the <see cref="MathMethodMutator" /> class.
    /// </summary>
    public MathMethodMutator()
        : base("math.method", MutationKind.MathMethod, _supportedSyntaxKinds) { }

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
        )
        {
            return [];
        }

        if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
        {
            return [];
        }

        if (!IsMathMethod(method, semanticModel.Compilation))
        {
            return [];
        }

        if (string.Equals(method.Name, AbsName, StringComparison.Ordinal))
        {
            return CreateAbsRemoval(invocation);
        }

        if (!_counterparts.TryGetValue(method.Name, out var targetName) || !HasMatchingOverload(method, targetName))
        {
            return [];
        }

        return [CreateRename(invocation, access, method.Name, targetName)];
    }

    /// <summary>
    /// Builds the mutation that drops the call to <c>Math.Abs</c>, keeping its single argument
    /// expression in its place.
    /// </summary>
    /// <param name="invocation">The call to <c>Math.Abs</c>.</param>
    /// <returns>The one mutation this call offers, or an empty sequence if it has no single argument.</returns>
    private IEnumerable<Mutation> CreateAbsRemoval(InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList.Arguments.Count != 1)
        {
            return [];
        }

        var argument = invocation.ArgumentList.Arguments[0].Expression;

        return [CreateMutation(invocation, argument, RemoveSuffix, $"{invocation} => {argument}")];
    }

    /// <summary>
    /// Builds the mutation that renames the called method to its counterpart, keeping the argument list
    /// exactly as it was written.
    /// </summary>
    /// <param name="invocation">The call that gets replaced.</param>
    /// <param name="access">The member access naming the called method.</param>
    /// <param name="sourceName">The name of the called method.</param>
    /// <param name="targetName">The name of the counterpart method.</param>
    /// <returns>The created mutation.</returns>
    private Mutation CreateRename(
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax access,
        string sourceName,
        string targetName
    )
    {
        var identifier = access.Name.Identifier;
        var name = SyntaxFactory.IdentifierName(
            SyntaxFactory.Identifier(identifier.LeadingTrivia, targetName, identifier.TrailingTrivia)
        );
        var replacement = invocation.WithExpression(access.WithName(name));

        return CreateMutation(
            invocation,
            replacement,
            $"{GetSuffix(sourceName)}-to-{GetSuffix(targetName)}",
            $"{sourceName} => {targetName}"
        );
    }

    /// <summary>
    /// Decides whether <paramref name="method" /> is a static method declared on
    /// <see cref="System.Math" /> itself, and one of the names this operator knows.
    /// </summary>
    /// <param name="method">The bound method symbol of the call.</param>
    /// <param name="compilation">The compilation the call belongs to.</param>
    /// <returns><see langword="true" /> if the call is one of the known <see cref="System.Math" /> calls.</returns>
    private static bool IsMathMethod(IMethodSymbol method, Compilation compilation)
    {
        if (method.MethodKind != MethodKind.Ordinary || !method.IsStatic)
        {
            return false;
        }

        if (!_counterparts.ContainsKey(method.Name) && !string.Equals(method.Name, AbsName, StringComparison.Ordinal))
        {
            return false;
        }

        var mathType = WellKnownTypeCache.GetType(compilation, MathMetadataName);

        return mathType is not null && SymbolEqualityComparer.Default.Equals(method.ContainingType, mathType);
    }

    /// <summary>
    /// Decides whether <see cref="System.Math" /> declares a static overload named
    /// <paramref name="targetName" /> whose parameters match <paramref name="method" />'s exactly, so
    /// that renaming the call to it is guaranteed to compile.
    /// </summary>
    /// <param name="method">The bound symbol of the called method.</param>
    /// <param name="targetName">The name of the counterpart method.</param>
    /// <returns><see langword="true" /> if a matching overload exists.</returns>
    private static bool HasMatchingOverload(IMethodSymbol method, string targetName)
    {
        var parameters = method.Parameters;

        foreach (var candidate in method.ContainingType.GetMembers(targetName).OfType<IMethodSymbol>())
        {
            if (!candidate.IsStatic || candidate.Parameters.Length != parameters.Length)
            {
                continue;
            }

            var matches = true;

            for (var index = 0; index < parameters.Length; index++)
            {
                if (!SymbolEqualityComparer.Default.Equals(candidate.Parameters[index].Type, parameters[index].Type))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the identifier fragment a method name contributes to the operator id.
    /// </summary>
    /// <param name="methodName">The name of the called or counterpart method.</param>
    /// <returns>The fragment, lower-cased.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="methodName" /> is none of the known names.</exception>
    private static string GetSuffix(string methodName) =>
        methodName switch
        {
            SinName => "sin",
            CosName => "cos",
            AsinName => "asin",
            AcosName => "acos",
            TanName => "tan",
            AtanName => "atan",
            SinhName => "sinh",
            CoshName => "cosh",
            MinName => "min",
            MaxName => "max",
            FloorName => "floor",
            CeilingName => "ceiling",
            _ => throw new ArgumentOutOfRangeException(
                nameof(methodName),
                methodName,
                "The method is not a known System.Math method."
            ),
        };
}

/// <summary>
/// A small helper adding both directions of a counterpart pair to an <see cref="ImmutableDictionary{TKey,TValue}" />
/// builder in one call.
/// </summary>
file static class ImmutableDictionaryBuilderExtensions
{
    public static ImmutableDictionary<string, string>.Builder AddPair(
        this ImmutableDictionary<string, string>.Builder builder,
        string first,
        string second
    )
    {
        builder.Add(first, second);
        builder.Add(second, first);

        return builder;
    }
}
