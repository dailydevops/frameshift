namespace NetEvolve.Frameshift.Tests.Unit;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;
using NetEvolve.Frameshift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Pins the canonical on-disk shape of a manifest. The file is checked in, so a byte that changes
/// without the surface changing shows up as noise in every review; the exact layout is therefore part
/// of the contract, together with the round trip through <see cref="TestSurfaceManifestReader" />.
/// </summary>
public class TestSurfaceManifestWriterTests
{
    private const string Header = "frameshift-test-surface/1";

    [Test]
    public async Task Write_Manifest_WritesHeaderThenTestsThenReferences()
    {
        var manifest = Create(["M:Tests.B.Second", "M:Tests.A.First"], ["M:Production.B.Beta", "M:Production.A.Alpha"]);

        var written = TestSurfaceManifestWriter.Write(manifest);

        _ = await Assert
            .That(written)
            .IsEqualTo(
                Header
                    + "\n"
                    + "T M:Tests.A.First\n"
                    + "T M:Tests.B.Second\n"
                    + "R M:Production.A.Alpha\n"
                    + "R M:Production.B.Beta\n"
            );
    }

    [Test]
    public async Task Write_EmptyManifest_WritesTheHeaderOnly()
    {
        var written = TestSurfaceManifestWriter.Write(TestSurfaceManifest.Empty);

        _ = await Assert.That(written).IsEqualTo(Header + "\n");
    }

    [Test]
    public async Task Write_Ids_AreSortedOrdinallyAndNotByCulture()
    {
        var manifest = Create(["M:A.b", "M:A.B", "M:A.a", "M:A.A"], []);

        var written = TestSurfaceManifestWriter.Write(manifest);

        _ = await Assert.That(written).IsEqualTo(Header + "\n" + "T M:A.A\n" + "T M:A.B\n" + "T M:A.a\n" + "T M:A.b\n");
    }

    [Test]
    public async Task Write_Manifest_UsesLineFeedOnlyAndEndsWithOne()
    {
        var written = TestSurfaceManifestWriter.Write(Create(["M:Tests.A.First"], ["M:Production.A.Alpha"]));

        _ = await Assert.That(written.Contains('\r', StringComparison.Ordinal)).IsFalse();
        _ = await Assert.That(written.EndsWith('\n')).IsTrue();
        _ = await Assert.That(written.Split('\n').Length).IsEqualTo(4);
    }

    [Test]
    public async Task Write_ManifestIsNull_ThrowsArgumentNullException()
    {
        var threw = ThrowsArgumentNull(() => _ = TestSurfaceManifestWriter.Write(null!));

        _ = await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task RoundTrip_WrittenManifest_ParsesBackIntoAnEqualManifest()
    {
        var manifest = Create(
            ["M:Tests.CalculatorTests.Add", "M:Tests.CalculatorTests.Subtract"],
            ["M:Production.Calculator.Add(System.Int32,System.Int32)", "P:Production.Calculator.Factor"]
        );

        var parsed = TestSurfaceManifestReader.TryRead(
            SourceText.From(TestSurfaceManifestWriter.Write(manifest)),
            out var roundTripped,
            out var error
        );

        _ = await Assert.That(parsed).IsTrue();
        _ = await Assert.That(error).IsNull();
        _ = await Assert.That(Join(roundTripped.TestMethodIds)).IsEqualTo(Join(manifest.TestMethodIds));
        _ = await Assert.That(Join(roundTripped.ReferencedMemberIds)).IsEqualTo(Join(manifest.ReferencedMemberIds));
    }

    [Test]
    public async Task RoundTrip_EmptyManifest_ParsesBackIntoAnEmptyManifest()
    {
        var written = TestSurfaceManifestWriter.Write(TestSurfaceManifest.Empty);

        var parsed = TestSurfaceManifestReader.TryRead(SourceText.From(written), out var roundTripped, out var error);

        _ = await Assert.That(parsed).IsTrue();
        _ = await Assert.That(error).IsNull();
        _ = await Assert.That(roundTripped.IsEmpty).IsTrue();
    }

    private static TestSurfaceManifest Create(string[] testMethodIds, string[] referencedMemberIds) =>
        new TestSurfaceManifest(
            ImmutableHashSet.Create(StringComparer.Ordinal, testMethodIds),
            ImmutableHashSet.Create(StringComparer.Ordinal, referencedMemberIds)
        );

    private static string Join(IEnumerable<string> ids) =>
        string.Join("|", ids.OrderBy(id => id, StringComparer.Ordinal));

    private static bool ThrowsArgumentNull(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentNullException)
        {
            return true;
        }

        return false;
    }
}
