namespace NetEvolve.FrameShift.Execution.Tests.Unit;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Exercises <see cref="Program.Main" /> directly, in-process: the two paths that never need a real build
/// to observe, <c>--help</c> and a malformed command line, and the exit codes they report, plus a full
/// success run against a hand-emitted production and test host pair.
/// </summary>
/// <remarks>
/// Every test here mutates the process-wide <see cref="Console" /> streams, so none of them may run in
/// parallel with another test that also reads or writes them. <see cref="NotInParallelAttribute" /> pins
/// every test of this class to the same constraint key, which serialises them against each other without
/// affecting the rest of the suite. Redirecting the writer TUnit itself logs through is deliberate here,
/// not an oversight - it is the only way to observe what <see cref="Program.Main" /> actually writes.
/// </remarks>
[NotInParallel(nameof(ProgramTests))]
public class ProgramTests
{
    [Test]
    [Arguments("--help")]
    [Arguments("-h")]
    public async Task Main_HelpFlag_PrintsUsageAndSucceeds(string helpFlag)
    {
        using var output = new StringWriter();
        var originalOut = Console.Out;
#pragma warning disable TUnit0055 // Overwriting the Console writer can break TUnit logging
        Console.SetOut(output);
#pragma warning restore TUnit0055

        int exitCode;
        try
        {
            exitCode = await Program.Main([helpFlag]).ConfigureAwait(false);
        }
        finally
        {
#pragma warning disable TUnit0055 // Overwriting the Console writer can break TUnit logging
            Console.SetOut(originalOut);
#pragma warning restore TUnit0055
        }

        using (Assert.Multiple())
        {
            _ = await Assert.That(exitCode).IsEqualTo(0);
            _ = await Assert.That(output.ToString()).Contains(ExecutionCliOptions.Usage);
        }
    }

    [Test]
    public async Task Main_MissingRequiredArguments_PrintsErrorAndUsageAndFailsWithExitCodeTwo()
    {
        using var output = new StringWriter();
        var originalError = Console.Error;
#pragma warning disable TUnit0055 // Overwriting the Console writer can break TUnit logging
        Console.SetError(output);
#pragma warning restore TUnit0055

        int exitCode;
        try
        {
            exitCode = await Program.Main([]).ConfigureAwait(false);
        }
        finally
        {
#pragma warning disable TUnit0055 // Overwriting the Console writer can break TUnit logging
            Console.SetError(originalError);
#pragma warning restore TUnit0055
        }

        using (Assert.Multiple())
        {
            _ = await Assert.That(exitCode).IsEqualTo(2);
            _ = await Assert.That(output.ToString()).Contains("Missing required argument");
            _ = await Assert.That(output.ToString()).Contains(ExecutionCliOptions.Usage);
        }
    }

    [Test]
    public async Task Main_UnrecognisedFlag_FailsWithExitCodeTwo()
    {
        using var output = new StringWriter();
        var originalError = Console.Error;
#pragma warning disable TUnit0055 // Overwriting the Console writer can break TUnit logging
        Console.SetError(output);
#pragma warning restore TUnit0055

        int exitCode;
        try
        {
            exitCode = await Program.Main(["--not-a-real-flag", "value"]).ConfigureAwait(false);
        }
        finally
        {
#pragma warning disable TUnit0055 // Overwriting the Console writer can break TUnit logging
            Console.SetError(originalError);
#pragma warning restore TUnit0055
        }

        using (Assert.Multiple())
        {
            _ = await Assert.That(exitCode).IsEqualTo(2);
            _ = await Assert.That(output.ToString()).Contains("Unrecognised argument");
        }
    }

    private const string ProductionSource = """
        namespace Fixture;

        public sealed class Calculator
        {
            public int Add(int left, int right) => left + right;
        }
        """;
    private const string TestHostSource = """
        var calculator = new Fixture.Calculator();

        return calculator.Add(2, 3) == 5 ? 0 : 1;
        """;
    private const string ProductionAssemblyFileName = "Production.dll";
    private const string TestHostAssemblyFileName = "TestHost.dll";

    [Test]
    public async Task Main_ValidRun_CompletesAndReportsSuccessExitCode()
    {
        var directory = Directory.CreateTempSubdirectory("frameshift-program-unit-");

        try
        {
            var sourcePath = await PrepareTestOutputAsync(directory.FullName).ConfigureAwait(false);

            using var output = new StringWriter();
            var originalOut = Console.Out;
#pragma warning disable TUnit0055 // Overwriting the Console writer can break TUnit logging
            Console.SetOut(output);
#pragma warning restore TUnit0055

            int exitCode;
            try
            {
                exitCode = await Program
                    .Main([
                        "--test-output",
                        directory.FullName,
                        "--production-dll",
                        ProductionAssemblyFileName,
                        "--test-dll",
                        TestHostAssemblyFileName,
                        "--source",
                        sourcePath,
                    ])
                    .ConfigureAwait(false);
            }
            finally
            {
#pragma warning disable TUnit0055 // Overwriting the Console writer can break TUnit logging
                Console.SetOut(originalOut);
#pragma warning restore TUnit0055
            }

            using (Assert.Multiple())
            {
                _ = await Assert.That(exitCode).IsEqualTo(0);
                _ = await Assert.That(output.ToString()).Contains("Mutation score:");
            }
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    /// <summary>
    /// Builds a real, on-disk production assembly, a test host that calls it, and a
    /// <c>*.runtimeconfig.json</c> the installed runtime can actually load, inside
    /// <paramref name="testOutputDirectory" />.
    /// </summary>
    /// <returns>The path of the production source file on disk.</returns>
    private static async Task<string> PrepareTestOutputAsync(string testOutputDirectory)
    {
        var sourcePath = Path.Combine(testOutputDirectory, "Calculator.cs");
        await File.WriteAllTextAsync(sourcePath, ProductionSource).ConfigureAwait(false);

        var productionTree = CSharpSyntaxTree.ParseText(ProductionSource, path: "Production.cs");
        var productionCompilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(ProductionAssemblyFileName),
            [productionTree],
            RuntimeReferences.Default,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        var productionBytes = Emit(productionCompilation);
        await File.WriteAllBytesAsync(Path.Combine(testOutputDirectory, ProductionAssemblyFileName), productionBytes)
            .ConfigureAwait(false);

        var testHostTree = CSharpSyntaxTree.ParseText(TestHostSource, path: "Program.cs");
        var testHostCompilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(TestHostAssemblyFileName),
            [testHostTree],
            RuntimeReferences.Default.Add(MetadataReference.CreateFromImage(productionBytes)),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication)
        );
        await File.WriteAllBytesAsync(
                Path.Combine(testOutputDirectory, TestHostAssemblyFileName),
                Emit(testHostCompilation)
            )
            .ConfigureAwait(false);

        var ownRuntimeConfigPath = Path.ChangeExtension(typeof(ProgramTests).Assembly.Location, ".runtimeconfig.json");
        File.Copy(
            ownRuntimeConfigPath,
            Path.Combine(testOutputDirectory, Path.ChangeExtension(TestHostAssemblyFileName, ".runtimeconfig.json")),
            overwrite: true
        );

        return sourcePath;
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
}
