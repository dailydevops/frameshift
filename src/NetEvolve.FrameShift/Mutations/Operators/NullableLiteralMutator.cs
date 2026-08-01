namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Moves a literal of a nullable value type (<c>bool?</c>, a nullable numeric type or <c>char?</c>)
/// between its written value, <see langword="null" /> and the default value of the underlying type.
/// </summary>
/// <remarks>
/// <para>
/// A nullable value type has one state a non-nullable value never has: the absence of a value. Code
/// that only ever compares against a concrete value - <c>flag == true</c>, <c>count == 1</c> - can look
/// identical to code that also has to account for "no value was given" once the type becomes nullable,
/// and a test suite that never provokes <see langword="null" /> would not notice the difference. This
/// operator produces that mutant: a written literal becomes <see langword="null" />, and
/// <see langword="null" /> becomes the type's default value, so a surviving mutant proves the code
/// never asked "is there a value at all", only "which value is it".
/// </para>
/// <para>
/// The written-literal side additionally moves to the default value of the underlying type - <c>0</c>,
/// <see langword="false" /> or <c>'\0'</c> - whenever the literal is not that value already, so a
/// surviving mutant there proves the code never distinguishes the type's default from an explicitly
/// given, different value. A literal that already is the default value only produces the
/// <see langword="null" /> mutant, since mutating it to itself would not be a mutation at all.
/// </para>
/// <para>
/// The converted type is resolved through the semantic model and has to be a nullable value type built
/// over <see langword="bool" />, <c>char</c> or one of the built-in numeric types, never the
/// corresponding non-nullable type and never a reference type. The check is load bearing rather than
/// cosmetic: on a plain, non-nullable type the mutant that introduces <see langword="null" /> does not
/// compile, and although <c>MutantCompiler</c> would discard it afterwards, it would already have
/// consumed a slot of the per member mutant budget.
/// </para>
/// <para>
/// A <see langword="null" /> literal on a reference type is deliberately out of scope. It belongs to a
/// separate and considerably riskier family, because the surviving mutant then depends on whether the
/// dereference is guarded rather than on the presence-of-a-value distinction this operator targets.
/// Types without a literal syntax of their own - an <see langword="enum" />, <c>DateTime</c> or a
/// user-defined struct - are equally out of scope, since there is no literal node for this operator to
/// mutate in the first place.
/// </para>
/// </remarks>
internal sealed class NullableLiteralMutator : MutationOperatorBase
{
    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds =
    [
        SyntaxKind.TrueLiteralExpression,
        SyntaxKind.FalseLiteralExpression,
        SyntaxKind.NumericLiteralExpression,
        SyntaxKind.CharacterLiteralExpression,
        SyntaxKind.NullLiteralExpression,
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="NullableLiteralMutator" /> class.
    /// </summary>
    public NullableLiteralMutator()
        : base("nullable-literal", MutationKind.NullableLiteral, _supportedSyntaxKinds) { }

    /// <inheritdoc />
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // MutationOperatorBase.CreateMutations only hands over nodes of one of the SupportedSyntaxKinds,
        // and all five of them are literal expressions, so the cast cannot fail and no type test is
        // needed here.
        var literal = (LiteralExpressionSyntax)node;

        if (ConstantContext.IsRequired(literal))
        {
            yield break;
        }

        var convertedType = semanticModel.GetTypeInfo(literal, cancellationToken).ConvertedType;
        if (!TryGetNullableUnderlyingType(convertedType, out var underlyingType))
        {
            yield break;
        }

        if (literal.IsKind(SyntaxKind.NullLiteralExpression))
        {
            var defaultLiteral = CreateDefaultLiteral(underlyingType);
            if (defaultLiteral is not null)
            {
                yield return CreateMutation(
                    literal,
                    defaultLiteral,
                    "null-to-default",
                    $"null => {defaultLiteral.Token.Text}"
                );
            }

            yield break;
        }

        yield return CreateMutation(
            literal,
            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression),
            "literal-to-null",
            $"{literal.Token.Text} => null"
        );

