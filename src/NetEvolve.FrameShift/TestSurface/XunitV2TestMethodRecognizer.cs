namespace NetEvolve.FrameShift.TestSurface;

using Microsoft.CodeAnalysis;

/// <summary>
/// Recognises xUnit v2 test methods, identified by an attribute that either is the
/// <c>Xunit.FactAttribute</c> of <c>xunit.core</c> itself or derives from it.
/// </summary>
/// <remarks>
/// <para>
/// Walking the whole base chain is what makes this correct rather than merely convenient:
/// <c>Xunit.TheoryAttribute</c> derives from <c>Xunit.FactAttribute</c>, and so does the long tail of
/// custom test attributes that xUnit projects define to attach their own traits, skip conditions or data
/// discovery. Matching only the exact type would drop every one of them from the test surface.
/// </para>
/// <para>
/// There is deliberately no rule matching an attribute by its simple name. The type this recogniser
/// compares against is resolved inside <c>xunit.core</c> by
/// <see cref="XunitV2TestFrameworkProbe" />, so it is exact even in a compilation that references xUnit
/// v2 and v3 at once — the ambiguity that once forced a name-based fallback cannot arise, and a
/// <c>FactAttribute</c> of the project itself or of xUnit v3 can never be mistaken for a v2 test
/// attribute.
/// </para>
/// <para>
/// A recogniser whose attribute type could not be resolved finds no tests at all instead of throwing:
/// judging a method fails closed, and a compilation whose tests cannot be seen must never be judged.
/// </para>
/// </remarks>
internal sealed class XunitV2TestMethodRecognizer : ITestMethodRecognizer
{
    private readonly INamedTypeSymbol? _testAttributeType;

    /// <summary>
    /// Initializes a new instance of the <see cref="XunitV2TestMethodRecognizer" /> class.
    /// </summary>
    /// <param name="testAttributeType">
    /// The <c>Xunit.FactAttribute</c> type resolved inside <c>xunit.core</c>, or <see langword="null" />
    /// when it could not be resolved, in which case no method is recognised as a test.
    /// </param>
    public XunitV2TestMethodRecognizer(INamedTypeSymbol? testAttributeType) => _testAttributeType = testAttributeType;

    /// <inheritdoc />
    public string FrameworkName => XunitV2TestFrameworkProbe.Name;

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="method" /> is <see langword="null" />.</exception>
    public bool IsTestMethod(IMethodSymbol method)
    {
        if (method is null)
        {
            throw new ArgumentNullException(nameof(method));
        }

        if (_testAttributeType is null)
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

        return false;
    }
}
