namespace NetEvolve.FrameShift.Execution.Tests.Integration;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

/// <summary>
/// Pins the contract the execution CLI's reference collection relies on: every reference resolves to a
/// file that actually exists, at least the core library the executing runtime is built on is included,
/// and every reference sits in the very same directory the executing runtime's core library lives in.
/// </summary>
public class RuntimeAssemblyReferencesTests
{
    [Test]
    public async Task Shared_IsNotEmpty() => _ = await Assert.That(RuntimeAssemblyReferences.Shared).IsNotEmpty();

    [Test]
    public async Task Shared_EveryReference_ResolvesToAnExistingFile()
    {
        var missing = RuntimeAssemblyReferences.Shared.Where(reference => !File.Exists(reference.Display));

        _ = await Assert.That(missing).IsEmpty();
    }

    [Test]
    public async Task Shared_IncludesTheExecutingCoreLibrary()
    {
        var coreLibraryPath = typeof(object).Assembly.Location;

        var matches = RuntimeAssemblyReferences.Shared.Any(reference =>
            string.Equals(reference.Display, coreLibraryPath, StringComparison.OrdinalIgnoreCase)
        );

        _ = await Assert.That(matches).IsTrue();
    }

    [Test]
    public async Task Shared_EveryReference_SitsInTheRuntimeDirectory()
    {
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);

        var mismatched = RuntimeAssemblyReferences.Shared.Where(reference =>
            !string.Equals(
                Path.GetDirectoryName(reference.Display),
                runtimeDirectory,
                StringComparison.OrdinalIgnoreCase
            )
        );

        _ = await Assert.That(mismatched).IsEmpty();
    }

    [Test]
    public async Task Shared_CalledTwice_ReturnsTheSameCachedInstance()
    {
        var first = RuntimeAssemblyReferences.Shared;
        var second = RuntimeAssemblyReferences.Shared;

        _ = await Assert.That(first).IsEqualTo(second);
    }

    [Test]
    public async Task Shared_ContainsNoDuplicateFileNames()
    {
        var fileNames = RuntimeAssemblyReferences
            .Shared.Select(reference => Path.GetFileName(reference.Display ?? string.Empty))
            .ToImmutableArray();

        var duplicateCount = fileNames.Length - fileNames.Distinct(StringComparer.OrdinalIgnoreCase).Count();

        _ = await Assert.That(duplicateCount).IsEqualTo(0);
    }
}
