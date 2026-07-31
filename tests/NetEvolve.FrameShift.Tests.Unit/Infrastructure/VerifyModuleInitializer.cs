namespace NetEvolve.FrameShift.Tests.Infrastructure;

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

/// <summary>
/// Configures Verify for a whole test assembly: the culture, where snapshots live and how the parts of a
/// snapshot that belong to the executing target framework rather than to the code under test are
/// normalised.
/// </summary>
/// <remarks>
/// <para>
/// <b>One file, two assemblies.</b> This file is compiled into the unit test project and linked into the
/// integration test project, so the very same module initializer exists once in each of the two
/// assemblies. That is correct and not a double registration: a module initializer runs once per module,
/// each test project is its own executable with its own process, and inside one process only one copy of
/// the type exists. The settings it touches are static state of the single <c>Verify</c> assembly loaded
/// into that process, therefore each assembly configures exactly its own run. The only thing the two
/// assemblies must not share is the snapshot directory, which is why the path derivation below is
/// relative to the project that owns the <em>source file</em> and combined with the project directory of
/// the <em>running assembly</em>: the smoke tests of this folder resolve to
/// <c>&lt;each project&gt;\_snapshots\Infrastructure</c> and never to one shared file two runs would
/// fight over.
/// </para>
/// <para>
/// <b>Eight target frameworks, one snapshot.</b> The test projects run on net6.0, net7.0, net8.0,
/// net9.0, net10.0 and, on Windows, on net472, net48 and net481 — in parallel, against the same
/// <c>.verified.txt</c>. Everything a snapshot inherits from the executing framework is therefore
/// scrubbed into a fixed token: runtime directories, target framework monikers, assembly versions,
/// public key tokens, the name of the core library, the effective language version and the temporary
/// directories the harness builds. Every pattern is anchored on the exact shape it normalises, so that a
/// real change in analyzer or generator output can never be swallowed by a scrubber.
/// </para>
/// </remarks>
internal static class VerifyModuleInitializer
{
    /// <summary>
    /// The directory below a test project that holds every snapshot of that project.
    /// </summary>
    public const string SnapshotDirectoryName = "_snapshots";

    private const string CultureName = "en-US";
    private const string ProjectFilePattern = "*.csproj";

    private const string CorlibToken = "{corlib}";
    private const string FrameworkToken = "{framework}";
    private const string TargetFrameworkToken = "{targetFramework}";
    private const string RuntimeDirectoryToken = "{runtimeDirectory}";
    private const string TemporaryDirectoryToken = "{temporaryDirectory}";
    private const string VersionToken = "{version}";
    private const string PublicKeyTokenToken = "{publicKeyToken}";
    private const string LanguageVersionToken = "CSharpLatest";

    /// <summary>
    /// The token Verify itself uses for the temporary directory. The literal replacement below uses the
    /// very same token, so that the result no longer depends on whether Verify replaced the prefix before
    /// or after this scrubber ran.
    /// </summary>
    private const string TempPathToken = "{TempPath}";

    private static readonly char[] _directorySeparators = ['\\', '/'];

    private static readonly TimeSpan _regexTimeout = TimeSpan.FromSeconds(1);

    private static readonly string _runtimeDirectory = GetRuntimeDirectory();

    private static readonly string _temporaryDirectory = Path.GetTempPath();

    /// <summary>
    /// Matches everything a path may contain in front of the part that identifies it: a drive, a root or
    /// one of the tokens Verify puts in front of a path it already recognised, followed by any number of
    /// directory names. A single space is part of a directory name only when a non blank character follows
    /// it, which is what makes <c>C:\Program Files\dotnet</c> match as a whole while the label of a line
    /// such as <c>runtime: C:\…</c> stays outside the match.
    /// </summary>
    private const string PathPrefixPattern =
        @"(?:\{TempPath\}|\{UserProfile\}|\{CurrentDirectory\}|[A-Za-z]:[\\/]|[\\/])(?:[^\s""]|[ ](?=[^\s""]))*?";

