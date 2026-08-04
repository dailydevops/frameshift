namespace NetEvolve.FrameShift.Execution.Tests.Unit;

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Mutations;

/// <summary>
/// Exercises <see cref="HtmlExecutionReportWriter" />: the null guard, the clean-report shortcut, the
/// per-group sections, and - most importantly - that user-controlled text is always HTML-encoded before
/// it reaches the returned markup.
/// </summary>
public class HtmlExecutionReportWriterTests
{
    [Test]
    public async Task Write_NullReport_ThrowsArgumentNullException() =>
        _ = await Assert.That(() => HtmlExecutionReportWriter.Write(null!)).Throws<ArgumentNullException>();

    [Test]
    public async Task Write_CleanReport_ContainsNothingLeftToDoMessageAndNoFailureSections()
    {
        var score = MutationScore.FromResults([
            CreateResult("class Fixture { void M() { var x = 1 + 2; } }", "Fixture.cs", MutantVerdict.Killed, "+ => -"),
        ]);
        var report = ExecutionReport.FromScore(score);

        var html = HtmlExecutionReportWriter.Write(report);

        using (Assert.Multiple())
        {
            _ = await Assert.That(html).Contains("Nothing left to do");
            _ = await Assert.That(html).DoesNotContain("Build-failed mutants");
            _ = await Assert.That(html).DoesNotContain("Timed-out mutants");
        }
    }

    [Test]
    public async Task Write_SurvivedMutantWithHtmlSpecialCharacters_EncodesTheDisplayName()
    {
        var score = MutationScore.FromResults([
            CreateResult(
                "class Fixture { void M() { var x = 1 < 2; } }",
                "Fixture.cs",
                MutantVerdict.Survived,
                "1 < 2"
            ),
        ]);
        var report = ExecutionReport.FromScore(score);

        var html = HtmlExecutionReportWriter.Write(report);

        using (Assert.Multiple())
        {
            _ = await Assert.That(html).Contains("1 &lt; 2");
            _ = await Assert.That(html).DoesNotContain("1 < 2");
        }
    }

    [Test]
    public async Task Write_AnyReport_ProducesACompleteHtmlDocument()
    {
        var score = MutationScore.FromResults([
            CreateResult("class Fixture { void M() { var x = 1 + 2; } }", "Fixture.cs", MutantVerdict.Killed, "+ => -"),
        ]);
        var report = ExecutionReport.FromScore(score);

        var html = HtmlExecutionReportWriter.Write(report);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(html.TrimStart().StartsWith("<!doctype html>", StringComparison.OrdinalIgnoreCase))
                .IsTrue();
            _ = await Assert.That(html).Contains("<html");
        }
    }

    [Test]
    public async Task Write_ReportWithAllThreeGroups_ContainsEverySectionHeadingWithItsCount()
    {
        var score = MutationScore.FromResults([
            CreateResult(
                "class Survived { void M() { var x = 1 + 2; } }",
                "Survived.cs",
                MutantVerdict.Survived,
                "survived-1"
            ),
            CreateResult(
                "class BuildFailed { void M() { var x = 1 - 2; } }",
                "BuildFailed.cs",
                MutantVerdict.BuildFailed,
                "build-failed-1"
            ),
            CreateResult(
                "class TimedOut { void M() { var x = 1 * 2; } }",
                "TimedOut.cs",
                MutantVerdict.Timeout,
                "timed-out-1"
            ),
        ]);
        var report = ExecutionReport.FromScore(score);

        var html = HtmlExecutionReportWriter.Write(report);

        using (Assert.Multiple())
        {
            _ = await Assert.That(html).Contains("Survived mutants (1)");
            _ = await Assert.That(html).Contains("Build-failed mutants (1)");
            _ = await Assert.That(html).Contains("Timed-out mutants (1)");
        }
    }

    private static MutantExecutionResult CreateResult(
        string source,
        string path,
        MutantVerdict verdict,
        string displayName
    )
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: path);
        var binary = tree.GetRoot().DescendantNodes().OfType<BinaryExpressionSyntax>().First();
        var mutation = new Mutation(
            MutationKind.ArithmeticOperator,
            "arithmetic.add-to-subtract",
            displayName,
            binary,
            binary
        );

        return new MutantExecutionResult(mutation, verdict, failure: null);
    }
}
