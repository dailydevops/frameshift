namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Shared plumbing for <see cref="StringComparerMutator" /> and <see cref="StringComparisonMutator" />:
/// the six well known members both <see cref="System.StringComparer" /> and <see cref="System.StringComparison" />
/// declare under the very same names, the two independent axes those members move along - ordinal versus
/// culture aware, and case sensitive versus case insensitive - and the prose that names the axes a mutation
/// moves along.
/// </summary>
/// <remarks>
/// <para>
/// The two operators differ in exactly one place: <c>StringComparer.Ordinal</c> resolves to an
/// <see cref="IPropertySymbol" /> while <c>StringComparison.Ordinal</c> resolves to an
/// <see cref="IFieldSymbol" />. <see cref="ResolveMember{TSymbol}" /> captures that single difference as a
/// type parameter, so every other step - building the mutation set, rewriting the access, and describing
/// the move - is written once and shared by both.
/// </para>
/// </remarks>
internal abstract class CultureCaseAxisMutatorBase : MutationOperatorBase
{
    /// <summary>
    /// The six well known members shared by <see cref="System.StringComparer" /> and
    /// <see cref="System.StringComparison" />, in ordinal name order, each one with the position it takes on
    /// the two axes. All of them exist on every supported target framework.
    /// </summary>
    private static readonly ImmutableArray<AxisMember> _members =
    [
        new AxisMember("CurrentCulture", "current-culture", CultureAxis.CurrentCulture, ignoresCase: false),
        new AxisMember(
            "CurrentCultureIgnoreCase",
            "current-culture-ignore-case",
            CultureAxis.CurrentCulture,
            ignoresCase: true
        ),
        new AxisMember("InvariantCulture", "invariant-culture", CultureAxis.InvariantCulture, ignoresCase: false),
        new AxisMember(
            "InvariantCultureIgnoreCase",
            "invariant-culture-ignore-case",
            CultureAxis.InvariantCulture,
            ignoresCase: true
        ),
        new AxisMember("Ordinal", "ordinal", CultureAxis.Ordinal, ignoresCase: false),
        new AxisMember("OrdinalIgnoreCase", "ordinal-ignore-case", CultureAxis.Ordinal, ignoresCase: true),
    ];

    /// <summary>
    /// The name the display text spells the mutated type with.
    /// </summary>
    private readonly string _typeDisplayName;

    /// <summary>
    /// Initializes a new instance of the <see cref="CultureCaseAxisMutatorBase" /> class.
    /// </summary>
    /// <param name="id">The stable identifier prefix of the operator, e.g. <c>culture.string-comparer</c>.</param>
    /// <param name="kind">The operator family the operator belongs to.</param>
    /// <param name="typeDisplayName">The name the display text spells the mutated type with.</param>
    protected CultureCaseAxisMutatorBase(string id, MutationKind kind, string typeDisplayName)
        : base(id, kind, [SyntaxKind.SimpleMemberAccessExpression]) => _typeDisplayName = typeDisplayName;

