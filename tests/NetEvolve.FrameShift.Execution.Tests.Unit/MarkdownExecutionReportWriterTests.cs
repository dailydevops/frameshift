namespace NetEvolve.FrameShift.Execution.Tests.Unit;

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Execution.Reports;
using NetEvolve.FrameShift.Mutations;

/// <summary>
/// Exercises <see cref="MarkdownExecutionReportWriter" /> against hand-built
/// <see cref="ExecutionReport" /> fixtures, since building the real execution pipeline just to get a
/// report would exercise far more than the writer itself.
/// </summary>
public class MarkdownExecutionReportWriterTests
{
    private static Mutation CreateMutation(string path, string source, string displayName)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: path);
        var binary = tree.GetRoot().DescendantNodes().OfType<BinaryExpressionSyntax>().First();

        return new Mutation(MutationKind.ArithmeticOperator, "arithmetic.add-to-subtract", displayName, binary, binary);
    }

    [Test]
    public async Task Write_NullReport_ThrowsArgumentNullException() =>
        _ = await Assert.That(() => MarkdownExecutionReportWriter.Write(null!)).Throws<ArgumentNullException>();

    [Test]
    public async Task Write_CleanReport_ContainsNothingLeftToDoMessageAndNoSectionHeadings()
    {
        var score = MutationScore.FromResults([]);
        var report = ExecutionReport.FromScore(score);

        var markdown = MarkdownExecutionReportWriter.Write(report);

        using (Assert.Multiple())
        {
            _ = await Assert.That(markdown).Contains("Nothing left to do");
            _ = await Assert.That(markdown).DoesNotContain("Survived mutants");
            _ = await Assert.That(markdown).DoesNotContain("Build-failed mutants");
            _ = await Assert.That(markdown).DoesNotContain("Timed-out mutants");
        }
    }

    [Test]
    public async Task Write_OnlySurvivedMutants_ContainsSurvivedHeadingOnly()
    {
        var mutation = CreateMutation("Calculator.cs", "class Fixture { void M() { var x = 1 + 2; } }", "+ => -");
        var score = MutationScore.FromResults([
            new MutantExecutionResult(mutation, MutantVerdict.Survived, failure: null),
        ]);
        var report = ExecutionReport.FromScore(score);

        var markdown = MarkdownExecutionReportWriter.Write(report);

        using (Assert.Multiple())
        {
            _ = await Assert.That(markdown).Contains("Survived mutants (missing test coverage) (1)");
            _ = await Assert.That(markdown).DoesNotContain("Build-failed mutants");
            _ = await Assert.That(markdown).DoesNotContain("Timed-out mutants");
        }
    }

    [Test]
    public async Task Write_ReportWithAllThreeGroups_ContainsEverySectionHeadingInDocumentedOrder()
    {
        var survived = CreateMutation("Calculator.cs", "class Fixture { void M() { var x = 1 + 2; } }", "+ => -");
        var buildFailed = CreateMutation("Broken.cs", "class Fixture { void M() { var x = 3 - 4; } }", "- => +");
        var timedOut = CreateMutation("Slow.cs", "class Fixture { void M() { var x = 5 * 6; } }", "* => /");
        var score = MutationScore.FromResults([
            new MutantExecutionResult(survived, MutantVerdict.Survived, failure: null),
            new MutantExecutionResult(buildFailed, MutantVerdict.BuildFailed, failure: null),
            new MutantExecutionResult(timedOut, MutantVerdict.Timeout, failure: null),
        ]);
        var report = ExecutionReport.FromScore(score);

        var markdown = MarkdownExecutionReportWriter.Write(report);

        var survivedIndex = markdown.IndexOf("Survived mutants (missing test coverage) (1)", StringComparison.Ordinal);
        var buildFailedIndex = markdown.IndexOf(
            "Build-failed mutants (fix the mutant or the test harness) (1)",
            StringComparison.Ordinal
        );
        var timedOutIndex = markdown.IndexOf(
            "Timed-out mutants (raise --timeout-seconds or investigate slow tests) (1)",
            StringComparison.Ordinal
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(survivedIndex).IsGreaterThanOrEqualTo(0);
            _ = await Assert.That(buildFailedIndex).IsGreaterThan(survivedIndex);
            _ = await Assert.That(timedOutIndex).IsGreaterThan(buildFailedIndex);
        }
    }

    [Test]
    public async Task Write_DisplayNameContainsPipeCharacter_EscapesPipeInsteadOfBreakingTheTable()
    {
        var mutation = CreateMutation("Fixture.cs", "class Fixture { void M() { var x = 1 + 2; } }", "a | b");
        var score = MutationScore.FromResults([
            new MutantExecutionResult(mutation, MutantVerdict.Survived, failure: null),
        ]);
        var report = ExecutionReport.FromScore(score);

        var markdown = MarkdownExecutionReportWriter.Write(report);

        using (Assert.Multiple())
        {
            _ = await Assert.That(markdown).Contains("a &#124; b");
            _ = await Assert.That(markdown).DoesNotContain("| b |");
        }
    }
}
