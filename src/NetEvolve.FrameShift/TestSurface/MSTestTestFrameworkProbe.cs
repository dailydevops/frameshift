namespace NetEvolve.FrameShift.TestSurface;

using Microsoft.CodeAnalysis;

/// <summary>
/// Detects MSTest in a compilation. Like every other probe it is a self-contained plug-in: nothing
/// framework-neutral knows that MSTest exists.
/// </summary>
/// <remarks>
/// <para>
/// MSTest is recognised by <c>Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute</c>,
/// which lives in the assembly <c>MSTest.TestFramework</c> since MSTest 4, in
/// <c>Microsoft.VisualStudio.TestPlatform.TestFramework</c> in MSTest 3 and earlier, and in
/// <c>Microsoft.VisualStudio.QualityTools.UnitTestFramework</c> for the legacy, in-box Visual Studio unit
/// test framework that predates MSTest V2 and NuGet packaging. All three identities are accepted, because
/// the namespace of the attribute did not change across the rename, nor across MSTest V2's predecessor,
/// and a project on any of them is equally an MSTest project.
/// </para>
/// <para>
/// Either part is enough, exactly as in every other probe: the attribute type resolves, <em>or</em> the
/// compilation references an assembly whose name starts with one of those prefixes. Demanding both would
/// make detection depend on <see cref="Compilation.GetTypeByMetadataName(string)" />, which also answers
/// <see langword="null" /> for an ambiguous name, and MSTest can be ambiguous — a project may reference
/// the framework alongside an assembly of its own that declares the very same attribute name. Reading
/// that as absence would switch the analysis off in silence. See <see cref="ITestFrameworkProbe" /> for
/// the contract this follows.
/// </para>
/// <para>
/// Detecting MSTest generously is safe because judging a single method is not. A referenced framework
/// assembly on its own only enables the name-based rule of
/// <see cref="MSTestTestMethodRecognizer" />, which accepts an attribute matched by its simple name
/// exclusively when that attribute is declared in an MSTest assembly, so an unrelated
/// <c>TestMethodAttribute</c> of the project leaves the recogniser finding nothing at all.
/// </para>
/// <para>
/// A project that declares a type under the framework's exact full name in the framework's own namespace
/// is a different matter: that name is the framework's identity, so the probe takes it at face value and
/// the recogniser matches it, precisely as the TUnit, xUnit and NUnit probes do with theirs. Recognising
/// the tests of such a project is the deliberate trade for never shutting the analysis down in silence.
/// </para>
/// <para>
/// The assembly name is compared case-insensitively. Assembly identities are not case-sensitive, and
/// this particular name is long, dotted and reproduced by hand in enough places — reference hints,
/// facade and extension assemblies, repackaged builds — that insisting on the exact casing would only
/// ever produce a false negative, which here means silently analysing nothing at all.
/// </para>
/// </remarks>
internal sealed class MSTestTestFrameworkProbe : ITestFrameworkProbe
{
    /// <summary>
    /// The display name of the framework this probe detects.
    /// </summary>
    public const string Name = "MSTest";

    /// <summary>
    /// The metadata name of the well-known test attribute of the framework.
    /// </summary>
    internal const string TestAttributeMetadataName =
        "Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute";

    /// <summary>
    /// The simple type name a framework attribute has to carry to be recognised by name.
    /// </summary>
    internal const string TestAttributeTypeName = "TestMethodAttribute";

    private static readonly string[] _assemblyPrefixes =
    [
        // MSTest 4 and later.
        "MSTest.TestFramework",
        // MSTest 3 and earlier.
        "Microsoft.VisualStudio.TestPlatform.TestFramework",
        // The legacy, in-box Visual Studio unit test framework that predates MSTest V2 and NuGet
        // packaging. It declares the identical well-known attribute type, so a classic project is
        // recognised the same way.
        "Microsoft.VisualStudio.QualityTools.UnitTestFramework",
    ];

    /// <summary>
    /// Gets the shared instance of the probe, which is stateless and therefore safe to reuse.
    /// </summary>
    public static ITestFrameworkProbe Instance { get; } = new MSTestTestFrameworkProbe();

    /// <inheritdoc />
    public string FrameworkName => Name;

    /// <inheritdoc />
    public string ConfigurationToken => Name;

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

        return new MSTestTestMethodRecognizer(testAttributeType);
    }

    /// <summary>
    /// Resolves the well-known MSTest test attribute type of <paramref name="compilation" />, which is
    /// <see langword="null" /> if the compilation does not reference MSTest, and also if the name is
    /// declared more than once and therefore ambiguous.
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
        assembly is not null && HasFrameworkAssemblyName(assembly.Name);

    private static bool HasFrameworkAssemblyName(string name) =>
        _assemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool ReferencesFrameworkAssembly(Compilation compilation) =>
        compilation.ReferencedAssemblyNames.Any(name => HasFrameworkAssemblyName(name.Name));
}
