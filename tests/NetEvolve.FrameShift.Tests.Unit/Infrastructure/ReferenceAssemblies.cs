namespace NetEvolve.FrameShift.Tests.Infrastructure;

using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using MSTestFramework = Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The metadata references every test compilation is built against.
/// </summary>
/// <remarks>
/// <para>
/// The references are taken from the assemblies the current process already trusts, which the host
/// publishes as <c>TRUSTED_PLATFORM_ASSEMBLIES</c>. That list contains the runtime of the executing
/// framework <em>and</em> every package assembly of the test project, so it is narrowed down to the
/// directory the runtime itself lives in. Without that filter every test compilation would silently
/// reference TUnit and the analyzer assembly, and the test-side analyzer classifies a compilation as a
/// test assembly by exactly that reference; tests asserting that it stays silent could then never fail.
/// </para>
/// <para>
/// A test framework is therefore added back explicitly, and always from the real package assemblies:
/// the framework adapters are recognised by metadata names and base types, so a hand-written look-alike
/// would hide exactly the defects these tests exist to catch. Every set is seeded with a type that only
/// its own framework declares, and the assembly reference graph is walked from there.
/// </para>
/// <para>
/// Building a <see cref="MetadataReference" /> reads and maps a file, therefore every set is computed
/// once per process and shared by every compilation.
/// </para>
/// </remarks>
internal static class ReferenceAssemblies
{
    private const string TrustedPlatformAssembliesKey = "TRUSTED_PLATFORM_ASSEMBLIES";
    private const string AssemblyExtension = ".dll";

    private static readonly Lazy<ImmutableArray<string>> _frameworkPaths = new Lazy<ImmutableArray<string>>(
        GetFrameworkPaths,
        LazyThreadSafetyMode.ExecutionAndPublication
    );

