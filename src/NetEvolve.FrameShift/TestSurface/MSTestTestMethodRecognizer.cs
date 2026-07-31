namespace NetEvolve.FrameShift.TestSurface;

using Microsoft.CodeAnalysis;

/// <summary>
/// Recognises MSTest test methods, identified by an attribute that either is
/// <c>Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute</c>, derives from it, or is named
/// <c>TestMethodAttribute</c> and declared in an MSTest assembly.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the well-known test attributes of other frameworks, <c>TestMethodAttribute</c> is not sealed:
/// MSTest itself derives <c>DataTestMethodAttribute</c> and <c>STATestMethodAttribute</c> from it, and
/// extending it is the documented way of writing a custom test attribute. Walking the base chain
/// therefore covers both the framework's own specialisations and user-defined ones, without hard-coding a
/// list of attribute names.
/// </para>
/// <para>
/// The base type genuinely is the marker here, so no second one has to be looked for. Every attribute of
/// the framework that makes a method a test derives from <c>TestMethodAttribute</c>, up to and including
/// MSTest 4, which added no marker outside that chain. <c>DataRowAttribute</c> and
/// <c>DynamicDataAttribute</c> in particular are not markers: they derive from <see cref="Attribute" />
/// and implement <c>ITestDataSource</c>, they only feed arguments to a method that is already a test, and
/// a method carrying nothing but one of them is not discovered by MSTest at all.
/// </para>
/// <para>
/// The name-based rule is the fallback for a compilation in which
/// <c>Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute</c> cannot be resolved by its
/// metadata name — because it is declared more than once and therefore ambiguous, or because the
/// compilation only carries the MSTest assembly reference. Requiring the declaring assembly to belong to
/// the framework keeps a look-alike <c>TestMethodAttribute</c> of the project itself from being mistaken
/// for a test attribute, so a recogniser left with nothing but this rule fails closed: it finds no tests
/// rather than the wrong ones.
/// </para>
/// <para>
/// <c>TestClassAttribute</c> marks the declaring type, not the method, and does not derive from
/// <c>TestMethodAttribute</c>. A method is consequently never recognised because of it — only an
/// attribute on the method itself makes the method a test. The same holds for the fixture attributes
/// <c>TestInitializeAttribute</c> and <c>TestCleanupAttribute</c>, which sit on a method but derive
/// straight from <see cref="Attribute" />.
/// </para>
/// </remarks>
internal sealed class MSTestTestMethodRecognizer : ITestMethodRecognizer
{
    private readonly INamedTypeSymbol? _testAttributeType;

    /// <summary>
    /// Initializes a new instance of the <see cref="MSTestTestMethodRecognizer" /> class.
    /// </summary>
    /// <param name="testAttributeType">
    /// The resolved <c>Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute</c> type, or
    /// <see langword="null" /> when only the name-based rule is available.
    /// </param>
    public MSTestTestMethodRecognizer(INamedTypeSymbol? testAttributeType) => _testAttributeType = testAttributeType;

    /// <inheritdoc />
    public string FrameworkName => MSTestTestFrameworkProbe.Name;

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

            if (IsFrameworkTestAttributeName(definition))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFrameworkTestAttributeName(INamedTypeSymbol definition) =>
        string.Equals(definition.Name, MSTestTestFrameworkProbe.TestAttributeTypeName, StringComparison.Ordinal)
        && MSTestTestFrameworkProbe.IsFrameworkAssembly(definition.ContainingAssembly);
}
