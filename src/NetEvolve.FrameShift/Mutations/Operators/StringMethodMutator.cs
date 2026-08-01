namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Mutates calls to well known <see cref="string" /> methods into each other, along the pairs
/// <c>StartsWith</c> / <c>EndsWith</c>, <c>Trim</c> / <c>TrimStart</c> / <c>TrimEnd</c> (rotated
/// pairwise, exactly like <see cref="ArithmeticAssignmentMutator" /> rotates the compound arithmetic
/// assignments) and the static <c>IsNullOrEmpty</c> / <c>IsNullOrWhiteSpace</c>. A test suite that
/// never distinguishes a prefix from a suffix check, trims the wrong side, or treats an empty string
/// the same as a whitespace-only one, cannot tell the mutant from the original.
/// </summary>
/// <remarks>
/// <para>
/// Every overload of these methods is preserved as it was found: the mutant keeps whatever
/// <c>StringComparison</c>, <c>CultureInfo</c> or <c>char</c>-versus-<c>string</c> argument list the
/// original call used, because only the method name changes. A mutant is only produced when the
/// target method actually declares an overload with the same parameter types, so that every produced
/// mutant compiles.
/// </para>
/// <para>
/// The bound method symbol decides whether a call is mutated at all, and its containing type has to be
/// <see cref="string" /> itself. A same-named, same-shaped method declared on another type - including a
/// user-defined one - is left untouched, and so is a null-conditional call, whose receiver is not a
/// plain member access, and a call in a position that requires a compile time constant.
/// </para>
/// <para>
/// Whether a mutant actually behaves differently is not decided here - proving equivalence is the job
/// of the equivalence classifier.
/// </para>
/// </remarks>
internal sealed class StringMethodMutator : MutationOperatorBase
{
    private const string StartsWithName = "StartsWith";
    private const string EndsWithName = "EndsWith";
    private const string TrimName = "Trim";
    private const string TrimStartName = "TrimStart";
    private const string TrimEndName = "TrimEnd";
    private const string IsNullOrEmptyName = "IsNullOrEmpty";
    private const string IsNullOrWhiteSpaceName = "IsNullOrWhiteSpace";

    private const string StringMetadataName = "System.String";

    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds = [SyntaxKind.InvocationExpression];

    private static readonly ImmutableArray<string> _fromStartsWith = [EndsWithName];

    private static readonly ImmutableArray<string> _fromEndsWith = [StartsWithName];

    private static readonly ImmutableArray<string> _fromTrim = [TrimStartName, TrimEndName];

    private static readonly ImmutableArray<string> _fromTrimStart = [TrimName, TrimEndName];

    private static readonly ImmutableArray<string> _fromTrimEnd = [TrimName, TrimStartName];

    private static readonly ImmutableArray<string> _fromIsNullOrEmpty = [IsNullOrWhiteSpaceName];

    private static readonly ImmutableArray<string> _fromIsNullOrWhiteSpace = [IsNullOrEmptyName];

    /// <summary>
    /// Initializes a new instance of the <see cref="StringMethodMutator" /> class.
    /// </summary>
    public StringMethodMutator()
        : base("string-method", MutationKind.StringMethod, _supportedSyntaxKinds) { }

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

        var targets = GetTargets(method.Name);
        if (targets.IsEmpty || !IsStringMethod(method, semanticModel.Compilation))
        {
            return [];
        }

