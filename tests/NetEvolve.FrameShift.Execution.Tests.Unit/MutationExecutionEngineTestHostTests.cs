namespace NetEvolve.FrameShift.Execution.Tests.Unit;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Mutations;

/// <summary>
/// Proves the second, deeper orchestration of <see cref="MutationExecutionEngine" /> at the unit tier: a
/// real subprocess, launched through <c>dotnet exec</c>, against a build output directory whose
/// production assembly was really swapped for a mutant on disk, including the path a hung host takes.
/// </summary>
public class MutationExecutionEngineTestHostTests
{
    private const string ProductionSource = """
        namespace Fixture;

        public sealed class Calculator
        {
            public int Add(int left, int right) => left + right;

            public int AlwaysZero() => 0;
        }
        """;

    private const string ProductionAssemblyFileName = "Production.dll";
    private const string TestHostAssemblyFileName = "TestHost.dll";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(500);

    [Test]
    public async Task ExecuteViaTestHostAsync_MutationOfTheExercisedMethod_TestHostExitsNonZero()
    {
        const string testHostSource = """
            var calculator = new Fixture.Calculator();

            return calculator.Add(2, 3) == 5 ? 0 : 1;
            """;

        using var workspace = await PrepareWorkspaceAsync(testHostSource).ConfigureAwait(false);
        var (compilation, tree, semanticModel) = CreateProduction();
        var mutation = FindMutation(tree, semanticModel, "Add", "+ => -");

        var result = await MutationExecutionEngine
            .ExecuteViaTestHostAsync(
                compilation,
                mutation,
                tree,
                workspace.Directory,
                ProductionAssemblyFileName,
                TestHostAssemblyFileName,
                DefaultTimeout
            )
            .ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.Verdict).IsEqualTo(MutantVerdict.Killed);
            _ = await Assert.That(result.Diagnostics).IsNotNull();
        }
    }

    [Test]
    public async Task ExecuteViaTestHostAsync_MutationOfAnUnrelatedMethod_TestHostExitsZero()
    {
        const string testHostSource = """
            var calculator = new Fixture.Calculator();

            return calculator.Add(2, 3) == 5 ? 0 : 1;
            """;

        using var workspace = await PrepareWorkspaceAsync(testHostSource).ConfigureAwait(false);
        var (compilation, tree, semanticModel) = CreateProduction();
        var mutation = FindMutation(tree, semanticModel, "AlwaysZero", "0 => 1");

        var result = await MutationExecutionEngine
            .ExecuteViaTestHostAsync(
                compilation,
                mutation,
                tree,
                workspace.Directory,
                ProductionAssemblyFileName,
                TestHostAssemblyFileName,
                DefaultTimeout
            )
            .ConfigureAwait(false);

        _ = await Assert.That(result.Verdict).IsEqualTo(MutantVerdict.Survived);
    }

    [Test]
    public async Task ExecuteViaTestHostAsync_TestHostHangs_IsReportedAsTimeout()
    {
        const string testHostSource = "System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);";

        using var workspace = await PrepareWorkspaceAsync(testHostSource).ConfigureAwait(false);
        var (compilation, tree, semanticModel) = CreateProduction();
        var mutation = FindMutation(tree, semanticModel, "AlwaysZero", "0 => 1");

        var result = await MutationExecutionEngine
            .ExecuteViaTestHostAsync(
                compilation,
                mutation,
                tree,
                workspace.Directory,
                ProductionAssemblyFileName,
                TestHostAssemblyFileName,
                ShortTimeout
            )
            .ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.Verdict).IsEqualTo(MutantVerdict.Timeout);
            _ = await Assert.That(result.Diagnostics).IsNull();
        }
    }

    [Test]
    public async Task ExecuteViaTestHostAsync_MutationThatFailsToBind_ReportsTheDiagnosticsInsteadOfDiscardingThem()
    {
        var (compilation, tree, _) = CreateProduction();
        var root = await tree.GetRootAsync().ConfigureAwait(false);
        var originalLiteral = root.DescendantNodes()
            .OfType<LiteralExpressionSyntax>()
            .Single(literal => string.Equals(literal.Token.ValueText, "0", StringComparison.Ordinal));
        var unboundReplacement = SyntaxFactory.IdentifierName("ThisSymbolDoesNotExistAnywhere");

        var mutation = new Mutation(
            MutationKind.NumericLiteral,
            "test.unbound-replacement",
            "0 => ThisSymbolDoesNotExistAnywhere",
            originalLiteral,
            unboundReplacement
        );

        var result = await MutationExecutionEngine
            .ExecuteViaTestHostAsync(
                compilation,
                mutation,
                tree,
                Path.GetTempPath(),
                ProductionAssemblyFileName,
                TestHostAssemblyFileName,
                DefaultTimeout
            )
            .ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.Verdict).IsEqualTo(MutantVerdict.BuildFailed);
            _ = await Assert.That(result.Diagnostics).IsNotNull();
            _ = await Assert.That(result.Diagnostics!).Contains("ThisSymbolDoesNotExistAnywhere");
        }
    }

    [Test]
    public async Task RunViaTestHostAsync_BothMutations_AggregatesIntoTheExpectedScore()
    {
        const string testHostSource = """
            var calculator = new Fixture.Calculator();

            return calculator.Add(2, 3) == 5 ? 0 : 1;
            """;

        using var workspace = await PrepareWorkspaceAsync(testHostSource).ConfigureAwait(false);
        var (compilation, tree, semanticModel) = CreateProduction();
        var mutations = new[]
        {
            FindMutation(tree, semanticModel, "Add", "+ => -"),
            FindMutation(tree, semanticModel, "AlwaysZero", "0 => 1"),
        };

        var score = await MutationExecutionEngine
            .RunViaTestHostAsync(
                compilation,
                mutations,
                tree,
                workspace.Directory,
                ProductionAssemblyFileName,
                TestHostAssemblyFileName,
                DefaultTimeout
            )
            .ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(score.Killed).IsEqualTo(1);
            _ = await Assert.That(score.Survived).IsEqualTo(1);
            _ = await Assert.That(score.BuildFailed).IsEqualTo(0);
        }
    }

    private static async Task<MutantSwapWorkspace> PrepareWorkspaceAsync(string testHostSource)
    {
        var (productionCompilation, _, _) = CreateProduction();
        var productionBytes = Emit(productionCompilation);

        var testHostCompilation = CreateTestHost(testHostSource, productionBytes);
        var testHostBytes = Emit(testHostCompilation);

        var stagingDirectory = Directory.CreateTempSubdirectory("frameshift-testhost-unit-staging-").FullName;

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, ProductionAssemblyFileName), productionBytes)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, TestHostAssemblyFileName), testHostBytes)
                .ConfigureAwait(false);

            var ownRuntimeConfigPath = Path.ChangeExtension(
                typeof(MutationExecutionEngineTestHostTests).Assembly.Location,
                ".runtimeconfig.json"
            );
            var testHostRuntimeConfigPath = Path.Combine(
                stagingDirectory,
                Path.ChangeExtension(TestHostAssemblyFileName, ".runtimeconfig.json")
            );
            File.Copy(ownRuntimeConfigPath, testHostRuntimeConfigPath, overwrite: true);

            return MutantSwapWorkspace.Prepare(stagingDirectory, ProductionAssemblyFileName, productionBytes);
        }
        finally
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    private static byte[] Emit(Compilation compilation)
    {
        using var stream = new MemoryStream();
        var emitResult = compilation.Emit(stream);

        return emitResult.Success
            ? stream.ToArray()
            : throw new InvalidOperationException(
                "Fixture failed to compile: " + string.Join("; ", emitResult.Diagnostics)
            );
    }

    private static (CSharpCompilation Compilation, SyntaxTree Tree, SemanticModel SemanticModel) CreateProduction()
    {
        var tree = CSharpSyntaxTree.ParseText(ProductionSource, path: "Production.cs");
        var compilation = CSharpCompilation.Create(
            "Production",
            [tree],
            RuntimeReferences.Default,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        return (compilation, tree, compilation.GetSemanticModel(tree));
    }

    private static CSharpCompilation CreateTestHost(string testHostSource, byte[] productionAssemblyBytes)
    {
        var mainTree = CSharpSyntaxTree.ParseText(testHostSource, path: "Program.cs");
        var references = RuntimeReferences.Default.Add(MetadataReference.CreateFromImage(productionAssemblyBytes));

        return CSharpCompilation.Create(
            "TestHost",
            [mainTree],
            references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication)
        );
    }

    private static Mutation FindMutation(
        SyntaxTree tree,
        SemanticModel semanticModel,
        string methodName,
        string displayName
    )
    {
        var root = tree.GetRoot();
        var method = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(candidate => string.Equals(candidate.Identifier.Text, methodName, StringComparison.Ordinal));

        var mutations = MutantGenerator
            .CreateMutations(root, semanticModel, CancellationToken.None)
            .Where(mutation => method.Span.Contains(mutation.Location.SourceSpan))
            .Where(mutation => string.Equals(mutation.DisplayName, displayName, StringComparison.Ordinal))
            .ToImmutableArray();

        return mutations.Length == 1
            ? mutations[0]
            : throw new InvalidOperationException(
                $"Expected exactly one '{displayName}' mutation inside '{methodName}', found {mutations.Length}."
            );
    }
}
