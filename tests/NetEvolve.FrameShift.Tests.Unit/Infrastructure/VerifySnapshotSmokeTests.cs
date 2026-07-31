namespace NetEvolve.FrameShift.Tests.Infrastructure;

using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Generation;
using TUnit.Core;

/// <summary>
/// Proves the Verify setup of <see cref="VerifyModuleInitializer" /> before any real conversion depends
/// on it: that a snapshot is found where the path derivation puts it, that the two shapes the analyzers
/// and the generator produce round-trip through a snapshot, and that every scrubber turns the executing
/// target framework into a fixed token.
/// </summary>
/// <remarks>
/// <para>
/// The three snapshots of this class are compared by all eight target frameworks — net6.0, net7.0,
/// net8.0, net9.0, net10.0 and, on Windows, net472, net48 and net481 — and, because this file is linked
/// into the integration test project as well, by both test assemblies. Each assembly keeps its own copy
/// below its own <c>_snapshots\Infrastructure</c>, so the parallel runs of one assembly share a file and
/// the two assemblies do not.
/// </para>
/// <para>
/// Everything that is snapshotted here is deliberately trivial. The value of these tests is not what
/// they assert but that they fail loudly, in every configuration of the matrix, the moment the snapshot
/// plumbing or one of the scrubbers stops working.
/// </para>
/// </remarks>
public class VerifySnapshotSmokeTests
{
    /// <summary>
    /// The folder the harness would create a temporary directory in. It never touches the file system
    /// here; only the shape of the path matters, because that shape is what the scrubber normalises.
    /// </summary>
    private const string TemporaryFolderName = "NetEvolve.FrameShift.Tests";

    private const string LineFeed = "\n";

    /// <summary>
    /// A fixture with exactly one compile error, whose message names nothing but two keywords and is
    /// therefore identical on every framework — as long as the culture is pinned, which is precisely what
    /// this snapshot proves.
    /// </summary>
    private const string SourceWithOneError = """
        namespace Fixture;

        public sealed class Sample
        {
            public int Value => "text";
        }
        """;

    /// <summary>
    /// A fixture with one TUnit test method that references nothing at all, so that the generated
    /// manifest consists of the header and a single entry.
    /// </summary>
    private const string SourceWithOneTest = """
        namespace Tests;

        public class SmokeTests
        {
            [TUnit.Core.Test]
            public void Smoke()
            {
            }
        }
        """;

    /// <summary>
    /// A diagnostic list, in the shape every analyzer test uses, survives a round trip through a
    /// snapshot.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    public async Task Verify_CompilerDiagnostics_MatchTheSnapshot()
    {
        var compilation = CompilationFactory.Create(SourceWithOneError);
        var errors = CompilationFactory.GetCompileErrors(compilation);

        _ = await Verify(DiagnosticAssertions.Describe(errors)).ConfigureAwait(false);
    }

    /// <summary>
    /// A generated source, in the shape every generator test uses, survives a round trip through a
    /// snapshot. The trailing line feed of the generated file is dropped, so that the snapshot is the
    /// same whether or not Verify trims the end of the content it writes.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    public async Task Verify_GeneratedManifest_MatchesTheSnapshot()
    {
        var compilation = CompilationFactory.Create(SourceWithOneTest, TestFramework.TUnit);
        var output = GeneratorRunner.Run(new TestSurfaceManifestGenerator(), compilation);
        var generated = output.TextOf(TestSurfaceManifestGenerator.HintName);

        _ = await Verify(generated.TrimEnd('\n')).ConfigureAwait(false);
    }

    /// <summary>
    /// Every value that differs between the eight target frameworks is normalised into a token, so that
    /// the eight runs agree on one snapshot. If a scrubber ever stops matching, the run that produced the
    /// unscrubbed value fails here instead of somewhere in the real conversions.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Test]
    public async Task Verify_FrameworkFingerprint_MatchesTheSnapshot() =>
        _ = await Verify(DescribeExecutingFramework()).ConfigureAwait(false);

    /// <summary>
    /// Describes the executing framework, one deliberately framework dependent value per line, sorted by
    /// their names.
    /// </summary>
    /// <returns>The description.</returns>
    private static string DescribeExecutingFramework()
    {
        var assembly = typeof(VerifySnapshotSmokeTests).Assembly;
        var corlib = typeof(object).Assembly;

        string[] lines =
        [
            "assemblyDirectory: " + Path.GetFileName(Path.GetDirectoryName(assembly.Location)),
            "corlib: " + corlib.GetName().Name,
            "framework: " + RuntimeInformation.FrameworkDescription,
            "language: " + GetEffectiveLanguageVersion(),
            "runtime: " + Path.GetDirectoryName(corlib.Location),
            "targetFramework: " + GetTargetFrameworkName(assembly),
            "temporary: " + CreateTemporaryDirectoryPath(),
        ];

        return string.Join(LineFeed, lines);
    }

    /// <summary>
    /// Reads the language version every test compilation is parsed with, resolved to the concrete version
    /// the referenced Roslyn maps <c>Latest</c> to.
    /// </summary>
    /// <returns>The name of the effective language version.</returns>
    private static string GetEffectiveLanguageVersion() =>
        CompilationFactory.ParseOptions.LanguageVersion.MapSpecifiedToEffectiveVersion().ToString();

    /// <summary>
    /// Reads the long target framework moniker the test assembly was compiled for.
    /// </summary>
    /// <param name="assembly">The test assembly.</param>
    /// <returns>The moniker, or an empty string when the assembly does not carry one.</returns>
    private static string GetTargetFrameworkName(Assembly assembly) =>
        assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? string.Empty;

    /// <summary>
    /// Builds the path of a temporary directory in exactly the shape the harness builds one, without
    /// creating it.
    /// </summary>
    /// <returns>The path.</returns>
    private static string CreateTemporaryDirectoryPath() =>
        Path.Combine(
            Path.GetTempPath(),
            TemporaryFolderName,
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)
        );
}
