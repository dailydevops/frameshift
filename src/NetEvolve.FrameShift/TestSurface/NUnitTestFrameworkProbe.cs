namespace NetEvolve.FrameShift.TestSurface;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

/// <summary>
/// Detects NUnit in a compilation. The framework lives in the assembly <c>nunit.framework</c> and marks
/// its tests with an attribute implementing <c>NUnit.Framework.Interfaces.ISimpleTestBuilder</c> or
/// <c>NUnit.Framework.Interfaces.ITestBuilder</c> — <c>NUnit.Framework.TestAttribute</c> being the most
/// prominent of them.
/// </summary>
/// <remarks>
/// <para>
/// Unlike other frameworks, NUnit has no single test attribute a recogniser could walk towards. Its test
/// attributes are siblings under <c>NUnit.Framework.NUnitAttribute</c>, an attribute base type that says
/// nothing about being a test — <c>SetUpAttribute</c> and <c>TestFixtureAttribute</c> derive from it just
/// as <c>TestAttribute</c> does. What NUnit itself keys on is the pair of builder interfaces, so that is
/// what the probe resolves and what <see cref="NUnitTestMethodRecognizer" /> matches.
/// </para>
/// <para>
/// The well-known attribute types are still resolved, but only to decide whether NUnit is present at
/// all. They are deliberately not the marker: the set of attributes implementing the builder interfaces
/// is larger than any list of names would be.
/// </para>
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

    /// <summary>
    /// The metadata name of the interface an attribute implements to build a single test from a method.
    /// </summary>
    internal const string SimpleTestBuilderInterfaceMetadataName = "NUnit.Framework.Interfaces.ISimpleTestBuilder";

    /// <summary>
    /// The metadata name of the interface an attribute implements to build one or more test cases from a
    /// method.
    /// </summary>
    internal const string TestBuilderInterfaceMetadataName = "NUnit.Framework.Interfaces.ITestBuilder";

    private const string AssemblyPrefix = "nunit";

    /// <summary>
    /// The metadata names of the well-known test attributes of the framework, whose presence establishes
    /// that a compilation is an NUnit one. They are not the full set of attributes that mark a method as a
    /// test — <c>TheoryAttribute</c> and the combining strategies do so as well — and are therefore never
    /// used to judge a single method.
    /// </summary>
    internal static readonly ImmutableArray<string> TestAttributeMetadataNames =
    [
        TestAttributeMetadataName,
        TestCaseAttributeMetadataName,
        TestCaseSourceAttributeMetadataName,
    ];

    /// <summary>
    /// The metadata names of the interfaces whose implementation makes an attribute mark a method as an
    /// NUnit test.
    /// </summary>
    internal static readonly ImmutableArray<string> TestBuilderInterfaceMetadataNames =
    [
        SimpleTestBuilderInterfaceMetadataName,
        TestBuilderInterfaceMetadataName,
    ];

    /// <summary>
    /// The simple type names an interface has to carry to be recognised by name. They are matched only in
    /// combination with a declaring assembly that belongs to the framework.
    /// </summary>
    internal static readonly ImmutableArray<string> TestBuilderInterfaceTypeNames =
    [
        "ISimpleTestBuilder",
        "ITestBuilder",
    ];

    /// <summary>
    /// Gets the shared instance of the probe, which is stateless and therefore safe to reuse.
    /// </summary>
    public static ITestFrameworkProbe Instance { get; } = new NUnitTestFrameworkProbe();

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

        var testAttributeTypes = GetTestAttributeTypes(compilation);

        if (testAttributeTypes.IsEmpty && !ReferencesFrameworkAssembly(compilation))
        {
            return null;
        }

        return new NUnitTestMethodRecognizer(GetTestBuilderInterfaceTypes(compilation));
    }

    /// <summary>
    /// Resolves the NUnit test-builder interfaces of <paramref name="compilation" />, which is empty if
    /// the compilation does not reference NUnit, and also for any name that is declared more than once and
    /// therefore ambiguous.
    /// </summary>
    /// <param name="compilation">The compilation to resolve the types in.</param>
    /// <returns>The resolved interface types, which may be empty.</returns>
    internal static ImmutableArray<INamedTypeSymbol> GetTestBuilderInterfaceTypes(Compilation compilation) =>
        TestBuilderInterfaceMetadataNames
            .Select(compilation.GetTypeByMetadataName)
            .Where(type => type is not null)
            .Select(type => type!)
            .ToImmutableArray();

    /// <summary>
    /// Resolves the well-known NUnit test attribute types of <paramref name="compilation" />, which is
    /// empty if the compilation does not reference NUnit. They establish that NUnit is present; the
    /// decision whether a single attribute marks a test is taken on the builder interfaces instead, by
    /// <see cref="NUnitTestMethodRecognizer" />.
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
