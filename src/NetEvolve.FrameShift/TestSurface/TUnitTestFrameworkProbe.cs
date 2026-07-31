namespace NetEvolve.FrameShift.TestSurface;

using Microsoft.CodeAnalysis;

/// <summary>
/// Detects TUnit in a compilation. This is the first supported test framework; further frameworks are
/// added as additional probes without touching anything framework-neutral.
/// </summary>
internal sealed class TUnitTestFrameworkProbe : ITestFrameworkProbe
{
    /// <summary>
    /// The display name of the framework this probe detects.
    /// </summary>
    public const string Name = "TUnit";

    /// <summary>
    /// The metadata name of the well-known test attribute of the framework.
    /// </summary>
    internal const string TestAttributeMetadataName = "TUnit.Core.TestAttribute";

    /// <summary>
    /// The metadata name of the abstract base type every test marker of the framework derives from. It is
    /// the type a marker is recognised by, because <c>TUnit.Core.TestAttribute</c> is sealed and is not the
    /// only marker the framework declares.
    /// </summary>
    internal const string BaseTestAttributeMetadataName = "TUnit.Core.BaseTestAttribute";

    /// <summary>
    /// The simple type name a framework attribute has to carry to be recognised by name, which is the name
    /// of the marker base type rather than the name of a concrete marker.
    /// </summary>
    internal const string BaseTestAttributeTypeName = "BaseTestAttribute";

    private const string AssemblyPrefix = "TUnit";

    /// <summary>
    /// Gets the shared instance of the probe, which is stateless and therefore safe to reuse.
    /// </summary>
    public static ITestFrameworkProbe Instance { get; } = new TUnitTestFrameworkProbe();

    /// <inheritdoc />
    public string FrameworkName => Name;

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="compilation" /> is <see langword="null" />.</exception>
    public ITestMethodRecognizer? TryCreateRecognizer(Compilation compilation)
    {
        if (compilation is null)
        {
            throw new ArgumentNullException(nameof(compilation));
        }

        var testAttributeType = GetTestAttributeType(compilation);
        var baseTestAttributeType = GetBaseTestAttributeType(compilation);

        if (testAttributeType is null && baseTestAttributeType is null && !ReferencesFrameworkAssembly(compilation))
        {
            return null;
        }

        return new TUnitTestMethodRecognizer(testAttributeType, baseTestAttributeType);
    }

    /// <summary>
    /// Resolves the well-known TUnit test attribute type of <paramref name="compilation" />, which is
    /// <see langword="null" /> if the compilation does not reference TUnit.
    /// </summary>
    /// <param name="compilation">The compilation to resolve the type in.</param>
    /// <returns>The resolved attribute type, or <see langword="null" />.</returns>
    internal static INamedTypeSymbol? GetTestAttributeType(Compilation compilation) =>
        compilation.GetTypeByMetadataName(TestAttributeMetadataName);

    /// <summary>
    /// Resolves the abstract marker base type <c>TUnit.Core.BaseTestAttribute</c> of
    /// <paramref name="compilation" />, which is <see langword="null" /> if the compilation does not
    /// reference TUnit.
    /// </summary>
    /// <param name="compilation">The compilation to resolve the type in.</param>
    /// <returns>The resolved base type, or <see langword="null" />.</returns>
    internal static INamedTypeSymbol? GetBaseTestAttributeType(Compilation compilation) =>
        compilation.GetTypeByMetadataName(BaseTestAttributeMetadataName);

    /// <summary>
    /// Determines whether <paramref name="assembly" /> belongs to the framework, judged by its name.
    /// </summary>
    /// <param name="assembly">The assembly to classify.</param>
    /// <returns>
    /// <see langword="true" /> if the assembly belongs to the framework; otherwise
    /// <see langword="false" />.
    /// </returns>
    internal static bool IsFrameworkAssembly(IAssemblySymbol? assembly) =>
        assembly is not null && assembly.Name.StartsWith(AssemblyPrefix, StringComparison.Ordinal);

    private static bool ReferencesFrameworkAssembly(Compilation compilation) =>
        compilation.ReferencedAssemblyNames.Any(name => name.Name.StartsWith(AssemblyPrefix, StringComparison.Ordinal));
}
