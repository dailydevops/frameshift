namespace NetEvolve.FrameShift.Tests.Unit.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Mutations;
using NetEvolve.FrameShift.Mutations.Operators;
using NetEvolve.FrameShift.Tests.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the <c>checked</c>/<c>unchecked</c> keyword swap, for both the expression form and the
/// statement block form.
/// </summary>
public class CheckedContextMutatorTests
{
    private const string ExpressionFixture = """
        namespace Fixtures;

        internal static class Overflow
        {
            internal static int Add(int left, int right) => /*!*/checked(left + right);
        }
        """;

    private const string UncheckedExpressionFixture = """
        namespace Fixtures;

        internal static class Overflow
        {
            internal static int Add(int left, int right) => /*!*/unchecked(left + right);
        }
        """;

    private const string StatementFixture = """
        namespace Fixtures;

        internal static class Overflow
        {
            internal static int Add(int left, int right)
            {
                var result = 0;
                /*!*/checked
                {
                    result = left + right;
                }
                return result;
            }
        }
        """;

    private const string UncheckedStatementFixture = """
        namespace Fixtures;

        internal static class Overflow
        {
            internal static int Add(int left, int right)
            {
                var result = 0;
                /*!*/unchecked
                {
                    result = left + right;
                }
                return result;
            }
        }
        """;

    private const string TriviaFixture = """
        namespace Fixtures;

        internal static class Overflow
        {
            internal static int Add(int left, int right) =>
                // leading
                /*!*/checked /* inner */(left + right); // tail
        }
        """;

    [Test]
    public async Task Metadata_Operator_ExposesIdKindAndSupportedKinds()
    {
        var mutator = new CheckedContextMutator();

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutator.Id).IsEqualTo("checked-context");
            _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.CheckedContext);
            _ = await Assert
                .That(mutator.SupportedSyntaxKinds)
                .IsEquivalentTo(
                    new[]
                    {
                        SyntaxKind.CheckedExpression,
                        SyntaxKind.UncheckedExpression,
                        SyntaxKind.CheckedStatement,
                        SyntaxKind.UncheckedStatement,
                    }
                );
        }
    }

    [Test]
    public async Task CreateMutations_CheckedExpression_SwapsToUnchecked()
    {
        var result = Mutate(ExpressionFixture);
        var mutation = result.Mutations.Single();

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutation.OperatorId).IsEqualTo("checked-context.checked-to-unchecked-expression");
            _ = await Assert.That(mutation.DisplayName).IsEqualTo("checked(...) => unchecked(...)");
            _ = await Assert.That(mutation.Kind).IsEqualTo(MutationKind.CheckedContext);
            _ = await Assert.That(mutation.Replacement.ToString()).IsEqualTo("unchecked(left + right)");
        }
    }

    [Test]
    public async Task CreateMutations_UncheckedExpression_SwapsToChecked()
    {
        var result = Mutate(UncheckedExpressionFixture);
        var mutation = result.Mutations.Single();

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutation.OperatorId).IsEqualTo("checked-context.unchecked-to-checked-expression");
            _ = await Assert.That(mutation.DisplayName).IsEqualTo("unchecked(...) => checked(...)");
            _ = await Assert.That(mutation.Replacement.ToString()).IsEqualTo("checked(left + right)");
        }
    }

    [Test]
    public async Task CreateMutations_CheckedStatement_SwapsToUnchecked()
    {
        var result = Mutate(StatementFixture);
        var mutation = result.Mutations.Single();

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutation.OperatorId).IsEqualTo("checked-context.checked-to-unchecked-statement");
            _ = await Assert.That(mutation.DisplayName).IsEqualTo("checked { } => unchecked { }");
            _ = await Assert.That(mutation.Kind).IsEqualTo(MutationKind.CheckedContext);
            _ = await Assert.That(mutation.Replacement.ToString()).StartsWith("unchecked");
        }
    }

    [Test]
    public async Task CreateMutations_UncheckedStatement_SwapsToChecked()
    {
        var result = Mutate(UncheckedStatementFixture);
        var mutation = result.Mutations.Single();

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutation.OperatorId).IsEqualTo("checked-context.unchecked-to-checked-statement");
            _ = await Assert.That(mutation.DisplayName).IsEqualTo("unchecked { } => checked { }");
            _ = await Assert.That(mutation.Replacement.ToString()).StartsWith("checked");
        }
    }

    [Test]
    public async Task ApplyTo_CheckedExpression_RewritesKeywordAndKeepsTrivia()
    {
        var result = Mutate(TriviaFixture);
        var mutation = result.Mutations.Single();

        var mutated = mutation.ApplyTo(result.Tree).ToString();

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutated).Contains("// leading");
            _ = await Assert.That(mutated).Contains("unchecked /* inner */(left + right); // tail");
        }
    }

    [Test]
    public async Task CreateMutations_CancelledToken_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(ExpressionFixture);
        var node = SyntaxNodeLocator.FindMarked<SyntaxNode>(tree);
        var mutator = new CheckedContextMutator();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = mutator.CreateMutations(node, semanticModel, cancellation.Token).ToList()
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    private static (ImmutableArray<Mutation> Mutations, SyntaxTree Tree, SyntaxNode Node) Mutate(string source)
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var errors = CompilationFactory.GetCompileErrors(compilation);
        if (!errors.IsEmpty)
        {
            throw new InvalidOperationException(
                $"The fixture does not compile: {DiagnosticAssertions.Describe(errors)}"
            );
        }

        var node = SyntaxNodeLocator.FindMarked<SyntaxNode>(tree);
        var mutator = new CheckedContextMutator();

        return ([.. mutator.CreateMutations(node, semanticModel, CancellationToken.None)], tree, node);
    }
}
