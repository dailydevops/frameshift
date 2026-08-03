namespace NetEvolve.FrameShift.Execution.Tests.Integration.Infrastructure;

using System.Globalization;
using System.Runtime.CompilerServices;

/// <summary>
/// Configures Verify for this test assembly: the culture and where snapshots live.
/// </summary>
/// <remarks>
/// Unlike the main product's test projects, this one targets net10.0 only, so there is no framework
/// matrix to normalise a snapshot against - no runtime directory, target framework moniker or assembly
/// version scrubbing is needed here.
/// </remarks>
internal static class VerifyModuleInitializer
{
    private const string SnapshotDirectoryName = "_snapshots";
    private const string CultureName = "en-US";

    /// <summary>
    /// Applies every Verify setting of this assembly before the first test runs.
    /// </summary>
    [ModuleInitializer]
    public static void Init()
    {
        PinCulture();
        ConfigureSnapshotPaths();
        ConfigureComparison();
    }

    private static void PinCulture()
    {
        var culture = CultureInfo.GetCultureInfo(CultureName);

        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    private static void ConfigureSnapshotPaths() =>
        DerivePathInfo(
            (sourceFile, projectDirectory, type, method) =>
                new PathInfo(CreateSnapshotDirectory(sourceFile, projectDirectory), type.Name, method.Name)
        );

    private static void ConfigureComparison()
    {
        VerifierSettings.AutoVerify(includeBuildServer: false, throwException: true);
        VerifierSettings.SortJsonObjects();
        VerifierSettings.SortPropertiesAlphabetically();
    }

    private static string CreateSnapshotDirectory(string sourceFile, string projectDirectory)
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFile) ?? projectDirectory;
        var relativePath = sourceDirectory.StartsWith(projectDirectory, StringComparison.OrdinalIgnoreCase)
            ? sourceDirectory.Substring(projectDirectory.Length).Trim('\\', '/')
            : string.Empty;

        var directory = Path.Combine(projectDirectory, SnapshotDirectoryName, relativePath);

        return Directory.CreateDirectory(directory).FullName;
    }
}
