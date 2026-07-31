namespace NetEvolve.FrameShift.Tests.Unit.Mutations;

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using NetEvolve.FrameShift.Mutations;
using NetEvolve.FrameShift.Tests.Infrastructure;
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

    /// <summary>
    /// The span the cache key tests compare against, and the two components of it separately, so that a
    /// key differing only in the start or only in the length can be spelled out.
    /// </summary>
    private const int KeySpanStart = 10;

    private const int KeySpanLength = 5;

    /// <summary>
    /// The metadata name of the cache key, which is a nested type of <see cref="MutantCompiler" />.
    /// </summary>
    private const string KeyTypeName = "NetEvolve.FrameShift.Mutations.MutantCompiler+MutantKey";

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

    /// <summary>
    /// A file whose second member does not parse, while the marked addition in the first one is perfectly
    /// fine. Code being typed is a normal input for an analyzer, and grafting a flawless replacement into
    /// such a tree cannot repair it, so the mutant is rejected on the syntax of the mutated tree, before
    /// anything is bound. This fixture is deliberately absent from
    /// <see cref="Fixtures_EveryCompilation_CompilesWithoutErrors" />.
    /// </summary>
    private const string SyntaxErrorSource = """
        namespace Fixture;

        public static class Halfway
        {
            public static int Add(int left, int right) => /*!*/left + right;

            public static int Broken(int value) => value +
        }
        """;

    /// <summary>
    /// The cache key of <see cref="MutantCompiler" />. It is an implementation detail of the compiler, and
    /// the members its equality contract requires of a value type — <see cref="object.Equals(object)" />
    /// and the two equality operators — are not reached by the memoising dictionary, which compares keys
    /// through <see cref="IEquatable{T}" />. They are part of the contract nonetheless, so they are
    /// exercised here. Every member of the key is public, so nothing but the type itself has to be looked
    /// up, and a rename makes these tests fail loudly instead of silently passing.
    /// </summary>
    private static readonly Type _keyType = typeof(MutantCompiler).Assembly.GetType(KeyTypeName, throwOnError: true)!;

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
        await source.CancelAsyncCompat().ConfigureAwait(false);

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

    /// <summary>
    /// A mutant of a file that already carries a syntax error somewhere else is rejected on the syntax of
    /// the mutated tree. The replacement itself parses, so the earlier gate on the replacement lets it
    /// through, and asking the semantic model would only pile bound errors on top of a broken parse.
    /// </summary>
    [Test]
    public async Task Verify_MutatedTreeStillCarriesASyntaxError_ReturnsDoesNotCompile()
    {
        var compilation = CompilationFactory.Create(SyntaxErrorSource);
        var tree = compilation.SyntaxTrees[0];
        var compiler = new MutantCompiler(compilation);
        var mutation = CreateSubtractionMutation(tree, ViableOperatorId);

        var viability = compiler.Verify(mutation, tree, CancellationToken.None);

        _ = await Assert.That(HasError(mutation.Replacement.GetDiagnostics())).IsFalse();
        _ = await Assert.That(HasError(tree.GetDiagnostics())).IsTrue();
        _ = await Assert.That(viability).IsEqualTo(MutantViability.DoesNotCompile);
    }

    /// <summary>
    /// The verdicts of two operators rewriting the same location have to survive each other in both
    /// directions. A key that ignored the operator would hand the second caller the memoised verdict of
    /// the first one, and the mutation report would silently change.
    /// </summary>
    [Test]
    public async Task Verify_TheSameLocationUnderTwoOperators_KeepsBothVerdicts()
    {
        var (compilation, tree) = CreateFixture();
        var compiler = new MutantCompiler(compilation);
        var viable = CreateSubtractionMutation(tree, ViableOperatorId);
        var broken = CreateStringLiteralMutation(tree, BrokenOperatorId);

        var verdicts = new[]
        {
            compiler.Verify(viable, tree, CancellationToken.None),
            compiler.Verify(broken, tree, CancellationToken.None),
            compiler.Verify(viable, tree, CancellationToken.None),
            compiler.Verify(broken, tree, CancellationToken.None),
        };

        _ = await Assert.That(broken.Location.SourceSpan).IsEqualTo(viable.Location.SourceSpan);
        _ = await Assert
            .That(verdicts)
            .IsEquivalentTo(
                new[]
                {
                    MutantViability.Viable,
                    MutantViability.DoesNotCompile,
                    MutantViability.Viable,
                    MutantViability.DoesNotCompile,
                }
            );
    }

    [Test]
    public async Task MutantKey_TwoKeysDescribingTheSameMutant_AreEqualAndShareTheirHashCode()
    {
        var left = CreateKey(ViableOperatorId, TwinPathA, CreateSpan());
        var right = CreateKey(ViableOperatorId, TwinPathA, CreateSpan());

        _ = await Assert.That(TypedEquals(left, right)).IsTrue();
        _ = await Assert.That(left.Equals(right)).IsTrue();
        _ = await Assert.That(OperatorEquals(left, right)).IsTrue();
        _ = await Assert.That(OperatorNotEquals(left, right)).IsFalse();
        _ = await Assert.That(left.GetHashCode()).IsEqualTo(right.GetHashCode());
    }

    /// <summary>
    /// Every single component of the key identifies a mutant on its own. A key that dropped one of them
    /// would let two different mutants share a cached verdict.
    /// </summary>
    /// <param name="operatorId">The operator id of the other key.</param>
    /// <param name="filePath">The file path of the other key.</param>
    /// <param name="start">The span start of the other key.</param>
    /// <param name="length">The span length of the other key.</param>
    [Test]
    [Arguments(SwapOperatorId, TwinPathA, KeySpanStart, KeySpanLength)]
    [Arguments(ViableOperatorId, TwinPathB, KeySpanStart, KeySpanLength)]
    [Arguments(ViableOperatorId, TwinPathA, KeySpanStart + 1, KeySpanLength)]
    [Arguments(ViableOperatorId, TwinPathA, KeySpanStart, KeySpanLength + 1)]
    public async Task MutantKey_OneComponentDiffers_IsNotEqual(
        string operatorId,
        string filePath,
        int start,
        int length
    )
    {
        var key = CreateKey(ViableOperatorId, TwinPathA, CreateSpan());
        var other = CreateKey(operatorId, filePath, new TextSpan(start, length));

        _ = await Assert.That(TypedEquals(key, other)).IsFalse();
        _ = await Assert.That(key.Equals(other)).IsFalse();
        _ = await Assert.That(OperatorEquals(key, other)).IsFalse();
        _ = await Assert.That(OperatorNotEquals(key, other)).IsTrue();
    }

    [Test]
    public async Task MutantKey_EqualsWithNullOrAForeignType_IsFalse()
    {
        var key = CreateKey(ViableOperatorId, TwinPathA, CreateSpan());

        _ = await Assert.That(ObjectEquals(key, null)).IsFalse();
        _ = await Assert.That(ObjectEquals(key, TwinPathA)).IsFalse();
        _ = await Assert.That(ObjectEquals(key, CreateSpan())).IsFalse();
    }

    [Test]
    public async Task MutantKey_GetHashCode_IsStableAcrossCalls()
    {
        var key = CreateKey(ViableOperatorId, TwinPathA, CreateSpan());

        var hashes = Enumerable.Range(0, 5).Select(_ => key.GetHashCode()).Distinct().ToArray();

        _ = await Assert.That(hashes).IsEquivalentTo(new[] { key.GetHashCode() });
    }

    /// <summary>
    /// A tree without a file path is the normal shape of an in-memory tree, and a tree reporting no path at
    /// all has to be treated exactly like one reporting an empty path. Otherwise the hash of the key would
    /// depend on which of the two shapes the compilation happened to hand over.
    /// </summary>
    [Test]
    public async Task MutantKey_FilePathIsNull_IsTheKeyOfTheEmptyFilePath()
    {
        var withoutPath = CreateKey(ViableOperatorId, filePath: null, CreateSpan());
        var withEmptyPath = CreateKey(ViableOperatorId, string.Empty, CreateSpan());
        var withPath = CreateKey(ViableOperatorId, TwinPathA, CreateSpan());

        _ = await Assert.That(TypedEquals(withoutPath, withEmptyPath)).IsTrue();
        _ = await Assert.That(withoutPath.GetHashCode()).IsEqualTo(withEmptyPath.GetHashCode());
        _ = await Assert.That(TypedEquals(withoutPath, withPath)).IsFalse();
    }

    private static TextSpan CreateSpan() => new TextSpan(KeySpanStart, KeySpanLength);

    /// <summary>
    /// Creates a boxed cache key, which is as close as a test can get to a private nested struct.
    /// </summary>
    /// <param name="operatorId">The operator id of the mutation.</param>
    /// <param name="filePath">The file path of the mutated tree.</param>
    /// <param name="span">The source span the mutation rewrites.</param>
    /// <returns>The boxed key.</returns>
    private static object CreateKey(string operatorId, string? filePath, TextSpan span) =>
        Activator.CreateInstance(_keyType, new object?[] { operatorId, filePath, span })!;

    /// <summary>
    /// Calls the <see cref="IEquatable{T}" /> overload, which is the one the memoising dictionary uses.
    /// </summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The result of the comparison.</returns>
    private static bool TypedEquals(object left, object right) =>
        (bool)_keyType.GetMethod("Equals", new[] { _keyType })!.Invoke(left, new object?[] { right })!;

    /// <summary>
    /// Calls the <see cref="object.Equals(object)" /> override, which no production path reaches, because
    /// the memoising dictionary always goes through the typed overload of the equatable struct.
    /// </summary>
    /// <param name="left">The key to compare.</param>
    /// <param name="right">The value to compare it with, possibly of a foreign type or <see langword="null" />.</param>
    /// <returns>The result of the comparison.</returns>
    private static bool ObjectEquals(object left, object? right) =>
        (bool)_keyType.GetMethod("Equals", new[] { typeof(object) })!.Invoke(left, new[] { right })!;

    private static bool OperatorEquals(object left, object right) => InvokeOperator("op_Equality", left, right);

    private static bool OperatorNotEquals(object left, object right) => InvokeOperator("op_Inequality", left, right);

    private static bool InvokeOperator(string name, object left, object right) =>
        (bool)_keyType.GetMethod(name)!.Invoke(null, new object?[] { left, right })!;

    private static bool HasError(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

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
