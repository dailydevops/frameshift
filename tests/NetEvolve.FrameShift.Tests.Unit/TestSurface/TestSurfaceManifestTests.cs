namespace NetEvolve.FrameShift.Tests.Unit;

using System.Collections.Immutable;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the manifest itself, which is the value the test side writes and the production side reads.
/// Both sides look up documentation comment ids in it, so the comparer it stores them under decides
/// whether a member is found again, and <see cref="TestSurfaceManifest.IsEmpty" /> decides whether the
/// production side considers the recorded surface usable at all.
/// </summary>
public class TestSurfaceManifestTests
{
    private const string TestId = "M:Tests.CalculatorTests.Add";
    private const string MemberId = "M:Production.Calculator.Add(System.Int32,System.Int32)";

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
    public async Task Empty_TheSharedManifest_HoldsNoIdAtAll()
    {
        var manifest = TestSurfaceManifest.Empty;

        _ = await Assert.That(manifest.IsEmpty).IsTrue();
        _ = await Assert.That(manifest.TestMethodIds).IsEmpty();
        _ = await Assert.That(manifest.ReferencedMemberIds).IsEmpty();
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

        _ = await Assert.That(manifest.IsEmpty).IsFalse();
    }

    [Test]
    public async Task IsEmpty_ManifestWithBothKindsOfId_ReturnsFalse()
    {
        var manifest = new TestSurfaceManifest(Ids(TestId), Ids(MemberId));

        _ = await Assert.That(manifest.IsEmpty).IsFalse();
        _ = await Assert.That(manifest.TestMethodIds).Contains(TestId);
        _ = await Assert.That(manifest.ReferencedMemberIds).Contains(MemberId);
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

        _ = await Assert.That(lax.Contains(TestId.ToUpperInvariant())).IsTrue();
        _ = await Assert.That(manifest.TestMethodIds.Contains(TestId.ToUpperInvariant())).IsFalse();
        _ = await Assert.That(manifest.ReferencedMemberIds.Contains(MemberId.ToUpperInvariant())).IsFalse();
        _ = await Assert.That(manifest.TestMethodIds.Contains(TestId)).IsTrue();
    }

    private static ImmutableHashSet<string> Ids(params string[] ids) =>
        ImmutableHashSet.Create(StringComparer.Ordinal, ids);
}
