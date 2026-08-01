namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Mutates a <c>System.Text.RegularExpressions.RegexOptions</c> flag expression by adding a flag that
/// is absent and by removing every flag that is present, one at a time.
/// </summary>
/// <remarks>
/// <para>
/// The operator belongs to the culture sensitivity family, because <c>CultureInvariant</c> decides
/// whether <c>IgnoreCase</c> folds case with the current culture or with the invariant one, which is the
/// same class of defect the other operators of the family target.
/// </para>
/// <para>
/// Only the flags that change how a valid pattern matches are offered: <c>IgnoreCase</c>,
/// <c>CultureInvariant</c>, <c>Multiline</c>, <c>Singleline</c>, <c>ExplicitCapture</c>,
/// <c>IgnorePatternWhitespace</c> and <c>RightToLeft</c>. Three flags are deliberately excluded.
/// <c>Compiled</c> only selects how the engine is built and cannot change a single match result, so a
/// mutant carrying it is equivalent by construction. <c>NonBacktracking</c> changes which constructs
/// are legal at all - a pattern with a lookaround or a backreference throws instead of matching
/// differently - so it produces a mutant that fails for a reason unrelated to the tested behaviour.
/// <c>ECMAScript</c> is legal only together with a small set of other flags and otherwise makes the
/// <c>Regex</c> constructor throw, which again is not a behaviour difference. Flags outside the offered
/// set are never added and never removed; when they are present they are carried over into every
/// replacement unchanged.
/// </para>
/// <para>
/// <c>RegexOptions.None</c> carries no bit and is dropped from every rebuilt combination, so adding a
/// flag to it replaces it instead of combining with it, and removing the last offered flag of a
/// combination yields <c>RegexOptions.None</c> again. The two directions the operator offers therefore
/// cover the transition from and to <c>None</c> without a separate mutation.
/// </para>
/// <para>
/// The operator answers only for the outermost expression of a flag combination and rebuilds it from its
/// operands, joined by <c>|</c> with a single space. The trivia around the whole expression is preserved
/// by <see cref="Mutation.ApplyTo(SyntaxTree)" />.
/// </para>
/// <para>
/// Unlike <see cref="BooleanLiteralMutator" /> this operator needs no constant context guard. Every
/// replacement it produces is a combination of enum members and therefore itself a compile time
/// constant, so it is legal everywhere the original was. That is what makes the <c>RegexOptions</c>
/// argument of <c>[GeneratedRegex]</c> worth mutating even though it is an attribute argument: the value
/// is fixed at compile time, but it is the input of a matcher whose behaviour is observed at run time,
/// which is unlike a <see langword="const" /> whose value is the observed thing itself.
/// </para>
/// </remarks>
internal sealed class RegexOptionsMutator : MutationOperatorBase
{
    /// <summary>
    /// The metadata name of the enum this operator answers for. Resolving it through the compilation is
    /// what keeps a same-named enum from another namespace out.
    /// </summary>
    private const string RegexOptionsMetadataName = "System.Text.RegularExpressions.RegexOptions";

    /// <summary>
    /// The name of the enum member standing for the empty flag set.
    /// </summary>
    private const string NoneFlagName = "None";

    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds =
    [
        SyntaxKind.SimpleMemberAccessExpression,
        SyntaxKind.BitwiseOrExpression,
    ];

    /// <summary>
    /// The offered flags, each with the identifier suffix it contributes, in the order the mutations are
    /// produced in. The order is fixed here rather than derived from the source, so that the produced set
    /// of a given expression is the same no matter how its operands were written down.
    /// </summary>
    private static readonly ImmutableArray<(string Name, string Suffix)> _mutableFlags =
    [
        ("IgnoreCase", "ignore-case"),
        ("CultureInvariant", "culture-invariant"),
        ("Multiline", "multiline"),
        ("Singleline", "singleline"),
        ("ExplicitCapture", "explicit-capture"),
        ("IgnorePatternWhitespace", "ignore-pattern-whitespace"),
        ("RightToLeft", "right-to-left"),
    ];

