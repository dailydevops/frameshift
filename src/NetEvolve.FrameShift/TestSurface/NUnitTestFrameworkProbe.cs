namespace NetEvolve.FrameShift.TestSurface;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

/// <summary>
/// Detects NUnit in a compilation. The framework lives in the assembly <c>nunit.framework</c> and marks
/// its tests with <c>NUnit.Framework.TestAttribute</c>, <c>NUnit.Framework.TestCaseAttribute</c> or
/// <c>NUnit.Framework.TestCaseSourceAttribute</c>.
/// </summary>
/// <remarks>
/// Unlike other frameworks, these three attributes are siblings rather than a base type and its
/// derivations, so a recogniser has to accept any of them instead of walking a single base type.
/// </remarks>
internal sealed class NUnitTestFrameworkProbe : ITestFrameworkProbe
{
    /// <summary>
    /// The display name of the framework this probe detects.
    /// </summary>
    public const string Name = "NUnit";

    /// <summary>
    /// The metadata name of the plain test attribute of the framework.
    /// </summary>
    internal const string TestAttributeMetadataName = "NUnit.Framework.TestAttribute";

    /// <summary>
    /// The metadata name of the data-driven test attribute of the framework.
    /// </summary>
    internal const string TestCaseAttributeMetadataName = "NUnit.Framework.TestCaseAttribute";

    /// <summary>
    /// The metadata name of the data-source-driven test attribute of the framework.
    /// </summary>
    internal const string TestCaseSourceAttributeMetadataName = "NUnit.Framework.TestCaseSourceAttribute";

    private const string AssemblyPrefix = "nunit";

    /// <summary>
    /// The metadata names of every attribute that marks a method as an NUnit test.
    /// </summary>
    internal static readonly ImmutableArray<string> TestAttributeMetadataNames =
    [
        TestAttributeMetadataName,
        TestCaseAttributeMetadataName,
        TestCaseSourceAttributeMetadataName,
    ];

    /// <summary>
    /// The simple type names an attribute has to carry to be recognised by name. They are matched only
    /// in combination with a declaring assembly that belongs to the framework.
    /// </summary>
    /// <remarks>
    /// <c>TestFixtureAttribute</c> is deliberately absent: it marks the class, never the method, and
    /// must therefore never turn a method into a test.
    /// </remarks>
    internal static readonly ImmutableArray<string> TestAttributeTypeNames =
    [
        "TestAttribute",
        "TestCaseAttribute",
        "TestCaseSourceAttribute",
    ];

    /// <summary>
    /// Gets the shared instance of the probe, which is stateless and therefore safe to reuse.
    /// </summary>
    public static ITestFrameworkProbe Instance { get; } = new NUnitTestFrameworkProbe();

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

        var testAttributeTypes = GetTestAttributeTypes(compilation);

        if (testAttributeTypes.IsEmpty && !ReferencesFrameworkAssembly(compilation))
        {
            return null;
        }

        return new NUnitTestMethodRecognizer(testAttributeTypes);
    }

    /// <summary>
    /// Resolves the well-known NUnit test attribute types of <paramref name="compilation" />, which is
    /// empty if the compilation does not reference NUnit.
    /// </summary>
    /// <param name="compilation">The compilation to resolve the types in.</param>
    /// <returns>The resolved attribute types, which may be empty.</returns>
    internal static ImmutableArray<INamedTypeSymbol> GetTestAttributeTypes(Compilation compilation) =>
        TestAttributeMetadataNames
            .Select(compilation.GetTypeByMetadataName)
            .Where(type => type is not null)
            .Select(type => type!)
            .ToImmutableArray();

    /// <summary>
    /// Determines whether <paramref name="assembly" /> belongs to the framework, judged by its name.
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
