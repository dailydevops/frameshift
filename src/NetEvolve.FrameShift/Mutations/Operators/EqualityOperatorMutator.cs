namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Mutates the equality operators <c>==</c> and <c>!=</c> into each other. A comparison bound to a
/// user-defined operator without a declared counterpart is left untouched, because the mutant would
/// not compile.
/// </summary>
internal sealed class EqualityOperatorMutator : MutationOperatorBase
{
    private const string EqualityMethodName = "op_Equality";
    private const string InequalityMethodName = "op_Inequality";

    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds =
    [
        SyntaxKind.EqualsExpression,
        SyntaxKind.NotEqualsExpression,
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="EqualityOperatorMutator" /> class.
    /// </summary>
    public EqualityOperatorMutator()
        : base("equality", MutationKind.EqualityOperator, _supportedSyntaxKinds) { }

    /// <inheritdoc />
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        // MutationOperatorBase.CreateMutations only hands over nodes of one of the SupportedSyntaxKinds,
        // and both equality kinds are binary expressions, so the cast cannot fail and no type test is
        // needed here.
        var binary = (BinaryExpressionSyntax)node;

        cancellationToken.ThrowIfCancellationRequested();

        var isEquals = binary.IsKind(SyntaxKind.EqualsExpression);
        if (!HasUsableCounterpart(binary, semanticModel, isEquals, cancellationToken))
        {
            return [];
        }

        var targetKind = isEquals ? SyntaxKind.NotEqualsExpression : SyntaxKind.EqualsExpression;
        var targetTokenKind = isEquals ? SyntaxKind.ExclamationEqualsToken : SyntaxKind.EqualsEqualsToken;

        var replacement = SyntaxFactory.BinaryExpression(
            targetKind,
            binary.Left,
            SyntaxFactory.Token(
                binary.OperatorToken.LeadingTrivia,
                targetTokenKind,
                binary.OperatorToken.TrailingTrivia
            ),
            binary.Right
        );

        var suffix = isEquals ? "equals-to-not-equals" : "not-equals-to-equals";
        var displayName = isEquals ? "== => !=" : "!= => ==";

        return [CreateMutation(binary, replacement, suffix, displayName)];
    }

    private static bool HasUsableCounterpart(
        BinaryExpressionSyntax binary,
        SemanticModel semanticModel,
        bool isEquals,
        CancellationToken cancellationToken
    )
    {
        if (semanticModel.GetSymbolInfo(binary, cancellationToken).Symbol is not IMethodSymbol method)
        {
            return true;
        }

        if (method.MethodKind != MethodKind.UserDefinedOperator)
        {
            return true;
        }

        // A user-defined operator is always declared inside a type, so this branch is not reachable. The
        // null check stays because ISymbol.ContainingType is declared as a nullable reference.
        var containingType = method.ContainingType;
        if (containingType is null)
        {
            return true;
        }

        var counterpartName = isEquals ? InequalityMethodName : EqualityMethodName;
        foreach (var member in containingType.GetMembers(counterpartName))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (
                member is IMethodSymbol counterpart
                && counterpart.MethodKind == MethodKind.UserDefinedOperator
                && HasMatchingParameters(method, counterpart)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasMatchingParameters(IMethodSymbol method, IMethodSymbol counterpart)
    {
        var parameters = method.Parameters;
        var counterpartParameters = counterpart.Parameters;

        if (parameters.Length != counterpartParameters.Length)
        {
            return false;
        }

        for (var index = 0; index < parameters.Length; index++)
        {
            var left = Unwrap(parameters[index].Type);
            var right = Unwrap(counterpartParameters[index].Type);

            if (!SymbolEqualityComparer.Default.Equals(left, right))
            {
                return false;
            }
        }

        return true;
    }

    private static ITypeSymbol? Unwrap(ITypeSymbol? type)
    {
        if (
            type is INamedTypeSymbol named
            && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            && named.TypeArguments.Length == 1
        )
        {
            return named.TypeArguments[0];
        }

        return type;
    }
}
