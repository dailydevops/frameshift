namespace NetEvolve.FrameShift.Mutations.Operators;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Replaces a null-coalescing expression <c>a ?? b</c> by its left or its right operand, and rewrites
/// the null-coalescing assignment <c>a ??= b</c> into the plain assignment <c>a = b</c>.
/// </summary>
internal sealed class NullCoalescingMutator : MutationOperatorBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NullCoalescingMutator" /> class.
    /// </summary>
    public NullCoalescingMutator()
        : base(
            "null-coalescing",
            MutationKind.NullCoalescing,
            [SyntaxKind.CoalesceExpression, SyntaxKind.CoalesceAssignmentExpression]
        ) { }

    /// <inheritdoc />
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node.IsKind(SyntaxKind.CoalesceAssignmentExpression))
        {
            // The C# language already requires the right side of a `??=` assignment to be implicitly
            // convertible to the left side's type, so the plain assignment `a = b` always compiles when
            // the original does - except when the right side is a `throw` expression: that is a legal
            // operand of `??=`, but a plain assignment is not one of the few positions a `throw`
            // expression is allowed in, so the mutant would not compile.
            var coalesceAssignment = (AssignmentExpressionSyntax)node;

            if (coalesceAssignment.Right.IsKind(SyntaxKind.ThrowExpression))
            {
                yield break;
            }

            var operatorToken = SyntaxFactory.Token(
                coalesceAssignment.OperatorToken.LeadingTrivia,
                SyntaxKind.EqualsToken,
                coalesceAssignment.OperatorToken.TrailingTrivia
            );
            var replacement = SyntaxFactory
                .AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    coalesceAssignment.Left,
                    operatorToken,
                    coalesceAssignment.Right
                )
                .WithTriviaFrom(coalesceAssignment);

            yield return CreateMutation(
                coalesceAssignment,
                replacement,
                "coalesce-assign-to-assign",
                "a ??= b => a = b"
            );

            yield break;
        }

        // MutationOperatorBase.CreateMutations only hands over nodes of one of the SupportedSyntaxKinds,
        // and SyntaxKind.CoalesceExpression is always a binary expression, so the cast cannot fail and no
        // type test is needed here.
        var coalesce = (BinaryExpressionSyntax)node;

        var targetType = semanticModel.GetTypeInfo(coalesce, cancellationToken).ConvertedType;

        if (IsConversionViable(semanticModel, coalesce.Left, targetType))
        {
            yield return CreateMutation(coalesce, coalesce.Left, "keep-left", "a ?? b => a");
        }

        if (IsConversionViable(semanticModel, coalesce.Right, targetType))
        {
            yield return CreateMutation(coalesce, coalesce.Right, "keep-right", "a ?? b => b");
        }
    }

    /// <summary>
    /// Determines whether <paramref name="candidate" /> can replace the whole coalesce expression
    /// without breaking the conversion the surrounding code expects. If the expected type is unknown,
    /// the decision is deferred to the later compilation viability check.
    /// </summary>
    /// <param name="semanticModel">The semantic model of the tree the expression belongs to.</param>
    /// <param name="candidate">The operand that would replace the coalesce expression.</param>
    /// <param name="targetType">The type the coalesce expression is converted to.</param>
    /// <returns><see langword="true" /> if the replacement is worth generating.</returns>
    private static bool IsConversionViable(
        SemanticModel semanticModel,
        ExpressionSyntax candidate,
        ITypeSymbol? targetType
    )
    {
        if (targetType is null || targetType.TypeKind == TypeKind.Error)
        {
            return true;
        }

        if (candidate.IsKind(SyntaxKind.ThrowExpression))
        {
            return false;
        }

        var conversion = semanticModel.ClassifyConversion(candidate, targetType);
        return conversion.Exists && conversion.IsImplicit;
    }
}
