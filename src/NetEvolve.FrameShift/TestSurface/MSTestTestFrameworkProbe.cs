namespace NetEvolve.FrameShift.TestSurface;

using Microsoft.CodeAnalysis;

/// <summary>
/// Detects MSTest in a compilation. Like every other probe it is a self-contained plug-in: nothing
/// framework-neutral knows that MSTest exists.
/// </summary>
/// <remarks>
/// <para>
/// MSTest is recognised by <c>Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute</c>,
/// which lives in the assembly <c>MSTest.TestFramework</c> since MSTest 4 and in
/// <c>Microsoft.VisualStudio.TestPlatform.TestFramework</c> in MSTest 3 and earlier. Both identities are
/// accepted, because the namespace of the attribute did not change with the rename and a project on
/// either major version is equally an MSTest project.
/// </para>
/// <para>
/// Both parts are required: the attribute type has to resolve <em>and</em> the compilation has to
/// reference an assembly whose name starts with one of those prefixes. The attribute alone is not enough,
/// because <c>Microsoft.VisualStudio.TestTools.UnitTesting</c> is an ordinary namespace that any project
/// is free to declare a look-alike attribute in, and a hand-written look-alike must never turn a project
/// into an MSTest project.
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
    ];

    /// <summary>
    /// Gets the shared instance of the probe, which is stateless and therefore safe to reuse.
    /// </summary>
    public static ITestFrameworkProbe Instance { get; } = new MSTestTestFrameworkProbe();

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

        if (testAttributeType is null || !ReferencesFrameworkAssembly(compilation))
        {
            return null;
        }

        return new MSTestTestMethodRecognizer(testAttributeType);
    }

    /// <summary>
    /// Resolves the well-known MSTest test attribute type of <paramref name="compilation" />, which is
    /// <see langword="null" /> if the compilation does not reference MSTest.
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
