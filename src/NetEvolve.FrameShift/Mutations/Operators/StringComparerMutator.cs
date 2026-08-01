namespace NetEvolve.FrameShift.Mutations.Operators;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Replaces an access to one of the six well known static properties of
/// <see cref="System.StringComparer" /> by every other one of them, which moves the comparer along two
/// independent axes: ordinal versus culture aware, and case sensitive versus case insensitive.
/// </summary>
/// <remarks>
/// <para>
/// The full matrix is offered, so every one of the six properties yields the five remaining ones. A
/// dictionary, a set or a sort silently changes its notion of key equality and of ordering when it is
/// built with the wrong comparer, and a test suite surviving all five mutants never observes that.
/// </para>
/// <para>
/// Unlike <see cref="StringComparisonMutator" />, which mutates enumeration members, the accessed symbol
/// here is a property returning an object. It is therefore resolved semantically and its containing type
/// is compared to <c>System.StringComparer</c> itself, so an equally named class from another namespace is
/// never touched. Only the name of the member access is rewritten, which keeps a simple, an aliased and a
/// fully qualified qualifier as written. A bare property name imported through <c>using static</c> is not a
/// member access at all and is therefore out of scope.
/// </para>
/// <para>
/// The axis logic - naming the two axes a mutation moves along, and the six-entry table describing where
/// every member sits on them - is shared with <see cref="StringComparisonMutator" /> through
/// <see cref="CultureCaseAxisMutatorBase" />. The only operator-specific step left here is resolving the
/// accessed member as an <see cref="IPropertySymbol" />.
/// </para>
/// </remarks>
internal sealed class StringComparerMutator : CultureCaseAxisMutatorBase
{
    /// <summary>
    /// The metadata name of the class this operator mutates.
    /// </summary>
    private const string StringComparerMetadataName = "System.StringComparer";

    /// <summary>
    /// Initializes a new instance of the <see cref="StringComparerMutator" /> class.
    /// </summary>
    public StringComparerMutator()
        : base("culture.string-comparer", MutationKind.StringComparer, "StringComparer") { }

    /// <inheritdoc />
    protected override AxisMember? ResolveSource(
        MemberAccessExpressionSyntax memberAccess,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    ) =>
        // The well known comparers are properties, not fields, so a member access resolving to anything else
        // - a field, a method, or the type of a qualifier such as 'System.StringComparer' - is out of scope.
        ResolveMember<IPropertySymbol>(memberAccess, semanticModel, StringComparerMetadataName, cancellationToken);
}
