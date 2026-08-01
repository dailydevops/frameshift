namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Mutates the unary sign operators, either by swapping <c>-x</c> and <c>+x</c> or by dropping the
/// operator entirely, leaving only the operand.
/// </summary>
/// <remarks>
/// Both mutations are skipped when the operand is a literal whose constant value already equals the
/// constant value of the whole unary expression, because in that case neither mutant could differ
/// from the original.
/// </remarks>
internal sealed class UnaryOperatorMutator : MutationOperatorBase
{
    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds =
    [
        SyntaxKind.UnaryMinusExpression,
        SyntaxKind.UnaryPlusExpression,
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="UnaryOperatorMutator" /> class.
    /// </summary>
    public UnaryOperatorMutator()
        : base("unary", MutationKind.UnaryOperator, _supportedSyntaxKinds) { }

    /// <inheritdoc />
    /// <remarks>
    /// No type test is needed: <see cref="MutationOperatorBase.CreateMutations" /> only forwards nodes
    /// whose kind is one of the <see cref="MutationOperatorBase.SupportedSyntaxKinds" />, and both the
    /// unary minus and the unary plus are a <see cref="PrefixUnaryExpressionSyntax" />.
    /// </remarks>
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    ) => CreateMutationsForUnary((PrefixUnaryExpressionSyntax)node, semanticModel, cancellationToken);

    private IEnumerable<Mutation> CreateMutationsForUnary(
        PrefixUnaryExpressionSyntax unary,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (IsConstantPreservingLiteral(unary, semanticModel, cancellationToken))
        {
            yield break;
        }

        var isNegation = unary.IsKind(SyntaxKind.UnaryMinusExpression);
        var boundOperator = semanticModel.GetSymbolInfo(unary, cancellationToken).Symbol as IMethodSymbol;
        var userDefinedOperator = boundOperator?.MethodKind == MethodKind.UserDefinedOperator ? boundOperator : null;

        var targetKind = isNegation ? SyntaxKind.UnaryPlusExpression : SyntaxKind.UnaryMinusExpression;
        var targetToken = isNegation ? SyntaxKind.PlusToken : SyntaxKind.MinusToken;
        var targetMetadataName = isNegation ? "op_UnaryPlus" : "op_UnaryNegation";

        if (userDefinedOperator is null || OperatorCounterpart.HasCounterpart(userDefinedOperator, targetMetadataName))
        {
            var operatorToken = SyntaxFactory.Token(
                unary.OperatorToken.LeadingTrivia,
                targetToken,
                unary.OperatorToken.TrailingTrivia
            );
            var swapped = SyntaxFactory
                .PrefixUnaryExpression(targetKind, operatorToken, unary.Operand)
                .WithTriviaFrom(unary);

            yield return CreateMutation(
                unary,
                swapped,
                isNegation ? "negate-to-plus" : "plus-to-negate",
                isNegation ? "-x => +x" : "+x => -x"
            );
        }

        cancellationToken.ThrowIfCancellationRequested();

        yield return CreateMutation(
            unary,
            unary.Operand.WithTriviaFrom(unary),
            isNegation ? "remove-negate" : "remove-plus",
            isNegation ? "-x => x" : "+x => x"
        );
    }

    private static bool IsConstantPreservingLiteral(
        PrefixUnaryExpressionSyntax unary,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (unary.Operand is not LiteralExpressionSyntax)
        {
            return false;
        }

        var unaryConstant = semanticModel.GetConstantValue(unary, cancellationToken);
        var operandConstant = semanticModel.GetConstantValue(unary.Operand, cancellationToken);
        if (!unaryConstant.HasValue || !operandConstant.HasValue)
        {
            return false;
        }

        return object.Equals(unaryConstant.Value, operandConstant.Value);
    }
}
