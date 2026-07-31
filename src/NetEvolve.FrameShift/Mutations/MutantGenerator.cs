namespace NetEvolve.FrameShift.Mutations;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Walks a syntax tree exactly once and asks every registered <see cref="IMutationOperator" /> for
/// the mutations it wants to create for the nodes it supports.
/// </summary>
/// <remarks>
/// <para>
/// The walk is deliberately kept single-pass and allocation-conscious, because it runs for every
/// syntax tree of the analysed compilation. Whether a syntax kind is interesting at all is answered
/// through a dense lookup table built once from
/// <see cref="MutationOperatorRegistry.SupportedSyntaxKinds" />, so the common case of an
/// uninteresting node costs a bounds check and an array read.
/// </para>
/// <para>
/// Machine written code is never a testing gap, therefore whole trees are dropped when their file
/// path looks generated or when they start with an <c>&lt;auto-generated&gt;</c> header, and whole
/// declarations are dropped when they carry <c>GeneratedCodeAttribute</c> or
/// <c>ExcludeFromCodeCoverageAttribute</c>. Excluded declarations are not descended into, so the
/// cost of an excluded type is paid once instead of once per contained node.
/// </para>
/// </remarks>
internal static class MutantGenerator
{
    private static readonly string[] _generatedFileSuffixes = [".g.cs", ".g.i.cs", ".designer.cs", ".generated.cs"];

    private static readonly bool[] _supportedKindLookup = BuildSupportedKindLookup();

    /// <summary>
    /// Creates all candidate mutations reachable from <paramref name="root" />.
    /// </summary>
    /// <param name="root">The root node to walk, usually the root of a single syntax tree.</param>
    /// <param name="semanticModel">
    /// The semantic model of the tree <paramref name="root" /> belongs to. It is handed to the
    /// operators unchanged.
    /// </param>
    /// <param name="cancellationToken">A token observed on every visited node.</param>
    /// <returns>
    /// The lazily evaluated candidate mutations. The sequence is empty for generated trees and for
    /// trees without any mutable node.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="root" /> or <paramref name="semanticModel" /> is <see langword="null" />.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    public static IEnumerable<Mutation> CreateMutations(
        SyntaxNode root,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (root is null)
        {
            throw new ArgumentNullException(nameof(root));
        }

        if (semanticModel is null)
        {
            throw new ArgumentNullException(nameof(semanticModel));
        }

        if (_supportedKindLookup.Length == 0 || IsGeneratedTree(root, cancellationToken))
        {
            return [];
        }