    /// <inheritdoc />
    protected sealed override IEnumerable<Mutation> CreateMutationsCore(
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
        var source = ResolveSource(memberAccess, semanticModel, cancellationToken);

        // Neither a property returning a comparer nor an enumeration member is ever a compile time constant
        // by itself in a position that both accepts one and is otherwise legal; StringComparison happens to
        // be a constant, but the guard is applied for both operators all the same, so that a mutant is never
        // offered for a position an analyzer would see as broken source while it is still being typed.
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
    /// Resolves the accessed member, answering <see langword="null" /> for everything that is not one of the
    /// six well known members declared by the derived operator's own type.
    /// </summary>
    /// <param name="memberAccess">The member access to resolve.</param>
    /// <param name="semanticModel">The semantic model of the tree the access belongs to.</param>
    /// <param name="cancellationToken">A token to observe while binding.</param>
    /// <returns>The described member, or <see langword="null" /> if the access is out of scope.</returns>
    protected abstract AxisMember? ResolveSource(
        MemberAccessExpressionSyntax memberAccess,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Resolves the symbol behind <paramref name="memberAccess" /> as a <typeparamref name="TSymbol" /> whose
    /// containing type is <paramref name="metadataName" />, and looks the resulting member up by name.
    /// </summary>
    /// <remarks>
    /// This is the one place the two derived operators differ: <c>StringComparer.Ordinal</c> resolves to an
    /// <see cref="IPropertySymbol" />, while <c>StringComparison.Ordinal</c> resolves to an
    /// <see cref="IFieldSymbol" />. The comparison against <paramref name="metadataName" /> is symbolic on
    /// purpose: a type sharing the name and the member names, declared in another namespace, must never
    /// match.
    /// </remarks>
    /// <typeparam name="TSymbol">The symbol kind the well known members resolve to.</typeparam>
    /// <param name="memberAccess">The member access to resolve.</param>
    /// <param name="semanticModel">The semantic model of the tree the access belongs to.</param>
    /// <param name="metadataName">The metadata name of the type the member must belong to.</param>
    /// <param name="cancellationToken">A token to observe while binding.</param>
    /// <returns>The described member, or <see langword="null" /> if the access is out of scope.</returns>
    protected static AxisMember? ResolveMember<TSymbol>(
        MemberAccessExpressionSyntax memberAccess,
        SemanticModel semanticModel,
        string metadataName,
        CancellationToken cancellationToken
    )
        where TSymbol : class, ISymbol
    {
        if (semanticModel.GetSymbolInfo(memberAccess, cancellationToken).Symbol is not TSymbol symbol)
        {
            return null;
        }

        var declaringType = semanticModel.Compilation.GetTypeByMetadataName(metadataName);

        if (!SymbolEqualityComparer.Default.Equals(symbol.ContainingType, declaringType))
        {
            return null;
        }

        return _members.FirstOrDefault(member => string.Equals(member.Name, symbol.Name, StringComparison.Ordinal));
    }

    /// <summary>
    /// Rewrites only the name of the member access, which keeps a simple, an aliased and a fully qualified
    /// qualifier, and the trivia around the name, exactly as written.
    /// </summary>
    /// <param name="memberAccess">The member access to rewrite.</param>
    /// <param name="target">The member the access is redirected to.</param>
    /// <returns>The rewritten member access.</returns>
    private static MemberAccessExpressionSyntax Rewrite(MemberAccessExpressionSyntax memberAccess, AxisMember target) =>
        memberAccess.WithName(SyntaxFactory.IdentifierName(target.Name).WithTriviaFrom(memberAccess.Name));

    /// <summary>
    /// Composes the display text of a mutation, naming both members and the axes the mutation moves along.
    /// </summary>
    /// <param name="source">The member found in the source.</param>
    /// <param name="target">The member replacing it.</param>
    /// <returns>The display text.</returns>
    private string Describe(AxisMember source, AxisMember target) =>
        $"{_typeDisplayName}.{source.Name} => {_typeDisplayName}.{target.Name} ({DescribeAxes(source, target)})";

    /// <summary>
    /// Names the axes that differ between the two members. Two distinct members always differ on at least
    /// one of them, so the result is never empty.
    /// </summary>
    /// <param name="source">The member found in the source.</param>
    /// <param name="target">The member replacing it.</param>
    /// <returns>The named axes, separated by a comma.</returns>
    private static string DescribeAxes(AxisMember source, AxisMember target)
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
    /// <param name="source">The axis position of the member found in the source.</param>
    /// <param name="target">The axis position of the member replacing it.</param>
    /// <returns>The description, or an empty string if both members share the position.</returns>
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
    /// <param name="source">Whether the member found in the source ignores case.</param>
    /// <param name="target">Whether the member replacing it ignores case.</param>
    /// <returns>The description, or an empty string if both members agree.</returns>
    private static string DescribeCaseAxis(bool source, bool target)
    {
        if (source == target)
        {
            return string.Empty;
        }

        return source ? "case-insensitive => case-sensitive" : "case-sensitive => case-insensitive";
    }

    /// <summary>
    /// The position a member takes on the ordinal-versus-culture axis.
    /// </summary>
    protected enum CultureAxis
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
    /// One well known member shared by <see cref="System.StringComparer" /> and
    /// <see cref="System.StringComparison" />, with the identifier suffix it contributes and its position on
    /// both axes.
    /// </summary>
    protected sealed class AxisMember
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AxisMember" /> class.
        /// </summary>
        /// <param name="name">The name of the member.</param>
        /// <param name="suffix">The kebab-case form of <paramref name="name" />.</param>
        /// <param name="axis">The position on the ordinal-versus-culture axis.</param>
        /// <param name="ignoresCase">Whether the member ignores case.</param>
        public AxisMember(string name, string suffix, CultureAxis axis, bool ignoresCase)
        {
            Name = name;
            Suffix = suffix;
            Axis = axis;
            IgnoresCase = ignoresCase;
        }

        /// <summary>
        /// Gets the name of the member.
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
        /// Gets a value indicating whether the member ignores case.
        /// </summary>
        public bool IgnoresCase { get; }
    }
}
