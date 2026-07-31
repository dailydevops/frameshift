namespace NetEvolve.FrameShift.TestSurface;

using Microsoft.CodeAnalysis;

/// <summary>
/// Recognises TUnit test methods, identified by an attribute whose base chain includes the abstract
/// marker base type <c>TUnit.Core.BaseTestAttribute</c>.
/// </summary>
/// <remarks>
/// <para>
/// The marker base type is the right hook because it is the one thing every test marker of the framework
/// has in common, and the only thing that stays true when the framework adds another one.
/// <c>TUnit.Core.TestAttribute</c> is sealed, so it can never be the base of anything, and it is not the
/// only marker: <c>TUnit.Core.DynamicTestBuilderAttribute</c> derives from
/// <c>TUnit.Core.BaseTestAttribute</c> as well and marks a test method just the same. Matching only the
/// sealed <c>TestAttribute</c> would leave those methods unrecognised, their production references would
/// never reach the test-surface manifest, and the production analyzer would then report a test-less
/// production member that is in truth covered.
/// </para>
/// <para>
/// The base type is also what makes a user-defined marker work. A derived marker cannot extend
/// <c>TUnit.Core.BaseTestAttribute</c> itself - its only constructor is internal to the framework - but it
/// can extend <c>TUnit.Core.DynamicTestBuilderAttribute</c>, and walking the base chain to the marker base
/// type recognises it without hard-coding a list of attribute names.
/// </para>
/// <para>
/// Data-source attributes are deliberately not markers. <c>ArgumentsAttribute</c>,
/// <c>MethodDataSourceAttribute</c>, <c>MatrixDataSourceAttribute</c> and <c>ClassDataSourceAttribute</c>
/// derive from <see cref="Attribute" /> rather than from the marker base type, and a marker such as
/// <c>[Test]</c> stays required next to them. The same holds for the attributes that only configure a test,
/// among them <c>RepeatAttribute</c>, <c>CategoryAttribute</c> and <c>SkipAttribute</c>: a method carrying
/// nothing but those is no test.
/// </para>
/// <para>
/// The name-based rule is only a fallback for a compilation in which
/// <c>TUnit.Core.BaseTestAttribute</c> cannot be resolved by its metadata name, which is the state a
/// recogniser created from nothing but the framework's assembly reference is in. It matches the simple name
/// of the marker <em>base</em> type, and it requires the declaring assembly to belong to the framework, so
/// a look-alike <c>BaseTestAttribute</c> of the project itself or of an unrelated package marks nothing. As
/// soon as the base type does resolve, the semantic rule is the whole rule: a same-named type from another
/// namespace never matches it.
/// </para>
/// </remarks>
internal sealed class TUnitTestMethodRecognizer : ITestMethodRecognizer
{
    private readonly INamedTypeSymbol? _testAttributeType;
    private readonly INamedTypeSymbol? _baseTestAttributeType;

    /// <summary>
    /// Initializes a new instance of the <see cref="TUnitTestMethodRecognizer" /> class.
    /// </summary>
    /// <param name="testAttributeType">
    /// The resolved <c>TUnit.Core.TestAttribute</c> type, or <see langword="null" /> when it could not be
    /// resolved.
    /// </param>
    /// <param name="baseTestAttributeType">
    /// The resolved <c>TUnit.Core.BaseTestAttribute</c> marker base type, or <see langword="null" /> when it
    /// could not be resolved and only the name-based fallback is available.
    /// </param>
    public TUnitTestMethodRecognizer(
        INamedTypeSymbol? testAttributeType,
        INamedTypeSymbol? baseTestAttributeType = null
    )
    {
        _testAttributeType = testAttributeType;
        _baseTestAttributeType = baseTestAttributeType;
    }

    /// <inheritdoc />
    public string FrameworkName => TUnitTestFrameworkProbe.Name;

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="method" /> is <see langword="null" />.</exception>
    public bool IsTestMethod(IMethodSymbol method)
    {
        if (method is null)
        {
            throw new ArgumentNullException(nameof(method));
        }

        return method.GetAttributes().Any(attribute => IsTestAttribute(attribute.AttributeClass));
    }

    private bool IsTestAttribute(INamedTypeSymbol? attributeClass)
    {
        for (var current = attributeClass; current is not null; current = current.BaseType)
        {
            var definition = current.OriginalDefinition;

            if (IsResolvedMarkerType(definition) || IsFrameworkMarkerName(definition))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Compares <paramref name="definition" /> against the types that were resolved for the compilation.
    /// The marker base type carries the recognition; the sealed <c>TestAttribute</c> is compared as well so
    /// that a caller holding only that type still recognises a plain <c>[Test]</c>.
    /// </summary>
    /// <param name="definition">The attribute definition of the current step of the base chain.</param>
    /// <returns>
    /// <see langword="true" /> if the definition is one of the resolved types; otherwise
    /// <see langword="false" />.
    /// </returns>
    private bool IsResolvedMarkerType(INamedTypeSymbol definition) =>
        Matches(definition, _baseTestAttributeType) || Matches(definition, _testAttributeType);

    /// <summary>
    /// Compares <paramref name="definition" /> against <paramref name="resolved" /> semantically, so that a
    /// same-named type from another namespace or another assembly never matches.
    /// </summary>
    /// <param name="definition">The attribute definition of the current step of the base chain.</param>
    /// <param name="resolved">The resolved type to compare against, which may be <see langword="null" />.</param>
    /// <returns>
    /// <see langword="true" /> if the definition is that very type; otherwise <see langword="false" />.
    /// </returns>
    private static bool Matches(INamedTypeSymbol definition, INamedTypeSymbol? resolved) =>
        resolved is not null && SymbolEqualityComparer.Default.Equals(definition, resolved);

    /// <summary>
    /// The fallback for the state in which the marker base type could not be resolved: the simple name of
    /// that base type, declared by an assembly of the framework.
    /// </summary>
    /// <param name="definition">The attribute definition of the current step of the base chain.</param>
    /// <returns>
    /// <see langword="true" /> if the definition is a framework marker base type by name; otherwise
    /// <see langword="false" />.
    /// </returns>
    private bool IsFrameworkMarkerName(INamedTypeSymbol definition) =>
        _baseTestAttributeType is null
        && string.Equals(definition.Name, TUnitTestFrameworkProbe.BaseTestAttributeTypeName, StringComparison.Ordinal)
        && TUnitTestFrameworkProbe.IsFrameworkAssembly(definition.ContainingAssembly);
}
