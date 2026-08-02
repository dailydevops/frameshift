namespace NetEvolve.FrameShift.Execution;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

/// <summary>
/// The base class library references the CLI's own compilation needs, on top of whatever is already
/// sitting in a test project's build output directory.
/// </summary>
/// <remarks>
/// A framework-dependent build output directory never contains the shared framework assemblies
/// themselves - <c>System.Private.CoreLib</c>, <c>System.Runtime</c> and the rest are resolved from the
/// installed shared runtime at execution time, not copied next to the application. Recompiling a
/// production source file from scratch therefore needs them named explicitly, taken from the very
/// runtime this CLI itself is executing on. That is only accurate when the CLI runs on the same major
/// runtime version the target project builds for, which is the common case for this repository's own
/// net10.0-only tooling but not a general guarantee - a mismatch would show up as the recompiled
/// production assembly failing to bind types the original project's own build had no trouble with.
/// </remarks>
internal static class RuntimeAssemblyReferences
{
    private const string TrustedPlatformAssembliesKey = "TRUSTED_PLATFORM_ASSEMBLIES";
    private const string AssemblyExtension = ".dll";

    private static readonly Lazy<ImmutableArray<MetadataReference>> _shared = new Lazy<
        ImmutableArray<MetadataReference>
    >(CreateShared, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Gets the shared framework assemblies of the runtime this process is executing on.
    /// </summary>
    public static ImmutableArray<MetadataReference> Shared => _shared.Value;

    private static ImmutableArray<MetadataReference> CreateShared()
    {
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);

        var frameworkPaths = GetTrustedPlatformAssemblies()
            .Where(path =>
                string.Equals(Path.GetDirectoryName(path), runtimeDirectory, StringComparison.OrdinalIgnoreCase)
            )
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return [.. frameworkPaths.Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))];
    }

    private static ImmutableArray<string> GetTrustedPlatformAssemblies()
    {
        if (AppContext.GetData(TrustedPlatformAssembliesKey) is not string value || value.Length == 0)
        {
            throw new InvalidOperationException(
                $"The current process does not publish '{TrustedPlatformAssembliesKey}', "
                    + "so no shared framework references can be resolved."
            );
        }

        return
        [
            .. value
                .Split(Path.PathSeparator)
                .Where(path => path.EndsWith(AssemblyExtension, StringComparison.OrdinalIgnoreCase)),
        ];
    }
}
