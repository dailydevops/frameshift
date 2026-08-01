namespace NetEvolve.FrameShift.Mutations.Operators;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Mutations;
using NetEvolve.FrameShift.Mutations.Operators;
using NetEvolve.FrameShift.Tests.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the statement removal operator: the bare <c>return;</c>, loop <c>break</c>/<c>continue</c>,
/// <c>throw</c> statement and standalone invocation constructs, each with its removal happening when
/// safe and skipped when it would not compile or would be a no-op.
/// </summary>
public class StatementRemovalMutatorTests
{
    private const string ReturnNotLastSource = """
        using System;

        public static class Sample
        {
            public static void Run(bool flag)
            {
                if (flag)
                {
                    return;
                }

                Console.WriteLine("continue");
            }
        }
        """;

    private const string ReturnTrailingSource = """
        using System;

        public static class Sample
        {
            public static void Run()
            {
                Console.WriteLine("run");
                return;
            }
        }
        """;

    private const string ReturnWithExpressionSource = """
        public static class Sample
        {
            public static int Run(int value)
            {
                return value;
            }
        }
        """;

    private const string ReturnInNonVoidMethodSource = """
        public static class Sample
        {
            public static int Run()
            {
                return;
            }
        }
        """;

    private const string ReturnInLocalFunctionSource = """
        using System;

        public static class Sample
        {
            public static void Run(bool flag)
            {
                void Local()
                {
                    if (flag)
                    {
                        return;
                    }

                    Console.WriteLine("local");
                }

                Local();
            }
        }
        """;

    private const string ReturnInLambdaSource = """
        using System;

        public static class Sample
        {
            public static void Run(bool flag)
            {
                Action action = () =>
                {
                    if (flag)
                    {
                        return;
                    }

                    Console.WriteLine("lambda");
                };

                action();
            }
        }
        """;

    private const string BreakInForLoopSource = """
        public static class Sample
        {
            public static void Run()
            {
                for (var i = 0; i < 10; i++)
                {
                    if (i == 5)
                    {
                        break;
                    }
                }
            }
        }
        """;

    private const string BreakInSwitchSource = """
        public static class Sample
        {
            public static void Run(int value)
            {
                switch (value)
                {
                    case 1:
                        break;
                    default:
                        break;
                }
            }
        }
        """;

    private const string BreakInSwitchInsideLoopSource = """
        public static class Sample
        {
            public static void Run()
            {
                for (var i = 0; i < 10; i++)
                {
                    switch (i)
                    {
                        case 1:
                            break;
                        default:
                            break;
                    }
                }
            }
        }
        """;

    private const string BreakInLoopInsideSwitchSource = """
        public static class Sample
        {
            public static void Run(int value)
            {
                switch (value)
                {
                    case 1:
                        for (var i = 0; i < 10; i++)
                        {
                            if (i == 5)
                            {
                                /*!*/
                                break;
                            }
                        }
                        break;
                    default:
                        break;
                }
            }
        }
        """;

    private const string ContinueInWhileLoopSource = """
        public static class Sample
        {
            public static void Run(int limit)
            {
                var i = 0;
                while (i < limit)
                {
                    i++;
                    if (i == 5)
                    {
                        continue;
                    }
                }
            }
        }
        """;

    private const string ContinueOutsideLoopSource = """
        public static class Sample
        {
            public static void Run()
            {
                continue;
            }
        }
        """;

    private const string ContinueCrossingLocalFunctionBoundarySource = """
        public static class Sample
        {
            public static void Run()
            {
                for (var i = 0; i < 10; i++)
                {
                    void Local()
                    {
                        continue;
                    }

                    Local();
                }
            }
        }
        """;

    private const string ThrowNotLastSource = """
        using System;

        public static class Sample
        {
            public static int Run(bool flag)
            {
                if (flag)
                {
                    throw new InvalidOperationException();
                }

                return 1;
            }
        }
        """;

    private const string ThrowLastInNonVoidSource = """
        using System;

        public static class Sample
        {
            public static int Run(bool flag)
            {
                if (flag)
                {
                    return 1;
                }

                throw new InvalidOperationException();
            }
        }
        """;

    private const string ThrowOnlyStatementInNonVoidSource = """
        using System;

        public static class Sample
        {
            public static int Run()
            {
                throw new InvalidOperationException();
            }
        }
        """;

    private const string ThrowLastInVoidSource = """
        using System;

        public static class Sample
        {
            public static void Run(bool flag)
            {
                if (flag)
                {
                    Console.WriteLine("ok");
                    return;
                }

                throw new InvalidOperationException();
            }
        }
        """;

    private const string InvocationVoidCallSource = """
        using System;

        public static class Sample
        {
            public static void Run()
            {
                Console.WriteLine("hi");
            }
        }
        """;

