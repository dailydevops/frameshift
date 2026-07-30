namespace NetEvolve.Frameshift.Tests.Infrastructure;

using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;

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
/// Building a <see cref="MetadataReference" /> reads and maps a file, therefore both sets are computed
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
    /// Selects <see cref="WithTUnit" /> or <see cref="Default" />.
    /// </summary>
    /// <param name="includeTUnit">Whether the TUnit assemblies are part of the result.</param>
    /// <returns>The selected references.</returns>
    public static ImmutableArray<MetadataReference> For(bool includeTUnit) => includeTUnit ? WithTUnit : Default;

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

    private static ImmutableArray<MetadataReference> CreateWithTUnit() =>
        CreateReferences([.. _frameworkPaths.Value, .. GetTestFrameworkPaths()]);

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
    /// Walks the assembly reference graph of the assembly declaring <c>TUnit.Core.TestAttribute</c>, so
    /// that the attribute type and every type it needs can be bound by a test compilation.
    /// </summary>
    /// <returns>The paths of the reachable assemblies that are backed by a file.</returns>
    private static ImmutableArray<string> GetTestFrameworkPaths()
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<Assembly>();
        var builder = ImmutableArray.CreateBuilder<string>();

        pending.Enqueue(GetTestFrameworkAssembly());

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

    private static Assembly GetTestFrameworkAssembly() => typeof(TUnit.Core.TestAttribute).Assembly;

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
