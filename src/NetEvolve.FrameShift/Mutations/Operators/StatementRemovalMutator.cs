namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Removes a whole statement outright, by replacing it with an empty statement (<c>;</c>): a bare
/// <c>return;</c> inside a <see langword="void" /> returning member, a loop <see langword="break"/> or
/// <see langword="continue"/>, a <see langword="throw"/> statement, and a standalone invocation of a <see langword="void" />
/// returning method with no <see langword="ref"/> or <see langword="out"/> arguments.
/// </summary>
/// <remarks>
/// <para>
/// The four constructs are covered by one operator instead of four, because every one of them boils
/// down to the same shape: a statement that either can be dropped without changing what runs next
/// (an intentionally omitted early exit, a call whose result nobody looks at) or that changes what runs
/// next in a way a test ought to notice (an early return that should not have run, a loop that keeps
/// going where it should have stopped, a guard that stops throwing). The classification lives in the
/// per-construct guard, the removal itself is always the same node replacement.
/// </para>
/// <para>
/// A statement is "removed" by replacing it with <see cref="SyntaxFactory.EmptyStatement()" />, not by
/// deleting the node from its parent. <see cref="Mutation" /> models a mutation as exactly one original
/// node replaced by exactly one replacement node, so an outright removal - which would change the shape
/// of the parent list instead of replacing a node - does not fit; an empty statement is itself a
/// legitimate, compiling "this statement now does nothing" mutation and expresses the same intent.
/// </para>
/// <para>
/// Every guard below is a cheap, syntactic pre-check that rules out a mutant which is either guaranteed
/// not to compile or guaranteed to be a no-op, before the mutant ever reaches the compile-check
/// pipeline. The pipeline still runs afterwards and remains the final word on every case a syntactic
/// guard cannot decide.
/// </para>
/// </remarks>
internal sealed class StatementRemovalMutator : MutationOperatorBase
{
    private const string ReturnSuffix = "return";
    private const string BreakSuffix = "break";
    private const string ContinueSuffix = "continue";
    private const string ThrowSuffix = "throw";
    private const string InvocationSuffix = "invocation";

    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds =
    [
        SyntaxKind.ReturnStatement,
        SyntaxKind.BreakStatement,
        SyntaxKind.ContinueStatement,
        SyntaxKind.ThrowStatement,
        SyntaxKind.ExpressionStatement,
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="StatementRemovalMutator" /> class.
    /// </summary>
    public StatementRemovalMutator()
        : base("statement-removal", MutationKind.StatementRemoval, _supportedSyntaxKinds) { }

    /// <inheritdoc />
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        return node switch
        {
            ReturnStatementSyntax returnStatement => CreateForReturn(returnStatement, semanticModel, cancellationToken),
            BreakStatementSyntax breakStatement => CreateForBreak(breakStatement),
            ContinueStatementSyntax continueStatement => CreateForContinue(continueStatement),
            ThrowStatementSyntax throwStatement => CreateForThrow(throwStatement, semanticModel, cancellationToken),
            ExpressionStatementSyntax expressionStatement => CreateForInvocation(
                expressionStatement,
                semanticModel,
                cancellationToken
            ),
            _ => [],
        };
    }

