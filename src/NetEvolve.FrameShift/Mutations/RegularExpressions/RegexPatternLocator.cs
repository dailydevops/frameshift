namespace NetEvolve.FrameShift.Mutations.RegularExpressions;

using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Decides whether a string literal is a regular expression pattern and resolves the
/// <c>RegexOptions</c> that apply to it. This is the Roslyn half of the regular expression foundation;
/// it produces <see cref="RegexPatternSite" /> instances and knows nothing about the pattern grammar.
/// </summary>
/// <remarks>
/// <para>
/// Every type is resolved semantically, through <see cref="Compilation.GetTypeByMetadataName(string)" />
/// and <see cref="SymbolEqualityComparer.Default" />. A <c>Regex</c> class of an unrelated namespace, a
/// hand-written <c>GeneratedRegexAttribute</c> or a project's own
/// <c>RegularExpressionAttribute</c> therefore never matches, and neither does a type derived from
/// <c>Regex</c>, whose constructor parameters are not the framework's.
/// </para>
/// <para>
/// The pattern position is likewise decided semantically, by the ordinal and the type of the parameter
/// the literal binds to, not by the name of the member it is passed to. A constructor and an attribute
/// take the pattern first; the static methods - <c>IsMatch</c>, <c>Match</c>, <c>Matches</c>,
/// <c>Replace</c>, <c>Split</c>, <c>Count</c> and <c>EnumerateMatches</c> - take the input first and the
/// pattern <em>second</em>. Those seven are exactly the static members of <c>Regex</c> whose second
/// parameter is a <see cref="string" />, so requiring an instance-less member with a
/// <see cref="string" /> at ordinal one both covers all of them and excludes the remaining static
/// members: <c>Escape</c> and <c>Unescape</c> take their single string first, and the second parameter of
/// <c>CompileToAssembly</c> is no string at all. Keeping the rule structural rather than listing names
/// also means a further overload of one of those methods is recognised the day it appears.
/// </para>
/// <para>
/// Named arguments are honoured, in calls and in attributes alike, and so is the C# 7.2 form in which a
/// named argument sits at its own position. Arguments are bound to parameters once, into a slot per
/// parameter, and both the pattern check and the options lookup read from those slots; that is what makes
/// <c>new Regex(options: RegexOptions.Multiline, pattern: "a+")</c> resolve exactly like the positional
/// spelling.
/// </para>
/// <para>
/// Only a literal at the call site is recognised. A pattern handed over through a variable, a
/// <see langword="const" /> referenced by name, an interpolated string or any other computed expression
/// is not a site, because there is no single literal a rewriter could replace; the constant's own
/// declaration is not one either, since nothing there says the value is ever used as a pattern.
/// </para>
/// <para>
/// The class is stateless and every member is static, so it is safe to use from concurrent analyzer
/// callbacks.
/// </para>
/// </remarks>
internal static class RegexPatternLocator
{
    private const string RegexMetadataName = "System.Text.RegularExpressions.Regex";

    private const string RegexOptionsMetadataName = "System.Text.RegularExpressions.RegexOptions";

    private const string GeneratedRegexAttributeMetadataName = "System.Text.RegularExpressions.GeneratedRegexAttribute";

    private const string RegularExpressionAttributeMetadataName =
        "System.ComponentModel.DataAnnotations.RegularExpressionAttribute";

    /// <summary>
    /// The parameter the pattern binds to in a <c>Regex</c> constructor and in both attributes.
    /// </summary>
    private const int LeadingPatternOrdinal = 0;

    /// <summary>
    /// The parameter the pattern binds to in a static <c>Regex</c> method, which takes the input first.
    /// </summary>
    private const int SecondPatternOrdinal = 1;

