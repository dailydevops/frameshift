namespace NetEvolve.FrameShift.Tests.Unit;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Tests.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using NetEvolve.FrameShift.TestSurface.Bridges;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Exercises <see cref="GeneratorDriverBridge" /> through <see cref="TestSurfaceCollector" />: the real
/// pattern a source-generator test harness writes, compiled and walked exactly like every other fixture
/// of this test class, instead of calling the bridge's internal methods directly.
/// </summary>
/// <remarks>
/// The fixture references the real <c>Microsoft.CodeAnalysis</c> and <c>Microsoft.CodeAnalysis.CSharp</c>
/// assemblies this test project itself already carries, so no extra package reference is needed to
/// compile a production generator and a test driving it through <see cref="CSharpGeneratorDriver" />.
/// </remarks>
public class GeneratorDriverBridgeTests
{
    private const string ProductionAssemblyName = "GeneratorProductionAssembly";

    private const string ProductionSource = """
        namespace Fixture;

        using Microsoft.CodeAnalysis;

        public sealed class MyIncrementalGenerator : IIncrementalGenerator
        {
            public void Initialize(IncrementalGeneratorInitializationContext context)
            {
            }
        }

        public sealed class MySourceGenerator : ISourceGenerator
        {
            public void Initialize(GeneratorInitializationContext context)
            {
            }

            public void Execute(GeneratorExecutionContext context)
            {
            }
        }
        """;

    private const string TestSource = """
        namespace Tests;

        using Microsoft.CodeAnalysis;
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

            [Test]
            public void RunsSourceGeneratorDirectly()
            {
                CSharpCompilation compilation = CSharpCompilation.Create("Empty");
                Fixture.MySourceGenerator generator = new Fixture.MySourceGenerator();

                _ = CSharpGeneratorDriver.Create(generator).RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
            }

            [Test]
            public void RunsGeneratorsPassedAsAnInlineArray()
            {
                CSharpCompilation compilation = CSharpCompilation.Create("Empty");

                _ = CSharpGeneratorDriver
                    .Create(
                        new ISourceGenerator[]
                        {
                            new Fixture.MyIncrementalGenerator().AsSourceGenerator(),
                            new Fixture.MySourceGenerator(),
                        }
                    )
                    .RunGenerators(compilation);
            }

            [Test]
            public void GeneratorsArrayStoredInALocal_IsNotTraced()
            {
                CSharpCompilation compilation = CSharpCompilation.Create("Empty");
                ISourceGenerator[] generators = new ISourceGenerator[] { new Fixture.MySourceGenerator() };

                _ = CSharpGeneratorDriver.Create(generators).RunGenerators(compilation);
            }

            [Test]
            public void CallingRunGeneratorsOnSomethingThatIsNotAGeneratorDriver_IsIgnored()
            {
                FakeDriver fake = new FakeDriver();

                _ = fake.RunGenerators();
            }

            private sealed class FakeDriver
            {
                public int RunGenerators() => 0;
            }
        }
        """;

    private const string IncrementalInitializeId =
        "M:Fixture.MyIncrementalGenerator.Initialize(Microsoft.CodeAnalysis.IncrementalGeneratorInitializationContext)";

    private const string SourceGeneratorInitializeId =
        "M:Fixture.MySourceGenerator.Initialize(Microsoft.CodeAnalysis.GeneratorInitializationContext)";

    private const string SourceGeneratorExecuteId =
        "M:Fixture.MySourceGenerator.Execute(Microsoft.CodeAnalysis.GeneratorExecutionContext)";

    private const string FluentDriverTestId = "M:Tests.BridgeTests.RunsIncrementalGeneratorThroughFluentDriver";

    private const string AsSourceGeneratorTestId = "M:Tests.BridgeTests.RunsSourceGeneratorDirectly";

    private const string InlineArrayTestId = "M:Tests.BridgeTests.RunsGeneratorsPassedAsAnInlineArray";

    private const string LocalArrayTestId = "M:Tests.BridgeTests.GeneratorsArrayStoredInALocal_IsNotTraced";

