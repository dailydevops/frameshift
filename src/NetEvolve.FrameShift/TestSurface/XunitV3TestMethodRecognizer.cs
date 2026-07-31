namespace NetEvolve.FrameShift.TestSurface;

using Microsoft.CodeAnalysis;

/// <summary>
/// Recognises xUnit version 3 test methods, identified by an attribute that either is the
/// <c>Xunit.FactAttribute</c> declared in <c>xunit.v3.core</c> or derives from it.
/// </summary>
/// <remarks>
/// <para>
/// Walking the whole base chain is what makes this correct rather than merely convenient:
/// <c>Xunit.TheoryAttribute</c> derives from <c>Xunit.FactAttribute</c>, and so does the long tail of
/// custom test attributes that xUnit projects define to attach their own traits, skip conditions or data
/// discovery. Matching only the exact type would drop every one of them from the test surface.
/// </para>
/// <para>
/// The judgement rests on symbol identity alone, never on a type name. The attribute type handed in was
/// resolved inside <c>xunit.v3.core</c> itself, so it can only ever be the one of version 3, even in a
/// compilation that also references version 2 and therefore declares the identical metadata name twice.
/// That exactness is what removes the need for any name-based rule: a <c>FactAttribute</c> of version 2,
/// or one a project happens to declare itself, is a different symbol and is not accepted here.
/// </para>
/// <para>
/// When the attribute type could not be resolved — the assembly is referenced by name but its metadata
/// is unavailable — the recogniser recognises nothing instead of guessing. Detecting the framework fails
/// open, judging a method fails closed.
/// </para>
/// </remarks>
internal sealed class XunitV3TestMethodRecognizer : ITestMethodRecognizer
{
    private readonly INamedTypeSymbol? _testAttributeType;

    /// <summary>
    /// Initializes a new instance of the <see cref="XunitV3TestMethodRecognizer" /> class.
    /// </summary>
    /// <param name="testAttributeType">
    /// The <c>Xunit.FactAttribute</c> resolved inside <c>xunit.v3.core</c>, or <see langword="null" />
    /// when it could not be resolved, in which case no method is recognised as a test.
    /// </param>
    public XunitV3TestMethodRecognizer(INamedTypeSymbol? testAttributeType) => _testAttributeType = testAttributeType;

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