    /// <summary>
    /// The <c>|</c> token every rebuilt combination is joined with, surrounded by a single space.
    /// </summary>
    private static readonly SyntaxToken _barToken = SyntaxFactory.Token(
        SyntaxFactory.TriviaList(SyntaxFactory.Space),
        SyntaxKind.BarToken,
        SyntaxFactory.TriviaList(SyntaxFactory.Space)
    );

    /// <summary>
    /// Initializes a new instance of the <see cref="RegexOptionsMutator" /> class.
    /// </summary>
    public RegexOptionsMutator()
        : base("culture.regex-options", MutationKind.RegexOptions, _supportedSyntaxKinds) { }

    /// <inheritdoc />
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // MutationOperatorBase.CreateMutations only hands over nodes of one of the SupportedSyntaxKinds,
        // and both a member access and a bitwise or are expressions, so the cast cannot fail.
        var expression = (ExpressionSyntax)node;
        var optionsType = WellKnownTypeCache.GetType(semanticModel.Compilation, RegexOptionsMetadataName);

        if (optionsType is null || !IsFlagExpressionRoot(expression, optionsType, semanticModel, cancellationToken))
        {
            return [];
        }

        var operands = GetOperands(expression, optionsType, semanticModel, cancellationToken);

        if (operands.IsDefaultOrEmpty)
        {
            return [];
        }

