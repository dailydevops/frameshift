namespace NetEvolve.FrameShift.TestSurface;

using System.Collections.Immutable;

/// <summary>
/// The parsed content of a test-surface manifest, bridging the test compilation and the production
/// compilation.
/// </summary>
/// <remarks>
/// The manifest is organised per test method: every discovered test method carries its test case count
/// and the set of production members it reaches. <see cref="TestMethodIds" /> and
/// <see cref="ReferencedMemberIds" /> are the flat unions over those blocks and are what the
/// reachability computation consumes.
/// </remarks>
internal sealed class TestSurfaceManifest
{
    private static readonly TestSurfaceManifest _empty = new TestSurfaceManifest(
        ImmutableDictionary<string, TestCaseCount>.Empty,
        ImmutableDictionary<string, ImmutableHashSet<string>>.Empty
    );

    /// <summary>
    /// Initializes a new instance of the <see cref="TestSurfaceManifest" /> class from the per-test
    /// blocks of a manifest.
    /// </summary>
    /// <param name="testCaseCounts">
    /// The test case count of every discovered test method, keyed by its documentation comment id.
    /// </param>
    /// <param name="referencesByTest">
    /// The documentation comment ids of the production members every discovered test method
    /// references, keyed by the documentation comment id of the test method.
    /// </param>
    /// <remarks>
    /// Both dictionaries are normalized to the union of their keys: a test method that appears in only
    /// one of them is completed with an empty reference set respectively with the lower bound
    /// <c>1+</c>, which is the safe assumption because it never satisfies a heuristic demanding an
    /// exact count.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="testCaseCounts" /> or <paramref name="referencesByTest" /> is
    /// <see langword="null" />.
    /// </exception>
    public TestSurfaceManifest(
        ImmutableDictionary<string, TestCaseCount> testCaseCounts,
        ImmutableDictionary<string, ImmutableHashSet<string>> referencesByTest
    )
        : this(testCaseCounts, referencesByTest, ImmutableHashSet<string>.Empty) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="TestSurfaceManifest" /> class from the flat unions
    /// of a manifest, i.e. without knowing which test method reached which production member.
    /// </summary>
    /// <param name="testMethodIds">
    /// The documentation comment ids of the discovered test methods.
    /// </param>
    /// <param name="referencedMemberIds">
    /// The documentation comment ids of the production members referenced by those test methods.
    /// </param>
    /// <remarks>
    /// Every test method is recorded with the lower bound <c>1+</c> and with the complete set of
    /// referenced members, because the flat form does not say which test reached what. Both are
    /// deliberately pessimistic: a lower bound suppresses every heuristic that needs an exact count,
    /// and attributing more members to a test only ever widens the set of tests reaching a member.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="testMethodIds" /> or <paramref name="referencedMemberIds" /> is
    /// <see langword="null" />.
    /// </exception>
    public TestSurfaceManifest(ImmutableHashSet<string> testMethodIds, ImmutableHashSet<string> referencedMemberIds)
        : this(
            BuildCounts(testMethodIds),
            BuildReferences(testMethodIds, referencedMemberIds),
            Ordinal(referencedMemberIds)
        ) { }

    private TestSurfaceManifest(
        ImmutableDictionary<string, TestCaseCount> testCaseCounts,
        ImmutableDictionary<string, ImmutableHashSet<string>> referencesByTest,
        ImmutableHashSet<string> additionalReferencedMemberIds
    )
    {
        if (testCaseCounts is null)
        {
            throw new ArgumentNullException(nameof(testCaseCounts));
        }

        if (referencesByTest is null)
        {
            throw new ArgumentNullException(nameof(referencesByTest));
        }

        var testMethodIds = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        testMethodIds.UnionWith(testCaseCounts.Keys);
        testMethodIds.UnionWith(referencesByTest.Keys);

        TestMethodIds = testMethodIds.ToImmutable();
        TestCaseCounts = NormalizeCounts(testCaseCounts, TestMethodIds);
        ReferencesByTest = NormalizeReferences(referencesByTest, TestMethodIds);
        ReferencedMemberIds = UnionReferences(ReferencesByTest, additionalReferencedMemberIds);
    }

    /// <summary>
    /// Gets an empty manifest, describing a compilation without any discovered test.
    /// </summary>
    public static TestSurfaceManifest Empty => _empty;

    /// <summary>
    /// Gets the test case count of every discovered test method, keyed ordinally by its documentation
    /// comment id. The keys are exactly <see cref="TestMethodIds" />.
    /// </summary>
    public ImmutableDictionary<string, TestCaseCount> TestCaseCounts { get; }

    /// <summary>
    /// Gets the production members every discovered test method references, keyed ordinally by the
    /// documentation comment id of the test method. The keys are exactly <see cref="TestMethodIds" />,
    /// and a test method without a single production reference maps to an empty set.
    /// </summary>
    public ImmutableDictionary<string, ImmutableHashSet<string>> ReferencesByTest { get; }

    /// <summary>
    /// Gets the documentation comment ids of the discovered test methods, compared ordinally. This is
    /// the key set of <see cref="TestCaseCounts" /> and of <see cref="ReferencesByTest" />.
    /// </summary>
    public ImmutableHashSet<string> TestMethodIds { get; }

    /// <summary>
    /// Gets the documentation comment ids of the production members referenced by the discovered
    /// test methods, compared ordinally. This is the union over <see cref="ReferencesByTest" />.
    /// </summary>
    public ImmutableHashSet<string> ReferencedMemberIds { get; }

