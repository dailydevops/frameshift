namespace NetEvolve.FrameShift.Execution;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

/// <summary>
/// The outcome of compiling one mutant all the way to bytes, as opposed to the analyzer's own
/// <c>MutantCompiler.Verify</c>, which only re-binds the mutated tree and never emits a program that
/// could actually run.
/// </summary>
internal sealed class MutantEmitResult
{
    private MutantEmitResult(bool success, byte[]? assemblyBytes, ImmutableArray<Diagnostic> diagnostics)
    {
        Success = success;
        AssemblyBytes = assemblyBytes;
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// Gets a value indicating whether the mutant compiled and emitted without an error diagnostic.
    /// </summary>
    public bool Success { get; }

    /// <summary>
    /// Gets the emitted assembly image, or <see langword="null" /> when <see cref="Success" /> is
    /// <see langword="false" />.
    /// </summary>
    public byte[]? AssemblyBytes { get; }

    /// <summary>
    /// Gets the diagnostics <c>Compilation.Emit</c> reported. Populated only when emission was attempted
    /// and failed; a mutant rejected before that point, because it does not even parse or bind, carries
    /// an empty set here.
    /// </summary>
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    /// <param name="assemblyBytes">The emitted assembly image.</param>
    /// <returns>The successful result.</returns>
    public static MutantEmitResult Emitted(byte[] assemblyBytes) =>
        new MutantEmitResult(success: true, assemblyBytes, ImmutableArray<Diagnostic>.Empty);

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    /// <param name="diagnostics">The diagnostics explaining the failure, possibly empty.</param>
    /// <returns>The failed result.</returns>
    public static MutantEmitResult Failed(ImmutableArray<Diagnostic> diagnostics) =>
        new MutantEmitResult(success: false, assemblyBytes: null, diagnostics);
}
