namespace NetEvolve.FrameShift.Execution.Tests.Unit;

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Mutations;

/// <summary>
/// Exercises <see cref="ConsoleExecutionReportWriter" /> against hand-built
/// <see cref="ExecutionReport" /> fixtures, since building the real execution pipeline just to get a
/// report would exercise far more than the writer itself.
/// </summary>
public class ConsoleExecutionReportWriterTests
{
    private static Mutation CreateMutation(string path, string source, string displayName)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: path);
        var binary = tree.GetRoot().DescendantNodes().OfType<BinaryExpressionSyntax>().First();

        return new Mutation(MutationKind.ArithmeticOperator, "arithmetic.add-to-subtract", displayName, binary, binary);
    }

    [Test]
    public async Task WriteAsync_CleanReport_WritesNothingToDoMessageAndNoSectionHeadings()
    {
        var score = MutationScore.FromResults([]);
        var report = ExecutionReport.FromScore(score);

        using var output = new StringWriter();
        await ConsoleExecutionReportWriter.WriteAsync(output, report).ConfigureAwait(false);

        var text = output.ToString();

        using (Assert.Multiple())
        {
            _ = await Assert.That(text).Contains("Next steps:");
            _ = await Assert.That(text).Contains("Nothing to do");
            _ = await Assert.That(text).DoesNotContain("Survived mutants");
            _ = await Assert.That(text).DoesNotContain("Build-failed mutants");
            _ = await Assert.That(text).DoesNotContain("Timed-out mutants");
        }
    }

    [Test]
    public async Task WriteAsync_OnlySurvivedMutants_WritesSurvivedSectionOnly()
    {
        var mutationOne = CreateMutation("Calculator.cs", "class Fixture { void M() { var x = 1 + 2; } }", "+ => -");
        var mutationTwo = CreateMutation("Other.cs", "class Fixture { void M() { var x = 3 - 4; } }", "- => +");
        var score = MutationScore.FromResults([
            new MutantExecutionResult(mutationOne, MutantVerdict.Survived, failure: null),
            new MutantExecutionResult(mutationTwo, MutantVerdict.Survived, failure: null),
        ]);
        var report = ExecutionReport.FromScore(score);

        using var output = new StringWriter();
        await ConsoleExecutionReportWriter.WriteAsync(output, report).ConfigureAwait(false);

        var text = output.ToString();

        using (Assert.Multiple())
        {
            _ = await Assert.That(text).Contains("Survived mutants (missing test coverage) (2):");
            _ = await Assert.That(text).Contains("Calculator.cs:1 '+ => -'");
            _ = await Assert.That(text).Contains("Other.cs:1 '- => +'");
            _ = await Assert.That(text).DoesNotContain("Build-failed mutants");
            _ = await Assert.That(text).DoesNotContain("Timed-out mutants");
        }
    }

    [Test]
    public async Task WriteAsync_AllThreeGroupsPresent_WritesEverySectionInDocumentedOrder()
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

        using var output = new StringWriter();
        await ConsoleExecutionReportWriter.WriteAsync(output, report).ConfigureAwait(false);

        var text = output.ToString();

        var survivedIndex = text.IndexOf("Survived mutants (missing test coverage) (1):", StringComparison.Ordinal);
        var buildFailedIndex = text.IndexOf(
            "Build-failed mutants (fix the mutant or the test harness) (1):",
            StringComparison.Ordinal
        );
        var timedOutIndex = text.IndexOf(
            "Timed-out mutants (raise --timeout-seconds or investigate slow tests) (1):",
            StringComparison.Ordinal
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(survivedIndex).IsGreaterThanOrEqualTo(0);
            _ = await Assert.That(buildFailedIndex).IsGreaterThan(survivedIndex);
            _ = await Assert.That(timedOutIndex).IsGreaterThan(buildFailedIndex);
            _ = await Assert.That(text).Contains("Calculator.cs:1 '+ => -'");
            _ = await Assert.That(text).Contains("Broken.cs:1 '- => +'");
            _ = await Assert.That(text).Contains("Slow.cs:1 '* => /'");
        }
    }

    [Test]
    public async Task WriteAsync_NullOutput_ThrowsArgumentNullException()
    {
        var report = ExecutionReport.FromScore(MutationScore.FromResults([]));

        _ = await Assert
            .That(async () => await ConsoleExecutionReportWriter.WriteAsync(null!, report).ConfigureAwait(false))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task WriteAsync_NullReport_ThrowsArgumentNullException()
    {
        using var output = new StringWriter();

        _ = await Assert
            .That(async () => await ConsoleExecutionReportWriter.WriteAsync(output, null!).ConfigureAwait(false))
            .Throws<ArgumentNullException>();
    }
}
