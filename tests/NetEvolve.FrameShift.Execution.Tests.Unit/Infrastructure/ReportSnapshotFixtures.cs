namespace NetEvolve.FrameShift.Execution.Tests.Unit.Infrastructure;

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Mutations;

/// <summary>
/// Deterministic reports the report-writer snapshot tests render: fixed paths, fixed lines, fixed counts.
/// </summary>
internal static class ReportSnapshotFixtures
{
    /// <summary>
    /// A report with one killed, two survived, one build-failed and one timed-out mutant. The survived rows
    /// differ in width (one cell longer, one shorter than its column header), carry HTML and Markdown
    /// special characters, and sit on lines greater than one. The score (1 of 3 real mutants) is not round.
    /// </summary>
    public static ExecutionReport Mixed() =>
        ExecutionReport.FromScore(
            MutationScore.FromResults([
                CreateResult("src/Killed.cs", 1, "+ => -", MutantVerdict.Killed),
                CreateResult("src/Calc<T>&`x`|y.cs", 3, "a < b & `c` | d", MutantVerdict.Survived),
                CreateResult("src/A.cs", 12, "x", MutantVerdict.Survived),
                CreateResult("src/Broken.cs", 7, "+ => *", MutantVerdict.BuildFailed),
                CreateResult("src/Slow.cs", 20, "+ => /", MutantVerdict.Timeout),
            ])
        );

    /// <summary>
    /// A report without a single actionable mutant.
    /// </summary>
    public static ExecutionReport Clean() =>
        ExecutionReport.FromScore(
            MutationScore.FromResults([CreateResult("src/Killed.cs", 1, "+ => -", MutantVerdict.Killed)])
        );

    private static MutantExecutionResult CreateResult(
        string path,
        int line,
        string displayName,
        MutantVerdict verdict
    )
    {
        var tree = CSharpSyntaxTree.ParseText(new string('\n', line - 1) + "class C { int M() => 1 + 2; }", path: path);
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