        return CreateFlagMutations(expression, operands, cancellationToken);
    }

    /// <summary>
    /// Decides whether <paramref name="expression" /> is the outermost expression of a
    /// <c>RegexOptions</c> flag combination, which is the only node this operator answers for.
    /// </summary>
    /// <param name="expression">The expression to inspect.</param>
    /// <param name="optionsType">The resolved <c>RegexOptions</c> enum.</param>
    /// <param name="semanticModel">The semantic model of the tree the expression belongs to.</param>
    /// <param name="cancellationToken">A token to observe while binding.</param>
    /// <returns><see langword="true" /> when the expression is such a root.</returns>
    private static bool IsFlagExpressionRoot(
        ExpressionSyntax expression,
        INamedTypeSymbol optionsType,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        var type = semanticModel.GetTypeInfo(expression, cancellationToken).Type;

        if (!SymbolEqualityComparer.Default.Equals(type, optionsType))
        {
            return false;
        }

        // The qualifier of a fully qualified member such as `System.Text.RegularExpressions.RegexOptions
        // .IgnoreCase` is itself a member access whose type is the enum. It denotes the type and not a
        // value, so it must not be mistaken for a flag expression.
        if (semanticModel.GetSymbolInfo(expression, cancellationToken).Symbol is ITypeSymbol)
        {
            return false;
        }

        return !IsInsideFlagExpression(expression, optionsType, semanticModel, cancellationToken);
    }

    /// <summary>
    /// Determines whether <paramref name="expression" /> is an operand of a wider <c>RegexOptions</c>
    /// combination, in which case the wider expression is the one that gets mutated.
    /// </summary>
    /// <param name="expression">The expression to inspect.</param>
    /// <param name="optionsType">The resolved <c>RegexOptions</c> enum.</param>
    /// <param name="semanticModel">The semantic model of the tree the expression belongs to.</param>
    /// <param name="cancellationToken">A token to observe while binding.</param>
    /// <returns><see langword="true" /> when a wider combination encloses the expression.</returns>
    private static bool IsInsideFlagExpression(
        ExpressionSyntax expression,
        INamedTypeSymbol optionsType,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        var current = expression.Parent;

        while (current is ParenthesizedExpressionSyntax)
        {
            current = current.Parent;
        }

        return current is BinaryExpressionSyntax binary
            && binary.IsKind(SyntaxKind.BitwiseOrExpression)
            && SymbolEqualityComparer.Default.Equals(
                semanticModel.GetTypeInfo(binary, cancellationToken).Type,
                optionsType
            );
    }

    /// <summary>
    /// Flattens <paramref name="expression" /> into the enum members it combines, in source order.
    /// </summary>
    /// <param name="expression">The flag expression to flatten.</param>
    /// <param name="optionsType">The resolved <c>RegexOptions</c> enum.</param>
    /// <param name="semanticModel">The semantic model of the tree the expression belongs to.</param>
    /// <param name="cancellationToken">A token to observe while binding.</param>
    /// <returns>
    /// The operands, or an empty array when any operand is not a member of <paramref name="optionsType" />
    /// and the expression therefore cannot be reasoned about.
    /// </returns>
    private static ImmutableArray<(ExpressionSyntax Expression, string FlagName)> GetOperands(
        ExpressionSyntax expression,
        INamedTypeSymbol optionsType,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        var builder = ImmutableArray.CreateBuilder<(ExpressionSyntax Expression, string FlagName)>();

        return CollectOperands(expression, optionsType, semanticModel, builder, cancellationToken)
            ? builder.ToImmutable()
            : [];
    }

    /// <summary>
    /// Adds the enum members <paramref name="expression" /> combines to <paramref name="builder" />.
    /// </summary>
    /// <param name="expression">The expression to walk.</param>
    /// <param name="optionsType">The resolved <c>RegexOptions</c> enum.</param>
    /// <param name="semanticModel">The semantic model of the tree the expression belongs to.</param>
    /// <param name="builder">The builder collecting the operands.</param>
    /// <param name="cancellationToken">A token to observe while binding.</param>
    /// <returns><see langword="false" /> when an operand is no member of the enum.</returns>
    private static bool CollectOperands(
        ExpressionSyntax expression,
        INamedTypeSymbol optionsType,
        SemanticModel semanticModel,
        ImmutableArray<(ExpressionSyntax Expression, string FlagName)>.Builder builder,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var unwrapped = Unwrap(expression);

        if (unwrapped is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.BitwiseOrExpression))
        {
            return CollectOperands(binary.Left, optionsType, semanticModel, builder, cancellationToken)
                && CollectOperands(binary.Right, optionsType, semanticModel, builder, cancellationToken);
        }

        if (
            semanticModel.GetSymbolInfo(unwrapped, cancellationToken).Symbol is not IFieldSymbol field
            || !field.IsStatic
            || !SymbolEqualityComparer.Default.Equals(field.ContainingType, optionsType)
        )
        {
            return false;
        }

        builder.Add((unwrapped, field.Name));

        return true;
    }

    /// <summary>
    /// Strips the redundant parentheses around an operand, so that <c>(RegexOptions.IgnoreCase)</c> is
    /// treated exactly like <c>RegexOptions.IgnoreCase</c>.
    /// </summary>
    /// <param name="expression">The expression to strip.</param>
    /// <returns>The innermost expression.</returns>
    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        var current = expression;

        while (current is ParenthesizedExpressionSyntax parenthesized)
        {
            current = parenthesized.Expression;
        }

        return current;
    }

    /// <summary>
    /// Produces the removal of every offered flag that is present, followed by the addition of every
    /// offered flag that is absent.
    /// </summary>
    /// <param name="expression">The flag expression that gets replaced.</param>
    /// <param name="operands">The enum members the expression combines.</param>
    /// <param name="cancellationToken">A token to observe while producing the mutations.</param>
    /// <returns>The candidate mutations.</returns>
    private IEnumerable<Mutation> CreateFlagMutations(
        ExpressionSyntax expression,
        ImmutableArray<(ExpressionSyntax Expression, string FlagName)> operands,
        CancellationToken cancellationToken
    )
    {
        // The style of the first operand is reused for every added member, so that a fixture written with
        // `using static` keeps its bare names and a qualified one keeps its qualifier. Both forms bind to
        // the very same member, which is what makes the mutant compile.
        var template = operands[0].Expression;

        foreach (var flag in _mutableFlags)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Contains(operands, flag.Name))
            {
                yield return CreateRemoval(expression, operands, template, flag);
            }
        }

        foreach (var flag in _mutableFlags)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Contains(operands, flag.Name))
            {
                yield return CreateAddition(expression, operands, template, flag);
            }
        }
    }

    /// <summary>
    /// Creates the mutation dropping every operand denoting <paramref name="flag" />.
    /// </summary>
    /// <param name="expression">The flag expression that gets replaced.</param>
    /// <param name="operands">The enum members the expression combines.</param>
    /// <param name="template">The operand whose spelling style added members follow.</param>
    /// <param name="flag">The flag to remove.</param>
    /// <returns>The created mutation.</returns>
    private Mutation CreateRemoval(
        ExpressionSyntax expression,
        ImmutableArray<(ExpressionSyntax Expression, string FlagName)> operands,
        ExpressionSyntax template,
        (string Name, string Suffix) flag
    )
    {
        var remaining = Retain(operands, flag.Name);
        var replacement = remaining.IsEmpty ? CreateFlagReference(template, NoneFlagName) : Combine(remaining);

        return CreateMutation(expression, replacement, $"remove-{flag.Suffix}", $"RegexOptions - {flag.Name}");
    }

    /// <summary>
    /// Creates the mutation adding <paramref name="flag" /> to the combination.
    /// </summary>
    /// <param name="expression">The flag expression that gets replaced.</param>
    /// <param name="operands">The enum members the expression combines.</param>
    /// <param name="template">The operand whose spelling style added members follow.</param>
    /// <param name="flag">The flag to add.</param>
    /// <returns>The created mutation.</returns>
    private Mutation CreateAddition(
        ExpressionSyntax expression,
        ImmutableArray<(ExpressionSyntax Expression, string FlagName)> operands,
        ExpressionSyntax template,
        (string Name, string Suffix) flag
    )
    {
        // An addition removes nothing, so the empty name excludes no member at all.
        var kept = Retain(operands, string.Empty);
        var replacement = Combine(kept.Add(CreateFlagReference(template, flag.Name)));

        return CreateMutation(expression, replacement, $"add-{flag.Suffix}", $"RegexOptions + {flag.Name}");
    }

    /// <summary>
    /// Selects the operand expressions that survive into a rebuilt combination: everything except the
    /// member being removed and except <c>RegexOptions.None</c>, which carries no bit and is therefore
    /// redundant next to any other member.
    /// </summary>
    /// <param name="operands">The enum members the expression combines.</param>
    /// <param name="removedFlagName">The member being removed, or an empty string when none is.</param>
    /// <returns>The surviving operand expressions, in source order.</returns>
    private static ImmutableArray<ExpressionSyntax> Retain(
        ImmutableArray<(ExpressionSyntax Expression, string FlagName)> operands,
        string removedFlagName
    ) =>
        operands
            .Where(operand =>
                !string.Equals(operand.FlagName, NoneFlagName, StringComparison.Ordinal)
                && !string.Equals(operand.FlagName, removedFlagName, StringComparison.Ordinal)
            )
            .Select(operand => operand.Expression)
            .ToImmutableArray();

    /// <summary>
    /// Determines whether <paramref name="operands" /> contains a member named <paramref name="flagName" />.
    /// </summary>
    /// <param name="operands">The enum members the expression combines.</param>
    /// <param name="flagName">The member name to look for.</param>
    /// <returns><see langword="true" /> when the member is part of the combination.</returns>
    private static bool Contains(
        ImmutableArray<(ExpressionSyntax Expression, string FlagName)> operands,
        string flagName
    ) => operands.Any(operand => string.Equals(operand.FlagName, flagName, StringComparison.Ordinal));

    /// <summary>
    /// Joins <paramref name="operands" /> with <c>|</c>, left associative, as the C# compiler parses it.
    /// </summary>
    /// <param name="operands">The operands to join, at least one.</param>
    /// <returns>The combined expression.</returns>
    private static ExpressionSyntax Combine(ImmutableArray<ExpressionSyntax> operands)
    {
        var result = operands[0].WithoutTrivia();

        for (var index = 1; index < operands.Length; index++)
        {
            result = SyntaxFactory.BinaryExpression(
                SyntaxKind.BitwiseOrExpression,
                result,
                _barToken,
                operands[index].WithoutTrivia()
            );
        }

        return result;
    }

    /// <summary>
    /// Spells out the enum member <paramref name="flagName" /> the way <paramref name="template" /> spells
    /// out its own member, which is either qualified by a type reference or a bare name.
    /// </summary>
    /// <param name="template">The operand whose spelling style is followed.</param>
    /// <param name="flagName">The member to reference.</param>
    /// <returns>The created reference.</returns>
    private static ExpressionSyntax CreateFlagReference(ExpressionSyntax template, string flagName)
    {
        if (template is not MemberAccessExpressionSyntax memberAccess)
        {
            return SyntaxFactory.IdentifierName(flagName);
        }

        return SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            memberAccess.Expression.WithoutTrivia(),
            SyntaxFactory.IdentifierName(flagName)
        );
    }
}