    /// <summary>
    /// Decides whether <paramref name="node" /> is a string literal used as a regular expression pattern
    /// and, if so, describes it together with the options that apply to it.
    /// </summary>
    /// <param name="node">The node to inspect, which is a candidate pattern literal.</param>
    /// <param name="semanticModel">The semantic model of the tree <paramref name="node" /> belongs to.</param>
    /// <param name="cancellationToken">A token to observe while binding.</param>
    /// <returns>
    /// The located site, or <see langword="null" /> when <paramref name="node" /> is no pattern literal.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="node" /> or <paramref name="semanticModel" /> is <see langword="null" />.
    /// </exception>
    public static RegexPatternSite? TryLocate(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (node is null)
        {
            throw new ArgumentNullException(nameof(node));
        }

        if (semanticModel is null)
        {
            throw new ArgumentNullException(nameof(semanticModel));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // The token value is what separates a string literal from every other literal, and it does so for
        // the ordinary, the verbatim and the raw form alike, without naming a single syntax kind. A `u8`
        // literal's token value is a string just the same - Roslyn only converts it to bytes when the
        // literal is bound against a target type - so this guard lets it through; it is rejected further
        // down instead, because it can only ever bind to a parameter that is no string.
        if (node is not LiteralExpressionSyntax literal || literal.Token.Value is not string)
        {
            return null;
        }

        return literal.Parent switch
        {
            ArgumentSyntax argument => TryLocateInCall(literal, argument, semanticModel, cancellationToken),
            AttributeArgumentSyntax argument => TryLocateInAttribute(
                literal,
                argument,
                semanticModel,
                cancellationToken
            ),
            _ => null,
        };
    }

    /// <summary>
    /// Handles the call forms: a <c>Regex</c> constructor, including a <c>base(...)</c> initializer that
    /// reaches one, and a static <c>Regex</c> method.
    /// </summary>
    /// <param name="literal">The candidate pattern literal.</param>
    /// <param name="argument">The argument <paramref name="literal" /> is the expression of.</param>
    /// <param name="semanticModel">The semantic model of the tree the literal belongs to.</param>
    /// <param name="cancellationToken">A token to observe while binding.</param>
    /// <returns>The located site, or <see langword="null" />.</returns>
    private static RegexPatternSite? TryLocateInCall(
        LiteralExpressionSyntax literal,
        ArgumentSyntax argument,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (argument.Parent is not ArgumentListSyntax list || list.Parent is null)
        {
            return null;
        }

        // Asking the parent of the argument list rather than testing its syntax kind covers `new Regex(...)`,
        // the target-typed `new(...)`, a plain invocation and a constructor initializer in one step.
        if (semanticModel.GetSymbolInfo(list.Parent, cancellationToken).Symbol is not IMethodSymbol method)
        {
            return null;
        }

        var regexType = semanticModel.Compilation.GetTypeByMetadataName(RegexMetadataName);

        if (regexType is null || !SymbolEqualityComparer.Default.Equals(method.ContainingType, regexType))
        {
            return null;
        }

        var origin = GetCallOrigin(method);

        if (origin is null)
        {
            return null;
        }

        var ordinal =
            origin.Value == RegexPatternOrigin.RegexConstructor ? LeadingPatternOrdinal : SecondPatternOrdinal;
        var slots = BindArguments(method, list.Arguments);

        if (!IsPatternSlot(method, slots, ordinal, literal))
        {
            return null;
        }

        var (options, optionsExpression) = ResolveOptions(method, slots, semanticModel, cancellationToken);

        return new RegexPatternSite(literal, origin.Value, options, optionsExpression);
    }

    /// <summary>
    /// Handles the attribute forms, <c>[GeneratedRegex]</c> and the DataAnnotations
    /// <c>[RegularExpression]</c>.
    /// </summary>
    /// <param name="literal">The candidate pattern literal.</param>
    /// <param name="argument">The attribute argument <paramref name="literal" /> is the expression of.</param>
    /// <param name="semanticModel">The semantic model of the tree the literal belongs to.</param>
    /// <param name="cancellationToken">A token to observe while binding.</param>
    /// <returns>The located site, or <see langword="null" />.</returns>
    private static RegexPatternSite? TryLocateInAttribute(
        LiteralExpressionSyntax literal,
        AttributeArgumentSyntax argument,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        // A `Name = value` argument initializes a property or field of the attribute and can never be the
        // pattern parameter of its constructor.
        if (
            argument.NameEquals is not null
            || argument.Parent is not AttributeArgumentListSyntax list
            || list.Parent is not AttributeSyntax attribute
        )
        {
            return null;
        }

        if (semanticModel.GetSymbolInfo(attribute, cancellationToken).Symbol is not IMethodSymbol constructor)
        {
            return null;
        }

        var origin = GetAttributeOrigin(constructor.ContainingType, semanticModel.Compilation);

        if (origin is null)
        {
            return null;
        }

        var slots = BindArguments(constructor, list.Arguments);

        if (!IsPatternSlot(constructor, slots, LeadingPatternOrdinal, literal))
        {
            return null;
        }

        if (origin.Value == RegexPatternOrigin.DataAnnotationsRegularExpression)
        {
            // The DataAnnotations attribute has no options parameter in any overload, so its pattern is
            // always parsed with RegexOptions.None. Stating that here rather than running the general
            // resolution keeps the answer right even in a compilation that does not reference the
            // RegexOptions enum at all, which is perfectly possible: the attribute lives in another
            // assembly and a model type carrying it needs nothing from System.Text.RegularExpressions.
            return new RegexPatternSite(literal, origin.Value, RegexOptions.None, optionsExpression: null);
        }

        var (options, optionsExpression) = ResolveOptions(constructor, slots, semanticModel, cancellationToken);

        return new RegexPatternSite(literal, origin.Value, options, optionsExpression);
    }

    /// <summary>
    /// Classifies a resolved <c>Regex</c> member as a constructor or a static method, rejecting the
    /// instance methods, whose pattern was fixed when the instance was built.
    /// </summary>
    /// <param name="method">The resolved member.</param>
    /// <returns>The matching origin, or <see langword="null" /> when the member carries no pattern.</returns>
    private static RegexPatternOrigin? GetCallOrigin(IMethodSymbol method)
    {
        if (method.MethodKind == MethodKind.Constructor)
        {
            return RegexPatternOrigin.RegexConstructor;
        }

        return method.IsStatic ? RegexPatternOrigin.RegexStaticMethod : null;
    }

    /// <summary>
    /// Classifies the attribute type an attribute usage resolved to.
    /// </summary>
    /// <param name="attributeType">The attribute type, which is the constructor's containing type.</param>
    /// <param name="compilation">The compilation the well-known attribute types are resolved in.</param>
    /// <returns>
    /// The matching origin, or <see langword="null" /> when the attribute is none of the two known ones.
    /// </returns>
    private static RegexPatternOrigin? GetAttributeOrigin(INamedTypeSymbol? attributeType, Compilation compilation)
    {
        if (attributeType is null)
        {
            return null;
        }

        var generated = compilation.GetTypeByMetadataName(GeneratedRegexAttributeMetadataName);

        if (generated is not null && SymbolEqualityComparer.Default.Equals(attributeType, generated))
        {
            return RegexPatternOrigin.GeneratedRegex;
        }

        var dataAnnotations = compilation.GetTypeByMetadataName(RegularExpressionAttributeMetadataName);

        return dataAnnotations is not null && SymbolEqualityComparer.Default.Equals(attributeType, dataAnnotations)
            ? RegexPatternOrigin.DataAnnotationsRegularExpression
            : null;
    }

    /// <summary>
    /// Determines whether <paramref name="literal" /> is the argument bound to the parameter at
    /// <paramref name="ordinal" /> and whether that parameter takes a <see cref="string" />.
    /// </summary>
    /// <param name="method">The resolved member the arguments were bound to.</param>
    /// <param name="slots">The expression bound to each parameter, indexed by ordinal.</param>
    /// <param name="ordinal">The ordinal the pattern parameter has in this form.</param>
    /// <param name="literal">The candidate pattern literal.</param>
    /// <returns><see langword="true" /> when the literal is the pattern of the call.</returns>
    private static bool IsPatternSlot(
        IMethodSymbol method,
        ExpressionSyntax?[] slots,
        int ordinal,
        LiteralExpressionSyntax literal
    ) =>
        ordinal < slots.Length
        && ReferenceEquals(slots[ordinal], literal)
        && method.Parameters[ordinal].Type.SpecialType == SpecialType.System_String;

    /// <summary>
    /// Resolves the options that apply to a located pattern.
    /// </summary>
    /// <param name="method">The resolved member the arguments were bound to.</param>
    /// <param name="slots">The expression bound to each parameter, indexed by ordinal.</param>
    /// <param name="semanticModel">The semantic model used to fold the options expression.</param>
    /// <param name="cancellationToken">A token to observe while folding.</param>
    /// <returns>
    /// The resolved options - <see langword="null" /> when they are not statically determinable - and the
    /// expression they were read from, which is <see langword="null" /> when the member has no options
    /// parameter.
    /// </returns>
    private static (RegexOptions? Options, ExpressionSyntax? Expression) ResolveOptions(
        IMethodSymbol method,
        ExpressionSyntax?[] slots,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        var optionsType = semanticModel.Compilation.GetTypeByMetadataName(RegexOptionsMetadataName);

        if (optionsType is null)
        {
            // The pattern was found through the Regex type, so the enum has to be resolvable; if it is not,
            // nothing can be said about the options and saying nothing is the whole point of the null.
            return (null, null);
        }

        var parameter = method.Parameters.FirstOrDefault(candidate =>
            SymbolEqualityComparer.Default.Equals(candidate.Type, optionsType)
        );

        if (parameter is null)
        {
            // The chosen overload takes no options, so the runtime uses RegexOptions.None. That is a
            // resolved value, not an assumption.
            return (RegexOptions.None, null);
        }

        var expression = parameter.Ordinal < slots.Length ? slots[parameter.Ordinal] : null;

        if (expression is null)
        {
            // No shipping overload has an optional options parameter, so a bound call always fills the slot.
            // Should one ever appear, reporting unknown options is the safe answer.
            return (null, null);
        }

        var constant = semanticModel.GetConstantValue(expression, cancellationToken);

        // Composed flags such as `RegexOptions.IgnoreCase | RegexOptions.Multiline` are a constant
        // expression in C#, so the semantic model folds them into the combined underlying value; anything
        // the compiler cannot fold - a field, a parameter, a method call - has no constant value and is
        // reported as unknown.
        return (constant.HasValue && constant.Value is int value ? (RegexOptions?)value : null, expression);
    }

    /// <summary>
    /// Binds the arguments of a call to the parameters of the resolved member.
    /// </summary>
    /// <param name="method">The resolved member.</param>
    /// <param name="arguments">The arguments of the call.</param>
    /// <returns>The expression bound to each parameter, indexed by ordinal.</returns>
    private static ExpressionSyntax?[] BindArguments(
        IMethodSymbol method,
        SeparatedSyntaxList<ArgumentSyntax> arguments
    )
    {
        var slots = new ExpressionSyntax?[method.Parameters.Length];

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            Assign(slots, method, argument.NameColon, index, argument.Expression);
        }

        return slots;
    }

