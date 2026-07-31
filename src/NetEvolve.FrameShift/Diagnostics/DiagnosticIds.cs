namespace NetEvolve.FrameShift.Diagnostics;

/// <summary>
/// The stable identifiers of all diagnostics reported by FrameShift.
/// </summary>
internal static class DiagnosticIds
{
    /// <summary>
    /// The category shared by every FrameShift diagnostic.
    /// </summary>
    public const string Category = "FrameShift";

    /// <summary>
    /// <c>FSH0001</c>: a mutation point is not reachable from any test.
    /// </summary>
    public const string UnreachableMutationPoint = "FSH0001";

    /// <summary>
    /// <c>FSH0002</c>: a mutant cannot change observable behaviour and is therefore trivial.
    /// </summary>
    public const string TrivialMutant = "FSH0002";

    /// <summary>
    /// <c>FSH0003</c>: the test-surface manifest is missing or malformed.
    /// </summary>
    public const string InvalidTestSurfaceManifest = "FSH0003";

    /// <summary>
    /// <c>FSH0004</c>: a test method does not reference any production member.
    /// </summary>
    public const string TestWithoutProductionReference = "FSH0004";

    /// <summary>
    /// <c>FSH0006</c>: a mutation point is reached by exactly one test case.
    /// </summary>
    /// <remarks>
    /// <c>FSH0005</c> is deliberately absent here. It is the setup warning the MSBuild assets of the
    /// package emit, not an analyzer diagnostic, so it is neither described by a
    /// <see cref="Microsoft.CodeAnalysis.DiagnosticDescriptor" /> nor tracked as an analyzer release.
    /// </remarks>
    public const string SingleTestCaseMutationPoint = "FSH0006";
}
