namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Exchanges the well-known static culture members of <c>System.Globalization.CultureInfo</c> for each
/// other, so that code which formats or parses with one culture is executed with another one.
/// </summary>
/// <remarks>
/// <para>
/// The valuable mutation of this family is <c>InvariantCulture</c> to <c>CurrentCulture</c>: code that
/// deliberately formats or parses invariantly keeps working in a suite that only ever runs under a single
/// locale, and the surviving mutant names exactly the assertion that locale hides. The reverse direction is
/// worth as much, because a build machine usually runs under an invariant or English locale, so code that
/// must be culture-sensitive is rarely proven to be.
/// </para>
/// <para>
/// The member sets are deliberately not the full three-by-two matrix:
/// </para>
/// <list type="bullet">
/// <item><c>InvariantCulture</c> becomes <c>CurrentCulture</c> only. <c>CurrentUICulture</c> would carry the
/// very same signal - <em>not invariant any more</em> - and a test surviving one of the two always survives
/// the other, so the second mutant would only duplicate a report entry.</item>
/// <item><c>CurrentCulture</c> becomes <c>InvariantCulture</c> and <c>CurrentUICulture</c>. The second swap
/// pins the difference between the formatting culture and the resource culture, which really do differ for a
/// user whose interface language and number format disagree.</item>
/// <item><c>CurrentUICulture</c> becomes <c>CurrentCulture</c> only, the mirror of the swap above.
/// <c>InvariantCulture</c> is left out: the invariant culture is the neutral resource fallback a lookup
/// reaches anyway in a suite without satellite assemblies, so that mutant would mostly survive as noise
/// rather than as a finding.</item>
/// </list>
/// <para>
/// <c>CultureInfo.GetCultureInfo("de-DE")</c> and <c>new CultureInfo("de-DE")</c> do not belong to this
/// operator. They name one concrete culture, and there is no defensible other culture to put in its place;
/// what carries the risk there is the culture name, which is a string literal and therefore already covered.
/// Neither construct is a member access to one of the well-known members, so both are skipped by the very
/// same check that skips every other member.
/// </para>
/// <para>
/// An assignment <em>to</em> a culture member is skipped as well. It installs ambient state for everything
/// that runs afterwards instead of describing how the code under test formats, so mutating it would change
/// the culture other code observes rather than the behaviour being measured - and for
/// <c>InvariantCulture</c>, which has no setter, the mutant would not even compile.
/// </para>
/// </remarks>
internal sealed class CultureInfoMutator : MutationOperatorBase
{
    /// <summary>
    /// The metadata name the receiver type is resolved by, so that a same-named type from another
    /// namespace can never match.
    /// </summary>
    private const string CultureInfoMetadataName = "System.Globalization.CultureInfo";

    private const string InvariantCultureName = "InvariantCulture";
    private const string CurrentCultureName = "CurrentCulture";
    private const string CurrentUICultureName = "CurrentUICulture";

    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds =
    [
        SyntaxKind.SimpleMemberAccessExpression,
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="CultureInfoMutator" /> class.
    /// </summary>
    public CultureInfoMutator()
        : base("culture.culture-info", MutationKind.CultureInfo, _supportedSyntaxKinds) { }

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

        if (IsConstantRequired(memberAccess) || IsAssignmentTarget(memberAccess))
        {
            return [];
        }

        var member = ResolveWellKnownMember(memberAccess, semanticModel, cancellationToken);

        return member is null ? [] : CreateSwaps(memberAccess, member);
    }

    /// <summary>
    /// Builds the swaps of the member <paramref name="member" />, whose reasoning is documented on the
    /// class itself.
    /// </summary>
    /// <param name="memberAccess">The member access to replace.</param>
    /// <param name="member">The name of the resolved well-known member.</param>
    /// <returns>The mutations of that member, never empty.</returns>
    private ImmutableArray<Mutation> CreateSwaps(MemberAccessExpressionSyntax memberAccess, string member) =>
        member switch
        {
            InvariantCultureName => [CreateSwap(memberAccess, CurrentCultureName, "invariant-to-current")],
            CurrentCultureName =>
            [
                CreateSwap(memberAccess, InvariantCultureName, "current-to-invariant"),
                CreateSwap(memberAccess, CurrentUICultureName, "current-to-current-ui"),
            ],

            // ResolveWellKnownMember answers with one of the three names only, so this arm is the
            // CurrentUICulture one and no further test is needed to reach it.
            _ => [CreateSwap(memberAccess, CurrentCultureName, "current-ui-to-current")],
        };

    /// <summary>
    /// Replaces only the member name of <paramref name="memberAccess" />, which keeps the receiver exactly
    /// as it was written: a simple name stays simple, a fully qualified one stays qualified, and an alias
    /// stays an alias.
    /// </summary>
    /// <param name="memberAccess">The member access to replace.</param>
    /// <param name="target">The member name to put in place.</param>
    /// <param name="operatorSuffix">The suffix identifying the concrete mutation.</param>
    /// <returns>The created mutation.</returns>
    private Mutation CreateSwap(MemberAccessExpressionSyntax memberAccess, string target, string operatorSuffix)
    {
        var name = SyntaxFactory.IdentifierName(target).WithTriviaFrom(memberAccess.Name);
        var displayName = $"{memberAccess.Name.Identifier.ValueText} => {target}";

        return CreateMutation(memberAccess, memberAccess.WithName(name), operatorSuffix, displayName);
    }

    /// <summary>
    /// Resolves <paramref name="memberAccess" /> to one of the well-known culture members, semantically and
    /// never by name alone: the accessed symbol has to be a property of exactly the
    /// <c>System.Globalization.CultureInfo</c> of this compilation.
    /// </summary>
    /// <param name="memberAccess">The member access to resolve.</param>
    /// <param name="semanticModel">The semantic model of the tree the access belongs to.</param>
    /// <param name="cancellationToken">A token to observe while resolving.</param>
    /// <returns>The member name, or <see langword="null" /> when the access is something else.</returns>
    private static string? ResolveWellKnownMember(
        MemberAccessExpressionSyntax memberAccess,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (semanticModel.GetSymbolInfo(memberAccess, cancellationToken).Symbol is not IPropertySymbol property)
        {
            return null;
        }

        if (!IsWellKnownName(property.Name))
        {
            return null;
        }

        var cultureInfo = semanticModel.Compilation.GetTypeByMetadataName(CultureInfoMetadataName);

        return cultureInfo is not null && SymbolEqualityComparer.Default.Equals(property.ContainingType, cultureInfo)
            ? property.Name
            : null;
    }

    /// <summary>
    /// Determines whether <paramref name="name" /> is one of the three well-known member names.
    /// </summary>
    /// <param name="name">The member name to check.</param>
    /// <returns><see langword="true" /> if the name is one of the well-known ones.</returns>
    private static bool IsWellKnownName(string name) =>
        string.Equals(name, InvariantCultureName, StringComparison.Ordinal)
        || string.Equals(name, CurrentCultureName, StringComparison.Ordinal)
        || string.Equals(name, CurrentUICultureName, StringComparison.Ordinal);

    /// <summary>
    /// Determines whether <paramref name="node" /> is the target of an assignment rather than a read of a
    /// culture.
    /// </summary>
    /// <param name="node">The node to inspect.</param>
    /// <returns><see langword="true" /> if the node is assigned to.</returns>
    private static bool IsAssignmentTarget(ExpressionSyntax node) =>
        node.Parent is AssignmentExpressionSyntax assignment && ReferenceEquals(assignment.Left, node);

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