        return CreateMutationsCore(root, semanticModel, cancellationToken);
    }

    private static IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode root,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        foreach (var node in root.DescendantNodesAndSelf(descendIntoChildren: ShouldDescendInto))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsSupported(node.Kind()) || IsExcludedDeclaration(node))
            {
                continue;
            }

            foreach (var mutationOperator in MutationOperatorRegistry.ForSyntaxKind(node.Kind()))
            {
                foreach (var mutation in mutationOperator.CreateMutations(node, semanticModel, cancellationToken))
                {
                    yield return mutation;
                }
            }
        }
    }

    /// <summary>
    /// Decides whether the children of <paramref name="node" /> can contain mutable code.
    /// </summary>
    /// <param name="node">The node whose children are about to be visited.</param>
    /// <returns><see langword="true" /> if the subtree is worth walking; otherwise <see langword="false" />.</returns>
    private static bool ShouldDescendInto(SyntaxNode node) =>
        !node.IsKind(SyntaxKind.AttributeList) && !IsExcludedDeclaration(node);

    /// <summary>
    /// Determines whether the tree <paramref name="root" /> belongs to is machine generated.
    /// </summary>
    /// <param name="root">The root node of the tree to inspect.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns><see langword="true" /> if the whole tree must be skipped; otherwise <see langword="false" />.</returns>
    /// <remarks>
    /// The owning tree is never checked for <see langword="null" />, because
    /// <see cref="SyntaxNode.SyntaxTree" /> always answers with a tree: a node that was created
    /// detached gets one on demand. A detached node reports an empty file path, which
    /// <see cref="HasGeneratedFileName" /> already treats as not generated.
    /// </remarks>
    private static bool IsGeneratedTree(SyntaxNode root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (HasGeneratedFileName(root.SyntaxTree.FilePath))
        {
            return true;
        }

        return HasAutoGeneratedHeader(root);
    }

    /// <summary>
    /// Determines whether <paramref name="filePath" /> uses one of the well known suffixes of
    /// generated C# files.
    /// </summary>
    /// <param name="filePath">The file path of the syntax tree, possibly empty for in-memory trees.</param>
    /// <returns><see langword="true" /> if the path looks generated; otherwise <see langword="false" />.</returns>
    private static bool HasGeneratedFileName(string? filePath) =>
        !string.IsNullOrEmpty(filePath)
        && _generatedFileSuffixes.Any(suffix => filePath!.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Determines whether the leading trivia of the first token of <paramref name="root" /> contains
    /// an <c>&lt;auto-generated&gt;</c> marker.
    /// </summary>
    /// <param name="root">The root node of the tree to inspect.</param>
    /// <returns><see langword="true" /> if the marker is present; otherwise <see langword="false" />.</returns>
    private static bool HasAutoGeneratedHeader(SyntaxNode root)
    {
        foreach (var trivia in root.GetLeadingTrivia())
        {
            if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) && !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
            {
                continue;
            }

            if (trivia.ToString().IndexOf("<auto-generated", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether <paramref name="node" /> is a declaration excluded from mutation by an
    /// attribute.
    /// </summary>
    /// <param name="node">The node to inspect.</param>
    /// <returns><see langword="true" /> if the declaration must be skipped; otherwise <see langword="false" />.</returns>
    private static bool IsExcludedDeclaration(SyntaxNode node) =>
        node switch
        {
            MemberDeclarationSyntax member => HasExcludingAttribute(member.AttributeLists),
            AccessorDeclarationSyntax accessor => HasExcludingAttribute(accessor.AttributeLists),
            LocalFunctionStatementSyntax localFunction => HasExcludingAttribute(localFunction.AttributeLists),
            _ => false,
        };

    /// <summary>
    /// Determines whether <paramref name="attributeLists" /> contains an attribute that excludes the
    /// annotated declaration from mutation.
    /// </summary>
    /// <param name="attributeLists">The attribute lists of a declaration.</param>
    /// <returns><see langword="true" /> if an excluding attribute is present; otherwise <see langword="false" />.</returns>
    private static bool HasExcludingAttribute(SyntaxList<AttributeListSyntax> attributeLists) =>
        attributeLists.Count > 0
        && attributeLists.Any(attributeList =>
            attributeList.Attributes.Any(attribute => IsExcludingAttributeName(GetAttributeSimpleName(attribute.Name)))
        );

    /// <summary>
    /// Determines whether <paramref name="name" /> names one of the excluding attributes, with and
    /// without the optional <c>Attribute</c> suffix.
    /// </summary>
    /// <param name="name">The simple name of an attribute.</param>
    /// <returns><see langword="true" /> if the name excludes the declaration; otherwise <see langword="false" />.</returns>
    private static bool IsExcludingAttributeName(string name) =>
        string.Equals(name, "GeneratedCode", StringComparison.Ordinal)
        || string.Equals(name, "GeneratedCodeAttribute", StringComparison.Ordinal)
        || string.Equals(name, "ExcludeFromCodeCoverage", StringComparison.Ordinal)
        || string.Equals(name, "ExcludeFromCodeCoverageAttribute", StringComparison.Ordinal);

    /// <summary>
    /// Reduces an attribute name to its rightmost identifier, so that qualified and alias qualified
    /// usages are recognised as well.
    /// </summary>
    /// <param name="name">The name syntax of an attribute.</param>
    /// <returns>The rightmost identifier, or an empty string for unsupported name shapes.</returns>
    private static string GetAttributeSimpleName(NameSyntax name) =>
        name switch
        {
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
            _ => string.Empty,
        };

    /// <summary>
    /// Determines whether at least one registered operator supports <paramref name="kind" />.
    /// </summary>
    /// <param name="kind">The syntax kind of the visited node.</param>
    /// <returns><see langword="true" /> if the kind is interesting; otherwise <see langword="false" />.</returns>
    private static bool IsSupported(SyntaxKind kind)
    {
        var index = (int)kind;
        return (uint)index < (uint)_supportedKindLookup.Length && _supportedKindLookup[index];
    }

    /// <summary>
    /// Builds the dense syntax kind lookup table from the registry.
    /// </summary>
    /// <returns>
    /// A table indexed by the numeric syntax kind, or an empty array if no operator is registered.
    /// </returns>
    private static bool[] BuildSupportedKindLookup()
    {
        var kinds = MutationOperatorRegistry.SupportedSyntaxKinds;
        if (kinds.IsDefaultOrEmpty)
        {
            return [];
        }

        var maximum = 0;
        foreach (var kind in kinds)
        {
            var index = (int)kind;
            if (index > maximum)
            {
                maximum = index;
            }
        }

        var lookup = new bool[maximum + 1];
        foreach (var kind in kinds)
        {
            lookup[(int)kind] = true;
        }

        return lookup;
    }
}
