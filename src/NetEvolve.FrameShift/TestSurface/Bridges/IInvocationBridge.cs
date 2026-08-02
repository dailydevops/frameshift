namespace NetEvolve.FrameShift.TestSurface.Bridges;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Recognises a well-known test-harness pattern where an invocation in test code dispatches into a
/// production member from inside an external assembly, invisible to the ordinary reachability walk, and
/// synthesizes the reachability edge that dispatch would otherwise hide.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GeneratorDriverBridge" /> is the first implementation: it recognises the Roslyn
/// source-generator test harness pattern, where a <c>GeneratorDriver</c> invokes
/// <c>IIncrementalGenerator.Initialize</c> from inside the compiler's own assembly. This interface exists
/// so that <see cref="TestSurfaceCollector" /> can hold a list of such recognisers instead of hard-coding
/// one, the day a second pattern - a different test harness dispatching through a different kind of
/// external call - needs the same treatment.
/// </para>
/// <para>
/// The context a bridge resolves in <see cref="CreateContext" /> is deliberately untyped here: every
/// bridge needs a different set of well-known types, and a shared interface should not force a shape on
/// data only the bridge itself interprets. The three members are always called together, in the order
/// <see cref="CreateContext" />, <see cref="IsApplicable" />, then <see cref="FindBridgedMembers" />, and a
/// bridge's own implementation is free to give <c>context</c> its real type back with a pattern match.
/// </para>
/// </remarks>
internal interface IInvocationBridge
{
    /// <summary>
    /// Resolves whatever well-known types or other per-compilation state this bridge needs, once per
    /// compilation.
    /// </summary>
    /// <param name="compilation">The test compilation to resolve state against.</param>
    /// <returns>The resolved context, passed back unchanged to every other member of this interface.</returns>
    object? CreateContext(Compilation compilation);

    /// <summary>
    /// Determines whether this bridge can ever recognise anything in a compilation with this context, so
    /// that a compilation whose precondition is not met - typically because it does not reference the
    /// assembly the pattern depends on - skips every per-invocation check entirely.
    /// </summary>
    /// <param name="context">The context <see cref="CreateContext" /> produced.</param>
    /// <returns><see langword="true" /> if this bridge may match something; otherwise <see langword="false" />.</returns>
    bool IsApplicable(object? context);

    /// <summary>
    /// Finds the production members that <paramref name="invocation" /> bridges to, if it matches this
    /// bridge's recognised pattern.
    /// </summary>
    /// <param name="semanticModel">The semantic model of the test method being walked.</param>
    /// <param name="invocation">The candidate invocation expression.</param>
    /// <param name="invokedMethod">
    /// The method symbol the caller already resolved for <paramref name="invocation" />, reused here
    /// instead of asking the binder to resolve it a second time.
    /// </param>
    /// <param name="context">The context <see cref="CreateContext" /> produced.</param>
    /// <param name="cancellationToken">A token observed while resolving symbols.</param>
    /// <returns>The bridged members; empty when <paramref name="invocation" /> does not match.</returns>
    IEnumerable<IMethodSymbol> FindBridgedMembers(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        IMethodSymbol invokedMethod,
        object? context,
        CancellationToken cancellationToken
    );
}
