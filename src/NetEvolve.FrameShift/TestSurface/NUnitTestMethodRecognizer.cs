namespace NetEvolve.FrameShift.TestSurface;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

/// <summary>
/// Recognises NUnit test methods, identified by an attribute implementing
/// <c>NUnit.Framework.Interfaces.ISimpleTestBuilder</c> or <c>NUnit.Framework.Interfaces.ITestBuilder</c>.
/// </summary>
/// <remarks>
/// <para>
/// Those two interfaces are what NUnit itself keys on: its method-level discovery accepts a method
/// exactly when one of the attributes on it implements either interface. Matching them therefore matches
/// the framework's own rule, instead of approximating it with a list of attribute names.
/// </para>
/// <para>
/// The distinction matters. NUnit's test attributes are siblings under
/// <c>NUnit.Framework.NUnitAttribute</c>, which is no test marker at all — <c>SetUpAttribute</c> and
/// <c>TestFixtureAttribute</c> derive from it as well — so there is no base attribute type to walk
/// towards, and a list of the obvious names is always short of the truth. Besides <c>TestAttribute</c>,
/// <c>TestCaseAttribute</c> and <c>TestCaseSourceAttribute</c>, NUnit ships <c>TheoryAttribute</c> and the
/// combining strategies <c>CombinatorialAttribute</c>, <c>PairwiseAttribute</c> and
/// <c>SequentialAttribute</c>, each of which builds tests from a method on its own and none of which a
/// three-name list catches.
/// </para>
/// <para>
/// Matching the interfaces also covers derivation for free: a user-written or framework-written
/// specialisation inherits the interface from the attribute it derives from, and the interfaces of base
/// types are part of <see cref="ITypeSymbol.AllInterfaces" />.
/// </para>
/// <para>
/// Attributes that decorate a test without making a method one stay out by construction, because they
/// implement neither interface: <c>RepeatAttribute</c> and <c>RetryAttribute</c> implement
/// <c>IRepeatTest</c>, <c>ValuesAttribute</c> and <c>ValueSourceAttribute</c> implement
/// <c>IParameterDataSource</c>, and <c>TestFixtureAttribute</c> implements <c>IFixtureBuilder</c> and
/// marks the class rather than the method.
/// </para>
/// <para>
/// The name-based rule is the fallback for a compilation in which the interfaces cannot be resolved by
/// their metadata name — because a name is declared more than once and therefore ambiguous, or because
/// the compilation only carries the NUnit assembly reference. Requiring the declaring assembly to belong
/// to the framework keeps a look-alike <c>ITestBuilder</c> of the project itself from being mistaken for
/// the framework's, so a recogniser left with nothing but this rule fails closed: it finds no tests
/// rather than the wrong ones.
/// </para>
/// </remarks>
internal sealed class NUnitTestMethodRecognizer : ITestMethodRecognizer
{
    private readonly ImmutableArray<INamedTypeSymbol> _testBuilderInterfaceTypes;

    /// <summary>
    /// Initializes a new instance of the <see cref="NUnitTestMethodRecognizer" /> class.
    /// </summary>
    /// <param name="testBuilderInterfaceTypes">
    /// The resolved NUnit test-builder interface types, which may be empty when only the name-based rule
    /// is available.
    /// </param>
    public NUnitTestMethodRecognizer(ImmutableArray<INamedTypeSymbol> testBuilderInterfaceTypes) =>
        _testBuilderInterfaceTypes = testBuilderInterfaceTypes.IsDefault ? [] : testBuilderInterfaceTypes;

    /// <inheritdoc />
    public string FrameworkName => NUnitTestFrameworkProbe.Name;

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

    private bool IsTestAttribute(INamedTypeSymbol? attributeClass) =>
        attributeClass is not null && attributeClass.AllInterfaces.Any(IsTestBuilderInterface);

    private bool IsTestBuilderInterface(INamedTypeSymbol interfaceType)
    {
        var definition = interfaceType.OriginalDefinition;

        return IsKnownTestBuilderInterface(definition) || IsFrameworkTestBuilderInterfaceName(definition);
    }

    private bool IsKnownTestBuilderInterface(INamedTypeSymbol definition) =>
        _testBuilderInterfaceTypes.Any(interfaceType =>
            SymbolEqualityComparer.Default.Equals(definition, interfaceType)
        );

    private static bool IsFrameworkTestBuilderInterfaceName(INamedTypeSymbol definition) =>
        NUnitTestFrameworkProbe.TestBuilderInterfaceTypeNames.Contains(definition.Name, StringComparer.Ordinal)
        && NUnitTestFrameworkProbe.IsFrameworkAssembly(definition.ContainingAssembly);
}
