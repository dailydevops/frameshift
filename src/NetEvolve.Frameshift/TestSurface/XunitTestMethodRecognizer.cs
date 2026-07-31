namespace NetEvolve.Frameshift.TestSurface;

using Microsoft.CodeAnalysis;

/// <summary>
/// Recognises xUnit test methods, identified by an attribute that either is <c>Xunit.FactAttribute</c>
/// itself, derives from it, or is named <c>FactAttribute</c> and declared in an xUnit assembly.
/// </summary>
/// <remarks>
/// <para>
/// Walking the whole base chain is what makes this correct rather than merely convenient:
/// <c>Xunit.TheoryAttribute</c> derives from <c>Xunit.FactAttribute</c>, and so does the long tail of
/// custom test attributes that xUnit projects define to attach their own traits, skip conditions or
/// data discovery. Matching only the exact type would drop every one of them from the test surface.
/// </para>
/// <para>
/// The name-based rule is the fallback for a compilation that references xUnit v2 and v3 at once, where
/// <c>Xunit.FactAttribute</c> exists twice and cannot be resolved by metadata name. Requiring the
/// declaring assembly to belong to the framework keeps an unrelated <c>FactAttribute</c> of the project
/// itself from being mistaken for a test attribute.
/// </para>
/// </remarks>
internal sealed class XunitTestMethodRecognizer : ITestMethodRecognizer
{
    private readonly INamedTypeSymbol? _testAttributeType;

    /// <summary>
    /// Initializes a new instance of the <see cref="XunitTestMethodRecognizer" /> class.
    /// </summary>
    /// <param name="testAttributeType">
    /// The resolved <c>Xunit.FactAttribute</c> type, or <see langword="null" /> when only the name-based
    /// rule is available.
    /// </param>
    public XunitTestMethodRecognizer(INamedTypeSymbol? testAttributeType) => _testAttributeType = testAttributeType;

    /// <inheritdoc />
    public string FrameworkName => XunitTestFrameworkProbe.Name;

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

            if (_testAttributeType is not null && SymbolEqualityComparer.Default.Equals(definition, _testAttributeType))
            {
                return true;
            }

            if (
                string.Equals(definition.Name, XunitTestFrameworkProbe.TestAttributeTypeName, StringComparison.Ordinal)
                && XunitTestFrameworkProbe.IsFrameworkAssembly(definition.ContainingAssembly)
            )
            {
                return true;
            }
        }

        return false;
    }
}
