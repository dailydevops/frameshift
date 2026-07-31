namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
/// </remarks>
internal sealed class StringComparerMutator : MutationOperatorBase
{
    /// <summary>
    /// The metadata name of the class this operator mutates.
    /// </summary>
    private const string StringComparerMetadataName = "System.StringComparer";

    /// <summary>
    /// The name the display text spells the class with.
    /// </summary>
    private const string TypeDisplayName = "StringComparer";

    /// <summary>
    /// The six well known comparers of <see cref="System.StringComparer" />, in ordinal name order, each one
    /// with the position it takes on the two axes. All of them exist on every supported target framework.
    /// </summary>
    private static readonly ImmutableArray<ComparerMember> _members =
    [
        new ComparerMember("CurrentCulture", "current-culture", CultureAxis.CurrentCulture, ignoresCase: false),
        new ComparerMember(
            "CurrentCultureIgnoreCase",
            "current-culture-ignore-case",
            CultureAxis.CurrentCulture,
            ignoresCase: true
        ),
        new ComparerMember("InvariantCulture", "invariant-culture", CultureAxis.InvariantCulture, ignoresCase: false),
        new ComparerMember(
            "InvariantCultureIgnoreCase",
            "invariant-culture-ignore-case",
            CultureAxis.InvariantCulture,
            ignoresCase: true
        ),
        new ComparerMember("Ordinal", "ordinal", CultureAxis.Ordinal, ignoresCase: false),
        new ComparerMember("OrdinalIgnoreCase", "ordinal-ignore-case", CultureAxis.Ordinal, ignoresCase: true),
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="StringComparerMutator" /> class.
    /// </summary>
    public StringComparerMutator()
        : base("culture.string-comparer", MutationKind.StringComparer, [SyntaxKind.SimpleMemberAccessExpression]) { }

    /// <inheritdoc />
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // MutationOperatorBase.CreateMutations only hands over nodes of one of the SupportedSyntaxKinds, and
        // SyntaxKind.SimpleMemberAccessExpression is always a member access expression, so the cast cannot
        // fail and no type test is needed here.
        var memberAccess = (MemberAccessExpressionSyntax)node;
        var source = ResolveMember(memberAccess, semanticModel, cancellationToken);

        // A comparer is an object reference and never a compile time constant, so no constant position accepts
        // one and valid code cannot reach the second guard. It is applied all the same, so that the operator
        // stays silent on a source file that does not compile - which is what an analyzer sees while typing.
        if (source is null || ConstantContext.IsRequired(memberAccess))
        {
            yield break;
        }

        foreach (var target in _members)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ReferenceEquals(target, source))
            {
                continue;
            }

