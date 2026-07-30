namespace NetEvolve.Frameshift.Mutations.Operators;

using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Replaces numeric literals by a neighbouring or boundary value, keeping the literal suffix of the
/// original literal and skipping every candidate that cannot be represented in the literal's type.
/// </summary>
internal sealed class NumericLiteralMutator : MutationOperatorBase
{
    private const string IntegralSuffixCharacters = "uUlL";
    private const string FloatingSuffixCharacters = "fFdDmM";

    /// <summary>
    /// Initializes a new instance of the <see cref="NumericLiteralMutator" /> class.
    /// </summary>
    public NumericLiteralMutator()
        : base("numeric-literal", MutationKind.NumericLiteral, [SyntaxKind.NumericLiteralExpression]) { }

    /// <inheritdoc />
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is not LiteralExpressionSyntax literal || IsConstantRequired(literal))
        {
            return [];
        }

        var constant = semanticModel.GetConstantValue(literal, cancellationToken);
        var value = constant.HasValue ? constant.Value : null;
        if (value is null)
        {
            return [];
        }

        var targetType = UnwrapNullable(semanticModel.GetTypeInfo(literal, cancellationToken).ConvertedType);
        if (targetType is not null && targetType.TypeKind is TypeKind.Enum or TypeKind.Error)
        {
            return [];
        }

        return value switch
        {
            sbyte or byte or short or ushort or int or uint or long or ulong => CreateIntegralMutations(
                literal,
                value,
                targetType
            ),
            float or double or decimal => CreateFloatingMutations(literal, value),
            _ => [],
        };
    }

    private IEnumerable<Mutation> CreateIntegralMutations(
        LiteralExpressionSyntax literal,
        object value,
        ITypeSymbol? targetType
    )
    {
        var current = ToDecimal(value);
        var suffix = GetLiteralSuffix(literal.Token.Text, IntegralSuffixCharacters);

        if (current == 0m)
        {
            var one = CreateIntegralLiteral(value, 1m, suffix, targetType);
            if (one is not null)
            {
                yield return CreateMutation(literal, one, "zero-to-one", "0 => 1");
            }

            yield break;
        }

        if (current == 1m)
        {
            var zero = CreateIntegralLiteral(value, 0m, suffix, targetType);
            if (zero is not null)
            {
                yield return CreateMutation(literal, zero, "one-to-zero", "1 => 0");
            }

            yield break;
        }

        var incremented = CreateIntegralLiteral(value, current + 1m, suffix, targetType);
        if (incremented is not null)
        {
            yield return CreateMutation(
                literal,
                incremented,
                "increment",
                $"{literal.Token.Text} => {incremented.Token.Text}"
            );
        }

        var decremented = CreateIntegralLiteral(value, current - 1m, suffix, targetType);
        if (decremented is not null)
        {
            yield return CreateMutation(
                literal,
                decremented,
                "decrement",
                $"{literal.Token.Text} => {decremented.Token.Text}"
            );
        }
    }

    private IEnumerable<Mutation> CreateFloatingMutations(LiteralExpressionSyntax literal, object value)
    {
        var suffix = GetLiteralSuffix(literal.Token.Text, FloatingSuffixCharacters);

        if (IsFloatingZero(value))
        {
            yield return CreateMutation(literal, CreateFloatingOneLiteral(value, suffix), "zero-to-one", "0 => 1");
            yield break;
        }

        if (literal.Parent?.IsKind(SyntaxKind.UnaryMinusExpression) == true)
        {
            yield break;
        }

        var negated = SyntaxFactory.PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression, literal.WithoutTrivia());
        yield return CreateMutation(literal, negated, "negate", $"{literal.Token.Text} => -{literal.Token.Text}");
    }

    private static LiteralExpressionSyntax? CreateIntegralLiteral(
        object originalValue,
        decimal candidate,
        string suffix,
        ITypeSymbol? targetType
    )
    {
        if (
            TryGetIntegralLimits(targetType, out var minimum, out var maximum)
            && (candidate < minimum || candidate > maximum)
        )
        {
            return null;
        }

        var text = candidate.ToString(CultureInfo.InvariantCulture) + suffix;
        SyntaxToken token;

        switch (originalValue)
        {
            case ulong:
                if (candidate < 0m || candidate > ulong.MaxValue)
                {
                    return null;
                }

                token = SyntaxFactory.Literal(text, (ulong)candidate);
                break;

            case long:
                if (candidate < long.MinValue || candidate > long.MaxValue)
                {
                    return null;
                }

                token = SyntaxFactory.Literal(text, (long)candidate);
                break;

            case uint:
                if (candidate < 0m || candidate > uint.MaxValue)
                {
                    return null;
                }

                token = SyntaxFactory.Literal(text, (uint)candidate);
                break;

            default:
                if (candidate < int.MinValue || candidate > int.MaxValue)
                {
                    return null;
                }

                token = SyntaxFactory.Literal(text, (int)candidate);
                break;
        }

        return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, token);
    }

    private static LiteralExpressionSyntax CreateFloatingOneLiteral(object originalValue, string suffix)
    {
        var token = originalValue switch
        {
            float => SyntaxFactory.Literal(suffix.Length == 0 ? "1f" : "1" + suffix, 1F),
            decimal => SyntaxFactory.Literal(suffix.Length == 0 ? "1m" : "1" + suffix, 1M),
            _ => SyntaxFactory.Literal(suffix.Length == 0 ? "1.0" : "1" + suffix, 1D),
        };

        return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, token);
    }

    private static bool IsFloatingZero(object value) =>
        value switch
        {
            float singleValue => singleValue == 0F,
            double doubleValue => doubleValue == 0D,
            decimal decimalValue => decimalValue == 0M,
            _ => false,
        };

    private static decimal ToDecimal(object value) =>
        value switch
        {
            sbyte signedByte => signedByte,
            byte unsignedByte => unsignedByte,
            short int16 => int16,
            ushort uint16 => uint16,
            int int32 => int32,
            uint uint32 => uint32,
            long int64 => int64,
            ulong uint64 => uint64,
            _ => 0m,
        };

    private static bool TryGetIntegralLimits(ITypeSymbol? type, out decimal minimum, out decimal maximum)
    {
        switch (type?.SpecialType)
        {
            case SpecialType.System_SByte:
                minimum = sbyte.MinValue;
                maximum = sbyte.MaxValue;
                return true;

            case SpecialType.System_Byte:
                minimum = byte.MinValue;
                maximum = byte.MaxValue;
                return true;

            case SpecialType.System_Int16:
                minimum = short.MinValue;
                maximum = short.MaxValue;
                return true;

            case SpecialType.System_UInt16:
                minimum = ushort.MinValue;
                maximum = ushort.MaxValue;
                return true;

            case SpecialType.System_Int32:
                minimum = int.MinValue;
                maximum = int.MaxValue;
                return true;

            case SpecialType.System_UInt32:
                minimum = uint.MinValue;
                maximum = uint.MaxValue;
                return true;

            case SpecialType.System_Int64:
                minimum = long.MinValue;
                maximum = long.MaxValue;
                return true;

            case SpecialType.System_UInt64:
                minimum = ulong.MinValue;
                maximum = ulong.MaxValue;
                return true;

            default:
                minimum = 0m;
                maximum = 0m;
                return false;
        }
    }

    private static ITypeSymbol? UnwrapNullable(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
        && named.TypeArguments.Length == 1
            ? named.TypeArguments[0]
            : type;

    private static string GetLiteralSuffix(string text, string allowedSuffixCharacters)
    {
        var index = text.Length;
        while (index > 0 && allowedSuffixCharacters.IndexOf(text[index - 1]) >= 0)
        {
            index--;
        }

        return text.Substring(index);
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