    private static readonly Lazy<ImmutableArray<MetadataReference>> _default = new Lazy<
        ImmutableArray<MetadataReference>
    >(CreateDefault, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<ImmutableArray<MetadataReference>> _withTUnit = new Lazy<
        ImmutableArray<MetadataReference>
    >(CreateWithTUnit, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<ImmutableArray<MetadataReference>> _withXunitV3 = new Lazy<
        ImmutableArray<MetadataReference>
    >(CreateWithXunitV3, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<ImmutableArray<MetadataReference>> _withXunitV2 = new Lazy<
        ImmutableArray<MetadataReference>
    >(CreateWithXunitV2, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<ImmutableArray<MetadataReference>> _withNUnit = new Lazy<
        ImmutableArray<MetadataReference>
    >(CreateWithNUnit, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<ImmutableArray<MetadataReference>> _withMSTest = new Lazy<
        ImmutableArray<MetadataReference>
    >(CreateWithMSTest, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<ImmutableArray<MetadataReference>> _withAllFrameworks = new Lazy<
        ImmutableArray<MetadataReference>
    >(CreateWithAllFrameworks, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Gets the runtime references of the currently executing framework, deduplicated by path.
    /// </summary>
    public static ImmutableArray<MetadataReference> Default => _default.Value;

    /// <summary>
    /// Gets <see cref="Default" /> plus the assembly declaring <c>TUnit.Core.TestAttribute</c> and every
    /// assembly it transitively needs, so that a fixture can carry real <c>[Test]</c> methods.
    /// </summary>
    public static ImmutableArray<MetadataReference> WithTUnit => _withTUnit.Value;

    /// <summary>
    /// Gets <see cref="Default" /> plus the xUnit.net v3 assemblies, so that a fixture can carry real
    /// <c>[Fact]</c> and <c>[Theory]</c> methods of that version.
    /// </summary>
    public static ImmutableArray<MetadataReference> WithXunitV3 => _withXunitV3.Value;

    /// <summary>
    /// Gets <see cref="Default" /> plus the xUnit.net v2 assemblies, so that a fixture can carry real
    /// <c>[Fact]</c> and <c>[Theory]</c> methods of that version.
    /// </summary>
    public static ImmutableArray<MetadataReference> WithXunitV2 => _withXunitV2.Value;

    /// <summary>
    /// Gets <see cref="Default" /> plus the NUnit assemblies, so that a fixture can carry real
    /// <c>[Test]</c> and <c>[TestCase]</c> methods.
    /// </summary>
    public static ImmutableArray<MetadataReference> WithNUnit => _withNUnit.Value;

    /// <summary>
    /// Gets <see cref="Default" /> plus the MSTest assemblies, so that a fixture can carry real
    /// <c>[TestMethod]</c> and <c>[DataRow]</c> methods.
    /// </summary>
    public static ImmutableArray<MetadataReference> WithMSTest => _withMSTest.Value;

    /// <summary>
    /// Gets <see cref="Default" /> plus every supported test framework at once, which is what a test
    /// proving that one adapter does not answer for another framework needs.
    /// </summary>
    /// <remarks>
    /// The two xUnit.net versions declare the very same type names in the very same namespaces. A
    /// fixture built against this set therefore cannot spell out a name such as <c>Xunit.FactAttribute</c>,
    /// and <see cref="Compilation.GetTypeByMetadataName(string)" /> returns <see langword="null" /> for it;
    /// only <c>GetTypesByMetadataName</c> sees both declarations.
    /// </remarks>
    public static ImmutableArray<MetadataReference> WithAllFrameworks => _withAllFrameworks.Value;

    /// <summary>
    /// Selects <see cref="WithTUnit" /> or <see cref="Default" />.
    /// </summary>
    /// <param name="includeTUnit">Whether the TUnit assemblies are part of the result.</param>
    /// <returns>The selected references.</returns>
    public static ImmutableArray<MetadataReference> For(bool includeTUnit) =>
        For(includeTUnit ? TestFramework.TUnit : TestFramework.None);

    /// <summary>
    /// Selects the reference set of <paramref name="framework" />.
    /// </summary>
    /// <param name="framework">The test framework the compilation is built for.</param>
    /// <returns>The selected references.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="framework" /> is not a known value.</exception>
    public static ImmutableArray<MetadataReference> For(TestFramework framework) =>
        framework switch
        {
            TestFramework.None => Default,
            TestFramework.TUnit => WithTUnit,
            TestFramework.XunitV3 => WithXunitV3,
            TestFramework.XunitV2 => WithXunitV2,
            TestFramework.NUnit => WithNUnit,
            TestFramework.MSTest => WithMSTest,
            TestFramework.All => WithAllFrameworks,
            _ => throw new ArgumentOutOfRangeException(
                nameof(framework),
                framework,
                "There is no reference set for this test framework."
            ),
        };

    /// <summary>
    /// Creates a reference to the assembly declaring <paramref name="type" />, for the rare fixture that
    /// needs a type from a package assembly.
    /// </summary>
    /// <param name="type">The type whose declaring assembly is referenced.</param>
    /// <returns>The created reference.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type" /> is <see langword="null" />.</exception>
    /// <exception cref="InvalidOperationException">The declaring assembly has no file on disk.</exception>
    public static MetadataReference Of(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var location = type.Assembly.Location;

        if (location.Length == 0)
        {
            throw new InvalidOperationException(
                $"The assembly declaring '{type.FullName}' is not backed by a file and cannot be referenced."
            );
        }

        return MetadataReference.CreateFromFile(location);
    }

    private static ImmutableArray<MetadataReference> CreateDefault() => CreateReferences(_frameworkPaths.Value);

    private static ImmutableArray<MetadataReference> CreateWithTUnit() => CreateWith(TUnitAnchors);

    private static ImmutableArray<MetadataReference> CreateWithXunitV3() => CreateWith(XunitV3Anchors);

    private static ImmutableArray<MetadataReference> CreateWithXunitV2() => CreateWith(XunitV2Anchors);

    private static ImmutableArray<MetadataReference> CreateWithNUnit() => CreateWith(NUnitAnchors);

    private static ImmutableArray<MetadataReference> CreateWithMSTest() => CreateWith(MSTestAnchors);

    private static ImmutableArray<MetadataReference> CreateWithAllFrameworks() =>
        CreateWith([.. TUnitAnchors, .. XunitV3Anchors, .. XunitV2Anchors, .. NUnitAnchors, .. MSTestAnchors]);

    /// <summary>
    /// The seed of the TUnit reference set.
    /// </summary>
    private static Type[] TUnitAnchors => [typeof(TUnit.Core.TestAttribute)];

    /// <summary>
    /// The seed of the xUnit.net v3 reference set. <c>Xunit.v3.XunitTestFramework</c> lives in
    /// <c>xunit.v3.core</c> and has no counterpart in v2, so it stays unambiguous when both versions are
    /// referenced by this test project at once.
    /// </summary>
    private static Type[] XunitV3Anchors => [typeof(Xunit.v3.XunitTestFramework)];

    /// <summary>
    /// The seed of the xUnit.net v2 reference set. <c>Xunit.Sdk.IXunitTestCase</c> lives in
    /// <c>xunit.core</c> and has no counterpart in v3, so it stays unambiguous when both versions are
    /// referenced by this test project at once.
    /// </summary>
    private static Type[] XunitV2Anchors => [typeof(Xunit.Sdk.IXunitTestCase)];

    /// <summary>
    /// The seed of the NUnit reference set.
    /// </summary>
    private static Type[] NUnitAnchors => [typeof(NUnit.Framework.TestAttribute)];

    /// <summary>
    /// The seed of the MSTest reference set.
    /// </summary>
    private static Type[] MSTestAnchors => [typeof(MSTestFramework.TestMethodAttribute)];

    /// <summary>
    /// Builds <see cref="Default" /> plus the assembly graph reachable from every anchor.
    /// </summary>
    /// <param name="anchors">The types whose declaring assemblies seed the walk.</param>
    /// <returns>The created references.</returns>
    private static ImmutableArray<MetadataReference> CreateWith(Type[] anchors) =>
        CreateReferences([.. _frameworkPaths.Value, .. GetPackagePaths(anchors)]);

    /// <summary>
    /// Maps every path to a reference, skipping paths that were already mapped.
    /// </summary>
    /// <param name="paths">The assembly paths to map, in the order they should be referenced.</param>
    /// <returns>The created references.</returns>
    private static ImmutableArray<MetadataReference> CreateReferences(IEnumerable<string> paths)
    {
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            builder.Add(MetadataReference.CreateFromFile(path));
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Selects the trusted platform assemblies that live next to the runtime itself.
    /// </summary>
    /// <returns>
    /// The runtime assembly paths, or all trusted platform assemblies when the runtime directory cannot
    /// be determined, which happens for a single-file host.
    /// </returns>
    private static ImmutableArray<string> GetFrameworkPaths()
    {
        var all = GetTrustedPlatformAssemblies();
        var location = typeof(object).Assembly.Location;
        var runtimeDirectory = location.Length == 0 ? null : Path.GetDirectoryName(location);
        var framework = all.Where(path => IsInDirectory(path, runtimeDirectory)).ToImmutableArray();

        return framework.IsEmpty ? all : framework;
    }

    /// <summary>
    /// Reads and splits the trusted platform assemblies of the current process.
    /// </summary>
    /// <returns>The paths of all trusted assemblies that are managed libraries.</returns>
    /// <exception cref="InvalidOperationException">The host did not publish the list.</exception>
    private static ImmutableArray<string> GetTrustedPlatformAssemblies()
    {
        if (AppContext.GetData(TrustedPlatformAssembliesKey) is not string value || value.Length == 0)
        {
            throw new InvalidOperationException(
                $"The current process does not publish '{TrustedPlatformAssembliesKey}', "
                    + "so no reference assemblies can be resolved."
            );
        }

        return
        [
            .. value
                .Split(Path.PathSeparator)
                .Where(path => path.EndsWith(AssemblyExtension, StringComparison.OrdinalIgnoreCase)),
        ];
    }

    private static bool IsInDirectory(string path, string? directory) =>
        directory is { Length: > 0 }
        && string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Walks the assembly reference graph of every anchor's declaring assembly, so that the anchor types
    /// and every type they need can be bound by a test compilation.
    /// </summary>
    /// <param name="anchors">The types whose declaring assemblies seed the walk.</param>
    /// <returns>The paths of the reachable assemblies that are backed by a file.</returns>
    /// <exception cref="InvalidOperationException">An anchor assembly is not backed by a file.</exception>
    private static ImmutableArray<string> GetPackagePaths(Type[] anchors)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<Assembly>();
        var builder = ImmutableArray.CreateBuilder<string>();

        foreach (var anchor in anchors)
        {
            pending.Enqueue(GetAnchorAssembly(anchor));
        }

        while (pending.Count > 0)
        {
            var assembly = pending.Dequeue();

            if (!visited.Add(assembly.FullName ?? assembly.ToString()))
            {
                continue;
            }

            if (assembly.Location.Length > 0)
            {
                builder.Add(assembly.Location);
            }

            EnqueueReferences(assembly, pending);
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Resolves the assembly of an anchor type and proves it is backed by a file, because a reference set
    /// that silently lost its framework would turn every test using it into a false pass.
    /// </summary>
    /// <param name="anchor">The type whose declaring assembly seeds the walk.</param>
    /// <returns>The declaring assembly.</returns>
    /// <exception cref="InvalidOperationException">The declaring assembly has no file on disk.</exception>
    private static Assembly GetAnchorAssembly(Type anchor)
    {
        var assembly = anchor.Assembly;

        if (assembly.Location.Length == 0)
        {
            throw new InvalidOperationException(
                $"The assembly declaring '{anchor.FullName}' is not backed by a file and cannot be referenced."
            );
        }

        return assembly;
    }

    private static void EnqueueReferences(Assembly assembly, Queue<Assembly> pending)
    {
        foreach (var name in assembly.GetReferencedAssemblies())
        {
            var referenced = TryLoad(name);

            if (referenced is not null)
            {
                pending.Enqueue(referenced);
            }
        }
    }

    private static Assembly? TryLoad(AssemblyName name)
    {
        try
        {
            return Assembly.Load(name);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (FileLoadException)
        {
            return null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }
}
