namespace NetEvolve.FrameShift.Mutations.Operators;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Replaces a member access on <see cref="System.StringComparison" /> by every other member of that
/// enumeration, which moves the comparison along two independent axes: ordinal versus culture aware,
/// and case sensitive versus case insensitive.
/// </summary>
/// <remarks>
/// <para>
/// The full matrix is offered, so every one of the six members yields the five remaining ones. A test
/// suite that survives all of them never observes the difference between an ordinal and a culture aware
/// comparison, nor between a case sensitive and a case insensitive one.
/// </para>
/// <para>
/// The accessed member is resolved semantically and its containing type is compared to
/// <c>System.StringComparison</c> itself, therefore an equally named enumeration from another namespace is
/// never touched. Only the name of the member access is rewritten, so a simple, an aliased and a fully
/// qualified qualifier all survive the mutation unchanged. A bare member name imported through
/// <c>using static</c> is not a member access at all and is therefore out of scope.
/// </para>
/// <para>
/// The axis logic - naming the two axes a mutation moves along, and the six-entry table describing where
/// every member sits on them - is shared with <see cref="StringComparerMutator" /> through
/// <see cref="CultureCaseAxisMutatorBase" />. The only operator-specific step left here is resolving the
/// accessed member as an <see cref="IFieldSymbol" />.
/// </para>
/// </remarks>
internal sealed class StringComparisonMutator : CultureCaseAxisMutatorBase
{
    /// <summary>
    /// The metadata name of the enumeration this operator mutates.
    /// </summary>
    private const string StringComparisonMetadataName = "System.StringComparison";

    /// <summary>
    /// Initializes a new instance of the <see cref="StringComparisonMutator" /> class.
    /// </summary>
    public StringComparisonMutator()
        : base("culture.string-comparison", MutationKind.StringComparison, "StringComparison") { }

    /// <inheritdoc />
    protected override AxisMember? ResolveSource(
        MemberAccessExpressionSyntax memberAccess,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    ) =>
        // An enumeration member is a constant field, and a member access resolving to anything else - a
        // property, a method, or the type of a qualifier such as 'System.StringComparison' - is not one.
        ResolveMember<IFieldSymbol>(memberAccess, semanticModel, StringComparisonMetadataName, cancellationToken);
}