    /// <summary>
    /// Matches a shared framework directory of a .NET installation, for example
    /// <c>C:\Program Files\dotnet\shared\Microsoft.NETCore.App\9.0.4</c> or
    /// <c>/usr/lib/dotnet/shared/Microsoft.NETCore.App/10.0.0-rc.2.12345.6</c>.
    /// </summary>
    private static readonly Regex _coreRuntimeDirectoryPattern = new Regex(
        PathPrefixPattern + @"dotnet[\\/]shared[\\/]Microsoft\.NETCore\.App[\\/]\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?",
        RegexOptions.None,
        _regexTimeout
    );

    /// <summary>
    /// Matches the runtime directory of a .NET Framework installation, for example
    /// <c>C:\Windows\Microsoft.NET\Framework64\v4.0.30319</c>.
    /// </summary>
    private static readonly Regex _frameworkRuntimeDirectoryPattern = new Regex(
        PathPrefixPattern + @"Microsoft\.NET[\\/]Framework(?:64)?[\\/]v\d+\.\d+\.\d+",
        RegexOptions.None,
        _regexTimeout
    );

    /// <summary>
    /// Matches a temporary directory of this harness, which is always a folder named
    /// <c>NetEvolve.FrameShift.Tests</c> below the temporary directory plus one random 32 digit name.
    /// Both the random name and the prefix — a literal path or the token Verify replaced it with —
    /// collapse into a single token.
    /// </summary>
    private static readonly Regex _temporaryDirectoryPattern = new Regex(
        PathPrefixPattern + @"NetEvolve\.FrameShift\.Tests[\\/][0-9a-f]{32}",
        RegexOptions.None,
        _regexTimeout
    );

    /// <summary>
    /// Matches a long target framework moniker, for example <c>.NETCoreApp,Version=v9.0</c> or
    /// <c>.NETFramework,Version=v4.7.2</c>.
    /// </summary>
    private static readonly Regex _frameworkNamePattern = new Regex(
        @"\.NET(?:CoreApp|Framework|Standard),Version=v\d+\.\d+(?:\.\d+)?",
        RegexOptions.None,
        _regexTimeout
    );

    /// <summary>
    /// Matches a runtime description, for example <c>.NET 9.0.4</c> or
    /// <c>.NET Framework 4.8.9310.0</c>.
    /// </summary>
    private static readonly Regex _frameworkDescriptionPattern = new Regex(
        @"\.NET(?: Framework| Core)? \d+\.\d+(?:\.\d+){0,2}(?:-[0-9A-Za-z.]+)?",
        RegexOptions.None,
        _regexTimeout
    );

    /// <summary>
    /// Matches a short target framework moniker, for example <c>net10.0</c> or <c>net472</c>, which is
    /// what an output directory of a test project is named after.
    /// </summary>
    private static readonly Regex _shortTargetFrameworkPattern = new Regex(
        @"\bnet(?:\d+\.\d+|4\d{1,2})\b",
        RegexOptions.None,
        _regexTimeout
    );

    /// <summary>
    /// Matches the four part version of an assembly identity, which differs between the core library of
    /// .NET Framework and the one of .NET, and which also carries the build version of this repository.
    /// </summary>
    private static readonly Regex _assemblyVersionPattern = new Regex(
        @"(?<=\bVersion=)\d+\.\d+\.\d+\.\d+",
        RegexOptions.None,
        _regexTimeout
    );

    /// <summary>
    /// Matches the public key token of an assembly identity, which differs between the core library of
    /// .NET Framework and the one of .NET.
    /// </summary>
    private static readonly Regex _publicKeyTokenPattern = new Regex(
        @"(?<=\bPublicKeyToken=)(?:[0-9a-fA-F]{16}|null)",
        RegexOptions.None,
        _regexTimeout
    );

    /// <summary>
    /// Matches the name of the core library, which is <c>mscorlib</c> on .NET Framework and
    /// <c>System.Private.CoreLib</c> on .NET.
    /// </summary>
    private static readonly Regex _corlibNamePattern = new Regex(
        @"\b(?:System\.Private\.CoreLib|mscorlib)\b",
        RegexOptions.None,
        _regexTimeout
    );

