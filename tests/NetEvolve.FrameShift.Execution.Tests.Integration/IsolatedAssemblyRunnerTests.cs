namespace NetEvolve.FrameShift.Execution.Tests.Integration;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Execution.Tests.Unit;

/// <summary>
/// Exercises <see cref="IsolatedAssemblyRunner" /> against an async test method, the branch
/// <see cref="MutationExecutionEngineTests" />'s synchronous fixture never reaches: a method returning a
/// <see cref="Task" /> is awaited synchronously before a verdict is produced, and a fault on that task
/// surfaces as the same kind of failure a synchronous throw does.
/// </summary>
public class IsolatedAssemblyRunnerTests
{
    private const string Source = """
        namespace Fixture;

        public sealed class AsyncTests
        {
            public async System.Threading.Tasks.Task Passes()
            {
                await System.Threading.Tasks.Task.Delay(1).ConfigureAwait(false);
            }

            public async System.Threading.Tasks.Task Fails()
            {
                await System.Threading.Tasks.Task.Delay(1).ConfigureAwait(false);

                throw new System.InvalidOperationException("async failure");
            }
        }
        """;

    private const string TypeFullName = "Fixture.AsyncTests";

    [Test]
    public async Task InvokeParameterlessTest_AsyncMethodCompletesSuccessfully_ReportsPassed()
    {
        var result = IsolatedAssemblyRunner.InvokeParameterlessTest(Emit(), TypeFullName, "Passes");

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.Outcome).IsEqualTo(TestOutcome.Passed);
            _ = await Assert.That(result.Failure).IsNull();
        }
    }

    [Test]
    public async Task InvokeParameterlessTest_AsyncMethodFaults_ReportsFailedWithTheFault()
    {
        var result = IsolatedAssemblyRunner.InvokeParameterlessTest(Emit(), TypeFullName, "Fails");

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.Outcome).IsEqualTo(TestOutcome.Failed);
            _ = await Assert.That(result.Failure).IsNotNull();
            _ = await Assert.That(result.Failure).IsTypeOf<InvalidOperationException>();
        }
    }

    private static byte[] Emit()
    {
        var tree = CSharpSyntaxTree.ParseText(Source, path: "AsyncTests.cs");
        var compilation = CSharpCompilation.Create(
            "NetEvolve.FrameShift.Execution.Tests.Integration.AsyncDogfood",
            [tree],
            RuntimeReferences.Default,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        using var stream = new MemoryStream();
        var emitResult = compilation.Emit(stream);

        return emitResult.Success
            ? stream.ToArray()
            : throw new InvalidOperationException(
                "Fixture failed to compile: " + string.Join("; ", emitResult.Diagnostics)
            );
    }
}
