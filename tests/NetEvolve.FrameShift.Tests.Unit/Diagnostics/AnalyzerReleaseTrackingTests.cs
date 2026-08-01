namespace NetEvolve.FrameShift.Tests.Unit.Diagnostics;

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using NetEvolve.FrameShift.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Pins the release-tracking invariants that <c>AnalyzerReleases.Shipped.md</c> and
/// <c>AnalyzerReleases.Unshipped.md</c> depend on, and that the RS2000/RS2001 analyzer-authoring
/// rules only catch at build time: every reported diagnostic id has a documentation page under
/// <c>docs/rules/</c>, every reported diagnostic id is tracked in exactly one of the two release
/// files, and <c>FSH0005</c> — the MSBuild-only warning that has no <see cref="DiagnosticDescriptor" />
/// — stays out of both.
/// </summary>
public class AnalyzerReleaseTrackingTests
{
    /// <summary>
    /// The MSBuild warning emitted by the package's build assets. It is not backed by a
    /// <see cref="DiagnosticDescriptor" />, so release tracking must never mention it.
    /// </summary>
    private const string MSBuildOnlyWarningId = "FSH0005";

    private static string RepositoryRoot([CallerFilePath] string sourceFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", "..", ".."));

    private static string DocsRulesDirectory() => Path.Combine(RepositoryRoot(), "docs", "rules");

    private static string AnalyzerProjectDirectory() => Path.Combine(RepositoryRoot(), "src", "NetEvolve.FrameShift");

    /// <summary>
    /// Every <see cref="DiagnosticDescriptor" /> the analyzer assembly reports, discovered through the
    /// internal <see cref="Descriptors" /> class rather than hard-coded, so a newly added rule is picked
    /// up automatically.
    /// </summary>
    private static string[] ReportedDiagnosticIds() =>
        typeof(Descriptors)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(DiagnosticDescriptor))
            .Select(property => ((DiagnosticDescriptor)property.GetValue(null)!).Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Parses the <c>Rule ID</c> column of the pipe-delimited table in an analyzer release file. Both
    /// <c>AnalyzerReleases.Shipped.md</c> and <c>AnalyzerReleases.Unshipped.md</c> use the same layout
    /// described by
    /// https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md.
    /// </summary>
    private static string[] TrackedRuleIds(string releaseFilePath) =>
        File.ReadLines(releaseFilePath)
            .Where(line => line.StartsWith("FSH", StringComparison.Ordinal))
            .Select(line => line.Split('|')[0].Trim())
            .ToArray();

    [Test]
    public async Task ReportedDiagnosticIds_EveryId_HasADocumentationPage()
    {
        var docsDirectory = DocsRulesDirectory();
        var missing = ReportedDiagnosticIds().Where(id => !File.Exists(Path.Combine(docsDirectory, id + ".md")));

        _ = await Assert.That(missing).IsEmpty();
    }

    [Test]
    public async Task MSBuildOnlyWarning_HasADocumentationPage()
    {
        var docPath = Path.Combine(DocsRulesDirectory(), MSBuildOnlyWarningId + ".md");

        _ = await Assert.That(File.Exists(docPath)).IsTrue();
    }

    [Test]
    public async Task MSBuildOnlyWarning_IsNotAReportedDiagnosticId() =>
        _ = await Assert.That(ReportedDiagnosticIds()).DoesNotContain(MSBuildOnlyWarningId);

    [Test]
    public async Task ReportedDiagnosticIds_EveryId_IsTrackedInExactlyOneReleaseFile()
    {
        var projectDirectory = AnalyzerProjectDirectory();
        var shipped = TrackedRuleIds(Path.Combine(projectDirectory, "AnalyzerReleases.Shipped.md"));
        var unshipped = TrackedRuleIds(Path.Combine(projectDirectory, "AnalyzerReleases.Unshipped.md"));

        using (Assert.Multiple())
        {
            // No id may be tracked twice, whether in the same file or split across both.
            _ = await Assert.That(shipped.Intersect(unshipped, StringComparer.Ordinal)).IsEmpty();
            _ = await Assert.That(shipped.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(shipped.Length);
            _ = await Assert.That(unshipped.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(unshipped.Length);

            var tracked = shipped.Concat(unshipped).ToArray();
            var untracked = ReportedDiagnosticIds().Where(id => !tracked.Contains(id, StringComparer.Ordinal));
            _ = await Assert.That(untracked).IsEmpty();
        }
    }

    [Test]
    public async Task AnalyzerReleaseFiles_NeitherFile_TracksTheMSBuildOnlyWarning()
    {
        var projectDirectory = AnalyzerProjectDirectory();
        var shipped = TrackedRuleIds(Path.Combine(projectDirectory, "AnalyzerReleases.Shipped.md"));
        var unshipped = TrackedRuleIds(Path.Combine(projectDirectory, "AnalyzerReleases.Unshipped.md"));

        using (Assert.Multiple())
        {
            _ = await Assert.That(shipped).DoesNotContain(MSBuildOnlyWarningId);
            _ = await Assert.That(unshipped).DoesNotContain(MSBuildOnlyWarningId);
        }
    }
}
