namespace NetEvolve.FrameShift.TestSurface;

using Microsoft.CodeAnalysis;

/// <summary>
/// Detects xUnit in a compilation, covering both major versions with a single probe: version 2 ships
/// <c>Xunit.FactAttribute</c> in the assembly <c>xunit.core</c>, version 3 ships a type of the very same
/// metadata name in <c>xunit.v3.core</c>.
/// </summary>
/// <remarks>
/// <para>
/// Because both versions use identical type names, a compilation that references them at the same time
/// declares <c>Xunit.FactAttribute</c> twice and
/// <see cref="Compilation.GetTypeByMetadataName(string)" /> answers <see langword="null" /> for it. That
/// is not treated as absence: the probe additionally accepts a referenced assembly whose name starts
/// with <c>xunit</c>, exactly as <see cref="TUnitTestFrameworkProbe" /> does, and the recogniser then
/// falls back to matching the attribute by its simple name plus its declaring assembly. A doubly
/// referenced xUnit is therefore recognised rather than silently ignored.
/// </para>
/// <para>
/// The probe is stateless and its shared instance is safe to reuse from concurrent analyzer callbacks.
/// </para>
/// </remarks>
internal sealed class XunitTestFrameworkProbe : ITestFrameworkProbe
{
    /// <summary>
    /// The display name of the framework this probe detects. Both major versions report the same name,
    /// because everything after detection is identical for them.
    /// </summary>
    public const string Name = "xUnit";

    /// <summary>
    /// The metadata name of the well-known test attribute of the framework.
    /// </summary>
    internal const string TestAttributeMetadataName = "Xunit.FactAttribute";

    /// <summary>
    /// The simple type name a framework attribute has to carry to be recognised by name.
    /// </summary>
    internal const string TestAttributeTypeName = "FactAttribute";

    private const string AssemblyPrefix = "xunit";

    /// <summary>
    /// Gets the shared instance of the probe, which is stateless and therefore safe to reuse.
    /// </summary>
    public static ITestFrameworkProbe Instance { get; } = new XunitTestFrameworkProbe();

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

        return new XunitTestMethodRecognizer(testAttributeType);
    }

    /// <summary>
    /// Resolves the well-known xUnit test attribute type of <paramref name="compilation" />, which is
    /// <see langword="null" /> if the compilation does not reference xUnit at all, and also if it
    /// references both major versions and the name is therefore ambiguous.
    /// </summary>
    /// <param name="compilation">The compilation to resolve the type in.</param>
    /// <returns>The resolved attribute type, or <see langword="null" />.</returns>
    internal static INamedTypeSymbol? GetTestAttributeType(Compilation compilation) =>
        compilation.GetTypeByMetadataName(TestAttributeMetadataName);

    /// <summary>
    /// Determines whether <paramref name="assembly" /> belongs to the framework, judged by its name.
    /// The comparison ignores case, because the assemblies of the framework are spelled in lower case
    /// while a project may well reference them through a differently cased alias.
    /// </summary>
    /// <param name="assembly">The assembly to classify.</param>
    /// <returns>
    /// <see langword="true" /> if the assembly belongs to the framework; otherwise
    /// <see langword="false" />.
    /// </returns>
    internal static bool IsFrameworkAssembly(IAssemblySymbol? assembly) =>
        assembly is not null && assembly.Name.StartsWith(AssemblyPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool ReferencesFrameworkAssembly(Compilation compilation) =>
        compilation.ReferencedAssemblyNames.Any(name =>
            name.Name.StartsWith(AssemblyPrefix, StringComparison.OrdinalIgnoreCase)
        );
}
