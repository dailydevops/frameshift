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
    private const string SharedOperatorId = "arithmetic.shared";

    private const string TwinPathA = "TwinA.cs";
    private const string TwinPathB = "TwinB.cs";

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

    /// <summary>
    /// A fixture whose semantic diagnostics contain a warning and nothing else, so that the search for
    /// errors has to walk past a diagnostic that is not one.
    /// </summary>
    private const string WarningSource = """
        namespace Fixture;

        public static class Warner
        {
            [System.Obsolete("Use Compute instead.")]
            public static int Legacy(int value) => value;

            public static int Compute(int value) => Legacy(/*!*/value + 2);
        }
        """;

    /// <summary>
    /// Two files that differ only in their namespace name, which has the same length in both, so that
    /// the marked addition sits at the very same source span in either file. The cache key therefore has
    /// nothing but the file path left to tell the two mutation points apart.
    /// </summary>
    private const string TwinSourceA = """
        namespace TwinA;

        public static class Twin
        {
            public static int Combine(int left, int right) => /*!*/left + right;
        }
        """;

    private const string TwinSourceB = """
        namespace TwinB;

        public static class Twin
        {
            public static int Combine(int left, int right) => /*!*/left + right;
        }
        """;

    [Test]
    public async Task Fixtures_EveryCompilation_CompilesWithoutErrors()
    {
        var errors = new[]
        {
            Describe(CompilationFactory.Create(CalculatorSource)),
            Describe(CompilationFactory.Create(DoublerSource)),
            Describe(CompilationFactory.Create(WarningSource)),
            Describe(CreateTwinFixture()),
        };

        _ = await Assert
            .That(string.Join(" / ", errors.Distinct(StringComparer.Ordinal)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

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

    [Test]
    public async Task Constructor_CompilationIsNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => _ = new MutantCompiler(null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("compilation");
    }

    [Test]
    public async Task Verify_MutationIsNull_ThrowsArgumentNullException()
    {
        var (compilation, tree) = CreateFixture();
        var compiler = new MutantCompiler(compilation);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = compiler.Verify(null!, tree, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("mutation");
    }

    [Test]
    public async Task Verify_OriginalTreeIsNull_ThrowsArgumentNullException()
    {
        var (compilation, tree) = CreateFixture();
        var compiler = new MutantCompiler(compilation);
        var mutation = CreateViableMutation(tree);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = compiler.Verify(mutation, null!, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("originalTree");
    }

    /// <summary>
    /// A tree that the compilation does not own cannot be swapped in, so the mutant is rejected instead
    /// of being verified against a compilation it has nothing to do with.
    /// </summary>
    [Test]
    public async Task Verify_OriginalTreeIsNotPartOfTheCompilation_ReturnsDoesNotCompile()
    {
        var (compilation, _) = CreateFixture();
        var foreign = CompilationFactory.Create(DoublerSource, assemblyName: "ForeignAssembly");
        var foreignTree = foreign.SyntaxTrees[0];
        var compiler = new MutantCompiler(compilation);

        var viability = compiler.Verify(CreateViableMutation(foreignTree), foreignTree, CancellationToken.None);

        _ = await Assert.That(compilation.SyntaxTrees.Contains(foreignTree)).IsFalse();
        _ = await Assert.That(viability).IsEqualTo(MutantViability.DoesNotCompile);
    }

    [Test]
    public async Task Verify_MutantDoesNotEvenParse_ReturnsDoesNotCompile()
    {
        var (compilation, tree) = CreateFixture();
        var compiler = new MutantCompiler(compilation);
        var mutation = CreateUnparsableMutation(tree);

        // The diagnostics have to be read from the replacement itself. Grafting it into the tree
        // attaches the trivia of the original node, and that drops the diagnostics the parser
        // recorded, which is exactly why the verification cannot rely on the mutated tree.
        var syntaxErrors = mutation
            .Replacement.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();

        var viability = compiler.Verify(mutation, tree, CancellationToken.None);

        _ = await Assert.That(syntaxErrors.Length).IsGreaterThan(0);
        _ = await Assert.That(viability).IsEqualTo(MutantViability.DoesNotCompile);
    }

    /// <summary>
    /// Only errors decide viability. A mutant whose binding produces warnings is still a mutant a test
    /// could kill, so reporting it as broken would silently drop a real mutation point.
    /// </summary>
    [Test]
    public async Task Verify_MutantOnlyProducesWarnings_ReturnsViable()
    {
        var compilation = CompilationFactory.Create(WarningSource);
        var tree = compilation.SyntaxTrees[0];
        var compiler = new MutantCompiler(compilation);
        var warningIds = compilation
            .GetSemanticModel(tree)
            .GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning)
            .Select(diagnostic => diagnostic.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var mutation = CreateSubtractionMutation(tree, ViableOperatorId);
        var viability = compiler.Verify(mutation, tree, CancellationToken.None);

        _ = await Assert.That(warningIds).Contains("CS0618");
        _ = await Assert.That(viability).IsEqualTo(MutantViability.Viable);
    }

    /// <summary>
    /// Two operators rewriting the very same node are two mutants. If the cache key ignored the operator,
    /// the second verdict would be the memoised first one.
    /// </summary>
    [Test]
    public async Task Verify_AnotherOperatorAtTheSameLocation_IsVerifiedOnItsOwn()
    {
        var (compilation, tree) = CreateFixture();
        var compiler = new MutantCompiler(compilation);
        var viable = CreateSubtractionMutation(tree, ViableOperatorId);
        var broken = CreateStringLiteralMutation(tree, BrokenOperatorId);

        var first = compiler.Verify(viable, tree, CancellationToken.None);
        var second = compiler.Verify(broken, tree, CancellationToken.None);

        _ = await Assert.That(broken.Location.SourceSpan).IsEqualTo(viable.Location.SourceSpan);
        _ = await Assert.That(first).IsEqualTo(MutantViability.Viable);
        _ = await Assert.That(second).IsEqualTo(MutantViability.DoesNotCompile);
    }

    /// <summary>
    /// The same operator at two locations of one file are two mutants. If the cache key ignored the span,
    /// the second verdict would be the memoised first one.
    /// </summary>
    [Test]
    public async Task Verify_TheSameOperatorAtAnotherLocation_IsVerifiedOnItsOwn()
    {
        var (compilation, tree) = CreateFixture();
        var compiler = new MutantCompiler(compilation);
        var viable = CreateSubtractionMutation(tree, SharedOperatorId);
        var broken = CreateBrokenMutation(tree, SharedOperatorId);

        var first = compiler.Verify(viable, tree, CancellationToken.None);
        var second = compiler.Verify(broken, tree, CancellationToken.None);

        _ = await Assert.That(broken.Location.SourceSpan).IsNotEqualTo(viable.Location.SourceSpan);
        _ = await Assert.That(first).IsEqualTo(MutantViability.Viable);
        _ = await Assert.That(second).IsEqualTo(MutantViability.DoesNotCompile);
    }

    /// <summary>
    /// The same operator at the same span in two different files are two mutants. If the cache key
    /// ignored the file path, the second verdict would be the memoised first one.
    /// </summary>
    [Test]
    public async Task Verify_TheSameOperatorAndSpanInAnotherFile_IsVerifiedOnItsOwn()
    {
        var compilation = CreateTwinFixture();
        var treeA = compilation.SyntaxTrees[0];
        var treeB = compilation.SyntaxTrees[1];
        var compiler = new MutantCompiler(compilation);
        var viable = CreateSubtractionMutation(treeA, SharedOperatorId);
        var broken = CreateStringLiteralMutation(treeB, SharedOperatorId);

        var first = compiler.Verify(viable, treeA, CancellationToken.None);
        var second = compiler.Verify(broken, treeB, CancellationToken.None);

        _ = await Assert.That(treeB.FilePath).IsNotEqualTo(treeA.FilePath);
        _ = await Assert.That(broken.Location.SourceSpan).IsEqualTo(viable.Location.SourceSpan);
        _ = await Assert.That(first).IsEqualTo(MutantViability.Viable);
        _ = await Assert.That(second).IsEqualTo(MutantViability.DoesNotCompile);
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
    /// Creates the two-file compilation whose marked additions share a source span.
    /// </summary>
    /// <returns>The created compilation.</returns>
    private static CSharpCompilation CreateTwinFixture() =>
        CompilationFactory.Create([(TwinPathA, TwinSourceA), (TwinPathB, TwinSourceB)]);

    /// <summary>
    /// Turns the marked <c>left + right</c> into <c>left - right</c>, which stays a legal program.
    /// </summary>
    /// <param name="tree">The tree holding the marked addition.</param>
    /// <returns>The mutation.</returns>
    private static Mutation CreateViableMutation(SyntaxTree tree) => CreateSubtractionMutation(tree, ViableOperatorId);

    /// <summary>
    /// Turns the marked addition into a subtraction under the given operator id.
    /// </summary>
    /// <param name="tree">The tree holding the marked addition.</param>
    /// <param name="operatorId">The operator id the mutation is cached under.</param>
    /// <returns>The mutation.</returns>
    private static Mutation CreateSubtractionMutation(SyntaxTree tree, string operatorId)
    {
        var original = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var replacement = SyntaxFactory.BinaryExpression(SyntaxKind.SubtractExpression, original.Left, original.Right);

        return new Mutation(MutationKind.ArithmeticOperator, operatorId, "+ => -", original, replacement);
    }

    /// <summary>
    /// Replaces the marked addition with a string literal, which keeps the span of the mutation point
    /// while making the surrounding method return the wrong type.
    /// </summary>
    /// <param name="tree">The tree holding the marked addition.</param>
    /// <param name="operatorId">The operator id the mutation is cached under.</param>
    /// <returns>The mutation.</returns>
    private static Mutation CreateStringLiteralMutation(SyntaxTree tree, string operatorId)
    {
        var original = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var replacement = SyntaxFactory.LiteralExpression(
            SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal("mutated")
        );

        return new Mutation(MutationKind.StringLiteral, operatorId, "+ => mutated", original, replacement);
    }

    /// <summary>
    /// Replaces the marked addition with an expression the parser could not read to the end, so that the
    /// mutated tree already carries a syntax error before anything is bound.
    /// </summary>
    /// <param name="tree">The tree holding the marked addition.</param>
    /// <returns>The mutation.</returns>
    private static Mutation CreateUnparsableMutation(SyntaxTree tree)
    {
        var original = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var replacement = SyntaxFactory.ParseExpression("left +");

        return new Mutation(MutationKind.ArithmeticOperator, SwapOperatorId, "+ => <broken>", original, replacement);
    }

    /// <summary>
    /// Replaces the numeric literal returned by <c>Constant</c> with a string literal, a rewrite no
    /// operator would ever produce and that therefore reliably breaks binding.
    /// </summary>
    /// <param name="tree">The tree holding the literal.</param>
    /// <returns>The mutation.</returns>
    private static Mutation CreateBrokenMutation(SyntaxTree tree) => CreateBrokenMutation(tree, BrokenOperatorId);

    /// <summary>
    /// Replaces the numeric literal returned by <c>Constant</c> with a string literal, under the given
    /// operator id.
    /// </summary>
    /// <param name="tree">The tree holding the literal.</param>
    /// <param name="operatorId">The operator id the mutation is cached under.</param>
    /// <returns>The mutation.</returns>
    private static Mutation CreateBrokenMutation(SyntaxTree tree, string operatorId)
    {
        var original = SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(
            tree,
            node => string.Equals(node.Token.ValueText, "7", StringComparison.Ordinal)
        );
        var replacement = SyntaxFactory.LiteralExpression(
            SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal("mutated")
        );

        return new Mutation(MutationKind.NumericLiteral, operatorId, "7 => mutated", original, replacement);
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
