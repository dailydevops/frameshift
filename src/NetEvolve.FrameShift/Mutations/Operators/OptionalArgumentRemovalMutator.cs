namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Drops the trailing comparer argument of an object creation, so that
/// <c>new Dictionary&lt;string, string&gt;(StringComparer.Ordinal)</c> is constructed as
/// <c>new Dictionary&lt;string, string&gt;()</c> instead.
/// </summary>
/// <remarks>
/// <para>
/// A number of collection constructors accept a trailing <c>System.StringComparer</c>,
/// <c>System.Collections.Generic.IComparer&lt;T&gt;</c> or <c>System.Collections.Generic.IEqualityComparer&lt;T&gt;</c>
/// argument that only overrides an implicit default. No existing operator mutates the argument
/// <em>list</em> to drop that argument outright, so a test suite that never asserts on comparer sensitive
/// behaviour - case sensitivity, culture awareness, a custom notion of equality or of ordering - survives a
/// mutant of <see cref="StringComparerMutator" /> just as easily as it would survive this one, because the
/// two ask different questions: that operator asks whether the test cares <em>which</em> comparer is used,
/// this one asks whether it cares <em>whether</em> a non-default comparer is used at all.
/// </para>
/// <para>
/// Unlike <see cref="FormatProviderArgumentMutator" />, which leaves overload resolution of the mutant to
/// the compiler, this operator resolves the constructor the mutated argument list would bind to itself,
/// through <c>SemanticModel.GetSpeculativeSymbolInfo</c>.
/// A comparer-shaped parameter is common enough, and sits next to a same-arity, differently-typed overload
/// often enough - an unrelated single-argument constructor of the same type, for instance - that letting an
/// invalid mutant through and relying on <see cref="MutantCompiler" /> to discard it would silently drop the
/// very case the acceptance criteria ask to be proven absent: the argument is only ever removed once the
/// mutated call is confirmed to resolve to a constructor of the very same type.
/// </para>
/// <para>
/// Only the last argument of the list is considered, and only when it is positional. A named argument or a
/// comparer sitting anywhere else in the list could require shifting or renaming a later argument to keep
/// the call meaningful, which is a different, considerably more involved mutation than dropping a trailing
/// default; the suggested widening to arbitrary optional parameters is left as a follow-up.
/// </para>
/// </remarks>
internal sealed class OptionalArgumentRemovalMutator : MutationOperatorBase
{
    /// <summary>
    /// The metadata name of <c>System.StringComparer</c>.
    /// </summary>
    private const string StringComparerMetadataName = "System.StringComparer";

    /// <summary>
    /// The metadata name of <c>System.Collections.Generic.IComparer&lt;T&gt;</c>.
    /// </summary>
    private const string ComparerMetadataName = "System.Collections.Generic.IComparer`1";

    /// <summary>
    /// The metadata name of <c>System.Collections.Generic.IEqualityComparer&lt;T&gt;</c>.
    /// </summary>
    private const string EqualityComparerMetadataName = "System.Collections.Generic.IEqualityComparer`1";

    /// <summary>
    /// The suffix of the only mutation this operator creates.
    /// </summary>
    private const string RemoveSuffix = "remove";

    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds = [SyntaxKind.ObjectCreationExpression];

    /// <summary>
    /// Initializes a new instance of the <see cref="OptionalArgumentRemovalMutator" /> class.
    /// </summary>
    public OptionalArgumentRemovalMutator()
        : base("argument.optional-removal", MutationKind.OptionalArgumentRemoval, _supportedSyntaxKinds) { }

    /// <inheritdoc />
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // MutationOperatorBase.CreateMutations only hands over nodes of one of the SupportedSyntaxKinds, and
        // SyntaxKind.ObjectCreationExpression is always an object creation expression, so the cast cannot
        // fail and no type test is needed here.
        var objectCreation = (ObjectCreationExpressionSyntax)node;
        var argumentList = objectCreation.ArgumentList;

        if (argumentList is null || argumentList.Arguments.Count == 0 || ConstantContext.IsRequired(objectCreation))
        {
            return [];
        }

        var lastIndex = argumentList.Arguments.Count - 1;
        var lastArgument = argumentList.Arguments[lastIndex];

        // A named argument is left alone: dropping it could require renaming or reordering the arguments
        // around it to keep the call meaningful, which this first iteration does not attempt.
        if (lastArgument.NameColon is not null)
        {
            return [];
        }

        if (
            semanticModel.GetSymbolInfo(objectCreation, cancellationToken).Symbol is not IMethodSymbol constructor
            || constructor.MethodKind != MethodKind.Constructor
            || lastIndex >= constructor.Parameters.Length
        )
        {
            return [];
        }

        var parameter = constructor.Parameters[lastIndex];

        if (parameter.IsParams || !IsComparerType(parameter.Type, semanticModel.Compilation))
        {
            return [];
        }

        if (!ResolvesToSameType(objectCreation, argumentList, lastIndex, constructor, semanticModel))
        {
            return [];
        }

