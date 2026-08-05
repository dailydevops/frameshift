namespace NetEvolve.FrameShift.Tests.Integration.Analyzers;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using NetEvolve.FrameShift.Generation;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using NetEvolve.FrameShift.TestSurface.Bridges;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Drives <see cref="GeneratorDriverBridge" /> through the real
/// <see cref="TestSurfaceManifestGenerator" /> pipeline, the way production code actually reaches it,
/// instead of through <see cref="TestSurfaceCollector" /> called directly by a unit test.
/// </summary>
/// <remarks>
/// <para>
/// <c>tests/NetEvolve.Frameshift.Tests.Unit/TestSurface/Bridges/GeneratorDriverBridgeTests.cs</c> already
/// proves every shape the bridge recognises — the fluent driver, <c>.AsSourceGenerator()</c>, an inline
/// array of generators, the local-array limitation, an unrelated <c>RunGenerators</c> look-alike — by
/// calling <see cref="TestSurfaceCollector.Collect" /> directly. None of those calls ever runs
/// <see cref="TestSurfaceManifestGenerator" /> itself, so the generator's own resolution of the bridge
/// through the shared bridge list of <see cref="TestSurfaceCollector" /> — building the
/// <see cref="GeneratorDriverBridge.Context" />, testing <c>IsApplicable</c>, and feeding the result back
/// into the emitted manifest — never executes under Integration-project coverage at all. This class closes
/// that gap by running the one shape that matters most, the fluent
/// <c>CSharpGeneratorDriver.Create(generator).RunGenerators(...)</c> pattern, through the real generator via
/// <see cref="GeneratorRunner" />, and reading the bridged entry point back out of the emitted manifest.
/// </para>
/// <para>
/// The fixture references the real <c>Microsoft.CodeAnalysis</c> and <c>Microsoft.CodeAnalysis.CSharp</c>
/// assemblies this test project itself already carries as package references, so no extra package is
/// needed to compile a production generator and a test driving it through <see cref="CSharpGeneratorDriver" />.
/// The reference set of every other fixture in this suite deliberately excludes them — see the remarks of
/// <c>ReferenceAssemblies</c> — so they are added back explicitly, exactly as the unit-level counterpart
/// does.
/// </para>
/// </remarks>
public class GeneratorDriverBridgeIntegrationTests
{
    private const string ProductionAssemblyName = "GeneratorProductionAssembly";
    private const string TestAssemblyName = "GeneratorTestAssembly";
    private const string ProductionPath = "Production.cs";
    private const string TestPath = "BridgeTests.cs";

    private const string ProductionSource = """
        namespace Fixture;

        using Microsoft.CodeAnalysis;

        public sealed class MyIncrementalGenerator : IIncrementalGenerator
        {
            public void Initialize(IncrementalGeneratorInitializationContext context)
            {
            }
        }
        """;

    private const string TestSource = """
        namespace Tests;

        using Microsoft.CodeAnalysis.CSharp;
        using TUnit.Core;

        public class BridgeTests
        {
            [Test]
            public void RunsIncrementalGeneratorThroughFluentDriver()
            {
                CSharpCompilation compilation = CSharpCompilation.Create("Empty");

                _ = CSharpGeneratorDriver.Create(new Fixture.MyIncrementalGenerator()).RunGenerators(compilation);
            }
        }
        """;

    private const string InitializeId =
        "M:Fixture.MyIncrementalGenerator.Initialize(Microsoft.CodeAnalysis.IncrementalGeneratorInitializationContext)";

    private const string DriverTestId = "M:Tests.BridgeTests.RunsIncrementalGeneratorThroughFluentDriver";

