namespace NetEvolve.FrameShift.Tests.Execution;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

/// <summary>
/// The metadata references a mutant compilation of this test project needs to actually run, as opposed
/// to only binding: <c>NetEvolve.FrameShift.Tests.Unit</c>'s reference sets exist to build analyzer test
/// fixtures and are internal to that project, so this is the minimal, CoreCLR-only equivalent this
/// project needs for its own dogfood fixtures.
/// </summary>
internal static class RuntimeReferences
{
    private const string TrustedPlatformAssembliesKey = "TRUSTED_PLATFORM_ASSEMBLIES";
    private const string AssemblyExtension = ".dll";

    private static readonly Lazy<ImmutableArray<MetadataReference>> _default = new Lazy<
        ImmutableArray<MetadataReference>
    >(CreateDefault, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Gets the runtime assemblies of the executing framework, so that a compiled fixture can be emitted
    /// and actually loaded and run, not just bound.
    /// </summary>
    public static ImmutableArray<MetadataReference> Default => _default.Value;

    private static ImmutableArray<MetadataReference> CreateDefault()
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
}
