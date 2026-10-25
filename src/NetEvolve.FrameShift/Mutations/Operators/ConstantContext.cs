namespace NetEvolve.FrameShift.Mutations.Operators;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// The one answer every mutation operator asks about the position of a node: whether that position only
/// accepts a compile time constant, so that no mutant put there could ever compile or would ever describe
/// behaviour that runs.
/// </summary>
/// <remarks>
/// <para>
/// The decision is deliberately syntactic. A constant position is recognisable from the shape of the tree
/// alone, the answer must be the same for a file that does not compile - which is what an analyzer sees
/// while the code is being typed - and asking the semantic model for a constant value would answer a
/// different question: whether the node <em>is</em> constant, not whether it <em>has to be</em>.
/// </para>
/// <para>
/// The walk climbs the parent chain and stops at the first node that decides. A member declaration or the
/// compilation unit ends it with <see langword="false" />, which is what keeps the walk from leaving the
/// member the node belongs to and from mistaking an enclosing member for a constant context.
/// </para>
/// </remarks>
internal static class ConstantContext
{
    /// <summary>
    /// Determines whether <paramref name="node" /> sits in a position that only accepts a compile time
    /// constant, such as an attribute argument, a <see langword="const" /> initializer, a default parameter
    /// value, a <see langword="case"/> label, a <c>goto case</c> statement, a constant pattern, a relational pattern
    /// or an enumeration member declaration.
    /// </summary>
    /// <param name="node">The node to inspect.</param>
    /// <returns><see langword="true" /> if the node must stay a constant expression.</returns>
    internal static bool IsRequired(SyntaxNode node)
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
