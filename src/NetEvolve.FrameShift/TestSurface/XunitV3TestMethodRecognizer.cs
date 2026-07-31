namespace NetEvolve.FrameShift.TestSurface;

using Microsoft.CodeAnalysis;

/// <summary>
/// Recognises xUnit version 3 test methods, identified by an attribute that implements the marker interface
/// <c>Xunit.v3.IFactAttribute</c> of <c>xunit.v3.core</c> or derives from the <c>Xunit.FactAttribute</c>
/// declared there.
/// </summary>
/// <remarks>
/// <para>
/// The interface is the primary rule, because it is the rule the framework applies to itself: version 3
/// discovers a test by collecting the attributes of a method as <c>Xunit.v3.IFactAttribute</c> — through
/// <c>Xunit.v3.ExtensibilityPointFactory.GetMethodFactAttributes</c>, surfaced as
/// <c>Xunit.v3.IXunitTestMethod.FactAttributes</c> — and never by asking whether an attribute derives from
/// <c>Xunit.FactAttribute</c>. That shipped attribute is one implementation of the interface among others.
/// <c>Xunit.TheoryAttribute</c>, <c>Xunit.CulturedFactAttribute</c> and <c>Xunit.CulturedTheoryAttribute</c>
/// derive from it and are caught either way, and <c>Xunit.v3.ITheoryAttribute</c> extends the marker
/// interface and therefore needs no rule of its own. An attribute that implements
/// <c>Xunit.v3.IFactAttribute</c> directly, however, shares no base type with <c>Xunit.FactAttribute</c> at
/// all: hooking the base attribute alone would drop it from the test surface, and the production analyzer
/// would then report mutations as unreached although the tests that reach them exist.
/// </para>
/// <para>
/// Because the interface is inherited, the base chain is walked and every interface of every type in it is
/// considered. The chain of base attributes is kept as a second rule so that the recogniser still works
/// when the interface cannot be resolved — an older version 3 build that does not declare it, or a trimmed
/// reference — instead of silently recognising nothing.
/// </para>
/// <para>
/// A data source is not a marker either. <c>Xunit.InlineDataAttribute</c>, <c>Xunit.MemberDataAttribute</c>
/// and <c>Xunit.ClassDataAttribute</c> derive from <c>Xunit.v3.DataAttribute</c> and implement
/// <c>Xunit.v3.IDataAttribute</c>, which is a different interface entirely, so a method carrying them
/// without <c>[Theory]</c> is correctly not a test — version 3 would not run it either.
/// </para>
/// <para>
/// Version 2 has no counterpart to any of this. Its <c>Xunit.FactAttribute</c> implements no interface, and
/// its discovery accepts only attributes assignable to that class, which is why
/// <see cref="XunitV2TestMethodRecognizer" /> hooks the base attribute and nothing else.
/// </para>
/// <para>
/// The judgement rests on symbol identity alone, never on a type name. Both types handed in were resolved
/// inside <c>xunit.v3.core</c> itself, so they can only ever be the ones of version 3, even in a
/// compilation that also references version 2 and therefore declares the identical metadata name twice.
/// That exactness is what removes the need for any name-based rule: a <c>FactAttribute</c> of version 2, or
/// one a project happens to declare itself, is a different symbol and is not accepted here.
/// </para>
/// <para>
/// When neither type could be resolved — the assembly is referenced by name but its metadata is
/// unavailable — the recogniser recognises nothing instead of guessing. Detecting the framework fails open,
/// judging a method fails closed.
/// </para>
/// </remarks>
internal sealed class XunitV3TestMethodRecognizer : ITestMethodRecognizer
{
    private readonly INamedTypeSymbol? _testAttributeType;
    private readonly INamedTypeSymbol? _testMarkerInterfaceType;

    /// <summary>
    /// Initializes a new instance of the <see cref="XunitV3TestMethodRecognizer" /> class.
    /// </summary>
    /// <param name="testAttributeType">
    /// The <c>Xunit.FactAttribute</c> resolved inside <c>xunit.v3.core</c>, or <see langword="null" />
    /// when it could not be resolved.
    /// </param>
    /// <param name="testMarkerInterfaceType">
    /// The <c>Xunit.v3.IFactAttribute</c> resolved inside <c>xunit.v3.core</c>, or <see langword="null" />
    /// when it could not be resolved. When both are <see langword="null" />, no method is recognised as a
    /// test.
    /// </param>
    public XunitV3TestMethodRecognizer(INamedTypeSymbol? testAttributeType, INamedTypeSymbol? testMarkerInterfaceType)
    {
        _testAttributeType = testAttributeType;
        _testMarkerInterfaceType = testMarkerInterfaceType;
    }

    /// <inheritdoc />
    public string FrameworkName => XunitV3TestFrameworkProbe.Name;

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="method" /> is <see langword="null" />.</exception>
    public bool IsTestMethod(IMethodSymbol method)
    {
        if (method is null)
        {
            throw new ArgumentNullException(nameof(method));
        }

        if (_testAttributeType is null && _testMarkerInterfaceType is null)
        {
            return false;
        }

        return method.GetAttributes().Any(attribute => IsTestAttribute(attribute.AttributeClass));
    }

    private bool IsTestAttribute(INamedTypeSymbol? attributeClass)
    {
        for (var current = attributeClass; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, _testAttributeType))
            {
                return true;
            }
        }

        return _testMarkerInterfaceType is not null && ImplementsMarkerInterface(attributeClass);
    }

    /// <summary>
    /// Determines whether <paramref name="attributeClass" /> implements the marker interface, directly or
    /// through a base type. <see cref="ITypeSymbol.AllInterfaces" /> already yields the inherited and
    /// transitively extended interfaces, so the marker is found whether an attribute states it itself,
    /// inherits it, or reaches it through <c>Xunit.v3.ITheoryAttribute</c>.
    /// </summary>
    /// <param name="attributeClass">The attribute type to judge, which may be <see langword="null" />.</param>
    /// <returns><see langword="true" /> when the marker interface is implemented.</returns>
    private bool ImplementsMarkerInterface(INamedTypeSymbol? attributeClass) =>
        attributeClass is not null
        && attributeClass.AllInterfaces.Any(candidate =>
            SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, _testMarkerInterfaceType)
        );
}
