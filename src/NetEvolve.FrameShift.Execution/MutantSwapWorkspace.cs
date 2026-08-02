namespace NetEvolve.FrameShift.Execution;

/// <summary>
/// A throwaway copy of a test project's real build output directory, with the production assembly
/// overwritten by one mutant. This is what makes execution-based verification work against a real test
/// host instead of only against a hand-invoked method: the already-compiled test assembly keeps
/// resolving its reference to the production assembly by file name, so it binds to the mutant the moment
/// the mutant's bytes sit at that path, without the test assembly ever being recompiled or even aware
/// anything changed.
/// </summary>
internal sealed class MutantSwapWorkspace : IDisposable
{
    private MutantSwapWorkspace(string directory) => Directory = directory;

    /// <summary>
    /// Gets the path of the temporary directory holding the swapped-in copy.
    /// </summary>
    public string Directory { get; }

    /// <summary>
    /// Copies <paramref name="testOutputDirectory" /> to a new temporary directory and overwrites the
    /// file named <paramref name="productionAssemblyFileName" /> in it with
    /// <paramref name="mutantAssemblyBytes" />.
    /// </summary>
    /// <param name="testOutputDirectory">
    /// The build output directory of the test project, containing the already-compiled test assembly and
    /// every assembly it depends on, including the unmutated production assembly.
    /// </param>
    /// <param name="productionAssemblyFileName">
    /// The file name (not a path) of the production assembly inside <paramref name="testOutputDirectory" />,
    /// e.g. <c>MyApp.dll</c>.
    /// </param>
    /// <param name="mutantAssemblyBytes">The mutant assembly image to write in its place.</param>
    /// <returns>The prepared workspace. The caller owns it and must dispose it to remove the copy.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="testOutputDirectory" />, <paramref name="productionAssemblyFileName" /> or
    /// <paramref name="mutantAssemblyBytes" /> is <see langword="null" />.
    /// </exception>
    public static MutantSwapWorkspace Prepare(
        string testOutputDirectory,
        string productionAssemblyFileName,
        byte[] mutantAssemblyBytes
    )
    {
        ArgumentNullException.ThrowIfNull(testOutputDirectory);
        ArgumentNullException.ThrowIfNull(productionAssemblyFileName);
        ArgumentNullException.ThrowIfNull(mutantAssemblyBytes);

        var workspace = System.IO.Directory.CreateTempSubdirectory("frameshift-mutant-");

        try
        {
            CopyDirectory(testOutputDirectory, workspace.FullName);

            var targetPath = Path.Combine(workspace.FullName, productionAssemblyFileName);
            File.WriteAllBytes(targetPath, mutantAssemblyBytes);

            return new MutantSwapWorkspace(workspace.FullName);
        }
        catch
        {
            // A failure while preparing the copy must not leave a half-written temporary directory
            // behind for every mutant a caller iterates over.
            TryDelete(workspace.FullName);
            throw;
        }
    }

    /// <summary>
    /// Removes the temporary copy.
    /// </summary>
    public void Dispose() => TryDelete(Directory);

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        foreach (var filePath in System.IO.Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, filePath);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            var destinationFileDirectory = Path.GetDirectoryName(destinationPath);

            if (!string.IsNullOrEmpty(destinationFileDirectory))
            {
                _ = System.IO.Directory.CreateDirectory(destinationFileDirectory);
            }

            File.Copy(filePath, destinationPath, overwrite: true);
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup: a file a just-exited process has not fully released yet must not
            // fail the caller that already has its verdict. The directory is left for the OS temp
            // cleanup to reclaim eventually.
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning as above.
        }
    }
}