    private const string InvocationNonVoidCallDiscardedSource = """
        public static class Sample
        {
            public static int Compute() => 42;

            public static void Run()
            {
                Compute();
            }
        }
        """;

    private const string InvocationWithRefArgumentSource = """
        public static class Sample
        {
            public static void Modify(ref int value) => value++;

            public static void Run()
            {
                var number = 1;
                Modify(ref number);
            }
        }
        """;

    private const string InvocationWithOutArgumentSource = """
        public static class Sample
        {
            public static void TryGet(out int value) => value = 1;

            public static void Run()
            {
                TryGet(out var number);
            }
        }
        """;

    private const string InvocationUnresolvedSymbolSource = """
        public static class Sample
        {
            public static void Run(dynamic value)
            {
                value.DoSomething();
            }
        }
        """;

    [Test]
    public async Task Metadata_Operator_DescribesStatementRemovalFamily()
    {
        var mutator = new StatementRemovalMutator();
        SyntaxKind[] supported = [.. mutator.SupportedSyntaxKinds];

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutator.Id).IsEqualTo("statement-removal");
            _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.StatementRemoval);
            _ = await Assert.That(supported).Count().IsEqualTo(5);
            _ = await Assert.That(supported).Contains(SyntaxKind.ReturnStatement);
            _ = await Assert.That(supported).Contains(SyntaxKind.BreakStatement);
            _ = await Assert.That(supported).Contains(SyntaxKind.ContinueStatement);
            _ = await Assert.That(supported).Contains(SyntaxKind.ThrowStatement);
            _ = await Assert.That(supported).Contains(SyntaxKind.ExpressionStatement);
        }
    }

    [Test]
    public async Task CreateMutations_UnsupportedSyntaxKind_ReturnsEmpty()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(ReturnNotLastSource);
        var mutator = new StatementRemovalMutator();
        var node = SyntaxNodeLocator.FindFirst<IfStatementSyntax>(tree);

        var mutations = mutator.CreateMutations(node, semanticModel, CancellationToken.None).ToList();

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ReturnNotLastStatementOfVoidMethod_RemovesIt()
    {
        var (tree, mutations) = Run<ReturnStatementSyntax>(ReturnNotLastSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(CompilationFactory.GetCompileErrors(Compile(ReturnNotLastSource))).IsEmpty();
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("statement-removal.return");
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("return; => (removed)");
            _ = await Assert.That(mutations[0].Replacement).IsTypeOf<EmptyStatementSyntax>();
            _ = await Assert
                .That(Rewrite(tree, mutations[0]))
                .Contains("if (flag)\n        {\n            ;\n        }");
            _ = await Assert.That(CompilationFactory.GetCompileErrors(Compile(Rewrite(tree, mutations[0])))).IsEmpty();
        }
    }

    [Test]
    public async Task CreateMutations_TrailingReturnOfMemberBody_ReturnsEmpty()
    {
        var (_, mutations) = Run<ReturnStatementSyntax>(ReturnTrailingSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ReturnWithExpression_ReturnsEmpty()
    {
        var (_, mutations) = Run<ReturnStatementSyntax>(ReturnWithExpressionSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ReturnInNonVoidMethod_ReturnsEmpty()
    {
        var (_, mutations) = Run<ReturnStatementSyntax>(ReturnInNonVoidMethodSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ReturnInLocalFunction_RemovesIt()
    {
        var (_, mutations) = Run<ReturnStatementSyntax>(ReturnInLocalFunctionSource);

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
    }

    [Test]
    public async Task CreateMutations_ReturnInLambda_RemovesIt()
    {
        var (_, mutations) = Run<ReturnStatementSyntax>(ReturnInLambdaSource);

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
    }

    [Test]
    public async Task CreateMutations_BreakInLoop_RemovesIt()
    {
        var (tree, mutations) = Run<BreakStatementSyntax>(BreakInForLoopSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("statement-removal.break");
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("break; => (removed)");
            _ = await Assert.That(CompilationFactory.GetCompileErrors(Compile(Rewrite(tree, mutations[0])))).IsEmpty();
        }
    }

    [Test]
    public async Task CreateMutations_BreakInSwitch_ReturnsEmpty()
    {
        var (_, mutations) = Run<BreakStatementSyntax>(BreakInSwitchSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_BreakInSwitchInsideLoop_ReturnsEmpty()
    {
        var (_, mutations) = Run<BreakStatementSyntax>(BreakInSwitchInsideLoopSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_BreakInLoopInsideSwitch_RemovesIt()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(BreakInLoopInsideSwitchSource);
        var node = SyntaxNodeLocator.FindMarked<BreakStatementSyntax>(tree);
        var mutator = new StatementRemovalMutator();

        var mutations = mutator.CreateMutations(node, semanticModel, CancellationToken.None).ToList();

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
    }

    [Test]
    public async Task CreateMutations_ContinueInLoop_RemovesIt()
    {
        var (tree, mutations) = Run<ContinueStatementSyntax>(ContinueInWhileLoopSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("statement-removal.continue");
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("continue; => (removed)");
            _ = await Assert.That(CompilationFactory.GetCompileErrors(Compile(Rewrite(tree, mutations[0])))).IsEmpty();
        }
    }

    [Test]
    public async Task CreateMutations_ContinueOutsideLoop_ReturnsEmpty()
    {
        var (_, mutations) = Run<ContinueStatementSyntax>(ContinueOutsideLoopSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ContinueCrossingLocalFunctionBoundary_ReturnsEmpty()
    {
        var (_, mutations) = Run<ContinueStatementSyntax>(ContinueCrossingLocalFunctionBoundarySource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ThrowNotLastStatementOfMemberBody_RemovesIt()
    {
        var (tree, mutations) = Run<ThrowStatementSyntax>(ThrowNotLastSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("statement-removal.throw");
            _ = await Assert.That(CompilationFactory.GetCompileErrors(Compile(Rewrite(tree, mutations[0])))).IsEmpty();
        }
    }

    [Test]
    public async Task CreateMutations_ThrowLastStatementOfNonVoidMethod_ReturnsEmpty()
    {
        var (_, mutations) = Run<ThrowStatementSyntax>(ThrowLastInNonVoidSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ThrowOnlyStatementInNonVoidMethod_ReturnsEmpty()
    {
        var (_, mutations) = Run<ThrowStatementSyntax>(ThrowOnlyStatementInNonVoidSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ThrowLastStatementOfVoidMethod_RemovesIt()
    {
        var (tree, mutations) = Run<ThrowStatementSyntax>(ThrowLastInVoidSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(CompilationFactory.GetCompileErrors(Compile(Rewrite(tree, mutations[0])))).IsEmpty();
        }
    }

    [Test]
    public async Task CreateMutations_StandaloneVoidInvocation_RemovesIt()
    {
        var (tree, mutations) = Run<ExpressionStatementSyntax>(InvocationVoidCallSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("statement-removal.invocation");
            _ = await Assert.That(CompilationFactory.GetCompileErrors(Compile(Rewrite(tree, mutations[0])))).IsEmpty();
        }
    }

    [Test]
    public async Task CreateMutations_DiscardedNonVoidInvocation_ReturnsEmpty()
    {
        var (_, mutations) = Run<ExpressionStatementSyntax>(InvocationNonVoidCallDiscardedSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_InvocationWithRefArgument_ReturnsEmpty()
    {
        var (_, mutations) = Run<ExpressionStatementSyntax>(InvocationWithRefArgumentSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_InvocationWithOutArgument_ReturnsEmpty()
    {
        var (_, mutations) = Run<ExpressionStatementSyntax>(InvocationWithOutArgumentSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_InvocationWithUnresolvedSymbol_ReturnsEmpty()
    {
        var (_, mutations) = Run<ExpressionStatementSyntax>(InvocationUnresolvedSymbolSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_NodeNull_ThrowsArgumentNullException()
    {
        var (_, semanticModel, _) = CompilationFactory.CreateWithModel(ReturnNotLastSource);
        var mutator = new StatementRemovalMutator();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(null!, semanticModel, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("node");
    }

    [Test]
    public async Task CreateMutations_SemanticModelNull_ThrowsArgumentNullException()
    {
        var (_, _, tree) = CompilationFactory.CreateWithModel(ReturnNotLastSource);
        var mutator = new StatementRemovalMutator();
        var node = SyntaxNodeLocator.FindFirst<ReturnStatementSyntax>(tree);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(node, null!, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("semanticModel");
    }

    [Test]
    public async Task CreateMutations_CancelledToken_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(ReturnNotLastSource);
        var mutator = new StatementRemovalMutator();
        var node = SyntaxNodeLocator.FindFirst<ReturnStatementSyntax>(tree);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = mutator.CreateMutations(node, semanticModel, cancellation.Token).ToList()
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    private static (SyntaxTree Tree, Mutation[] Mutations) Run<TNode>(string source)
        where TNode : SyntaxNode
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var mutator = new StatementRemovalMutator();
        var node = SyntaxNodeLocator.FindFirst<TNode>(tree);

        return (tree, [.. mutator.CreateMutations(node, semanticModel, CancellationToken.None)]);
    }

    private static string Rewrite(SyntaxTree tree, Mutation mutation) => mutation.ApplyTo(tree).ToString();

    private static CSharpCompilation Compile(string source) => CompilationFactory.Create(source);
}
