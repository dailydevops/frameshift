namespace NetEvolve.Frameshift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Replaces a binary arithmetic operator (<c>+</c>, <c>-</c>, <c>*</c>, <c>/</c>, <c>%</c>) by each
/// of the remaining four arithmetic operators, which also covers the <c>+</c> and <c>-</c> pairing.
/// </summary>
/// <remarks>
/// String concatenations written with <c>+</c> are left untouched, they belong to the string
/// operators. An expression bound to a user defined operator is only mutated into operators the
/// declaring type actually provides, so that the mutant still binds.
/// </remarks>
internal sealed class ArithmeticOperatorMutator : MutationOperatorBase
{
    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds =
    [
        SyntaxKind.AddExpression,
        SyntaxKind.SubtractExpression,
        SyntaxKind.MultiplyExpression,
        SyntaxKind.DivideExpression,
        SyntaxKind.ModuloExpression,
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="ArithmeticOperatorMutator" /> class.
    /// </summary>
    public ArithmeticOperatorMutator()
        : base("arithmetic", MutationKind.ArithmeticOperator, _supportedSyntaxKinds) { }

    /// <inheritdoc />
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (node is not BinaryExpressionSyntax binary)
        {
            return [];
        }

        return CreateMutationsForBinary(binary, semanticModel, cancellationToken);
    }

    private IEnumerable<Mutation> CreateMutationsForBinary(
        BinaryExpressionSyntax binary,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        var leftType = semanticModel.GetTypeInfo(binary.Left, cancellationToken).ConvertedType;
        var rightType = semanticModel.GetTypeInfo(binary.Right, cancellationToken).ConvertedType;
        if (!IsArithmeticOperand(leftType) || !IsArithmeticOperand(rightType))
        {
            yield break;
        }

        var boundOperator = semanticModel.GetSymbolInfo(binary, cancellationToken).Symbol as IMethodSymbol;
        if (IsStringConcatenation(boundOperator))
        {
            yield break;
        }

        var userDefined = boundOperator?.MethodKind == MethodKind.UserDefinedOperator ? boundOperator : null;
        var originalKind = binary.Kind();

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
                binary.OperatorToken.LeadingTrivia,
                GetOperatorToken(targetKind),
                binary.OperatorToken.TrailingTrivia
            );
            var replacement = SyntaxFactory
                .BinaryExpression(targetKind, binary.Left, operatorToken, binary.Right)
                .WithTriviaFrom(binary);

            yield return CreateMutation(
                binary,
                replacement,
                $"{GetName(originalKind)}-to-{GetName(targetKind)}",
                $"{GetSymbol(originalKind)} => {GetSymbol(targetKind)}"
            );
        }
    }

    private static string GetName(SyntaxKind expressionKind) =>
        expressionKind switch
        {
            SyntaxKind.AddExpression => "add",
            SyntaxKind.SubtractExpression => "subtract",
            SyntaxKind.MultiplyExpression => "multiply",
            SyntaxKind.DivideExpression => "divide",
            SyntaxKind.ModuloExpression => "modulo",
            _ => throw NotArithmetic(expressionKind),
        };

    private static string GetSymbol(SyntaxKind expressionKind) =>
        expressionKind switch
        {
            SyntaxKind.AddExpression => "+",
            SyntaxKind.SubtractExpression => "-",
            SyntaxKind.MultiplyExpression => "*",
            SyntaxKind.DivideExpression => "/",
            SyntaxKind.ModuloExpression => "%",
            _ => throw NotArithmetic(expressionKind),
        };

    private static SyntaxKind GetOperatorToken(SyntaxKind expressionKind) =>
        expressionKind switch
        {
            SyntaxKind.AddExpression => SyntaxKind.PlusToken,
            SyntaxKind.SubtractExpression => SyntaxKind.MinusToken,
            SyntaxKind.MultiplyExpression => SyntaxKind.AsteriskToken,
            SyntaxKind.DivideExpression => SyntaxKind.SlashToken,
            SyntaxKind.ModuloExpression => SyntaxKind.PercentToken,
            _ => throw NotArithmetic(expressionKind),
        };

    private static string GetMetadataName(SyntaxKind expressionKind) =>
        expressionKind switch
        {
            SyntaxKind.AddExpression => "op_Addition",
            SyntaxKind.SubtractExpression => "op_Subtraction",
            SyntaxKind.MultiplyExpression => "op_Multiply",
            SyntaxKind.DivideExpression => "op_Division",
            SyntaxKind.ModuloExpression => "op_Modulus",
            _ => throw NotArithmetic(expressionKind),
        };

    private static ArgumentOutOfRangeException NotArithmetic(SyntaxKind expressionKind) =>
        new ArgumentOutOfRangeException(
            nameof(expressionKind),
            expressionKind,
            "The syntax kind is not a binary arithmetic expression."
        );

    private static bool IsStringConcatenation(IMethodSymbol? boundOperator)
    {
        if (boundOperator is null)
        {
            return false;
        }

        if (boundOperator.ContainingType?.SpecialType == SpecialType.System_String)
        {
            return true;
        }

        return string.Equals(boundOperator.Name, "Concat", StringComparison.Ordinal);
    }

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