        if (!IsDefaultValue(literal, underlyingType, semanticModel, cancellationToken))
        {
            var defaultLiteral = CreateDefaultLiteral(underlyingType);
            if (defaultLiteral is not null)
            {
                yield return CreateMutation(
                    literal,
                    defaultLiteral,
                    "literal-to-default",
                    $"{literal.Token.Text} => {defaultLiteral.Token.Text}"
                );
            }
        }
    }

    /// <summary>
    /// Determines whether <paramref name="type" /> is <c>System.Nullable&lt;T&gt;</c> for a
    /// <paramref name="underlyingType" /> this operator knows how to build a default-value literal for.
    /// </summary>
    /// <param name="type">The type to inspect, which may be <see langword="null" /> for an unresolved node.</param>
    /// <param name="underlyingType">The special type of <c>T</c> when this method returns <see langword="true" />.</param>
    /// <returns><see langword="true" /> if the type is a supported nullable value type.</returns>
    private static bool TryGetNullableUnderlyingType(ITypeSymbol? type, out SpecialType underlyingType)
    {
        if (
            type is INamedTypeSymbol named
            && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            && named.TypeArguments.Length == 1
        )
        {
            underlyingType = named.TypeArguments[0].SpecialType;

            return underlyingType
                is SpecialType.System_Boolean
                    or SpecialType.System_Char
                    or SpecialType.System_SByte
                    or SpecialType.System_Byte
                    or SpecialType.System_Int16
                    or SpecialType.System_UInt16
                    or SpecialType.System_Int32
                    or SpecialType.System_UInt32
                    or SpecialType.System_Int64
                    or SpecialType.System_UInt64
                    or SpecialType.System_Single
                    or SpecialType.System_Double
                    or SpecialType.System_Decimal;
        }

        underlyingType = SpecialType.None;
        return false;
    }

    /// <summary>
    /// Determines whether <paramref name="literal" /> already holds the default value of
    /// <paramref name="underlyingType" />, so that mutating it to that same default would not be a
    /// mutation at all.
    /// </summary>
    private static bool IsDefaultValue(
        LiteralExpressionSyntax literal,
        SpecialType underlyingType,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (underlyingType == SpecialType.System_Boolean)
        {
            return literal.IsKind(SyntaxKind.FalseLiteralExpression);
        }

        var constant = semanticModel.GetConstantValue(literal, cancellationToken);
        if (!constant.HasValue)
        {
            return false;
        }

        return constant.Value switch
        {
            char charValue => charValue == '\0',
            sbyte sbyteValue => sbyteValue == 0,
            byte byteValue => byteValue == 0,
            short shortValue => shortValue == 0,
            ushort ushortValue => ushortValue == 0,
            int intValue => intValue == 0,
            uint uintValue => uintValue == 0,
            long longValue => longValue == 0,
            ulong ulongValue => ulongValue == 0,
            float floatValue => floatValue == 0F,
            double doubleValue => doubleValue == 0D,
            decimal decimalValue => decimalValue == 0M,
            _ => false,
        };
    }

    /// <summary>
    /// Builds the literal expression for the default value of <paramref name="underlyingType" />.
    /// </summary>
    private static LiteralExpressionSyntax? CreateDefaultLiteral(SpecialType underlyingType) =>
        underlyingType switch
        {
            SpecialType.System_Boolean => SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression),
            SpecialType.System_Char => SyntaxFactory.LiteralExpression(
                SyntaxKind.CharacterLiteralExpression,
                SyntaxFactory.Literal('\0')
            ),
            SpecialType.System_SByte
            or SpecialType.System_Byte
            or SpecialType.System_Int16
            or SpecialType.System_UInt16
            or SpecialType.System_Int32 => SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(0)
            ),
            SpecialType.System_UInt32 => SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(0U)
            ),
            SpecialType.System_Int64 => SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(0L)
            ),
            SpecialType.System_UInt64 => SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(0UL)
            ),
            SpecialType.System_Single => SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(0F)
            ),
            SpecialType.System_Double => SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(0D)
            ),
            SpecialType.System_Decimal => SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(0M)
            ),
            _ => null,
        };
}
