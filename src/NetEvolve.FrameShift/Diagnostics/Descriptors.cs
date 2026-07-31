namespace NetEvolve.FrameShift.Diagnostics;

using Microsoft.CodeAnalysis;

/// <summary>
/// The <see cref="DiagnosticDescriptor" /> instances backing the FrameShift diagnostics.
/// </summary>
internal static class Descriptors
{
    private const string HelpLinkPrefix = "https://github.com/dailydevops/frameshift/blob/main/docs/rules/";

    private static readonly DiagnosticDescriptor _unreachableMutationPoint = Create(
        id: DiagnosticIds.UnreachableMutationPoint,
        title: "Mutation point is not reachable from any test",
        messageFormat: "Mutation '{0}' at this location is not reachable from any test; a surviving mutant here would go unnoticed",
        description: "The analyzed member can be mutated, but no discovered test reaches it. A mutant introduced here would survive unnoticed, which means the behaviour is untested.",
        defaultSeverity: DiagnosticSeverity.Warning
    );

    private static readonly DiagnosticDescriptor _trivialMutant = Create(
        id: DiagnosticIds.TrivialMutant,
        title: "Mutant cannot change observable behaviour",
        messageFormat: "Mutation '{0}' cannot change observable behaviour ({1})",
        description: "The mutant is equivalent to the original code, so no test could ever distinguish it. Such mutants are reported for information only and do not indicate a testing gap.",
        defaultSeverity: DiagnosticSeverity.Info
    );

    private static readonly DiagnosticDescriptor _invalidTestSurfaceManifest = Create(
        id: DiagnosticIds.InvalidTestSurfaceManifest,
        title: "Test-surface manifest is missing or malformed",
        messageFormat: "Test-surface manifest '{0}' could not be read: {1}",
        description: "FrameShift needs a readable test-surface manifest to know which production members are covered by tests. Without it, reachability cannot be determined.",
        defaultSeverity: DiagnosticSeverity.Warning
    );

    private static readonly DiagnosticDescriptor _testWithoutProductionReference = Create(
        id: DiagnosticIds.TestWithoutProductionReference,
        title: "Test method does not reference any production member",
        messageFormat: "Test method '{0}' does not reference any production member",
        description: "The test method does not reference any member of the production assembly, so it cannot contribute to the tested surface.",
        defaultSeverity: DiagnosticSeverity.Info
    );

    /// <summary>
    /// Gets the descriptor for <see cref="DiagnosticIds.UnreachableMutationPoint" /> (<c>FSH0001</c>).
    /// </summary>
    public static DiagnosticDescriptor UnreachableMutationPoint => _unreachableMutationPoint;

    /// <summary>
    /// Gets the descriptor for <see cref="DiagnosticIds.TrivialMutant" /> (<c>FSH0002</c>).
    /// </summary>
    public static DiagnosticDescriptor TrivialMutant => _trivialMutant;

    /// <summary>
    /// Gets the descriptor for <see cref="DiagnosticIds.InvalidTestSurfaceManifest" /> (<c>FSH0003</c>).
    /// </summary>
    public static DiagnosticDescriptor InvalidTestSurfaceManifest => _invalidTestSurfaceManifest;

    /// <summary>
    /// Gets the descriptor for <see cref="DiagnosticIds.TestWithoutProductionReference" /> (<c>FSH0004</c>).
    /// </summary>
    public static DiagnosticDescriptor TestWithoutProductionReference => _testWithoutProductionReference;

    private static DiagnosticDescriptor Create(
        string id,
        string title,
        string messageFormat,
        string description,
        DiagnosticSeverity defaultSeverity
    ) =>
        new DiagnosticDescriptor(
            id: id,
            title: title,
            messageFormat: messageFormat,
            category: DiagnosticIds.Category,
            defaultSeverity: defaultSeverity,
            isEnabledByDefault: true,
            description: description,
            helpLinkUri: HelpLinkPrefix + id + ".md"
        );
}
