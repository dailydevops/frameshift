namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
/// </remarks>
internal sealed class StringComparisonMutator : MutationOperatorBase
{
    /// <summary>
    /// The metadata name of the enumeration this operator mutates.
    /// </summary>
    private const string StringComparisonMetadataName = "System.StringComparison";

    /// <summary>
    /// The name the display text spells the enumeration with.
    /// </summary>
    private const string TypeDisplayName = "StringComparison";

    /// <summary>
    /// The six members of <see cref="System.StringComparison" />, in declaration order, each one with the
    /// position it takes on the two axes. All of them exist on every supported target framework.
    /// </summary>
    private static readonly ImmutableArray<ComparisonMember> _members =
    [
        new ComparisonMember("CurrentCulture", "current-culture", CultureAxis.CurrentCulture, ignoresCase: false),
        new ComparisonMember(
            "CurrentCultureIgnoreCase",
            "current-culture-ignore-case",
            CultureAxis.CurrentCulture,
            ignoresCase: true
        ),
        new ComparisonMember("InvariantCulture", "invariant-culture", CultureAxis.InvariantCulture, ignoresCase: false),
        new ComparisonMember(
            "InvariantCultureIgnoreCase",
            "invariant-culture-ignore-case",
            CultureAxis.InvariantCulture,
            ignoresCase: true
        ),
        new ComparisonMember("Ordinal", "ordinal", CultureAxis.Ordinal, ignoresCase: false),
        new ComparisonMember("OrdinalIgnoreCase", "ordinal-ignore-case", CultureAxis.Ordinal, ignoresCase: true),
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="StringComparisonMutator" /> class.
    /// </summary>
    public StringComparisonMutator()
        : base("culture.string-comparison", MutationKind.StringComparison, [SyntaxKind.SimpleMemberAccessExpression])
    { }

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

        if (source is null || IsConstantRequired(memberAccess))
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
    /// Resolves the accessed enumeration member, answering <see langword="null" /> for everything that is
    /// not a member of <c>System.StringComparison</c> itself.
    /// </summary>
    /// <param name="memberAccess">The member access to resolve.</param>
    /// <param name="semanticModel">The semantic model of the tree the access belongs to.</param>
    /// <param name="cancellationToken">A token to observe while binding.</param>
    /// <returns>The described member, or <see langword="null" /> if the access is out of scope.</returns>
    private static ComparisonMember? ResolveMember(
        MemberAccessExpressionSyntax memberAccess,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        // An enumeration member is a constant field, and a member access resolving to anything else - a
        // property, a method, or the type of a qualifier such as 'System.StringComparison' - is not one.
        if (semanticModel.GetSymbolInfo(memberAccess, cancellationToken).Symbol is not IFieldSymbol field)
        {
            return null;
        }

        // The comparison is symbolic on purpose: an enumeration named StringComparison in another namespace
        // declares the very same member names, and matching by name would mutate it as well.
        var declaringType = semanticModel.Compilation.GetTypeByMetadataName(StringComparisonMetadataName);

        if (!SymbolEqualityComparer.Default.Equals(field.ContainingType, declaringType))
        {
            return null;
        }

        return _members.FirstOrDefault(member => string.Equals(member.Name, field.Name, StringComparison.Ordinal));
    }

    /// <summary>
    /// Rewrites only the name of the member access, which keeps a simple, an aliased and a fully qualified
    /// qualifier, and the trivia around the name, exactly as written.
    /// </summary>
    /// <param name="memberAccess">The member access to rewrite.</param>
    /// <param name="target">The member the access is redirected to.</param>
    /// <returns>The rewritten member access.</returns>
    private static MemberAccessExpressionSyntax Rewrite(
        MemberAccessExpressionSyntax memberAccess,
        ComparisonMember target
    ) => memberAccess.WithName(SyntaxFactory.IdentifierName(target.Name).WithTriviaFrom(memberAccess.Name));

    /// <summary>
    /// Composes the display text of a mutation, naming both members and the axes the mutation moves along.
    /// </summary>
    /// <param name="source">The member found in the source.</param>
    /// <param name="target">The member replacing it.</param>
    /// <returns>The display text.</returns>
    private static string Describe(ComparisonMember source, ComparisonMember target) =>
        $"{TypeDisplayName}.{source.Name} => {TypeDisplayName}.{target.Name} ({DescribeAxes(source, target)})";

    /// <summary>
    /// Names the axes that differ between the two members. Two distinct members always differ on at least
    /// one of them, so the result is never empty.
    /// </summary>
    /// <param name="source">The member found in the source.</param>
    /// <param name="target">The member replacing it.</param>
    /// <returns>The named axes, separated by a comma.</returns>
    private static string DescribeAxes(ComparisonMember source, ComparisonMember target)
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
    /// Determines whether <paramref name="node" /> sits in a position that only accepts a compile
    /// time constant, such as an attribute argument, a <see langword="const" /> initializer, a default
    /// parameter value, a <c>case</c> label, a <c>goto case</c> statement or a constant pattern.
    /// </summary>
    /// <param name="node">The node to inspect.</param>
    /// <returns><see langword="true" /> if the node must stay a constant expression.</returns>
    /// <remarks>
    /// An enumeration member is itself a constant, so such a mutant would compile. It is still skipped:
    /// those positions describe metadata, a declaration or a label rather than a comparison that runs, and
    /// mutating them proves nothing about the behaviour of the code under test.
    /// </remarks>
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

    /// <summary>
    /// The position a member takes on the ordinal-versus-culture axis.
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
    /// One member of <see cref="System.StringComparison" />, with the identifier suffix it contributes and
    /// its position on both axes.
    /// </summary>
    private sealed class ComparisonMember
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ComparisonMember" /> class.
        /// </summary>
        /// <param name="name">The name of the enumeration member.</param>
        /// <param name="suffix">The kebab-case form of <paramref name="name" />.</param>
        /// <param name="axis">The position on the ordinal-versus-culture axis.</param>
        /// <param name="ignoresCase">Whether the member compares case insensitively.</param>
        public ComparisonMember(string name, string suffix, CultureAxis axis, bool ignoresCase)
        {
            Name = name;
            Suffix = suffix;
            Axis = axis;
            IgnoresCase = ignoresCase;
        }

        /// <summary>
        /// Gets the name of the enumeration member.
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
        /// Gets a value indicating whether the member compares case insensitively.
        /// </summary>
        public bool IgnoresCase { get; }
    }
}