    /// <summary>
    /// Binds the positional and <c>name:</c> arguments of an attribute usage to the parameters of the
    /// resolved constructor.
    /// </summary>
    /// <param name="method">The resolved constructor.</param>
    /// <param name="arguments">The arguments of the attribute usage.</param>
    /// <returns>The expression bound to each parameter, indexed by ordinal.</returns>
    private static ExpressionSyntax?[] BindArguments(
        IMethodSymbol method,
        SeparatedSyntaxList<AttributeArgumentSyntax> arguments
    )
    {
        var slots = new ExpressionSyntax?[method.Parameters.Length];

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            // In an attribute usage every `Name = value` argument follows the constructor arguments, so the
            // first one ends the part of the list that binds to parameters and the indexes stay correct.
            if (argument.NameEquals is not null)
            {
                break;
            }

            Assign(slots, method, argument.NameColon, index, argument.Expression);
        }

        return slots;
    }

    /// <summary>
    /// Writes one argument into the slot of the parameter it binds to.
    /// </summary>
    /// <param name="slots">The slots to fill.</param>
    /// <param name="method">The resolved member.</param>
    /// <param name="nameColon">The <c>name:</c> of the argument, or <see langword="null" />.</param>
    /// <param name="index">The position of the argument in its list.</param>
    /// <param name="expression">The expression of the argument.</param>
    private static void Assign(
        ExpressionSyntax?[] slots,
        IMethodSymbol method,
        NameColonSyntax? nameColon,
        int index,
        ExpressionSyntax expression
    )
    {
        var ordinal = GetParameterOrdinal(method, nameColon, index);

        if (ordinal >= 0 && ordinal < slots.Length)
        {
            slots[ordinal] = expression;
        }
    }

    /// <summary>
    /// Resolves the ordinal of the parameter an argument binds to. A positional argument binds to the
    /// parameter at its own position, which also holds for a named argument standing at that position; a
    /// named argument standing anywhere else binds by the name the language spells out.
    /// </summary>
    /// <param name="method">The resolved member.</param>
    /// <param name="nameColon">The <c>name:</c> of the argument, or <see langword="null" />.</param>
    /// <param name="index">The position of the argument in its list.</param>
    /// <returns>The ordinal, or <c>-1</c> when the argument binds to no parameter.</returns>
    private static int GetParameterOrdinal(IMethodSymbol method, NameColonSyntax? nameColon, int index)
    {
        if (nameColon is null)
        {
            return index;
        }

        var name = nameColon.Name.Identifier.ValueText;
        var parameter = method.Parameters.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.Ordinal)
        );

        return parameter?.Ordinal ?? -1;
    }
}
