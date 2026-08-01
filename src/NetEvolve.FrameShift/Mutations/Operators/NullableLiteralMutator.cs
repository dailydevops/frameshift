namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Moves a literal of a nullable value type (<c>bool?</c>, a nullable numeric type, <c>char?</c> or
/// <c>System.Guid?</c>) between its written value, <see langword="null" /> and the default value of the
/// underlying type.
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
/// <c>bool?</c> gets one further mutant of its own: <see langword="null" /> also moves to
/// <see langword="true" />, in addition to <see langword="false" />, its default. Unlike every other
/// supported type, whose non-default values are an open, unbounded set not worth enumerating,
/// <see langword="bool" /> has exactly one other value, and three valued logic treats it as its own
/// case - <c>flag == true</c> behaves differently from <c>flag != false</c> once <c>flag</c> is
/// <see langword="null" /> - so both directions out of <see langword="null" /> are worth provoking.
/// </para>
/// <para>
/// <c>System.Guid?</c> is included even though <c>Guid</c> has no literal syntax of its own: there is no
/// way to write a <c>Guid</c> value as a literal token, so this operator only ever encounters one for it
/// on the <see langword="null" /> side, moving it to <c>Guid.Empty</c>, the type's default value.
/// </para>
/// <para>
/// The converted type is resolved through the semantic model and has to be a nullable value type built
/// over <see langword="bool" />, <c>char</c>, one of the built-in numeric types or <c>System.Guid</c>,
/// never the corresponding non-nullable type and never a reference type. The check is load bearing
/// rather than cosmetic: on a plain, non-nullable type the mutant that introduces <see langword="null" />
/// does not compile, and although <c>MutantCompiler</c> would discard it afterwards, it would already
/// have consumed a slot of the per member mutant budget.
/// </para>
/// <para>
/// A negative numeric literal such as <c>-5</c> is a <see langword="unary" /> minus expression wrapping
/// the literal <c>5</c>, and the operand's own converted type is whatever the unary operator requires -
/// the non-nullable numeric type, never the nullable one the whole expression converts to afterwards.
/// Handling only <see cref="LiteralExpressionSyntax" /> would therefore never see a negative value at
/// all. This operator additionally recognises a unary minus over a numeric literal as its own node,
/// resolving the nullable conversion of the <em>whole</em> expression instead of the inner literal, and
/// replaces that whole expression the same way it replaces a bare literal.
/// </para>
/// <para>
/// A <see langword="null" /> literal on a reference type is deliberately out of scope. It belongs to a
/// separate and considerably riskier family, because the surviving mutant then depends on whether the
/// dereference is guarded rather than on the presence-of-a-value distinction this operator targets.
/// Other types without a literal syntax of their own - an <see langword="enum" />, <c>DateTime</c> or a
/// user-defined struct - are equally out of scope, since <c>Guid</c> is special-cased explicitly and
/// there is no literal node for this operator to mutate for any of the others in the first place.
/// </para>
/// </remarks>
internal sealed class NullableLiteralMutator : MutationOperatorBase
{
    /// <summary>
    /// The metadata name the underlying type is resolved by, so that a same-named type from another
    /// namespace can never match.
    /// </summary>
    private const string GuidMetadataName = "System.Guid";

    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds =
    [
        SyntaxKind.TrueLiteralExpression,
        SyntaxKind.FalseLiteralExpression,
        SyntaxKind.NumericLiteralExpression,
        SyntaxKind.CharacterLiteralExpression,
        SyntaxKind.NullLiteralExpression,
        SyntaxKind.UnaryMinusExpression,
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

        // Every supported syntax kind reaches this point as a literal expression, except the unary
        // minus over a negative numeric literal, which arrives as its own distinct node shape.
        var mutations = node is PrefixUnaryExpressionSyntax unaryMinus
            ? CreateMutationsForNegativeLiteral(unaryMinus, semanticModel, cancellationToken)
            : CreateMutationsForLiteralNode((LiteralExpressionSyntax)node, semanticModel, cancellationToken);

        foreach (var mutation in mutations)
        {
            yield return mutation;
        }
    }

    /// <summary>
    /// Builds the mutations for a literal node: the <see langword="null" />-state mutations for a
    /// <see langword="null" /> literal, or the written-value mutations for any other literal.
    /// </summary>
    private IEnumerable<Mutation> CreateMutationsForLiteralNode(
        LiteralExpressionSyntax literal,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (ConstantContext.IsRequired(literal))
        {
            yield break;
        }

        var convertedType = semanticModel.GetTypeInfo(literal, cancellationToken).ConvertedType;
        if (!TryGetNullableUnderlyingType(convertedType, semanticModel.Compilation, out var underlyingKind))
        {
            yield break;
        }

        var mutations = literal.IsKind(SyntaxKind.NullLiteralExpression)
            ? CreateMutationsForNull(literal, underlyingKind)
            : CreateMutationsForNonNullValue(
                literal,
                literal.Token.Text,
                underlyingKind,
                semanticModel,
                cancellationToken
            );

        foreach (var mutation in mutations)
        {
            yield return mutation;
        }
    }

    /// <summary>
    /// Builds the mutations for a negative numeric literal such as <c>-5</c>: resolves the nullable
    /// conversion of the whole unary expression, not of the inner literal, since that is where it
    /// actually happens.
    /// </summary>
    private IEnumerable<Mutation> CreateMutationsForNegativeLiteral(
        PrefixUnaryExpressionSyntax unaryMinus,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (
            unaryMinus.Operand is not LiteralExpressionSyntax operand
            || !operand.IsKind(SyntaxKind.NumericLiteralExpression)
        )
        {
            yield break;
        }

        if (ConstantContext.IsRequired(unaryMinus))
        {
            yield break;
        }

        var convertedType = semanticModel.GetTypeInfo(unaryMinus, cancellationToken).ConvertedType;
        if (!TryGetNullableUnderlyingType(convertedType, semanticModel.Compilation, out var underlyingKind))
        {
            yield break;
        }

        var displayText = unaryMinus.OperatorToken.Text + operand.Token.Text;

        foreach (
            var mutation in CreateMutationsForNonNullValue(
                unaryMinus,
                displayText,
                underlyingKind,
                semanticModel,
                cancellationToken
            )
        )
        {
            yield return mutation;
        }
    }

    /// <summary>
    /// Builds the mutations for a <see langword="null" /> literal: a move to the underlying type's
    /// default value, plus, for <see langword="bool" />, an additional move to <see langword="true" />.
    /// </summary>
    private IEnumerable<Mutation> CreateMutationsForNull(LiteralExpressionSyntax literal, UnderlyingKind underlyingKind)
    {
        var defaultExpression = CreateDefaultExpression(underlyingKind);
        if (defaultExpression is not null)
        {
            yield return CreateMutation(
                literal,
                defaultExpression,
                "null-to-default",
                $"null => {DisplayText(underlyingKind, defaultExpression)}"
            );
        }

        if (underlyingKind == UnderlyingKind.Boolean)
        {
            yield return CreateMutation(
                literal,
                SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression),
                "null-to-true",
                "null => true"
            );
        }
    }

    /// <summary>
    /// Builds the mutations for a written, non-<see langword="null" /> value - a bare literal or a
    /// negative numeric literal: a move to <see langword="null" />, plus a move to the underlying type's
    /// default value when the value is not that default already.
    /// </summary>
    private IEnumerable<Mutation> CreateMutationsForNonNullValue(
        ExpressionSyntax valueNode,
        string displayText,
        UnderlyingKind underlyingKind,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        yield return CreateMutation(
            valueNode,
            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression),
            "literal-to-null",
            $"{displayText} => null"
        );

        if (IsDefaultValue(valueNode, underlyingKind, semanticModel, cancellationToken))
        {
            yield break;
        }

        var defaultExpression = CreateDefaultExpression(underlyingKind);
        if (defaultExpression is not null)
        {
            yield return CreateMutation(
                valueNode,
                defaultExpression,
                "literal-to-default",
                $"{displayText} => {DisplayText(underlyingKind, defaultExpression)}"
            );
        }
    }

    /// <summary>
    /// The underlying types this operator knows how to build a default-value expression for, grouping
    /// the integral types that are all written as a plain, suffix-less numeric literal.
    /// </summary>
    private enum UnderlyingKind
    {
        Boolean,
        Char,
        Int32OrSmaller,
        UInt32,
        Int64,
        UInt64,
        Single,
        Double,
        Decimal,
        Guid,
    }

    /// <summary>
    /// Determines whether <paramref name="type" /> is <c>System.Nullable&lt;T&gt;</c> for an underlying
    /// <c>T</c> this operator supports.
    /// </summary>
    /// <param name="type">The type to inspect, which may be <see langword="null" /> for an unresolved node.</param>
    /// <param name="compilation">The compilation to resolve <c>System.Guid</c> against.</param>
    /// <param name="underlyingKind">The kind of <c>T</c> when this method returns <see langword="true" />.</param>
    /// <returns><see langword="true" /> if the type is a supported nullable value type.</returns>
    private static bool TryGetNullableUnderlyingType(
        ITypeSymbol? type,
        Compilation compilation,
        out UnderlyingKind underlyingKind
    )
    {
        if (
            type is INamedTypeSymbol named
            && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            && named.TypeArguments.Length == 1
        )
        {
            var underlying = named.TypeArguments[0];

            switch (underlying.SpecialType)
            {
                case SpecialType.System_Boolean:
                    underlyingKind = UnderlyingKind.Boolean;
                    return true;
                case SpecialType.System_Char:
                    underlyingKind = UnderlyingKind.Char;
                    return true;
                case SpecialType.System_SByte:
                case SpecialType.System_Byte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                    underlyingKind = UnderlyingKind.Int32OrSmaller;
                    return true;
                case SpecialType.System_UInt32:
                    underlyingKind = UnderlyingKind.UInt32;
                    return true;
                case SpecialType.System_Int64:
                    underlyingKind = UnderlyingKind.Int64;
                    return true;
                case SpecialType.System_UInt64:
                    underlyingKind = UnderlyingKind.UInt64;
                    return true;
                case SpecialType.System_Single:
                    underlyingKind = UnderlyingKind.Single;
                    return true;
                case SpecialType.System_Double:
                    underlyingKind = UnderlyingKind.Double;
                    return true;
                case SpecialType.System_Decimal:
                    underlyingKind = UnderlyingKind.Decimal;
                    return true;
            }

            var guidType = compilation.GetTypeByMetadataName(GuidMetadataName);
            if (guidType is not null && SymbolEqualityComparer.Default.Equals(underlying, guidType))
            {
                underlyingKind = UnderlyingKind.Guid;
                return true;
            }
        }

        underlyingKind = default;
        return false;
    }

    /// <summary>
    /// Determines whether <paramref name="valueNode" /> already holds the default value of
    /// <paramref name="underlyingKind" />, so that mutating it to that same default would not be a
    /// mutation at all.
    /// </summary>
    /// <remarks>
    /// <c>Guid</c> has no literal syntax, so <paramref name="valueNode" /> can never actually resolve to
    /// it here; the case exists only to keep the switch exhaustive.
    /// </remarks>
    private static bool IsDefaultValue(
        ExpressionSyntax valueNode,
        UnderlyingKind underlyingKind,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (underlyingKind == UnderlyingKind.Boolean)
        {
            return valueNode.IsKind(SyntaxKind.FalseLiteralExpression);
        }

        if (underlyingKind == UnderlyingKind.Guid)
        {
            return false;
        }

        var constant = semanticModel.GetConstantValue(valueNode, cancellationToken);
        if (!constant.HasValue)
        {
            return false;
        }

        return constant.Value switch
        {
            // sbyte, byte, short and ushort never appear here: C# has no literal syntax for them, so a
            // numeric literal targeting one of those types is always folded to int by the compiler before
            // this method ever sees it - int is the arm that actually fires for all four.
            char charValue => charValue == '\0',
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
    /// Builds the expression for the default value of <paramref name="underlyingKind" />: a literal for
    /// every built-in type, and <c>global::System.Guid.Empty</c> for <c>Guid</c>, which has no literal of
    /// its own.
    /// </summary>
    private static ExpressionSyntax? CreateDefaultExpression(UnderlyingKind underlyingKind) =>
        underlyingKind switch
        {
            UnderlyingKind.Boolean => SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression),
            UnderlyingKind.Char => SyntaxFactory.LiteralExpression(
                SyntaxKind.CharacterLiteralExpression,
                SyntaxFactory.Literal('\0')
            ),
            UnderlyingKind.Int32OrSmaller => SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(0)
            ),
            UnderlyingKind.UInt32 => SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(0U)
            ),
            UnderlyingKind.Int64 => SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(0L)
            ),
            UnderlyingKind.UInt64 => SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(0UL)
            ),
            UnderlyingKind.Single => SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(0F)
            ),
            UnderlyingKind.Double => SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(0D)
            ),
            UnderlyingKind.Decimal => SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(0M)
            ),
            UnderlyingKind.Guid => CreateGloballyQualifiedMemberAccess("System", "Guid", "Empty"),
            _ => null,
        };

    /// <summary>
    /// The human-readable text for a default-value expression, which is the expression's own text for a
    /// plain literal, and the short, unqualified <c>Guid.Empty</c> for the globally-qualified expression
    /// this operator builds for <c>Guid</c>.
    /// </summary>
    private static string DisplayText(UnderlyingKind underlyingKind, ExpressionSyntax defaultExpression) =>
        underlyingKind == UnderlyingKind.Guid ? "Guid.Empty" : defaultExpression.ToString();

    /// <summary>
    /// Builds <c>global::</c><paramref name="namespaceName" />.<paramref name="typeName" />.
    /// <paramref name="memberName" />, fully qualified so the mutant resolves regardless of what the
    /// mutated file has - or has not - imported.
    /// </summary>
    private static MemberAccessExpressionSyntax CreateGloballyQualifiedMemberAccess(
        string namespaceName,
        string typeName,
        string memberName
    )
    {
        var globalNamespace = SyntaxFactory.AliasQualifiedName(
            SyntaxFactory.IdentifierName(SyntaxFactory.Token(SyntaxKind.GlobalKeyword)),
            SyntaxFactory.IdentifierName(namespaceName)
        );

        var qualifiedType = SyntaxFactory.QualifiedName(globalNamespace, SyntaxFactory.IdentifierName(typeName));

        return SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            qualifiedType,
            SyntaxFactory.IdentifierName(memberName)
        );
    }
}
