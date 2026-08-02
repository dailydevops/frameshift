namespace NetEvolve.FrameShift.Execution;

using System.IO;
using Microsoft.CodeAnalysis;
using NetEvolve.FrameShift.Mutations;

/// <summary>
/// Turns a candidate <see cref="Mutation" /> into a real, loadable assembly image, which is the one step
/// the analyzer itself deliberately never takes: <c>MutantCompiler.Verify</c> only re-binds the mutated
/// tree to reject a mutant that cannot compile, because full emission is far too expensive to repeat for
/// every mutation point of every build. Here it is the point: execution-based verification cannot answer
/// "would a test notice this mutant" without a program that can actually run.
/// </summary>
internal static class MutantAssemblyBuilder
{
    /// <summary>
    /// Applies <paramref name="mutation" /> to <paramref name="originalTree" />, swaps the mutated tree
    /// into <paramref name="compilation" /> and emits the result to an in-memory assembly image.
    /// </summary>
    /// <param name="compilation">The unmutated compilation the mutation is applied to.</param>
    /// <param name="mutation">The candidate mutation to apply.</param>
    /// <param name="originalTree">
    /// The unmutated syntax tree containing <see cref="Mutation.Original" />. It must be one of the
    /// trees of <paramref name="compilation" />.
    /// </param>
    /// <param name="cancellationToken">A token observed while binding and emitting.</param>
    /// <returns>
    /// The emitted assembly image, or the diagnostics explaining why the mutant does not compile.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="compilation" />, <paramref name="mutation" /> or <paramref name="originalTree" />
    /// is <see langword="null" />.
    /// </exception>
    public static MutantEmitResult TryEmit(
        Compilation compilation,
        Mutation mutation,
        SyntaxTree originalTree,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(originalTree);

        cancellationToken.ThrowIfCancellationRequested();

        Compilation mutatedCompilation;
        try
        {
            var mutatedTree = mutation.ApplyTo(originalTree);
            mutatedCompilation = compilation.ReplaceSyntaxTree(originalTree, mutatedTree);
        }
        catch (ArgumentException)
        {
            // The mutation does not belong to this tree, or the tree does not belong to this
            // compilation: the mutant cannot be built at all, which is reported the same way a mutant
            // that fails to emit is.
            return MutantEmitResult.Failed([]);
        }

        using var assemblyStream = new MemoryStream();
        var emitResult = mutatedCompilation.Emit(assemblyStream, cancellationToken: cancellationToken);

        return emitResult.Success
            ? MutantEmitResult.Emitted(assemblyStream.ToArray())
            : MutantEmitResult.Failed(emitResult.Diagnostics);
    }
}
