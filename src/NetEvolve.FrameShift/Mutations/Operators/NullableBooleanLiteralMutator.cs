namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Moves a literal between the three states of <c>bool?</c>, replacing <see langword="true" /> and
/// <see langword="false" /> by <see langword="null" /> and <see langword="null" /> by both
/// <see langword="true" /> and <see langword="false" />.
/// </summary>
/// <remarks>
/// <para>
/// A <c>bool?</c> has three states, and three valued logic treats the third one as its own case:
/// <c>null &amp; false</c> is <see langword="false" /> and <c>null | true</c> is <see langword="true" />,
/// but <c>null &amp; true</c> is <see langword="null" />, and <c>if (flag == true)</c> behaves differently
/// from <c>if (flag != false)</c> once <c>flag</c> is <see langword="null" />. Swapping
/// <see langword="true" /> against <see langword="false" />, which
/// <see cref="BooleanLiteralMutator" /> already does, never reaches that third state, so the defects
/// living in it stay unprovoked.
/// </para>
/// <para>
/// The converted type is resolved through the semantic model and has to be <c>bool?</c>, never
/// <c>bool</c> and never a reference type. The check is load bearing rather than cosmetic: on a plain
/// <c>bool</c> the mutant <c>true</c> to <see langword="null" /> does not compile, and although
/// <c>MutantCompiler</c> would discard it afterwards, it would already have consumed a slot of the per
/// member mutant budget.
/// </para>
/// <para>
/// A <see langword="null" /> literal on a reference type is deliberately out of scope. It belongs to a
/// separate and considerably riskier family, because the surviving mutant then depends on whether the
/// dereference is guarded rather than on the three valued logic this operator targets.
/// </para>
/// </remarks>
internal sealed class NullableBooleanLiteralMutator : MutationOperatorBase
{
    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds =
    [
        SyntaxKind.TrueLiteralExpression,
        SyntaxKind.FalseLiteralExpression,
        SyntaxKind.NullLiteralExpression,
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="NullableBooleanLiteralMutator" /> class.
    /// </summary>
    public NullableBooleanLiteralMutator()
        : base("nullable-boolean-literal", MutationKind.NullableBooleanLiteral, _supportedSyntaxKinds) { }

    /// <inheritdoc />
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // MutationOperatorBase.CreateMutations only hands over nodes of one of the SupportedSyntaxKinds,
        // and all three of them are literal expressions, so the cast cannot fail and no type test is
        // needed here.
        var literal = (LiteralExpressionSyntax)node;

        if (ConstantContext.IsRequired(literal))
        {
            yield break;
        }

        if (!IsNullableBoolean(semanticModel.GetTypeInfo(literal, cancellationToken).ConvertedType))
        {
            yield break;
        }

        if (literal.IsKind(SyntaxKind.NullLiteralExpression))
        {
            yield return CreateMutation(
                literal,
                SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression),
                "null-to-true",
                "null => true"
            );

            yield return CreateMutation(
                literal,
                SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression),
                "null-to-false",
                "null => false"
            );

            yield break;
        }

        var suffix = literal.IsKind(SyntaxKind.TrueLiteralExpression) ? "true-to-null" : "false-to-null";
        var displayName = literal.IsKind(SyntaxKind.TrueLiteralExpression) ? "true => null" : "false => null";

        yield return CreateMutation(
            literal,
            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression),
            suffix,
            displayName
        );
    }

    /// <summary>
    /// Determines whether <paramref name="type" /> is <c>System.Nullable&lt;bool&gt;</c>.
    /// </summary>
    /// <param name="type">The type to inspect, which may be <see langword="null" /> for an unresolved node.</param>
    /// <returns><see langword="true" /> if the type is <c>bool?</c>.</returns>
    private static bool IsNullableBoolean(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
        && named.TypeArguments.Length == 1
        && named.TypeArguments[0].SpecialType == SpecialType.System_Boolean;
}
