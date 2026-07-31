namespace NetEvolve.FrameShift.TestSurface;

using System.Collections.Immutable;

/// <summary>
/// The parsed content of a test-surface manifest, bridging the test compilation and the production
/// compilation.
/// </summary>
internal sealed class TestSurfaceManifest
{
    private static readonly TestSurfaceManifest _empty = new TestSurfaceManifest([], []);

    /// <summary>
    /// Initializes a new instance of the <see cref="TestSurfaceManifest" /> class.
    /// </summary>
    /// <param name="testMethodIds">
    /// The documentation comment ids of the discovered test methods.
    /// </param>
    /// <param name="referencedMemberIds">
    /// The documentation comment ids of the production members referenced by those test methods.
    /// </param>
    public TestSurfaceManifest(ImmutableHashSet<string> testMethodIds, ImmutableHashSet<string> referencedMemberIds)
    {
        if (testMethodIds is null)
        {
            throw new ArgumentNullException(nameof(testMethodIds));
        }

        if (referencedMemberIds is null)
        {
            throw new ArgumentNullException(nameof(referencedMemberIds));
        }

        TestMethodIds = testMethodIds.WithComparer(StringComparer.Ordinal);
        ReferencedMemberIds = referencedMemberIds.WithComparer(StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets an empty manifest, describing a compilation without any discovered test.
    /// </summary>
    public static TestSurfaceManifest Empty => _empty;

    /// <summary>
    /// Gets the documentation comment ids of the discovered test methods, compared ordinally.
    /// </summary>
    public ImmutableHashSet<string> TestMethodIds { get; }

    /// <summary>
    /// Gets the documentation comment ids of the production members referenced by the discovered
    /// test methods, compared ordinally.
    /// </summary>
    public ImmutableHashSet<string> ReferencedMemberIds { get; }

    /// <summary>
    /// Gets a value indicating whether the manifest contains neither a test method nor a referenced
    /// production member.
    /// </summary>
    public bool IsEmpty => TestMethodIds.IsEmpty && ReferencedMemberIds.IsEmpty;
}
