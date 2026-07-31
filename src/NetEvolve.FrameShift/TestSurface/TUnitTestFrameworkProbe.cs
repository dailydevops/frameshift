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
    /// The simple type name a framework attribute has to carry to be recognised by name.
    /// </summary>
    internal const string TestAttributeTypeName = "TestAttribute";

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

        if (testAttributeType is null && !ReferencesFrameworkAssembly(compilation))
        {
            return null;
        }

        return new TUnitTestMethodRecognizer(testAttributeType);
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
