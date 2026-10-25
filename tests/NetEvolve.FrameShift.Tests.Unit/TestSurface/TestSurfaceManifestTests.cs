namespace NetEvolve.FrameShift.Tests.Unit.TestSurface;

using System.Collections.Immutable;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the manifest itself, which is the value the test side writes and the production side reads.
/// Both sides look up documentation comment ids in it, so the comparer it stores them under decides
/// whether a member is found again, <see cref="TestSurfaceManifest.IsEmpty" /> decides whether the
/// production side considers the recorded surface usable at all, and the per-test blocks decide which
/// tests the single-test-case heuristic aggregates over.
/// </summary>
public class TestSurfaceManifestTests
{
    private const string TestId = "M:Tests.CalculatorTests.Add";
    private const string OtherTestId = "M:Tests.CalculatorTests.Subtract";
    private const string MemberId = "M:Production.Calculator.Add(System.Int32,System.Int32)";
    private const string OtherMemberId = "M:Production.Calculator.Subtract(System.Int32,System.Int32)";

    [Test]
    public async Task Constructor_TestMethodIdsAreNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => _ = new TestSurfaceManifest(null!, Ids(MemberId)));

        _ = await Assert.That(exception.ParamName).IsEqualTo("testMethodIds");
    }

    [Test]
    public async Task Constructor_ReferencedMemberIdsAreNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => _ = new TestSurfaceManifest(Ids(TestId), null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("referencedMemberIds");
    }

    [Test]
    public async Task Constructor_TestCaseCountsAreNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = new TestSurfaceManifest(null!, ImmutableDictionary<string, ImmutableHashSet<string>>.Empty)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("testCaseCounts");
    }

    [Test]
    public async Task Constructor_ReferencesByTestAreNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = new TestSurfaceManifest(ImmutableDictionary<string, TestCaseCount>.Empty, null!)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("referencesByTest");
    }

    [Test]
    public async Task Empty_TheSharedManifest_HoldsNoIdAtAll()
    {
        var manifest = TestSurfaceManifest.Empty;

        using (Assert.Multiple())
        {
            _ = await Assert.That(manifest.IsEmpty).IsTrue();
            _ = await Assert.That(manifest.TestMethodIds).IsEmpty();
            _ = await Assert.That(manifest.ReferencedMemberIds).IsEmpty();
            _ = await Assert.That(manifest.TestCaseCounts).IsEmpty();
            _ = await Assert.That(manifest.ReferencesByTest).IsEmpty();
        }
    }

    [Test]
    public async Task IsEmpty_ManifestWithoutAnyId_ReturnsTrue()
    {
        var manifest = new TestSurfaceManifest(Ids(), Ids());

        _ = await Assert.That(manifest.IsEmpty).IsTrue();
    }

    [Test]
    public async Task IsEmpty_ManifestWithATestMethodOnly_ReturnsFalse()
    {
        var manifest = new TestSurfaceManifest(Ids(TestId), Ids());

        _ = await Assert.That(manifest.IsEmpty).IsFalse();
    }

    /// <summary>
    /// A manifest that records a referenced member without a test method is malformed, but it still
    /// describes a recorded surface and must not be mistaken for an empty one.
    /// </summary>
    [Test]
    public async Task IsEmpty_ManifestWithAReferencedMemberOnly_ReturnsFalse()
    {
        var manifest = new TestSurfaceManifest(Ids(), Ids(MemberId));

        using (Assert.Multiple())
        {
            _ = await Assert.That(manifest.IsEmpty).IsFalse();
            _ = await Assert.That(Join(manifest.ReferencedMemberIds)).IsEqualTo(MemberId);
            _ = await Assert.That(manifest.TestMethodIds).IsEmpty();
        }
    }

    [Test]
    public async Task IsEmpty_ManifestWithBothKindsOfId_ReturnsFalse()
    {
        var manifest = new TestSurfaceManifest(Ids(TestId), Ids(MemberId));

        using (Assert.Multiple())
        {
            _ = await Assert.That(manifest.IsEmpty).IsFalse();
            _ = await Assert.That(manifest.TestMethodIds).Contains(TestId);
            _ = await Assert.That(manifest.ReferencedMemberIds).Contains(MemberId);
        }
    }

    /// <summary>
    /// Documentation comment ids are case sensitive, so a manifest handed a set with a laxer comparer has
    /// to be re-compared ordinally before anything looks an id up in it.
    /// </summary>
    [Test]
    public async Task Constructor_SetsWithALaxComparer_AreReComparedOrdinally()
    {
        var lax = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, TestId);

        var manifest = new TestSurfaceManifest(
            lax,
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, MemberId)
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(lax.Contains(TestId.ToUpperInvariant())).IsTrue();
            _ = await Assert.That(manifest.TestMethodIds.Contains(TestId.ToUpperInvariant())).IsFalse();
            _ = await Assert.That(manifest.ReferencedMemberIds.Contains(MemberId.ToUpperInvariant())).IsFalse();
            _ = await Assert.That(manifest.TestMethodIds.Contains(TestId)).IsTrue();
        }
    }

    /// <summary>
    /// The flat form does not say which test reached which member, so it has to state the pessimistic
    /// answer: every count is a lower bound, which suppresses every heuristic that needs an exact count.
    /// </summary>
    [Test]
    public async Task Constructor_TheFlatForm_RecordsEveryTestWithALowerBoundAndEveryReference()
    {
        var manifest = new TestSurfaceManifest(Ids(TestId, OtherTestId), Ids(MemberId, OtherMemberId));

        using (Assert.Multiple())
        {
            _ = await Assert.That(manifest.TestCaseCounts[TestId]).IsEqualTo(TestCaseCount.AtLeast(1));
            _ = await Assert.That(manifest.TestCaseCounts[OtherTestId]).IsEqualTo(TestCaseCount.AtLeast(1));
            _ = await Assert.That(Join(manifest.ReferencesByTest[TestId])).IsEqualTo(MemberId + "|" + OtherMemberId);
            _ = await Assert
                .That(Join(manifest.ReferencesByTest[OtherTestId]))
                .IsEqualTo(MemberId + "|" + OtherMemberId);
        }
    }

    [Test]
    public async Task Constructor_TheBlockForm_DerivesBothUnionsFromTheBlocks()
    {
        var manifest = Blocks(
            (TestId, TestCaseCount.Exact(3), [MemberId]),
            (OtherTestId, TestCaseCount.AtLeast(1), [MemberId, OtherMemberId])
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(Join(manifest.TestMethodIds)).IsEqualTo(TestId + "|" + OtherTestId);
            _ = await Assert.That(Join(manifest.ReferencedMemberIds)).IsEqualTo(MemberId + "|" + OtherMemberId);
            _ = await Assert.That(manifest.TestCaseCounts[TestId]).IsEqualTo(TestCaseCount.Exact(3));
            _ = await Assert.That(manifest.TestCaseCounts[OtherTestId]).IsEqualTo(TestCaseCount.AtLeast(1));
            _ = await Assert.That(manifest.IsEmpty).IsFalse();
        }
    }

    [Test]
    public async Task Constructor_ABlockWithoutAnyReference_KeepsTheTestWithAnEmptyReferenceSet()
    {
        var manifest = Blocks((TestId, TestCaseCount.Exact(1), []));

        using (Assert.Multiple())
        {
            _ = await Assert.That(Join(manifest.TestMethodIds)).IsEqualTo(TestId);
            _ = await Assert.That(manifest.ReferencedMemberIds).IsEmpty();
            _ = await Assert.That(manifest.ReferencesByTest[TestId]).IsEmpty();
            _ = await Assert.That(manifest.IsEmpty).IsFalse();
        }
    }

    /// <summary>
    /// Both dictionaries describe the same set of test methods, so a test that only one of them knows
    /// has to be completed instead of leaving a lookup on the other one to throw.
    /// </summary>
    [Test]
    public async Task Constructor_ATestMissingFromTheCounts_IsCompletedWithALowerBoundOfOne()
    {
        var references = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(StringComparer.Ordinal);
        references[TestId] = Ids(MemberId);

        var manifest = new TestSurfaceManifest(
            ImmutableDictionary<string, TestCaseCount>.Empty,
            references.ToImmutable()
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(Join(manifest.TestMethodIds)).IsEqualTo(TestId);
            _ = await Assert.That(manifest.TestCaseCounts[TestId]).IsEqualTo(TestCaseCount.AtLeast(1));
        }
    }

    [Test]
    public async Task Constructor_ATestMissingFromTheReferences_IsCompletedWithAnEmptySet()
    {
        var counts = ImmutableDictionary.CreateBuilder<string, TestCaseCount>(StringComparer.Ordinal);
        counts[TestId] = TestCaseCount.Exact(2);

        var manifest = new TestSurfaceManifest(
            counts.ToImmutable(),
            ImmutableDictionary<string, ImmutableHashSet<string>>.Empty
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(Join(manifest.TestMethodIds)).IsEqualTo(TestId);
            _ = await Assert.That(manifest.ReferencesByTest[TestId]).IsEmpty();
            _ = await Assert.That(manifest.TestCaseCounts[TestId]).IsEqualTo(TestCaseCount.Exact(2));
        }
    }

    [Test]
    public async Task Constructor_TheBlockForm_ReComparesEveryReferenceSetOrdinally()
    {
        var references = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(StringComparer.Ordinal);
        references[TestId] = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, MemberId);

        var manifest = new TestSurfaceManifest(
            ImmutableDictionary<string, TestCaseCount>.Empty,
            references.ToImmutable()
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(manifest.ReferencesByTest[TestId].Contains(MemberId.ToUpperInvariant())).IsFalse();
            _ = await Assert.That(manifest.ReferencesByTest[TestId].Contains(MemberId)).IsTrue();
        }
    }

    [Test]
    public async Task Constructor_ThreeArgumentForm_DerivesBehavioralReferencedMemberIdsAsTheUnion()
    {
        var manifest = BlocksWithBehavioral(
            (TestId, TestCaseCount.Exact(3), [MemberId], [MemberId]),
            (OtherTestId, TestCaseCount.AtLeast(1), [MemberId, OtherMemberId], [OtherMemberId])
        );

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Join(manifest.BehavioralReferencedMemberIds))
                .IsEqualTo(MemberId + "|" + OtherMemberId);
            _ = await Assert.That(Join(manifest.BehavioralReferencesByTest[TestId])).IsEqualTo(MemberId);
            _ = await Assert.That(Join(manifest.BehavioralReferencesByTest[OtherTestId])).IsEqualTo(OtherMemberId);
        }
    }

    /// <summary>
    /// The type does not enforce that a behavioral reference also appears as a plain reference: the two
    /// maps are stored independently.
    /// </summary>
    [Test]
    public async Task Constructor_ABehavioralReferenceNotPresentInTheReferences_IsNotRejected()
    {
        var counts = ImmutableDictionary.CreateBuilder<string, TestCaseCount>(StringComparer.Ordinal);
        counts[TestId] = TestCaseCount.Exact(1);
        var references = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(StringComparer.Ordinal);
        references[TestId] = Ids(MemberId);
        var behavioralReferences = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(
            StringComparer.Ordinal
        );
        behavioralReferences[TestId] = Ids(OtherMemberId);

        var manifest = new TestSurfaceManifest(
            counts.ToImmutable(),
            references.ToImmutable(),
            behavioralReferences.ToImmutable()
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(Join(manifest.ReferencesByTest[TestId])).IsEqualTo(MemberId);
            _ = await Assert.That(Join(manifest.BehavioralReferencesByTest[TestId])).IsEqualTo(OtherMemberId);
        }
    }

    /// <summary>
    /// Two frameworks contributing to the same compilation produce one manifest each, and the merged
    /// result has to keep the blocks apart: a shared member is reached by both tests, which is exactly
    /// what suppresses the single-test-case finding.
    /// </summary>
    [Test]
    public async Task Merge_ManifestsOfTwoFrameworks_KeepsTheBlocksApart()
    {
        var first = Blocks((TestId, TestCaseCount.Exact(3), [MemberId]));
        var second = Blocks((OtherTestId, TestCaseCount.Exact(1), [MemberId, OtherMemberId]));

        var merged = TestSurfaceManifest.Merge([first, second]);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Join(merged.TestMethodIds)).IsEqualTo(TestId + "|" + OtherTestId);
            _ = await Assert.That(Join(merged.ReferencedMemberIds)).IsEqualTo(MemberId + "|" + OtherMemberId);
            _ = await Assert.That(merged.TestCaseCounts[TestId]).IsEqualTo(TestCaseCount.Exact(3));
            _ = await Assert.That(merged.TestCaseCounts[OtherTestId]).IsEqualTo(TestCaseCount.Exact(1));
            _ = await Assert.That(Join(merged.ReferencesByTest[TestId])).IsEqualTo(MemberId);
            _ = await Assert.That(Join(merged.ReferencesByTest[OtherTestId])).IsEqualTo(MemberId + "|" + OtherMemberId);
        }
    }

    /// <summary>
    /// The same test method can be recognised by two probes. It is still one test with one number of
    /// cases, so the count must not be added up while the references are unioned.
    /// </summary>
    [Test]
    public async Task Merge_TheSameTestInTwoManifests_UnionsTheReferencesAndKeepsTheFirstCount()
    {
        var first = Blocks((TestId, TestCaseCount.Exact(3), [MemberId]));
        var second = Blocks((TestId, TestCaseCount.Exact(3), [OtherMemberId]));

        var merged = TestSurfaceManifest.Merge([first, second]);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Join(merged.TestMethodIds)).IsEqualTo(TestId);
            _ = await Assert.That(merged.TestCaseCounts[TestId]).IsEqualTo(TestCaseCount.Exact(3));
            _ = await Assert.That(Join(merged.ReferencesByTest[TestId])).IsEqualTo(MemberId + "|" + OtherMemberId);
        }
    }

    [Test]
    public async Task Merge_ManifestsWithBehavioralReferences_UnionsThemAcrossManifests()
    {
        var first = BlocksWithBehavioral((TestId, TestCaseCount.Exact(3), [MemberId], [MemberId]));
        var second = BlocksWithBehavioral((TestId, TestCaseCount.Exact(3), [OtherMemberId], [OtherMemberId]));

        var merged = TestSurfaceManifest.Merge([first, second]);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Join(merged.BehavioralReferencedMemberIds)).IsEqualTo(MemberId + "|" + OtherMemberId);
            _ = await Assert
                .That(Join(merged.BehavioralReferencesByTest[TestId]))
                .IsEqualTo(MemberId + "|" + OtherMemberId);
        }
    }

    [Test]
    public async Task Merge_NoManifestAtAll_ReturnsAnEmptyManifest()
    {
        var merged = TestSurfaceManifest.Merge([]);

        _ = await Assert.That(merged.IsEmpty).IsTrue();
    }

    /// <summary>
    /// A merged flat manifest has no attribution to keep, but its references must not get lost either.
    /// </summary>
    [Test]
    public async Task Merge_AFlatManifestWithoutAnyTest_KeepsItsReferences()
    {
        var flat = new TestSurfaceManifest(Ids(), Ids(OtherMemberId));

        var merged = TestSurfaceManifest.Merge([Blocks((TestId, TestCaseCount.Exact(1), [MemberId])), flat]);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Join(merged.TestMethodIds)).IsEqualTo(TestId);
            _ = await Assert.That(Join(merged.ReferencedMemberIds)).IsEqualTo(MemberId + "|" + OtherMemberId);
            _ = await Assert.That(Join(merged.ReferencesByTest[TestId])).IsEqualTo(MemberId);
        }
    }

    [Test]
    public async Task Merge_ManifestsAreNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => _ = TestSurfaceManifest.Merge(null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("manifests");
    }

    [Test]
    public async Task Merge_OneOfTheManifestsIsNull_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            _ = TestSurfaceManifest.Merge([TestSurfaceManifest.Empty, null!])
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("manifests");
    }

    private static TestSurfaceManifest Blocks(
        params (string TestMethodId, TestCaseCount Count, string[] ReferencedMemberIds)[] blocks
    )
    {
        var counts = ImmutableDictionary.CreateBuilder<string, TestCaseCount>(StringComparer.Ordinal);
        var references = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(StringComparer.Ordinal);

        foreach (var (testMethodId, count, referencedMemberIds) in blocks)
        {
            counts[testMethodId] = count;
            references[testMethodId] = Ids(referencedMemberIds);
        }

        return new TestSurfaceManifest(counts.ToImmutable(), references.ToImmutable());
    }

    private static TestSurfaceManifest BlocksWithBehavioral(
        params (
            string TestMethodId,
            TestCaseCount Count,
            string[] ReferencedMemberIds,
            string[] BehavioralReferencedMemberIds
        )[] blocks
    )
    {
        var counts = ImmutableDictionary.CreateBuilder<string, TestCaseCount>(StringComparer.Ordinal);
        var references = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(StringComparer.Ordinal);
        var behavioralReferences = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(
            StringComparer.Ordinal
        );

        foreach (var (testMethodId, count, referencedMemberIds, behavioralReferencedMemberIds) in blocks)
        {
            counts[testMethodId] = count;
            references[testMethodId] = Ids(referencedMemberIds);
            behavioralReferences[testMethodId] = Ids(behavioralReferencedMemberIds);
        }

        return new TestSurfaceManifest(
            counts.ToImmutable(),
            references.ToImmutable(),
            behavioralReferences.ToImmutable()
        );
    }

    private static ImmutableHashSet<string> Ids(params string[] ids) =>
        ImmutableHashSet.Create(StringComparer.Ordinal, ids);

    private static string Join(IEnumerable<string> ids) =>
        string.Join("|", ids.OrderBy(id => id, StringComparer.Ordinal));
}