            yield return CreateMutation(
                memberAccess,
                Rewrite(memberAccess, target),
                $"{source.Suffix}-to-{target.Suffix}",
                Describe(source, target)
            );
        }
    }

    /// <summary>
    /// Resolves the accessed property, answering <see langword="null" /> for everything that is not one of
    /// the well known comparers declared by <c>System.StringComparer</c> itself.
    /// </summary>
    /// <param name="memberAccess">The member access to resolve.</param>
    /// <param name="semanticModel">The semantic model of the tree the access belongs to.</param>
    /// <param name="cancellationToken">A token to observe while binding.</param>
    /// <returns>The described comparer, or <see langword="null" /> if the access is out of scope.</returns>
    private static ComparerMember? ResolveMember(
        MemberAccessExpressionSyntax memberAccess,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        // The well known comparers are properties, not fields, so a member access resolving to anything else
        // - a field, a method, or the type of a qualifier such as 'System.StringComparer' - is out of scope.
        if (semanticModel.GetSymbolInfo(memberAccess, cancellationToken).Symbol is not IPropertySymbol property)
        {
            return null;
        }

        // The comparison is symbolic on purpose: a class named StringComparer in another namespace can expose
        // the very same property names, and matching by name would mutate it as well. The containing type is
        // the declaring one, so a derived comparer inheriting the properties still resolves to this type.
        var declaringType = semanticModel.Compilation.GetTypeByMetadataName(StringComparerMetadataName);

        if (!SymbolEqualityComparer.Default.Equals(property.ContainingType, declaringType))
        {
            return null;
        }

        return _members.FirstOrDefault(member => string.Equals(member.Name, property.Name, StringComparison.Ordinal));
    }

    /// <summary>
    /// Rewrites only the name of the member access, which keeps a simple, an aliased and a fully qualified
    /// qualifier, and the trivia around the name, exactly as written.
    /// </summary>
    /// <param name="memberAccess">The member access to rewrite.</param>
    /// <param name="target">The comparer the access is redirected to.</param>
    /// <returns>The rewritten member access.</returns>
    private static MemberAccessExpressionSyntax Rewrite(
        MemberAccessExpressionSyntax memberAccess,
        ComparerMember target
    ) => memberAccess.WithName(SyntaxFactory.IdentifierName(target.Name).WithTriviaFrom(memberAccess.Name));

    /// <summary>
    /// Composes the display text of a mutation, naming both comparers and the axes the mutation moves along.
    /// </summary>
    /// <param name="source">The comparer found in the source.</param>
    /// <param name="target">The comparer replacing it.</param>
    /// <returns>The display text.</returns>
    private static string Describe(ComparerMember source, ComparerMember target) =>
        $"{TypeDisplayName}.{source.Name} => {TypeDisplayName}.{target.Name} ({DescribeAxes(source, target)})";

    /// <summary>
    /// Names the axes that differ between the two comparers. Two distinct comparers always differ on at
    /// least one of them, so the result is never empty.
    /// </summary>
    /// <param name="source">The comparer found in the source.</param>
    /// <param name="target">The comparer replacing it.</param>
    /// <returns>The named axes, separated by a comma.</returns>
    private static string DescribeAxes(ComparerMember source, ComparerMember target)
    {
        var axes = new List<string>(2);
        var culture = DescribeCultureAxis(source.Axis, target.Axis);
        var casing = DescribeCaseAxis(source.IgnoresCase, target.IgnoresCase);

        if (culture.Length > 0)
        {
            axes.Add(culture);
        }

        if (casing.Length > 0)
        {
            axes.Add(casing);
        }

        return string.Join(", ", axes);
    }

    /// <summary>
    /// Describes the move on the ordinal-versus-culture axis.
    /// </summary>
    /// <param name="source">The axis position of the comparer found in the source.</param>
    /// <param name="target">The axis position of the comparer replacing it.</param>
    /// <returns>The description, or an empty string if both comparers share the position.</returns>
    private static string DescribeCultureAxis(CultureAxis source, CultureAxis target)
    {
        if (source == target)
        {
            return string.Empty;
        }

        if (source == CultureAxis.Ordinal)
        {
            return "ordinal => culture";
        }

        if (target == CultureAxis.Ordinal)
        {
            return "culture => ordinal";
        }

        return source == CultureAxis.InvariantCulture
            ? "invariant culture => current culture"
            : "current culture => invariant culture";
    }

    /// <summary>
    /// Describes the move on the case-sensitivity axis.
    /// </summary>
    /// <param name="source">Whether the comparer found in the source ignores case.</param>
    /// <param name="target">Whether the comparer replacing it ignores case.</param>
    /// <returns>The description, or an empty string if both comparers agree.</returns>
    private static string DescribeCaseAxis(bool source, bool target)
    {
        if (source == target)
        {
            return string.Empty;
        }

        return source ? "case-insensitive => case-sensitive" : "case-sensitive => case-insensitive";
    }

    /// <summary>
    /// The position a comparer takes on the ordinal-versus-culture axis.
    /// </summary>
    private enum CultureAxis
    {
        /// <summary>
        /// Compares by the numeric value of the characters, without any culture involved.
        /// </summary>
        Ordinal,

        /// <summary>
        /// Compares with the linguistic rules of the invariant culture.
        /// </summary>
        InvariantCulture,

        /// <summary>
        /// Compares with the linguistic rules of the culture of the current thread.
        /// </summary>
        CurrentCulture,
    }

    /// <summary>
    /// One well known comparer of <see cref="System.StringComparer" />, with the identifier suffix it
    /// contributes and its position on both axes.
    /// </summary>
    private sealed class ComparerMember
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ComparerMember" /> class.
        /// </summary>
        /// <param name="name">The name of the property.</param>
        /// <param name="suffix">The kebab-case form of <paramref name="name" />.</param>
        /// <param name="axis">The position on the ordinal-versus-culture axis.</param>
        /// <param name="ignoresCase">Whether the comparer ignores case.</param>
        public ComparerMember(string name, string suffix, CultureAxis axis, bool ignoresCase)
        {
            Name = name;
            Suffix = suffix;
            Axis = axis;
            IgnoresCase = ignoresCase;
        }

        /// <summary>
        /// Gets the name of the property.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the kebab-case form of <see cref="Name" />, used in the operator identifier.
        /// </summary>
        public string Suffix { get; }

        /// <summary>
        /// Gets the position on the ordinal-versus-culture axis.
        /// </summary>
        public CultureAxis Axis { get; }

        /// <summary>
        /// Gets a value indicating whether the comparer ignores case.
        /// </summary>
        public bool IgnoresCase { get; }
    }
}
