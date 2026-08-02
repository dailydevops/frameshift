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

    private static readonly DiagnosticDescriptor _singleTestCaseMutationPoint = Create(
        id: DiagnosticIds.SingleTestCaseMutationPoint,
        title: "Mutation point is reached by a single test case",
        messageFormat: "Mutation '{0}' is reached by a single test case, the one of test method '{1}'; "
            + "a mutant that only differs for other inputs would survive",
        description: "The mutation point is covered, but every test reaching it contributes exactly one "
            + "input combination and there is only one such combination in total. A mutant that behaves like "
            + "the original code for that one combination and differently for any other would therefore "
            + "survive unnoticed. Adding a test case with different inputs closes the gap.",
        defaultSeverity: DiagnosticSeverity.Info
    );

    private static readonly DiagnosticDescriptor _reachabilityOnlyMutationPoint = Create(
        id: DiagnosticIds.ReachabilityOnlyMutationPoint,
        title: "Mutation point is reachable without a behavioral assertion",
        messageFormat: "Mutation '{0}' at this location is only reached by tests that carry no credible "
            + "basis for believing a mutant here would be noticed; the reachable member is called, but "
            + "no discovered test asserts on its behaviour",
        description: "A test reaches the analyzed member, but only through a bare method-group reference "
            + "without a call, or without calling a recognised, non-trivial assertion afterwards. Such a "
            + "'reachability-only' test can clear the FSH0001 gap without a surviving mutant here ever "
            + "being noticed, so this is reported separately from full coverage.",
        defaultSeverity: DiagnosticSeverity.Warning
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

    /// <summary>
    /// Gets the descriptor for <see cref="DiagnosticIds.SingleTestCaseMutationPoint" /> (<c>FSH0006</c>).
    /// </summary>
    public static DiagnosticDescriptor SingleTestCaseMutationPoint => _singleTestCaseMutationPoint;

    /// <summary>
    /// Gets the descriptor for <see cref="DiagnosticIds.ReachabilityOnlyMutationPoint" /> (<c>FSH0007</c>).
    /// </summary>
    public static DiagnosticDescriptor ReachabilityOnlyMutationPoint => _reachabilityOnlyMutationPoint;

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
