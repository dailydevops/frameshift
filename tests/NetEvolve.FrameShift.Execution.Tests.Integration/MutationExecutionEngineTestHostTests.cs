namespace NetEvolve.FrameShift.Execution.Tests.Integration;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Execution;
using NetEvolve.FrameShift.Execution.Tests.Unit;
using NetEvolve.FrameShift.Mutations;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Proves the second, deeper orchestration of <see cref="MutationExecutionEngine" />: a real subprocess,
/// launched through <c>dotnet exec</c> exactly like a CI runner would, against a build output directory
/// whose production assembly was really swapped for a mutant on disk. Nothing here is simulated - the
/// test host's exit code is read from a process this test genuinely spawned and genuinely waited for.
/// </summary>
/// <remarks>
/// The test host is a tiny console application, not a real test framework: no package in this
/// dependency-free project can host TUnit, xUnit, NUnit or MSTest as a subprocess without a full test
/// discovery pipeline behind it. What is being proven does not need one - <c>ProcessTestHostRunner</c>
/// only ever reads the process exit code, and a console application returning <c>0</c> or <c>1</c>
/// produces exactly the same signal a real test host's <c>0</c>-or-nonzero convention does. The host's
/// <c>*.runtimeconfig.json</c> is copied from this very test assembly's own build output, which is
/// guaranteed to already match the installed runtime, instead of being hand-written and risking a
/// version mismatch <c>dotnet exec</c> would reject.
/// </remarks>
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

    private const string TestHostSource = """
        var calculator = new Fixture.Calculator();

        return calculator.Add(2, 3) == 5 ? 0 : 1;
        """;

    private const string ProductionAssemblyFileName = "Production.dll";
    private const string TestHostAssemblyFileName = "TestHost.dll";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Test]
    public async Task Execute_MutationOfTheExercisedMethod_TestHostExitsNonZero()
    {
        using var workspace = await PrepareWorkspaceAsync().ConfigureAwait(false);
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
                Timeout
            )
            .ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.Verdict).IsEqualTo(MutantVerdict.Killed);
            _ = await Assert.That(result.Diagnostics).IsNotNull();
        }
    }

    [Test]
    public async Task Execute_MutationOfAnUnrelatedMethod_TestHostExitsZero()
    {
        using var workspace = await PrepareWorkspaceAsync().ConfigureAwait(false);
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
                Timeout
            )
            .ConfigureAwait(false);

        _ = await Assert.That(result.Verdict).IsEqualTo(MutantVerdict.Survived);
    }

    /// <summary>
    /// Builds a real, on-disk build output directory: the unmutated production assembly, a console test
    /// host that calls it, and a <c>*.runtimeconfig.json</c> the installed runtime can actually load.
    /// </summary>
    private static async Task<MutantSwapWorkspace> PrepareWorkspaceAsync()
    {
        var (productionCompilation, _, _) = CreateProduction();
        var productionBytes = Emit(productionCompilation);

        var testHostCompilation = CreateTestHost(productionBytes);
        var testHostBytes = Emit(testHostCompilation);

        var stagingDirectory = Directory.CreateTempSubdirectory("frameshift-testhost-staging-").FullName;

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

    private static CSharpCompilation CreateTestHost(byte[] productionAssemblyBytes)
    {
        var mainTree = CSharpSyntaxTree.ParseText(TestHostSource, path: "Program.cs");
        var references = RuntimeReferences.Default.Add(MetadataReference.CreateFromImage(productionAssemblyBytes));

        return CSharpCompilation.Create(
            "TestHost",
            [mainTree],
            references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication)
        );
    }

    /// <summary>
    /// Finds the one candidate mutation of <paramref name="methodName" /> whose display name is
    /// <paramref name="displayName" />, failing loudly if it is missing or ambiguous: a test that
    /// silently picked no mutation, or the wrong one, would prove nothing about execution at all.
    /// </summary>
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