    /// <summary>
    /// Removes a bare <c>return;</c> inside a <see langword="void" /> returning method, local function
    /// or lambda, unless it carries an expression, the containing member cannot be determined to be
    /// <see langword="void" />, or it is the trailing statement of the member's own body - a no-op mutant,
    /// since control already falls through to the same place once the member ends.
    /// </summary>
    private IEnumerable<Mutation> CreateForReturn(
        ReturnStatementSyntax returnStatement,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (returnStatement.Expression is not null)
        {
            yield break;
        }

        var owner = FindContainingExecutableMember(returnStatement);

        if (owner is not (MethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
        {
            yield break;
        }

        if (ReturnsVoid(owner, semanticModel, cancellationToken) != true)
        {
            yield break;
        }

        if (IsTrailingStatementOfMemberBody(returnStatement, owner))
        {
            yield break;
        }

        yield return Remove(returnStatement, ReturnSuffix, "return; => (removed)");
    }

    /// <summary>
    /// Removes a <see langword="break"/>, unless the nearest enclosing breakable construct - stopping the search at
    /// a member or lambda boundary - is a <see langword="switch"/> section rather than a loop; removing a
    /// switch-section <see langword="break"/> changes fall-through semantics this operator does not touch.
    /// </summary>
    private IEnumerable<Mutation> CreateForBreak(BreakStatementSyntax breakStatement)
    {
        var construct = FindNearestBreakableConstruct(breakStatement, includeSwitch: true);

        if (!IsLoop(construct))
        {
            yield break;
        }

        yield return Remove(breakStatement, BreakSuffix, "break; => (removed)");
    }

    /// <summary>
    /// Removes a <see langword="continue"/>, unless no enclosing loop can be found before crossing a member or
    /// lambda boundary.
    /// </summary>
    private IEnumerable<Mutation> CreateForContinue(ContinueStatementSyntax continueStatement)
    {
        var construct = FindNearestBreakableConstruct(continueStatement, includeSwitch: false);

        if (!IsLoop(construct))
        {
            yield break;
        }

        yield return Remove(continueStatement, ContinueSuffix, "continue; => (removed)");
    }

    /// <summary>
    /// Removes a <see langword="throw"/> statement, unless it is the trailing statement of the body of a member that
    /// does not return <see langword="void" /> - removing it would leave that code path without a
    /// required return, which the compiler would reject.
    /// </summary>
    private IEnumerable<Mutation> CreateForThrow(
        ThrowStatementSyntax throwStatement,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        var owner = FindContainingExecutableMember(throwStatement);

        if (
            owner is not null
            && ReturnsVoid(owner, semanticModel, cancellationToken) != true
            && IsTrailingStatementOfMemberBody(throwStatement, owner)
        )
        {
            yield break;
        }

        yield return Remove(throwStatement, ThrowSuffix, "throw ...; => (removed)");
    }

    /// <summary>
    /// Removes a standalone invocation statement, provided the invoked method is known to return
    /// <see langword="void" /> and none of its arguments are passed by <see langword="ref"/> or <see langword="out"/>.
    /// </summary>
    private IEnumerable<Mutation> CreateForInvocation(
        ExpressionStatementSyntax expressionStatement,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        if (expressionStatement.Expression is not InvocationExpressionSyntax invocation)
        {
            yield break;
        }

        if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
        {
            yield break;
        }

        if (!method.ReturnsVoid)
        {
            yield break;
        }

        var hasByRefArgument = invocation.ArgumentList.Arguments.Any(argument =>
            argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
            || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)
        );

        if (hasByRefArgument)
        {
            yield break;
        }

        yield return Remove(expressionStatement, InvocationSuffix, $"{invocation} => (removed)");
    }

    private Mutation Remove(StatementSyntax statement, string suffix, string displayName) =>
        CreateMutation(statement, SyntaxFactory.EmptyStatement(), suffix, displayName);

    /// <summary>
    /// Finds the innermost method, local function, lambda or accessor declaration owning the executable
    /// code <paramref name="node" /> lives in.
    /// </summary>
    private static SyntaxNode? FindContainingExecutableMember(SyntaxNode node) =>
        node.Ancestors()
            .FirstOrDefault(ancestor =>
                ancestor
                    is BaseMethodDeclarationSyntax
                        or AccessorDeclarationSyntax
                        or LocalFunctionStatementSyntax
                        or AnonymousFunctionExpressionSyntax
            );

    /// <summary>
    /// Resolves whether <paramref name="owner" /> returns <see langword="void" />, or <see langword="null" />
    /// if the symbol cannot be resolved.
    /// </summary>
    private static bool? ReturnsVoid(SyntaxNode owner, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        var method = owner is AnonymousFunctionExpressionSyntax lambda
            ? semanticModel.GetSymbolInfo(lambda, cancellationToken).Symbol as IMethodSymbol
            : semanticModel.GetDeclaredSymbol(owner, cancellationToken) as IMethodSymbol;

        return method?.ReturnsVoid;
    }

    /// <summary>
    /// Determines whether <paramref name="statement" /> is the last statement of the block that is
    /// directly the body of <paramref name="owner" />, in which case removing it changes nothing: control
    /// already falls through to the same place once the member ends.
    /// </summary>
    private static bool IsTrailingStatementOfMemberBody(StatementSyntax statement, SyntaxNode owner)
    {
        var body = GetBody(owner);

        return body is not null
            && statement.Parent == body
            && body.Statements.Count > 0
            && body.Statements[body.Statements.Count - 1] == statement;
    }

    private static BlockSyntax? GetBody(SyntaxNode owner) =>
        owner switch
        {
            BaseMethodDeclarationSyntax method => method.Body,
            AccessorDeclarationSyntax accessor => accessor.Body,
            LocalFunctionStatementSyntax localFunction => localFunction.Body,
            SimpleLambdaExpressionSyntax lambda => lambda.Block,
            ParenthesizedLambdaExpressionSyntax lambda => lambda.Block,
            AnonymousMethodExpressionSyntax anonymous => anonymous.Block,
            _ => null,
        };

    /// <summary>
    /// Walks upwards from <paramref name="node" />, without crossing a member or lambda boundary, and
    /// returns the nearest loop, or the nearest <see langword="switch"/> statement when <paramref name="includeSwitch" />
    /// is <see langword="true" />, whichever comes first.
    /// </summary>
    private static SyntaxNode? FindNearestBreakableConstruct(SyntaxNode node, bool includeSwitch)
    {
        foreach (var ancestor in node.Ancestors())
        {
            if (
                ancestor
                is BaseMethodDeclarationSyntax
                    or AccessorDeclarationSyntax
                    or LocalFunctionStatementSyntax
                    or AnonymousFunctionExpressionSyntax
            )
            {
                return null;
            }

            if (IsLoop(ancestor))
            {
                return ancestor;
            }

            if (includeSwitch && ancestor is SwitchStatementSyntax)
            {
                return ancestor;
            }
        }

        return null;
    }

    private static bool IsLoop(SyntaxNode? node) =>
        node
            is ForStatementSyntax
                or ForEachStatementSyntax
                or ForEachVariableStatementSyntax
                or WhileStatementSyntax
                or DoStatementSyntax;
}
