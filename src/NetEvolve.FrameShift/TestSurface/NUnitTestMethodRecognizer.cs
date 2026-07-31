namespace NetEvolve.FrameShift.TestSurface;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

/// <summary>
/// Recognises NUnit test methods, identified by an attribute that either is one of
/// <c>NUnit.Framework.TestAttribute</c>, <c>NUnit.Framework.TestCaseAttribute</c> and
/// <c>NUnit.Framework.TestCaseSourceAttribute</c>, derives from one of them, or carries one of their
/// simple names while being declared in an NUnit assembly.
/// </summary>
/// <remarks>
/// <para>
/// In NUnit the three attributes are siblings, not a base type and its derivations, so the recogniser
/// accepts any of them instead of walking towards a single base type. Derived attributes are still
/// recognised, because user-defined and framework-defined specialisations — <c>TestCaseSourceAttribute</c>
/// is commonly extended — inherit from one of the three.
/// </para>
/// <para>
/// <c>TestFixtureAttribute</c> is intentionally not part of the rule: it marks the class, never the
/// method, and therefore must not make a method a test.
/// </para>
/// </remarks>
internal sealed class NUnitTestMethodRecognizer : ITestMethodRecognizer
{
    private readonly ImmutableArray<INamedTypeSymbol> _testAttributeTypes;

    /// <summary>
    /// Initializes a new instance of the <see cref="NUnitTestMethodRecognizer" /> class.
    /// </summary>
    /// <param name="testAttributeTypes">
    /// The resolved NUnit test attribute types, which may be empty when only the name-based rule is
    /// available.
    /// </param>
    public NUnitTestMethodRecognizer(ImmutableArray<INamedTypeSymbol> testAttributeTypes) =>
        _testAttributeTypes = testAttributeTypes.IsDefault ? [] : testAttributeTypes;

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

    private bool IsTestAttribute(INamedTypeSymbol? attributeClass)
    {
        for (var current = attributeClass; current is not null; current = current.BaseType)
        {
            var definition = current.OriginalDefinition;

            if (IsKnownTestAttributeType(definition) || IsFrameworkTestAttributeName(definition))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsKnownTestAttributeType(INamedTypeSymbol definition) =>
        _testAttributeTypes.Any(testAttributeType =>
            SymbolEqualityComparer.Default.Equals(definition, testAttributeType)
        );

    private static bool IsFrameworkTestAttributeName(INamedTypeSymbol definition) =>
        NUnitTestFrameworkProbe.TestAttributeTypeNames.Contains(definition.Name, StringComparer.Ordinal)
        && NUnitTestFrameworkProbe.IsFrameworkAssembly(definition.ContainingAssembly);
}