    /// <summary>
    /// Gets a value indicating whether the manifest contains neither a test method nor a referenced
    /// production member.
    /// </summary>
    public bool IsEmpty => TestMethodIds.IsEmpty && ReferencedMemberIds.IsEmpty;

    /// <summary>
    /// Merges several manifests into one, as required when more than one test framework contributed a
    /// manifest to the same compilation.
    /// </summary>
    /// <param name="manifests">The manifests to merge.</param>
    /// <returns>
    /// The merged manifest, keeping the per-test blocks intact. A test method declared by more than one
    /// manifest keeps the count of the first manifest declaring it and the union of its references,
    /// because the same test method has the same number of cases no matter which manifest recorded it.
    /// </returns>
    /// <remarks>
    /// The two callers that merge across files rely on exactly that: a multi-targeted test project emits
    /// one manifest per target framework, and summing the counts of a test declared by all of them would
    /// turn one test case into as many as there are target frameworks — which is precisely the number the
    /// single-test-case heuristic must not get wrong. The references are united instead, because a
    /// conditionally compiled test body can touch different members per framework.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="manifests" /> is <see langword="null" />.</exception>
    public static TestSurfaceManifest Merge(IEnumerable<TestSurfaceManifest> manifests)
    {
        if (manifests is null)
        {
            throw new ArgumentNullException(nameof(manifests));
        }

        var counts = ImmutableDictionary.CreateBuilder<string, TestCaseCount>(StringComparer.Ordinal);
        var references = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(StringComparer.Ordinal);
        var referencedMemberIds = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);

        foreach (var manifest in manifests)
        {
            if (manifest is null)
            {
                throw new ArgumentException("One of the manifests to merge is null.", nameof(manifests));
            }

            MergeOne(manifest, counts, references, referencedMemberIds);
        }

        return new TestSurfaceManifest(
            counts.ToImmutable(),
            references.ToImmutable(),
            referencedMemberIds.ToImmutable()
        );
    }

    private static void MergeOne(
        TestSurfaceManifest manifest,
        ImmutableDictionary<string, TestCaseCount>.Builder counts,
        ImmutableDictionary<string, ImmutableHashSet<string>>.Builder references,
        ImmutableHashSet<string>.Builder referencedMemberIds
    )
    {
        referencedMemberIds.UnionWith(manifest.ReferencedMemberIds);

        foreach (var testMethodId in manifest.TestMethodIds)
        {
            if (!counts.ContainsKey(testMethodId))
            {
                counts[testMethodId] = manifest.TestCaseCounts[testMethodId];
            }

            references[testMethodId] = references.TryGetValue(testMethodId, out var existing)
                ? existing.Union(manifest.ReferencesByTest[testMethodId])
                : manifest.ReferencesByTest[testMethodId];
        }
    }

    private static ImmutableDictionary<string, TestCaseCount> BuildCounts(ImmutableHashSet<string> testMethodIds)
    {
        if (testMethodIds is null)
        {
            throw new ArgumentNullException(nameof(testMethodIds));
        }

        var counts = ImmutableDictionary.CreateBuilder<string, TestCaseCount>(StringComparer.Ordinal);

        foreach (var testMethodId in testMethodIds)
        {
            counts[testMethodId] = TestCaseCount.AtLeast(1);
        }

        return counts.ToImmutable();
    }

    private static ImmutableDictionary<string, ImmutableHashSet<string>> BuildReferences(
        ImmutableHashSet<string> testMethodIds,
        ImmutableHashSet<string> referencedMemberIds
    )
    {
        if (referencedMemberIds is null)
        {
            throw new ArgumentNullException(nameof(referencedMemberIds));
        }

        var references = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(StringComparer.Ordinal);
        var ordinal = Ordinal(referencedMemberIds);

        foreach (var testMethodId in testMethodIds)
        {
            references[testMethodId] = ordinal;
        }

        return references.ToImmutable();
    }

    private static ImmutableDictionary<string, TestCaseCount> NormalizeCounts(
        ImmutableDictionary<string, TestCaseCount> testCaseCounts,
        ImmutableHashSet<string> testMethodIds
    )
    {
        var counts = ImmutableDictionary.CreateBuilder<string, TestCaseCount>(StringComparer.Ordinal);

        foreach (var testMethodId in testMethodIds)
        {
            counts[testMethodId] = testCaseCounts.TryGetValue(testMethodId, out var count)
                ? count
                : TestCaseCount.AtLeast(1);
        }

        return counts.ToImmutable();
    }

    private static ImmutableDictionary<string, ImmutableHashSet<string>> NormalizeReferences(
        ImmutableDictionary<string, ImmutableHashSet<string>> referencesByTest,
        ImmutableHashSet<string> testMethodIds
    )
    {
        var references = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(StringComparer.Ordinal);

        foreach (var testMethodId in testMethodIds)
        {
            references[testMethodId] = referencesByTest.TryGetValue(testMethodId, out var referenced)
                ? Ordinal(referenced)
                : ImmutableHashSet<string>.Empty;
        }

        return references.ToImmutable();
    }

    private static ImmutableHashSet<string> UnionReferences(
        ImmutableDictionary<string, ImmutableHashSet<string>> referencesByTest,
        ImmutableHashSet<string> additionalReferencedMemberIds
    )
    {
        var referencedMemberIds = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        referencedMemberIds.UnionWith(additionalReferencedMemberIds);

        foreach (var referenced in referencesByTest.Values)
        {
            referencedMemberIds.UnionWith(referenced);
        }

        return referencedMemberIds.ToImmutable();
    }

    private static ImmutableHashSet<string> Ordinal(ImmutableHashSet<string> ids) =>
        ids.WithComparer(StringComparer.Ordinal);
}
