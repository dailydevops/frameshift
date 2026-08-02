namespace NetEvolve.FrameShift.Execution.Tests.Integration;

/// <summary>
/// Exercises <see cref="MutantSwapWorkspace" /> as a plain file-system operation, independent of whether
/// the copied files are ever actually executed: nested directories are copied, an existing file at the
/// swap target is genuinely overwritten, and disposal is idempotent and tolerant of a copy already gone.
/// </summary>
public class MutantSwapWorkspaceTests
{
    private const string ProductionAssemblyFileName = "Production.dll";

    [Test]
    public async Task Prepare_NestedDirectories_CopiesEveryFile()
    {
        var sourceDirectory = Directory.CreateTempSubdirectory("frameshift-swap-source-");

        try
        {
            var nestedDirectory = Directory.CreateDirectory(Path.Combine(sourceDirectory.FullName, "nested"));
            await File.WriteAllBytesAsync(Path.Combine(sourceDirectory.FullName, ProductionAssemblyFileName), [1, 2, 3])
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(nestedDirectory.FullName, "dependency.txt"), "content")
                .ConfigureAwait(false);

            var mutantBytes = new byte[] { 9, 9, 9 };

            using var workspace = MutantSwapWorkspace.Prepare(
                sourceDirectory.FullName,
                ProductionAssemblyFileName,
                mutantBytes
            );

            var copiedNestedFile = Path.Combine(workspace.Directory, "nested", "dependency.txt");
            var copiedProductionAssembly = Path.Combine(workspace.Directory, ProductionAssemblyFileName);

            using (Assert.Multiple())
            {
                _ = await Assert.That(File.Exists(copiedNestedFile)).IsTrue();
                _ = await Assert
                    .That(await File.ReadAllTextAsync(copiedNestedFile).ConfigureAwait(false))
                    .IsEqualTo("content");
                _ = await Assert
                    .That(await File.ReadAllBytesAsync(copiedProductionAssembly).ConfigureAwait(false))
                    .IsEquivalentTo(mutantBytes);
            }
        }
        finally
        {
            Directory.Delete(sourceDirectory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task Prepare_ProductionAssemblyAlreadyExists_OverwritesIt()
    {
        var sourceDirectory = Directory.CreateTempSubdirectory("frameshift-swap-source-");

        try
        {
            var originalBytes = new byte[] { 1, 1, 1 };
            await File.WriteAllBytesAsync(
                    Path.Combine(sourceDirectory.FullName, ProductionAssemblyFileName),
                    originalBytes
                )
                .ConfigureAwait(false);

            var mutantBytes = new byte[] { 2, 2, 2, 2 };

            using var workspace = MutantSwapWorkspace.Prepare(
                sourceDirectory.FullName,
                ProductionAssemblyFileName,
                mutantBytes
            );

            var swappedBytes = await File.ReadAllBytesAsync(
                    Path.Combine(workspace.Directory, ProductionAssemblyFileName)
                )
                .ConfigureAwait(false);

            _ = await Assert.That(swappedBytes).IsEquivalentTo(mutantBytes);
        }
        finally
        {
            Directory.Delete(sourceDirectory.FullName, recursive: true);
        }
    }

    [Test]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var sourceDirectory = Directory.CreateTempSubdirectory("frameshift-swap-source-");

        try
        {
            var workspace = MutantSwapWorkspace.Prepare(sourceDirectory.FullName, ProductionAssemblyFileName, [1]);

            workspace.Dispose();
            workspace.Dispose();
        }
        finally
        {
            Directory.Delete(sourceDirectory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task Dispose_RemovesTheTemporaryCopy()
    {
        var sourceDirectory = Directory.CreateTempSubdirectory("frameshift-swap-source-");
        string workspaceDirectory;

        try
        {
            var workspace = MutantSwapWorkspace.Prepare(sourceDirectory.FullName, ProductionAssemblyFileName, [1]);
            workspaceDirectory = workspace.Directory;

            workspace.Dispose();
        }
        finally
        {
            Directory.Delete(sourceDirectory.FullName, recursive: true);
        }

        _ = await Assert.That(Directory.Exists(workspaceDirectory)).IsFalse();
    }

    [Test]
    public async Task Prepare_SourceDirectoryDoesNotExist_PropagatesTheFailure()
    {
        var missingSourceDirectory = Path.Combine(Path.GetTempPath(), "frameshift-swap-missing-" + Guid.NewGuid());

        _ = await Assert
            .That(() => MutantSwapWorkspace.Prepare(missingSourceDirectory, ProductionAssemblyFileName, [1]))
            .Throws<DirectoryNotFoundException>();
    }

    [Test]
    public void Dispose_DirectoryAlreadyRemovedExternally_DoesNotThrow()
    {
        var sourceDirectory = Directory.CreateTempSubdirectory("frameshift-swap-source-");

        try
        {
            var workspace = MutantSwapWorkspace.Prepare(sourceDirectory.FullName, ProductionAssemblyFileName, [1]);

            Directory.Delete(workspace.Directory, recursive: true);

            workspace.Dispose();
        }
        finally
        {
            Directory.Delete(sourceDirectory.FullName, recursive: true);
        }
    }
}
