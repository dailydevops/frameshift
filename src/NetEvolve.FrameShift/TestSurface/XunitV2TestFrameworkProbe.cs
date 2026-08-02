namespace NetEvolve.FrameShift.TestSurface;

using Microsoft.CodeAnalysis;

/// <summary>
/// Detects xUnit v2 in a compilation. Version 2 ships <c>Xunit.FactAttribute</c> in the assembly
/// <c>xunit.core</c>, while version 3 ships a type of the very same metadata name in
/// <c>xunit.v3.core</c>, so the two major versions are probed separately by
/// <see cref="XunitV2TestFrameworkProbe" /> and <see cref="XunitV3TestFrameworkProbe" />.
/// </summary>
/// <remarks>
/// <para>
/// Separating the versions is what makes the detection exact. Because both declare
/// <c>Xunit.FactAttribute</c> under an identical metadata name, a compilation referencing both declares
/// that name twice and <see cref="Compilation.GetTypeByMetadataName(string)" /> answers
/// <see langword="null" /> for it — not because the type is absent but because it is ambiguous. This
/// probe therefore never asks the compilation: it locates the referenced assembly that identifies
/// version 2 and resolves the type <em>inside that assembly</em> with
/// <see cref="IAssemblySymbol.GetTypeByMetadataName(string)" />, which is unambiguous by construction
/// and removes any need to fall back to matching an attribute by its simple name.
/// </para>
/// <para>
/// The assembly guard accepts exactly <c>xunit.core</c> and nothing else. It has to be an exact match
/// rather than the <c>xunit</c> prefix the combined probe used, because every version 3 assembly is
/// also named <c>xunit.…</c> and a prefix would make the v2 probe claim a pure v3 project. Of the
/// remaining v2 assemblies, <c>xunit.abstractions</c> carries only the runner-facing interfaces and
/// <c>xunit.assert</c> only the assertion library — neither declares a test attribute, both are
/// consumed without the v2 execution model (<c>xunit.assert</c> is even distributed as source), so
/// accepting them would detect a framework whose tests can never be recognised. <c>xunit.core</c> is
/// the one assembly that declares <c>Xunit.FactAttribute</c>, which makes it both the correct guard and
/// the assembly the type is resolved in.
/// </para>
/// <para>
/// Detection still fails open, as the shared probe contract demands: a recogniser is handed out as soon
/// as <em>either</em> the attribute type resolves <em>or</em> <c>xunit.core</c> appears among the
/// referenced assembly names, so a reference that cannot be bound to a symbol never switches the whole
/// analysis off silently. Judging a method fails closed in return — a recogniser without a resolved
/// attribute type simply finds no tests.
/// </para>
/// <para>
/// The probe is stateless and its shared instance is safe to reuse from concurrent analyzer callbacks.
/// </para>
/// </remarks>
internal sealed class XunitV2TestFrameworkProbe : ITestFrameworkProbe
{
    /// <summary>
    /// The display name of the framework this probe detects. It appears in diagnostic messages and
    /// deliberately names the major version, so that a mixed project can be told apart from a v3 one.
    /// </summary>
    public const string Name = "xUnit v2";

    /// <summary>
    /// The metadata name of the well-known test attribute of the framework.
    /// </summary>
    internal const string TestAttributeMetadataName = "Xunit.FactAttribute";

    /// <summary>
    /// The name of the assembly that identifies xUnit v2 and declares
    /// <see cref="TestAttributeMetadataName" />.
    /// </summary>
    internal const string FrameworkAssemblyName = "xunit.core";

    /// <summary>
    /// Gets the shared instance of the probe, which is stateless and therefore safe to reuse.
    /// </summary>
    public static ITestFrameworkProbe Instance { get; } = new XunitV2TestFrameworkProbe();

    /// <inheritdoc />
    public string FrameworkName => Name;

    /// <inheritdoc />
    public string ConfigurationToken => "XunitV2";

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

        return new XunitV2TestMethodRecognizer(testAttributeType);
    }

    /// <summary>
    /// Resolves the well-known xUnit v2 test attribute type of <paramref name="compilation" /> inside the
    /// v2 assembly itself, so that a compilation referencing both major versions still resolves exactly
    /// the version 2 type. The result is <see langword="null" /> when the compilation does not reference
    /// xUnit v2.
    /// </summary>
    /// <param name="compilation">The compilation to resolve the type in.</param>
    /// <returns>The resolved attribute type, or <see langword="null" />.</returns>
    internal static INamedTypeSymbol? GetTestAttributeType(Compilation compilation) =>
        FindFrameworkAssembly(compilation)?.GetTypeByMetadataName(TestAttributeMetadataName);

    /// <summary>
    /// Determines whether <paramref name="assembly" /> is the xUnit v2 assembly, judged by its name. The
    /// comparison ignores case, because assembly identities are not case-sensitive and the casing a
    /// reference hint or repackaged build happens to use must never decide whether FrameShift runs.
    /// </summary>
    /// <param name="assembly">The assembly to classify.</param>
    /// <returns>
    /// <see langword="true" /> if the assembly belongs to the framework; otherwise
    /// <see langword="false" />.
    /// </returns>
    internal static bool IsFrameworkAssembly(IAssemblySymbol? assembly) => HasFrameworkAssemblyName(assembly?.Name);

    /// <summary>
    /// Finds the referenced assembly symbol that identifies xUnit v2, which is the one the attribute type
    /// is resolved in.
    /// </summary>
    /// <param name="compilation">The compilation to search the referenced assemblies of.</param>
    /// <returns>The assembly symbol, or <see langword="null" /> if the compilation has no v2 reference.</returns>
    internal static IAssemblySymbol? FindFrameworkAssembly(Compilation compilation) =>
        compilation.SourceModule.ReferencedAssemblySymbols.FirstOrDefault(IsFrameworkAssembly);

    private static bool ReferencesFrameworkAssembly(Compilation compilation) =>
        compilation.ReferencedAssemblyNames.Any(name => HasFrameworkAssemblyName(name.Name));

    private static bool HasFrameworkAssemblyName(string? name) =>
        string.Equals(name, FrameworkAssemblyName, StringComparison.OrdinalIgnoreCase);
}
