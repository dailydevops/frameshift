namespace NetEvolve.Frameshift.TestSurface;

using Microsoft.CodeAnalysis;

/// <summary>
/// Recognises TUnit test methods, identified by an attribute that either is
/// <c>TUnit.Core.TestAttribute</c> or derives from an attribute named <c>TestAttribute</c> that is
/// declared in a TUnit assembly.
/// </summary>
/// <remarks>
/// The second rule exists because <c>TUnit.Core.TestAttribute</c> is sealed, so every data-driven or
/// otherwise specialised test attribute has to derive from a different type of the framework. Matching
/// on the simple name plus the declaring assembly keeps those recognisable without hard-coding the
/// full list of framework attributes.
/// </remarks>
internal sealed class TUnitTestMethodRecognizer : ITestMethodRecognizer
{
    private readonly INamedTypeSymbol? _testAttributeType;

    /// <summary>
    /// Initializes a new instance of the <see cref="TUnitTestMethodRecognizer" /> class.
    /// </summary>
    /// <param name="testAttributeType">
    /// The resolved <c>TUnit.Core.TestAttribute</c> type, or <see langword="null" /> when only the
    /// name-based rule is available.
    /// </param>
    public TUnitTestMethodRecognizer(INamedTypeSymbol? testAttributeType) => _testAttributeType = testAttributeType;

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

            if (_testAttributeType is not null && SymbolEqualityComparer.Default.Equals(definition, _testAttributeType))
            {
                return true;
            }

            if (
                string.Equals(definition.Name, TUnitTestFrameworkProbe.TestAttributeTypeName, StringComparison.Ordinal)
                && TUnitTestFrameworkProbe.IsFrameworkAssembly(definition.ContainingAssembly)
            )
            {
                return true;
            }
        }

        return false;
    }
}