    [Test]
    public async Task Fixtures_BothAssemblies_CompileWithoutErrors()
    {
        var production = CreateProduction();
        var test = CreateTest(production);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(production)))
                .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
            _ = await Assert
                .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(test)))
                .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    /// <summary>
    /// The real generator resolves <see cref="GeneratorDriverBridge.Context" />, finds it applicable to a
    /// compilation that references the Roslyn generator API, and records the bridged <c>Initialize</c>
    /// entry point in the manifest it emits — under the very test whose own syntax never spells that method
    /// name anywhere. Without this bridge the entry point would never be reachable at all, and the
    /// production analyzer would report it as an uncovered mutation point although a test exercises it
    /// through the driver.
    /// </summary>
    [Test]
    public async Task Generate_FluentGeneratorDriverPattern_RecordsTheBridgedInitializeMethod()
    {
        var test = CreateTest(CreateProduction());
        var text = Generate(test);
        var (success, error, manifest) = Read(text);

        using (Assert.Multiple())
        {
            _ = await Assert.That(success).IsTrue();
            _ = await Assert.That(error).IsEqualTo(string.Empty);
            _ = await Assert.That(manifest.TestMethodIds.Contains(DriverTestId)).IsTrue();
            _ = await Assert.That(manifest.ReferencesByTest[DriverTestId].Contains(InitializeId)).IsTrue();
        }
    }

    /// <summary>
    /// Two runs of the same compilation through the real generator still agree on the bridged reference,
    /// which is what an incremental generator promises: the bridge itself keeps no state between runs, and
    /// nothing about resolving its context depends on the order invocations are walked in.
    /// </summary>
    [Test]
    public async Task Generate_SameCompilationTwice_AgreeOnTheBridgedReference()
    {
        var test = CreateTest(CreateProduction());

        var first = Generate(test);
        var second = Generate(test);

        _ = await Assert.That(second).IsEqualTo(first);
    }

    private static GeneratorRunner.Output Run(Compilation compilation) =>
        GeneratorRunner.Run(new TestSurfaceManifestGenerator(), compilation);

    private static string Generate(Compilation compilation) =>
        Run(compilation).TextOf(TestSurfaceManifestGenerator.HintName);

    /// <summary>
    /// Parses the generated file the way the MSBuild target does: drop the first and the last line, hand
    /// the rest to the reader.
    /// </summary>
    /// <param name="generated">The content of the generated source file.</param>
    /// <returns>Whether the text parsed, the reported error and the parsed manifest.</returns>
    private static (bool Success, string Error, TestSurfaceManifest Manifest) Read(string generated)
    {
        var inner = string.Join("\n", Lines(generated).Skip(1).SkipLast(1)) + "\n";
        var success = TestSurfaceManifestReader.TryRead(SourceText.From(inner), out var manifest, out var error);

        return (success, error ?? string.Empty, manifest);
    }

    /// <summary>
    /// Splits the generated text into its lines, dropping the empty remainder behind the trailing line
    /// feed, which is the end of the last line and not a line of its own.
    /// </summary>
    /// <param name="text">The generated text.</param>
    /// <returns>The lines, without their line endings.</returns>
    private static ImmutableArray<string> Lines(string text)
    {
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();

        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return [.. lines];
    }

    private static CSharpCompilation CreateProduction() =>
        CompilationFactory.Create(
            ProductionSource,
            TestFramework.None,
            ProductionAssemblyName,
            additionalReferences: RoslynReferences(),
            filePath: ProductionPath
        );

    private static CSharpCompilation CreateTest(Compilation production) =>
        CompilationFactory.Create(
            TestSource,
            TestFramework.TUnit,
            TestAssemblyName,
            additionalReferences: [production.ToMetadataReference(), .. RoslynReferences()],
            filePath: TestPath
        );

    /// <summary>
    /// The metadata references the fixtures need to see the Roslyn generator API at all: this test project
    /// already carries every one of these assemblies as package references, so no additional package is
    /// required, but the default reference set every other fixture builds against deliberately excludes
    /// them.
    /// </summary>
    /// <remarks>
    /// <c>System.Collections.Immutable</c> is not part of the default reference-assembly set on the
    /// .NET Framework target frameworks this project builds for, even though it is on every modern .NET
    /// target. <see cref="IncrementalGeneratorInitializationContext" /> and friends expose members typed
    /// through it, so without this reference a .NET Framework compilation of the fixture fails with
    /// <c>CS0012</c> - the type is used but its defining assembly was never named.
    /// </remarks>
    private static MetadataReference[] RoslynReferences() =>
        [
            MetadataReference.CreateFromFile(typeof(IIncrementalGenerator).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(CSharpGeneratorDriver).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Immutable.ImmutableArray<>).Assembly.Location),
        ];
}