    private const string FakeDriverTestId =
        "M:Tests.BridgeTests.CallingRunGeneratorsOnSomethingThatIsNotAGeneratorDriver_IsIgnored";

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
    /// The fluent <c>CSharpGeneratorDriver.Create(generator).RunGenerators(...)</c> shape bridges straight
    /// to <c>Initialize</c>, even though the test never spells that method name anywhere in its own
    /// source: the driver invokes it from inside an assembly this analyzer never inspects.
    /// </summary>
    [Test]
    public async Task FluentIncrementalGeneratorDriver_BridgesToInitialize()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        _ = await Assert.That(manifest.ReferencesByTest[FluentDriverTestId].Contains(IncrementalInitializeId)).IsTrue();
    }

    /// <summary>
    /// <c>ISourceGenerator</c> wrapped with <c>.AsSourceGenerator()</c> bridges to both of its entry
    /// points, <c>Initialize</c> and <c>Execute</c>.
    /// </summary>
    [Test]
    public async Task AsSourceGeneratorDriver_BridgesToInitializeAndExecute()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(manifest.ReferencesByTest[AsSourceGeneratorTestId].Contains(SourceGeneratorInitializeId))
                .IsTrue();
            _ = await Assert
                .That(manifest.ReferencesByTest[AsSourceGeneratorTestId].Contains(SourceGeneratorExecuteId))
                .IsTrue();
        }
    }

    /// <summary>
    /// An inline array of several generators bridges to the entry points of every one of them.
    /// </summary>
    [Test]
    public async Task InlineArrayOfGenerators_BridgesToEveryGeneratorsEntryPoints()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(manifest.ReferencesByTest[InlineArrayTestId].Contains(IncrementalInitializeId))
                .IsTrue();
            _ = await Assert
                .That(manifest.ReferencesByTest[InlineArrayTestId].Contains(SourceGeneratorInitializeId))
                .IsTrue();
            _ = await Assert
                .That(manifest.ReferencesByTest[InlineArrayTestId].Contains(SourceGeneratorExecuteId))
                .IsTrue();
        }
    }

    /// <summary>
    /// The documented limitation: an array assembled in an earlier statement and referenced by a local is
    /// not traced back to its elements, so none of the wrapped generator's entry points are bridged.
    /// </summary>
    [Test]
    public async Task GeneratorsArrayStoredInALocal_BridgesNothing()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(manifest.ReferencesByTest[LocalArrayTestId].Contains(SourceGeneratorInitializeId))
                .IsFalse();
            _ = await Assert
                .That(manifest.ReferencesByTest[LocalArrayTestId].Contains(SourceGeneratorExecuteId))
                .IsFalse();
        }
    }

    /// <summary>
    /// A method that happens to be named <c>RunGenerators</c> on a type that is not, and does not derive
    /// from, <see cref="GeneratorDriver" /> is not a match: the name alone is not the pattern.
    /// </summary>
    [Test]
    public async Task RunGeneratorsOnAnUnrelatedType_BridgesNothing()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(manifest.ReferencesByTest[FakeDriverTestId].Contains(IncrementalInitializeId))
                .IsFalse();
            _ = await Assert
                .That(manifest.ReferencesByTest[FakeDriverTestId].Contains(SourceGeneratorInitializeId))
                .IsFalse();
        }
    }

    /// <summary>
    /// A bridged reference is exactly as good as any other invocation: it counts towards reachability, but
    /// never towards the behavioral classification unless the test also calls a recognised assertion. None
    /// of this fixture's tests do, so <see cref="TestSurfaceManifest.BehavioralReferencedMemberIds" /> stays
    /// empty of every bridged member.
    /// </summary>
    [Test]
    public async Task BridgedReferences_AreNeverBehavioralWithoutAnAssertion()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        _ = await Assert.That(manifest.BehavioralReferencedMemberIds.Contains(IncrementalInitializeId)).IsFalse();
    }

    private static TestSurfaceManifest CollectSurface(Compilation test) =>
        TestSurfaceCollector.Collect(test, CreateRecognizer(test), CancellationToken.None);

    private static TUnitTestMethodRecognizer CreateRecognizer(Compilation compilation) =>
        new TUnitTestMethodRecognizer(TUnitTestFrameworkProbe.GetTestAttributeType(compilation));

    private static CSharpCompilation CreateProduction() =>
        CompilationFactory.Create(
            ProductionSource,
            ProductionAssemblyName,
            additionalReferences: RoslynReferences(),
            filePath: "Production.cs"
        );

    private static CSharpCompilation CreateTest(Compilation production) =>
        CompilationFactory.Create(
            TestSource,
            includeTUnit: true,
            additionalReferences: [production.ToMetadataReference(), .. RoslynReferences()],
            filePath: "BridgeTests.cs"
        );

    /// <summary>
    /// The metadata references the fixtures need to see the Roslyn generator API at all: this test
    /// project already carries every one of these assemblies, so no additional package is required.
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