    /// <summary>
    /// Matches an effective language version such as <c>CSharp13</c>, which follows the Roslyn version
    /// the analyzer is compiled against rather than anything under test.
    /// </summary>
    private static readonly Regex _languageVersionPattern = new Regex(
        @"\bCSharp\d+(?:_\d+)?\b",
        RegexOptions.None,
        _regexTimeout
    );

    /// <summary>
    /// Applies every Verify setting of this assembly before the first test runs.
    /// </summary>
    [ModuleInitializer]
    public static void Init()
    {
        PinCulture();
        ConfigureSnapshotPaths();
        ConfigureComparison();
        RegisterScrubbers();
    }

    /// <summary>
    /// Pins the culture of the whole test run, so that no snapshot depends on the locale of the machine
    /// that produced it.
    /// </summary>
    private static void PinCulture()
    {
        var culture = CultureInfo.GetCultureInfo(CultureName);

        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    /// <summary>
    /// Puts every snapshot below <c>_snapshots</c> inside the project directory of the running assembly,
    /// mirroring the directory structure of the source file the test is declared in.
    /// </summary>
    private static void ConfigureSnapshotPaths() =>
        DerivePathInfo(
            (sourceFile, projectDirectory, type, method) =>
                new PathInfo(CreateSnapshotDirectory(sourceFile, projectDirectory), type.Name, method.Name)
        );

    /// <summary>
    /// Turns a first run into a written snapshot and a failing test instead of a pending diff, and makes
    /// the serialized shape of a snapshot independent of member and property order.
    /// </summary>
    private static void ConfigureComparison()
    {
        VerifierSettings.AutoVerify(includeBuildServer: false, throwException: true);
        VerifierSettings.SortJsonObjects();
        VerifierSettings.SortPropertiesAlphabetically();
    }

    /// <summary>
    /// Registers the single scrubber that makes one snapshot valid for all eight target frameworks.
    /// </summary>
    /// <remarks>
    /// Everything runs inside one line scrubber on purpose. Verify inserts a registration at the front of
    /// its list, so two separate registrations would depend on an order that is not part of its contract —
    /// and getting that order wrong is not a loud failure but a half normalised path such as
    /// <c>C:\Program Files{runtimeDirectory}</c>, which then differs per machine.
    /// </remarks>
    private static void RegisterScrubbers() => VerifierSettings.ScrubLinesWithReplace(NormaliseLine);

    /// <summary>
    /// Replaces the two directories whose exact value this process knows. The literal replacement runs
    /// before every pattern, because it is the only one that cannot mistake where a path begins.
    /// </summary>
    /// <param name="line">The line to normalise.</param>
    /// <returns>The line with the known directories replaced.</returns>
    private static string ReplaceKnownDirectories(string line)
    {
        var result = line;

        if (_runtimeDirectory.Length > 0)
        {
            result = result.Replace(_runtimeDirectory, RuntimeDirectoryToken, StringComparison.Ordinal);
        }

        if (_temporaryDirectory.Length > 0)
        {
            result = result.Replace(_temporaryDirectory, TempPathToken, StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>
    /// Normalises every framework dependent token of a single line, in a fixed order: the directories this
    /// process knows literally, then the remaining paths, because they contain monikers, then the monikers,
    /// then the parts of an assembly identity, then the remaining names and versions.
    /// </summary>
    /// <param name="line">The line to normalise.</param>
    /// <returns>The normalised line.</returns>
    private static string NormaliseLine(string line)
    {
        var result = _coreRuntimeDirectoryPattern.Replace(ReplaceKnownDirectories(line), RuntimeDirectoryToken);

        result = _frameworkRuntimeDirectoryPattern.Replace(result, RuntimeDirectoryToken);
        result = _temporaryDirectoryPattern.Replace(result, TemporaryDirectoryToken);
        result = _frameworkNamePattern.Replace(result, TargetFrameworkToken);
        result = _frameworkDescriptionPattern.Replace(result, FrameworkToken);
        result = _shortTargetFrameworkPattern.Replace(result, TargetFrameworkToken);
        result = _assemblyVersionPattern.Replace(result, VersionToken);
        result = _publicKeyTokenPattern.Replace(result, PublicKeyTokenToken);
        result = _corlibNamePattern.Replace(result, CorlibToken);

        return _languageVersionPattern.Replace(result, LanguageVersionToken);
    }

    /// <summary>
    /// Creates and returns the snapshot directory of one test.
    /// </summary>
    /// <param name="sourceFile">The source file the test is declared in.</param>
    /// <param name="projectDirectory">The project directory of the running assembly.</param>
    /// <returns>The full path of the created directory.</returns>
    private static string CreateSnapshotDirectory(string sourceFile, string projectDirectory)
    {
        var relativePath = GetSourceRelativeDirectory(sourceFile, projectDirectory);
        var directory = Path.Combine(projectDirectory, SnapshotDirectoryName, relativePath);

        return Directory.CreateDirectory(directory).FullName;
    }

    /// <summary>
    /// Determines the directory of <paramref name="sourceFile" /> relative to the project that owns that
    /// file.
    /// </summary>
    /// <param name="sourceFile">The source file the test is declared in.</param>
    /// <param name="projectDirectory">The project directory of the running assembly.</param>
    /// <returns>The relative directory, which is empty for a file next to the project file.</returns>
    /// <remarks>
    /// For a file of the running project this is simply the part below the project directory. For a file
    /// that another project links in — every file of this folder is linked into the integration test
    /// project — the project directory of the running assembly is not a prefix of the source path at all,
    /// and taking the relative path would climb out of <c>_snapshots</c> and into the other project. The
    /// owning project is therefore found by walking up from the source file, which mirrors exactly what
    /// the <c>Link</c> metadata of the shared compile item does.
    /// </remarks>
    private static string GetSourceRelativeDirectory(string sourceFile, string projectDirectory)
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFile);

        if (sourceDirectory is not { Length: > 0 })
        {
            return string.Empty;
        }

        if (IsBelow(sourceDirectory, projectDirectory))
        {
            return Trim(sourceDirectory.Substring(projectDirectory.Length));
        }

        var owningProjectDirectory = FindProjectDirectory(sourceDirectory);

        if (owningProjectDirectory is null)
        {
            return Path.GetFileName(sourceDirectory.TrimEnd(_directorySeparators));
        }

        return Trim(sourceDirectory.Substring(owningProjectDirectory.Length));
    }

    /// <summary>
    /// Determines whether <paramref name="directory" /> is the project directory itself or below it.
    /// </summary>
    /// <param name="directory">The directory to check.</param>
    /// <param name="projectDirectory">The project directory, which ends with a separator.</param>
    /// <returns><see langword="true" /> when the project directory is a prefix of the directory.</returns>
    private static bool IsBelow(string directory, string projectDirectory) =>
        directory.Length >= projectDirectory.Length
        && directory.StartsWith(projectDirectory, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Walks up from <paramref name="directory" /> until a directory contains a project file.
    /// </summary>
    /// <param name="directory">The directory to start at.</param>
    /// <returns>That directory, or <see langword="null" /> when there is none.</returns>
    private static string? FindProjectDirectory(string directory)
    {
        var current = new DirectoryInfo(directory);

        while (current is not null)
        {
            if (current.Exists && current.GetFiles(ProjectFilePattern).Length > 0)
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string Trim(string path) => path.Trim(_directorySeparators);

    /// <summary>
    /// Resolves the directory the core library of the executing framework lives in.
    /// </summary>
    /// <returns>The directory, or an empty string when the core library has no file on disk.</returns>
    private static string GetRuntimeDirectory()
    {
        var location = typeof(object).Assembly.Location;

        if (location.Length == 0)
        {
            return string.Empty;
        }

        return Path.GetDirectoryName(location) ?? string.Empty;
    }
}
