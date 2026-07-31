namespace NetEvolve.Frameshift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Replaces a compound arithmetic assignment (<c>+=</c>, <c>-=</c>, <c>*=</c>, <c>/=</c>, <c>%=</c>)
/// by each of the remaining four compound arithmetic assignments.
/// </summary>
/// <remarks>
/// <para>
/// String appends written with <c>+=</c> as well as event and delegate subscriptions are left
/// untouched. They are already excluded by the operand check: a string append has a
/// <see cref="string" /> on the left, and the type of an event or of a delegate variable is a delegate
/// type, which <c>IsArithmeticOperand</c> rejects. The C# language requires the type of an event to be
/// a delegate type, so an event subscription can never look arithmetic.
/// </para>
/// <para>
/// An assignment bound to a user defined operator is only mutated into operators the declaring type
/// actually provides, so that the mutant still binds.
/// </para>
/// </remarks>
internal sealed class ArithmeticAssignmentMutator : MutationOperatorBase
{
    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds =
    [
        SyntaxKind.AddAssignmentExpression,
        SyntaxKind.SubtractAssignmentExpression,
        SyntaxKind.MultiplyAssignmentExpression,
        SyntaxKind.DivideAssignmentExpression,
        SyntaxKind.ModuloAssignmentExpression,
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="ArithmeticAssignmentMutator" /> class.
    /// </summary>
    public ArithmeticAssignmentMutator()
        : base("arithmetic-assignment", MutationKind.ArithmeticAssignment, _supportedSyntaxKinds) { }

    /// <inheritdoc />
    /// <remarks>
    /// No type check is needed: the base class only forwards a node whose kind is one of
    /// <see cref="MutationOperatorBase.SupportedSyntaxKinds" />, and every kind this operator supports is
    /// an assignment expression, so the cast cannot fail.
    /// </remarks>
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    ) => CreateMutationsForAssignment((AssignmentExpressionSyntax)node, semanticModel, cancellationToken);

    private IEnumerable<Mutation> CreateMutationsForAssignment(
        AssignmentExpressionSyntax assignment,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        var leftType = semanticModel.GetTypeInfo(assignment.Left, cancellationToken).ConvertedType;
        var rightType = semanticModel.GetTypeInfo(assignment.Right, cancellationToken).ConvertedType;
        if (!IsArithmeticOperand(leftType) || !IsArithmeticOperand(rightType))
        {
            yield break;
        }

        var boundOperator = semanticModel.GetSymbolInfo(assignment, cancellationToken).Symbol as IMethodSymbol;
        var userDefined = boundOperator?.MethodKind == MethodKind.UserDefinedOperator ? boundOperator : null;
        var originalKind = assignment.Kind();

        foreach (var targetKind in _supportedSyntaxKinds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (targetKind == originalKind)
            {
                continue;
            }

            if (userDefined is not null && !HasCounterpart(userDefined, GetMetadataName(targetKind)))
            {
                continue;
            }

            var operatorToken = SyntaxFactory.Token(
                assignment.OperatorToken.LeadingTrivia,
                GetOperatorToken(targetKind),
                assignment.OperatorToken.TrailingTrivia
            );
            var replacement = SyntaxFactory
                .AssignmentExpression(targetKind, assignment.Left, operatorToken, assignment.Right)
                .WithTriviaFrom(assignment);

            yield return CreateMutation(
                assignment,
                replacement,
                $"{GetName(originalKind)}-to-{GetName(targetKind)}",
                $"{GetSymbol(originalKind)} => {GetSymbol(targetKind)}"
            );
        }
    }

    private static string GetName(SyntaxKind expressionKind) =>
        expressionKind switch
        {
            SyntaxKind.AddAssignmentExpression => "add-assign",
            SyntaxKind.SubtractAssignmentExpression => "subtract-assign",
            SyntaxKind.MultiplyAssignmentExpression => "multiply-assign",
            SyntaxKind.DivideAssignmentExpression => "divide-assign",
            SyntaxKind.ModuloAssignmentExpression => "modulo-assign",
            _ => throw NotArithmeticAssignment(expressionKind),
        };

    private static string GetSymbol(SyntaxKind expressionKind) =>
        expressionKind switch
        {
            SyntaxKind.AddAssignmentExpression => "+=",
            SyntaxKind.SubtractAssignmentExpression => "-=",
            SyntaxKind.MultiplyAssignmentExpression => "*=",
            SyntaxKind.DivideAssignmentExpression => "/=",
            SyntaxKind.ModuloAssignmentExpression => "%=",
            _ => throw NotArithmeticAssignment(expressionKind),
        };

    private static SyntaxKind GetOperatorToken(SyntaxKind expressionKind) =>
        expressionKind switch
        {
            SyntaxKind.AddAssignmentExpression => SyntaxKind.PlusEqualsToken,
            SyntaxKind.SubtractAssignmentExpression => SyntaxKind.MinusEqualsToken,
            SyntaxKind.MultiplyAssignmentExpression => SyntaxKind.AsteriskEqualsToken,
            SyntaxKind.DivideAssignmentExpression => SyntaxKind.SlashEqualsToken,
            SyntaxKind.ModuloAssignmentExpression => SyntaxKind.PercentEqualsToken,
            _ => throw NotArithmeticAssignment(expressionKind),
        };

    private static string GetMetadataName(SyntaxKind expressionKind) =>
        expressionKind switch
        {
            SyntaxKind.AddAssignmentExpression => "op_Addition",
            SyntaxKind.SubtractAssignmentExpression => "op_Subtraction",
            SyntaxKind.MultiplyAssignmentExpression => "op_Multiply",
            SyntaxKind.DivideAssignmentExpression => "op_Division",
            SyntaxKind.ModuloAssignmentExpression => "op_Modulus",
            _ => throw NotArithmeticAssignment(expressionKind),
        };

    private static ArgumentOutOfRangeException NotArithmeticAssignment(SyntaxKind expressionKind) =>
        new ArgumentOutOfRangeException(
            nameof(expressionKind),
            expressionKind,
            "The syntax kind is not a compound arithmetic assignment."
        );

    /// <summary>
    /// Decides whether a side of the assignment can take part in an arithmetic mutation. A
    /// <see cref="string" /> or an <see cref="object" /> side means a string append, a delegate side means
    /// an event subscription or a delegate combination, and neither belongs to this operator family.
    /// </summary>
    /// <param name="type">The converted type of the side, or <see langword="null" /> if unknown.</param>
    /// <returns><see langword="true" /> if the side is arithmetic.</returns>
    private static bool IsArithmeticOperand(ITypeSymbol? type)
    {
        if (type is null || type.TypeKind is TypeKind.Error or TypeKind.Delegate or TypeKind.Pointer)
        {
            return false;
        }

        return type.SpecialType is not (SpecialType.System_String or SpecialType.System_Object);
    }

    private static bool HasCounterpart(IMethodSymbol userDefinedOperator, string metadataName)
    {
        var containingType = userDefinedOperator.ContainingType;
        if (containingType is null)
        {
            return false;
        }

        foreach (var member in containingType.GetMembers(metadataName))
        {
            if (
                member is IMethodSymbol candidate
                && candidate.MethodKind == MethodKind.UserDefinedOperator
                && candidate.Parameters.Length == userDefinedOperator.Parameters.Length
            )
            {
                return true;
            }
        }

        return false;
    }
}