        return [CreateRemoval(argumentList, lastIndex)];
    }

    /// <summary>
    /// Determines whether removing the argument at <paramref name="argumentIndex" /> still binds to a
    /// constructor of the very type <paramref name="constructor" /> was declared on, by speculatively
    /// binding the mutated argument list at the position of the original expression.
    /// </summary>
    /// <param name="objectCreation">The original object creation expression.</param>
    /// <param name="argumentList">The argument list of that expression.</param>
    /// <param name="argumentIndex">The index of the argument to omit.</param>
    /// <param name="constructor">The constructor the original expression binds to.</param>
    /// <param name="semanticModel">The semantic model of the tree the expression belongs to.</param>
    /// <returns>
    /// <see langword="true" /> if the mutated call resolves to a constructor declared on the same type as
    /// <paramref name="constructor" />.
    /// </returns>
    private static bool ResolvesToSameType(
        ObjectCreationExpressionSyntax objectCreation,
        ArgumentListSyntax argumentList,
        int argumentIndex,
        IMethodSymbol constructor,
        SemanticModel semanticModel
    )
    {
        var candidateArguments = argumentList.WithArguments(argumentList.Arguments.RemoveAt(argumentIndex));
        var candidateNode = objectCreation.WithArgumentList(candidateArguments);

        var speculativeSymbol =
            semanticModel
                .GetSpeculativeSymbolInfo(
                    objectCreation.SpanStart,
                    candidateNode,
                    SpeculativeBindingOption.BindAsExpression
                )
                .Symbol as IMethodSymbol;

        return speculativeSymbol is not null
            && speculativeSymbol.MethodKind == MethodKind.Constructor
            && SymbolEqualityComparer.Default.Equals(speculativeSymbol.ContainingType, constructor.ContainingType);
    }

    /// <summary>
    /// Determines whether <paramref name="type" /> is <c>System.StringComparer</c>, derives from it, is
    /// <c>System.Collections.Generic.IComparer&lt;T&gt;</c> or <c>IEqualityComparer&lt;T&gt;</c>, or
    /// implements either of those two interfaces.
    /// </summary>
    /// <param name="type">The declared type of the parameter the trailing argument binds to.</param>
    /// <param name="compilation">The compilation the parameter was resolved from.</param>
    /// <returns><see langword="true" /> if the parameter is comparer shaped.</returns>
    private static bool IsComparerType(ITypeSymbol type, Compilation compilation)
    {
        var stringComparer = compilation.GetTypeByMetadataName(StringComparerMetadataName);

        for (var current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, stringComparer))
            {
                return true;
            }
        }

        var comparer = compilation.GetTypeByMetadataName(ComparerMetadataName);
        var equalityComparer = compilation.GetTypeByMetadataName(EqualityComparerMetadataName);

        return IsOrImplementsGenericInterface(type, comparer) || IsOrImplementsGenericInterface(type, equalityComparer);
    }

    /// <summary>
    /// Determines whether <paramref name="type" /> is itself the open generic interface
    /// <paramref name="openGenericInterface" /> constructed with any type argument, or implements it.
    /// </summary>
    /// <param name="type">The type to inspect.</param>
    /// <param name="openGenericInterface">The unbound generic interface to match, e.g. <c>IComparer&lt;T&gt;</c>.</param>
    /// <returns><see langword="true" /> if <paramref name="type" /> is or implements that interface.</returns>
    private static bool IsOrImplementsGenericInterface(ITypeSymbol type, INamedTypeSymbol? openGenericInterface)
    {
        if (openGenericInterface is null)
        {
            return false;
        }

        if (
            type is INamedTypeSymbol named
            && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, openGenericInterface)
        )
        {
            return true;
        }

        return type.AllInterfaces.Any(candidate =>
            SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, openGenericInterface)
        );
    }

    /// <summary>
    /// Builds the mutation that removes the argument at <paramref name="argumentIndex" />, keeping the
    /// remaining arguments, their separators and all trivia between them exactly as they were written.
    /// </summary>
    /// <param name="argumentList">The argument list of the object creation.</param>
    /// <param name="argumentIndex">The index of the comparer argument inside that list.</param>
    /// <returns>The created mutation, located at the removed argument.</returns>
    /// <remarks>
    /// The mutation is created directly instead of through the <c>CreateMutation</c> helper of the base
    /// class, because the rewritten node is the argument list while the interesting location is the
    /// argument that disappears from it, and only the full constructor takes a location.
    /// </remarks>
    private Mutation CreateRemoval(ArgumentListSyntax argumentList, int argumentIndex)
    {
        var argument = argumentList.Arguments[argumentIndex];
        var replacement = argumentList.WithArguments(argumentList.Arguments.RemoveAt(argumentIndex));
        var removed = argument.ToString();

        return new Mutation(
            Kind,
            $"{Id}.{RemoveSuffix}",
            $"{removed} => (removed)",
            argumentList,
            replacement,
            argument.GetLocation()
        );
    }
}
