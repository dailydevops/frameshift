namespace NetEvolve.Frameshift.Diagnostics;

/// <summary>
/// The stable identifiers of all diagnostics reported by Frameshift.
/// </summary>
internal static class DiagnosticIds
{
    /// <summary>
    /// The category shared by every Frameshift diagnostic.
    /// </summary>
    public const string Category = "Frameshift";

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
}
