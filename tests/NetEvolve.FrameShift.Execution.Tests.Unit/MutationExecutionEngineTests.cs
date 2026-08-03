namespace NetEvolve.FrameShift.Execution.Tests.Unit;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Execution;
using NetEvolve.FrameShift.Mutations;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Proves that <see cref="MutationExecutionEngine" /> is genuinely execution-based: it compiles a real
/// mutant to IL, loads it into an isolated process boundary, and runs real code against it, instead of
/// reasoning about the mutation statically the way the analyzer's own heuristics do.
/// </summary>
/// <remarks>
/// The fixture is deliberately small and dependency-free: the "test" the engine runs is a plain method
/// that throws on failure, no test framework attributes or assertion library involved, matching the
/// narrow method convention <see cref="IsolatedAssemblyRunner" /> documents. What matters here is that
/// the <em>same</em> production method, mutated two different ways, produces two different real
/// verdicts - one a real test genuinely fails against, one it does not - which is precisely the
/// distinction static analysis cannot make.
/// </remarks>
public class MutationExecutionEngineTests
{
    private const string Source = """
        namespace Fixture;

        public sealed class Calculator
        {
            public int Add(int left, int right) => left + right;

            public int AlwaysZero() => 0;
        }

        public sealed class CalculatorTests
        {
            public void Add_ReturnsTheSum()
            {
                var result = new Calculator().Add(2, 3);

                if (result != 5)
                {
                    throw new System.Exception("Expected 5, got " + result);
                }
            }
        }
        """;

    private const string TestTypeFullName = "Fixture.CalculatorTests";
    private const string TestMethodName = "Add_ReturnsTheSum";

    [Test]
    public async Task Fixture_Compiles_WithoutErrors()
    {
        var (compilation, _, _) = CreateFixture();
        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        _ = await Assert.That(errors).IsEmpty();
    }

    /// <summary>
    /// The acceptance criterion of this whole project: a mutation of the method the test actually
    /// exercises, with an input the mutation changes the result for, makes the real test genuinely fail
    /// when it is really executed. This is not a static prediction - the mutant assembly is loaded and
    /// its <c>Add</c> method really returns <c>-1</c> instead of <c>5</c> for <c>(2, 3)</c>, and the test
    /// method really throws because of it.
    /// </summary>
    [Test]
    public async Task Execute_MutationOfTheExercisedMethod_IsGenuinelyKilled()
    {
        var (compilation, tree, semanticModel) = CreateFixture();
        var mutation = FindMutation(tree, semanticModel, "Add", "+ => -");

        var result = MutationExecutionEngine.Execute(compilation, mutation, tree, TestTypeFullName, TestMethodName);

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.Verdict).IsEqualTo(MutantVerdict.Killed);
            _ = await Assert.That(result.Failure).IsNotNull();
        }
    }

    /// <summary>
    /// The counter-example: a mutation of a method the test never calls at all cannot be noticed by that
    /// test, no matter what the mutation does. The mutant assembly is just as real as the killed one -
    /// its <c>AlwaysZero</c> really returns <c>1</c> instead of <c>0</c> - the test genuinely runs against
    /// it and genuinely passes anyway, because it never invokes the mutated member.
    /// </summary>
    [Test]
    public async Task Execute_MutationOfAnUnrelatedMethod_GenuinelySurvives()
    {
        var (compilation, tree, semanticModel) = CreateFixture();
        var mutation = FindMutation(tree, semanticModel, "AlwaysZero", "0 => 1");

        var result = MutationExecutionEngine.Execute(compilation, mutation, tree, TestTypeFullName, TestMethodName);

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.Verdict).IsEqualTo(MutantVerdict.Survived);
            _ = await Assert.That(result.Failure).IsNull();
        }
    }

    /// <summary>
    /// The batch orchestrator aggregates exactly the two individual verdicts above into a real mutation
    /// score: one real kill out of two real mutants is <c>0.5</c>, not a static estimate of it.
    /// </summary>
    [Test]
    public async Task Run_BothMutations_AggregatesIntoTheExpectedScore()
    {
        var (compilation, tree, semanticModel) = CreateFixture();
        var mutations = new[]
        {
            FindMutation(tree, semanticModel, "Add", "+ => -"),
            FindMutation(tree, semanticModel, "AlwaysZero", "0 => 1"),
        };

        var score = MutationExecutionEngine.Run(compilation, mutations, tree, TestTypeFullName, TestMethodName);

        using (Assert.Multiple())
        {
            _ = await Assert.That(score.Killed).IsEqualTo(1);
            _ = await Assert.That(score.Survived).IsEqualTo(1);
            _ = await Assert.That(score.BuildFailed).IsEqualTo(0);
            _ = await Assert.That(score.Score).IsEqualTo(0.5);
        }
    }

    /// <summary>
    /// A mutation whose <see cref="Mutation.Original" /> node does not belong to the tree it is applied
    /// to cannot be built at all: <see cref="Mutation.ApplyTo" /> rejects it before there is ever a
    /// mutated compilation to emit. This is reported the same way a mutant that emits but fails is, not
    /// as an exception the caller has to guard against.
    /// </summary>
    [Test]
    public async Task Execute_MutationNotBelongingToTheTree_IsReportedAsBuildFailed()
    {
        var (compilation, tree, _) = CreateFixture();
        var unrelatedNode = SyntaxFactory.ClassDeclaration("Unrelated");

        var mutation = new Mutation(
            MutationKind.ArithmeticOperator,
            "test.unrelated-node",
            "unrelated",
            unrelatedNode,
            unrelatedNode
        );

        var result = MutationExecutionEngine.Execute(compilation, mutation, tree, TestTypeFullName, TestMethodName);

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.Verdict).IsEqualTo(MutantVerdict.BuildFailed);
            _ = await Assert.That(result.Failure).IsNull();
        }
    }

    private static (CSharpCompilation Compilation, SyntaxTree Tree, SemanticModel SemanticModel) CreateFixture()
    {
        var tree = CSharpSyntaxTree.ParseText(Source, path: "Fixture.cs");
        var compilation = CSharpCompilation.Create(
            "NetEvolve.FrameShift.Tests.Execution.Dogfood",
            [tree],
            RuntimeReferences.Default,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        return (compilation, tree, compilation.GetSemanticModel(tree));
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
