namespace NetEvolve.FrameShift.TestSurface;

using Microsoft.CodeAnalysis;

/// <summary>
/// Detects xUnit version 3 in a compilation. The framework ships <c>Xunit.FactAttribute</c> in the
/// assembly <c>xunit.v3.core</c>, which consolidates what version 2 spread across <c>xunit.core</c>,
/// <c>xunit.execution.*</c> and <c>xunit.abstractions</c>.
/// </summary>
/// <remarks>
/// <para>
/// Version 2 and version 3 declare their test attribute under the very same metadata name,
/// <c>Xunit.FactAttribute</c>, so a compilation referencing both declares that name twice and
/// <see cref="Compilation.GetTypeByMetadataName(string)" /> answers <see langword="null" /> for it —
/// ambiguity, not absence. This probe therefore never asks the compilation. It looks up the referenced
/// assembly of its own major version and resolves the attribute type <em>inside that assembly</em>
/// through <see cref="IAssemblySymbol.GetTypeByMetadataName(string)" />, which is unambiguous by
/// construction. Both major versions can consequently be recognised exactly and at the same time, and
/// the simple-name fallback the combined probe needed is gone.
/// </para>
/// <para>
/// The two versions are told apart by the assembly name alone: every version 3 assembly is prefixed
/// <c>xunit.v3</c>, while no assembly of version 2 is. Detection accepts that prefix and so stays open
/// even when the reference itself cannot be resolved to a symbol, while the attribute type is only ever
/// taken from the one assembly that is allowed to declare it.
/// </para>
/// <para>
/// Nothing here depends on a compile-time reference to xUnit: the probe works purely from symbols, which
/// is what lets the analyzer assembly target <c>netstandard2.0</c> and carry no test-framework package
/// reference at all.
/// </para>
/// <para>
/// The probe is stateless and its shared instance is safe to reuse from concurrent analyzer callbacks.
/// </para>
/// </remarks>
internal sealed class XunitV3TestFrameworkProbe : ITestFrameworkProbe
{
    /// <summary>
    /// The display name of the framework this probe detects.
    /// </summary>
    public const string Name = "xUnit v3";

    /// <summary>
    /// The metadata name of the well-known test attribute of the framework. Version 2 uses the identical
    /// name, which is why the type is resolved inside <see cref="FrameworkAssemblyName" /> rather than in
    /// the compilation.
    /// </summary>
    internal const string TestAttributeMetadataName = "Xunit.FactAttribute";

    /// <summary>
    /// The name of the assembly that declares the well-known test attribute of the framework.
    /// </summary>
    internal const string FrameworkAssemblyName = "xunit.v3.core";

    private const string AssemblyPrefix = "xunit.v3";

    /// <summary>
    /// Gets the shared instance of the probe, which is stateless and therefore safe to reuse.
    /// </summary>
    public static ITestFrameworkProbe Instance { get; } = new XunitV3TestFrameworkProbe();

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

        return new XunitV3TestMethodRecognizer(testAttributeType);
    }

    /// <summary>
    /// Resolves the well-known test attribute type of version 3 inside the referenced
    /// <c>xunit.v3.core</c> assembly of <paramref name="compilation" />, which is <see langword="null" />
    /// if that assembly is not referenced or does not declare the type.
    /// </summary>
    /// <param name="compilation">The compilation to resolve the type in.</param>
    /// <returns>The resolved attribute type, or <see langword="null" />.</returns>
    /// <remarks>
    /// Resolving through the assembly symbol instead of the compilation is what keeps the result exact
    /// when version 2 is referenced as well: the compilation sees <c>Xunit.FactAttribute</c> twice, the
    /// assembly sees only its own.
    /// </remarks>
    internal static INamedTypeSymbol? GetTestAttributeType(Compilation compilation) =>
        FindFrameworkAssembly(compilation)?.GetTypeByMetadataName(TestAttributeMetadataName);

    /// <summary>
    /// Finds the referenced assembly that declares the test attribute of version 3.
    /// </summary>
    /// <param name="compilation">The compilation to search the referenced assemblies of.</param>
    /// <returns>The assembly symbol, or <see langword="null" /> if it is not referenced.</returns>
    /// <remarks>
    /// The comparison ignores case, because assembly identities are not case-sensitive and a reference
    /// hint, facade assembly or repackaged build may well spell the name differently.
    /// </remarks>
    internal static IAssemblySymbol? FindFrameworkAssembly(Compilation compilation) =>
        compilation.SourceModule.ReferencedAssemblySymbols.FirstOrDefault(assembly =>
            string.Equals(assembly.Name, FrameworkAssemblyName, StringComparison.OrdinalIgnoreCase)
        );

    /// <summary>
    /// Determines whether <paramref name="assembly" /> belongs to version 3 of the framework, judged by
    /// its name. Every assembly of version 3 carries the prefix <c>xunit.v3</c>, which no assembly of
    /// version 2 does, so the name alone separates the two major versions.
    /// </summary>
    /// <param name="assembly">The assembly to classify.</param>
    /// <returns>
    /// <see langword="true" /> if the assembly belongs to version 3; otherwise <see langword="false" />.
    /// </returns>
    internal static bool IsFrameworkAssembly(IAssemblySymbol? assembly) =>
        assembly is not null && assembly.Name.StartsWith(AssemblyPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool ReferencesFrameworkAssembly(Compilation compilation) =>
        compilation.ReferencedAssemblyNames.Any(name =>
            name.Name.StartsWith(AssemblyPrefix, StringComparison.OrdinalIgnoreCase)
        );
}