        return CreateRenames(invocation, access, method, targets, cancellationToken);
    }

    /// <summary>
    /// Yields one mutation per counterpart of the called method that also declares a matching
    /// overload, in the order the pairs are declared.
    /// </summary>
    /// <param name="invocation">The call that gets replaced.</param>
    /// <param name="access">The member access naming the called method.</param>
    /// <param name="method">The bound method symbol of the call.</param>
    /// <param name="targets">The candidate counterpart names.</param>
    /// <param name="cancellationToken">A token to observe while creating the mutations.</param>
    /// <returns>The mutations of the call.</returns>
    private IEnumerable<Mutation> CreateRenames(
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax access,
        IMethodSymbol method,
        ImmutableArray<string> targets,
        CancellationToken cancellationToken
    )
    {
        var sourceName = method.Name;

        foreach (var targetName in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!HasMatchingOverload(method, targetName))
            {
                continue;
            }

            yield return CreateMutation(
                invocation,
                Rewrite(invocation, access, targetName),
                $"{GetSuffix(sourceName)}-to-{GetSuffix(targetName)}",
                $"{sourceName} => {targetName}"
            );
        }
    }

    /// <summary>
    /// Renames the called method. Every other token of the call is reused, so the receiver, the dot,
    /// the argument list, the parentheses and all of their trivia survive the rewrite unchanged - the
    /// overload check already ensures the target declares a matching overload.
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

        return invocation.WithExpression(access.WithName(name));
    }

    /// <summary>
    /// Decides whether <paramref name="method" /> is one of the well known string methods this operator
    /// covers, declared by <see cref="string" /> itself.
    /// </summary>
    /// <param name="method">The bound method symbol of the call.</param>
    /// <param name="compilation">The compilation the call belongs to.</param>
    /// <returns><see langword="true" /> if the call is one of the covered methods of <see cref="string" />.</returns>
    private static bool IsStringMethod(IMethodSymbol method, Compilation compilation)
    {
        if (method.MethodKind != MethodKind.Ordinary || method.IsExtensionMethod)
        {
            return false;
        }

        var stringType = compilation.GetTypeByMetadataName(StringMetadataName);

        return stringType is not null && SymbolEqualityComparer.Default.Equals(method.ContainingType, stringType);
    }

    /// <summary>
    /// Decides whether the containing type of <paramref name="method" /> also declares
    /// <paramref name="targetName" /> with the same staticness and the same parameter types, so that the
    /// renamed call keeps binding to the same argument list.
    /// </summary>
    /// <param name="method">The bound method symbol of the call.</param>
    /// <param name="targetName">The candidate counterpart name.</param>
    /// <returns><see langword="true" /> if a matching overload exists.</returns>
    private static bool HasMatchingOverload(IMethodSymbol method, string targetName)
    {
        var containingType = method.ContainingType;
        if (containingType is null)
        {
            return false;
        }

        foreach (var member in containingType.GetMembers(targetName))
        {
            if (member is IMethodSymbol candidate && HasSameParameters(method, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSameParameters(IMethodSymbol source, IMethodSymbol candidate)
    {
        if (candidate.IsStatic != source.IsStatic || candidate.Parameters.Length != source.Parameters.Length)
        {
            return false;
        }

        for (var index = 0; index < source.Parameters.Length; index++)
        {
            if (!SymbolEqualityComparer.Default.Equals(source.Parameters[index].Type, candidate.Parameters[index].Type))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets the counterparts of a covered method, or an empty array if the name is none of the seven.
    /// </summary>
    /// <param name="methodName">The name of the called method.</param>
    /// <returns>The counterparts, in the order they are offered.</returns>
    private static ImmutableArray<string> GetTargets(string methodName) =>
        methodName switch
        {
            StartsWithName => _fromStartsWith,
            EndsWithName => _fromEndsWith,
            TrimName => _fromTrim,
            TrimStartName => _fromTrimStart,
            TrimEndName => _fromTrimEnd,
            IsNullOrEmptyName => _fromIsNullOrEmpty,
            IsNullOrWhiteSpaceName => _fromIsNullOrWhiteSpace,
            _ => [],
        };

    /// <summary>
    /// Gets the identifier fragment a method name contributes to the operator id.
    /// </summary>
    /// <param name="methodName">The name of one of the seven covered methods.</param>
    /// <returns>The fragment, for example <c>starts-with</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="methodName" /> is none of the seven.</exception>
    private static string GetSuffix(string methodName) =>
        methodName switch
        {
            StartsWithName => "starts-with",
            EndsWithName => "ends-with",
            TrimName => "trim",
            TrimStartName => "trim-start",
            TrimEndName => "trim-end",
            IsNullOrEmptyName => "is-null-or-empty",
            IsNullOrWhiteSpaceName => "is-null-or-white-space",
            _ => throw new ArgumentOutOfRangeException(
                nameof(methodName),
                methodName,
                "The method is not one of the string methods covered by this operator."
            ),
        };
}
