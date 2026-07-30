namespace NetEvolve.Frameshift.Tests.Unit.Mutations;

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.Frameshift.Mutations;
using NetEvolve.Frameshift.Tests.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Tests <see cref="MutantCompiler" />, whose single promise is that a mutant is only called viable
/// when the rewritten tree still binds, and that asking the question never disturbs the compilation
/// the rest of the analysis works on.
/// </summary>
public class MutantCompilerTests
{
    private const string ViableOperatorId = "arithmetic.add-to-subtract";
    private const string BrokenOperatorId = "numeric.literal-to-string";
    private const string SwapOperatorId = "arithmetic.swap";

    private const string CalculatorSource = """
        namespace Fixture;

        public static class Calculator
        {
            public static int Add(int left, int right) => /*!*/left + right;

            public static int Scale(int value) => value * 2;

            public static int Offset(int value) => value + 3;

            public static int Constant() => 7;
        }
        """;

    private const string DoublerSource = """
        namespace Other;

        public static class Doubler
        {
            public static int Double(int value) => /*!*/value + value;
        }
        """;

    [Test]
    public async Task Verify_MutationKeepsTheCodeValid_ReturnsViable()
    {
        var (compilation, tree) = CreateFixture();
        var compiler = new MutantCompiler(compilation);

        var viability = compiler.Verify(CreateViableMutation(tree), tree, CancellationToken.None);

        _ = await Assert.That(Describe(compilation)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        _ = await Assert.That(viability).IsEqualTo(MutantViability.Viable);
    }

    [Test]
    public async Task Verify_ReplacementIsTypeIncorrect_ReturnsDoesNotCompile()
    {
        var (compilation, tree) = CreateFixture();
        var compiler = new MutantCompiler(compilation);

        var viability = compiler.Verify(CreateBrokenMutation(tree), tree, CancellationToken.None);

        _ = await Assert.That(Describe(compilation)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        _ = await Assert.That(viability).IsEqualTo(MutantViability.DoesNotCompile);
    }

    [Test]
    public async Task Verify_MutationBelongsToAnotherTree_ReturnsDoesNotCompile()
    {
        var (compilation, tree) = CreateFixture();
        var foreign = CompilationFactory.Create(DoublerSource, assemblyName: "ForeignAssembly");
        var compiler = new MutantCompiler(compilation);

        var viability = compiler.Verify(CreateViableMutation(foreign.SyntaxTrees[0]), tree, CancellationToken.None);

        _ = await Assert.That(viability).IsEqualTo(MutantViability.DoesNotCompile);
    }

    [Test]
    public async Task Verify_CalledRepeatedly_ReturnsTheSameVerdictForViableMutants()
    {
        var (compilation, tree) = CreateFixture();
        var compiler = new MutantCompiler(compilation);
        var mutation = CreateViableMutation(tree);

        var verdicts = Repeat(() => compiler.Verify(mutation, tree, CancellationToken.None), repetitions: 5);

        _ = await Assert.That(verdicts.Distinct()).Count().IsEqualTo(1);
        _ = await Assert.That(verdicts[0]).IsEqualTo(MutantViability.Viable);
    }

    [Test]
    public async Task Verify_CalledRepeatedly_ReturnsTheSameVerdictForBrokenMutants()
    {
        var (compilation, tree) = CreateFixture();
        var compiler = new MutantCompiler(compilation);
        var mutation = CreateBrokenMutation(tree);

        var verdicts = Repeat(() => compiler.Verify(mutation, tree, CancellationToken.None), repetitions: 5);

        _ = await Assert.That(verdicts.Distinct()).Count().IsEqualTo(1);
        _ = await Assert.That(verdicts[0]).IsEqualTo(MutantViability.DoesNotCompile);
    }

    [Test]
    public async Task Verify_AfterVerification_LeavesTheOriginalCompilationUnchanged()
    {
        var (compilation, tree) = CreateFixture();
        var textBefore = tree.ToString();
        var treesBefore = compilation.SyntaxTrees;
        var compiler = new MutantCompiler(compilation);

        _ = compiler.Verify(CreateViableMutation(tree), tree, CancellationToken.None);
        _ = compiler.Verify(CreateBrokenMutation(tree), tree, CancellationToken.None);

        _ = await Assert.That(Describe(compilation)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        _ = await Assert.That(compilation.SyntaxTrees.Length).IsEqualTo(treesBefore.Length);
        _ = await Assert.That(ReferenceEquals(compilation.SyntaxTrees[0], tree)).IsTrue();
        _ = await Assert.That(tree.ToString()).IsEqualTo(textBefore);
    }

    [Test]
    public async Task Verify_CancelledToken_ThrowsOperationCanceledException()
    {
        var (compilation, tree) = CreateFixture();
        var compiler = new MutantCompiler(compilation);
        var mutation = CreateViableMutation(tree);
        using var source = new CancellationTokenSource();
        await source.CancelAsync().ConfigureAwait(false);

        OperationCanceledException? caught = null;
        try
        {
            _ = compiler.Verify(mutation, tree, source.Token);
        }
        catch (OperationCanceledException exception)
        {
            caught = exception;
        }

        _ = await Assert.That(caught).IsNotNull();
    }

    [Test]
    public async Task Verify_ConcurrentlyForDistinctMutations_ReturnsViableForEveryOne()
    {
        var (compilation, tree) = CreateFixture();
        var compiler = new MutantCompiler(compilation);
        var mutations = CreateOperatorSwapMutations(tree);
        var results = new ConcurrentBag<MutantViability>();

        _ = Parallel.ForEach(
            Enumerable.Range(0, 32),
            _ =>
            {
                foreach (var mutation in mutations)
                {
                    results.Add(compiler.Verify(mutation, tree, CancellationToken.None));
                }
            }
        );

        _ = await Assert.That(mutations.Length).IsEqualTo(3);
        _ = await Assert.That(results).Count().IsEqualTo(32 * mutations.Length);
        _ = await Assert.That(results.Distinct()).Count().IsEqualTo(1);
        _ = await Assert.That(results.First()).IsEqualTo(MutantViability.Viable);
    }

    [Test]
    public async Task Verify_ConcurrentlyForTheSameMutations_ReturnsConsistentVerdicts()
    {
        var (compilation, tree) = CreateFixture();
        var compiler = new MutantCompiler(compilation);
        var viable = CreateViableMutation(tree);
        var broken = CreateBrokenMutation(tree);
        var results = new ConcurrentBag<(string OperatorId, MutantViability Viability)>();

        _ = Parallel.ForEach(
            Enumerable.Range(0, 32),
            _ =>
            {
                results.Add((ViableOperatorId, compiler.Verify(viable, tree, CancellationToken.None)));
                results.Add((BrokenOperatorId, compiler.Verify(broken, tree, CancellationToken.None)));
            }
        );

        _ = await Assert.That(VerdictsOf(results, ViableOperatorId)).IsEquivalentTo(new[] { MutantViability.Viable });
        _ = await Assert
            .That(VerdictsOf(results, BrokenOperatorId))
            .IsEquivalentTo(new[] { MutantViability.DoesNotCompile });
    }

    private static MutantViability[] VerdictsOf(
        IEnumerable<(string OperatorId, MutantViability Viability)> results,
        string operatorId
    ) =>
        [
            .. results
                .Where(result => string.Equals(result.OperatorId, operatorId, StringComparison.Ordinal))
                .Select(result => result.Viability)
                .Distinct(),
        ];

    private static MutantViability[] Repeat(Func<MutantViability> verify, int repetitions) =>
        [.. Enumerable.Range(0, repetitions).Select(_ => verify())];

    private static string Describe(Compilation compilation) =>
        DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation));

    private static (CSharpCompilation Compilation, SyntaxTree Tree) CreateFixture()
    {
        var compilation = CompilationFactory.Create(CalculatorSource);

        return (compilation, compilation.SyntaxTrees[0]);
    }

    /// <summary>
    /// Turns the marked <c>left + right</c> into <c>left - right</c>, which stays a legal program.
    /// </summary>
    /// <param name="tree">The tree holding the marked addition.</param>
    /// <returns>The mutation.</returns>
    private static Mutation CreateViableMutation(SyntaxTree tree)
    {
        var original = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var replacement = SyntaxFactory.BinaryExpression(SyntaxKind.SubtractExpression, original.Left, original.Right);

        return new Mutation(MutationKind.ArithmeticOperator, ViableOperatorId, "+ => -", original, replacement);
    }

    /// <summary>
    /// Replaces the numeric literal returned by <c>Constant</c> with a string literal, a rewrite no
    /// operator would ever produce and that therefore reliably breaks binding.
    /// </summary>
    /// <param name="tree">The tree holding the literal.</param>
    /// <returns>The mutation.</returns>
    private static Mutation CreateBrokenMutation(SyntaxTree tree)
    {
        var original = SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(
            tree,
            node => string.Equals(node.Token.ValueText, "7", StringComparison.Ordinal)
        );
        var replacement = SyntaxFactory.LiteralExpression(
            SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal("mutated")
        );

        return new Mutation(MutationKind.NumericLiteral, BrokenOperatorId, "7 => mutated", original, replacement);
    }

    /// <summary>
    /// Creates one viable mutation per binary expression of the fixture, which is the set the
    /// concurrency tests hammer the shared cache with.
    /// </summary>
    /// <param name="tree">The tree to mutate.</param>
    /// <returns>The mutations.</returns>
    private static ImmutableArray<Mutation> CreateOperatorSwapMutations(SyntaxTree tree)
    {
        var builder = ImmutableArray.CreateBuilder<Mutation>();

        foreach (var original in SyntaxNodeLocator.FindAll<BinaryExpressionSyntax>(tree))
        {
            var replacementKind = original.IsKind(SyntaxKind.MultiplyExpression)
                ? SyntaxKind.DivideExpression
                : SyntaxKind.SubtractExpression;
            var replacement = SyntaxFactory.BinaryExpression(replacementKind, original.Left, original.Right);

            builder.Add(
                new Mutation(MutationKind.ArithmeticOperator, SwapOperatorId, "operator swap", original, replacement)
            );
        }

        return builder.ToImmutable();
    }
}
