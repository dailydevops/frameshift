namespace NetEvolve.FrameShift.Tests.Unit;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;
using NetEvolve.FrameShift.TestSurface;
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
    public async Task Write_Manifest_WritesOneBlockPerTestInOrdinalOrder()
    {
        var manifest = Blocks(
            ("M:Tests.B.Second", TestCaseCount.Exact(1), ["M:Production.B.Beta"]),
            ("M:Tests.A.First", TestCaseCount.Exact(3), ["M:Production.B.Beta", "M:Production.A.Alpha"])
        );

        var written = TestSurfaceManifestWriter.Write(manifest);

        _ = await Assert
            .That(written)
            .IsEqualTo(
                Header
                    + "\n"
                    + "T M:Tests.A.First 3\n"
                    + "R M:Production.A.Alpha\n"
                    + "R M:Production.B.Beta\n"
                    + "T M:Tests.B.Second 1\n"
                    + "R M:Production.B.Beta\n"
            );
    }

    [Test]
    public async Task Write_ALowerBoundCount_CarriesThePlusSuffix()
    {
        var manifest = Blocks(("M:Tests.A.First", TestCaseCount.AtLeast(1), ["M:Production.A.Alpha"]));

        var written = TestSurfaceManifestWriter.Write(manifest);

        _ = await Assert.That(written).IsEqualTo(Header + "\n" + "T M:Tests.A.First 1+\n" + "R M:Production.A.Alpha\n");
    }

    [Test]
    public async Task Write_ATestWithoutAnyReference_WritesTheTestLineOnly()
    {
        var manifest = Blocks(("M:Tests.A.First", TestCaseCount.Exact(1), []));

        var written = TestSurfaceManifestWriter.Write(manifest);

        _ = await Assert.That(written).IsEqualTo(Header + "\n" + "T M:Tests.A.First 1\n");
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
        var manifest = Blocks(
            ("M:A.b", TestCaseCount.Exact(1), []),
            ("M:A.B", TestCaseCount.Exact(1), []),
            ("M:A.a", TestCaseCount.Exact(1), []),
            ("M:A.A", TestCaseCount.Exact(1), [])
        );

        var written = TestSurfaceManifestWriter.Write(manifest);

        _ = await Assert
            .That(written)
            .IsEqualTo(Header + "\n" + "T M:A.A 1\n" + "T M:A.B 1\n" + "T M:A.a 1\n" + "T M:A.b 1\n");
    }

    [Test]
    public async Task Write_References_AreSortedOrdinallyWithinTheirBlock()
    {
        var manifest = Blocks(("M:Tests.A.First", TestCaseCount.Exact(2), ["P:X.b", "P:X.B", "P:X.a", "P:X.A"]));

        var written = TestSurfaceManifestWriter.Write(manifest);

        _ = await Assert
            .That(written)
            .IsEqualTo(Header + "\n" + "T M:Tests.A.First 2\n" + "R P:X.A\n" + "R P:X.B\n" + "R P:X.a\n" + "R P:X.b\n");
    }

    [Test]
    public async Task Write_Manifest_UsesLineFeedOnlyAndEndsWithOne()
    {
        var manifest = Blocks(("M:Tests.A.First", TestCaseCount.Exact(1), ["M:Production.A.Alpha"]));

        var written = TestSurfaceManifestWriter.Write(manifest);

        using (Assert.Multiple())
        {
            _ = await Assert.That(written.Contains('\r', StringComparison.Ordinal)).IsFalse();
            _ = await Assert.That(written[^1]).IsEqualTo('\n');
            _ = await Assert.That(written.Split('\n').Length).IsEqualTo(4);
        }
    }

    /// <summary>
    /// A manifest built from the flat unions knows no attribution, so every test is written with the
    /// lower bound and with every referenced member; nothing is lost, but nothing exact is invented.
    /// </summary>
    [Test]
    public async Task Write_AManifestBuiltFromTheFlatUnions_WritesEveryReferenceUnderEveryTest()
    {
        var manifest = new TestSurfaceManifest(
            ImmutableHashSet.Create(StringComparer.Ordinal, "M:Tests.A.First", "M:Tests.B.Second"),
            ImmutableHashSet.Create(StringComparer.Ordinal, "M:Production.A.Alpha")
        );

        var written = TestSurfaceManifestWriter.Write(manifest);

        _ = await Assert
            .That(written)
            .IsEqualTo(
                Header
                    + "\n"
                    + "T M:Tests.A.First 1+\n"
                    + "R M:Production.A.Alpha\n"
                    + "T M:Tests.B.Second 1+\n"
                    + "R M:Production.A.Alpha\n"
            );
    }

    /// <summary>
    /// The format cannot express a reference that belongs to no test, because a reference line before the
    /// first test line is malformed. Such a manifest only arises from the flat form without a single
    /// test, and the writer states the header instead of emitting something the reader would reject.
    /// </summary>
    [Test]
    public async Task Write_ReferencesWithoutAnyTest_CannotBeExpressedAndAreDropped()
    {
        var manifest = new TestSurfaceManifest(
            ImmutableHashSet.Create<string>(StringComparer.Ordinal),
            ImmutableHashSet.Create(StringComparer.Ordinal, "M:Production.A.Alpha")
        );

        var written = TestSurfaceManifestWriter.Write(manifest);

        _ = await Assert.That(written).IsEqualTo(Header + "\n");
    }

    [Test]
    public async Task Write_Manifest_WritesBehavioralReferenceLinesAfterReferencesAndOrdinally()
    {
        var manifest = BlocksWithBehavioral(
            (
                "M:Tests.A.First",
                TestCaseCount.Exact(2),
                ["M:Production.A.Alpha", "M:Production.B.Beta"],
                ["M:Production.B.Beta", "M:Production.A.Alpha"]
            )
        );

        var written = TestSurfaceManifestWriter.Write(manifest);

        _ = await Assert
            .That(written)
            .IsEqualTo(
                Header
                    + "\n"
                    + "T M:Tests.A.First 2\n"
                    + "R M:Production.A.Alpha\n"
                    + "R M:Production.B.Beta\n"
                    + "B M:Production.A.Alpha\n"
                    + "B M:Production.B.Beta\n"
            );
    }

    [Test]
    public async Task Write_ManifestWithoutAnyBehavioralReference_WritesNoBehavioralLines()
    {
        var manifest = Blocks(("M:Tests.A.First", TestCaseCount.Exact(1), ["M:Production.A.Alpha"]));

        var written = TestSurfaceManifestWriter.Write(manifest);

        using (Assert.Multiple())
        {
            _ = await Assert.That(written.Contains('B', StringComparison.Ordinal)).IsFalse();
            _ = await Assert
                .That(written)
                .IsEqualTo(Header + "\n" + "T M:Tests.A.First 1\n" + "R M:Production.A.Alpha\n");
        }
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
        var manifest = Blocks(
            (
                "M:Tests.CalculatorTests.Add",
                TestCaseCount.Exact(3),
                ["M:Production.Calculator.Add(System.Int32,System.Int32)", "P:Production.Calculator.Factor"]
            ),
            ("M:Tests.CalculatorTests.Subtract", TestCaseCount.AtLeast(1), ["P:Production.Calculator.Factor"])
        );

        var parsed = TestSurfaceManifestReader.TryRead(
            SourceText.From(TestSurfaceManifestWriter.Write(manifest)),
            out var roundTripped,
            out var error
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(parsed).IsTrue();
            _ = await Assert.That(error).IsNull();
        }
        _ = await AssertSameSurface(manifest, roundTripped).ConfigureAwait(false);
    }

    [Test]
    public async Task RoundTrip_ATestWithoutAnyReference_SurvivesTheRoundTrip()
    {
        var manifest = Blocks(("M:Tests.A.First", TestCaseCount.Exact(1), []));

        var parsed = TestSurfaceManifestReader.TryRead(
            SourceText.From(TestSurfaceManifestWriter.Write(manifest)),
            out var roundTripped,
            out var error
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(parsed).IsTrue();
            _ = await Assert.That(error).IsNull();
        }
        _ = await AssertSameSurface(manifest, roundTripped).ConfigureAwait(false);
        _ = await Assert.That(roundTripped.ReferencesByTest["M:Tests.A.First"]).IsEmpty();
    }

    [Test]
    public async Task RoundTrip_EmptyManifest_ParsesBackIntoAnEmptyManifest()
    {
        var written = TestSurfaceManifestWriter.Write(TestSurfaceManifest.Empty);

        var parsed = TestSurfaceManifestReader.TryRead(SourceText.From(written), out var roundTripped, out var error);

        using (Assert.Multiple())
        {
            _ = await Assert.That(parsed).IsTrue();
            _ = await Assert.That(error).IsNull();
            _ = await Assert.That(roundTripped.IsEmpty).IsTrue();
        }
        _ = await AssertSameSurface(TestSurfaceManifest.Empty, roundTripped).ConfigureAwait(false);
    }

    /// <summary>
    /// Writing an already written manifest again has to produce the very same bytes, otherwise the
    /// checked-in file would keep churning.
    /// </summary>
    [Test]
    public async Task RoundTrip_WritingTwice_ProducesTheSameText()
    {
        var manifest = Blocks(
            ("M:Tests.A.First", TestCaseCount.Exact(2), ["M:Production.A.Alpha"]),
            ("M:Tests.B.Second", TestCaseCount.AtLeast(4), [])
        );

        var written = TestSurfaceManifestWriter.Write(manifest);
        _ = TestSurfaceManifestReader.TryRead(SourceText.From(written), out var roundTripped, out _);

        _ = await Assert.That(TestSurfaceManifestWriter.Write(roundTripped)).IsEqualTo(written);
    }

    [Test]
    public async Task RoundTrip_ManifestWithBehavioralReferences_PreservesBehavioralData()
    {
        var manifest = BlocksWithBehavioral(
            (
                "M:Tests.CalculatorTests.Add",
                TestCaseCount.Exact(3),
                ["M:Production.Calculator.Add(System.Int32,System.Int32)", "P:Production.Calculator.Factor"],
                ["M:Production.Calculator.Add(System.Int32,System.Int32)"]
            ),
            ("M:Tests.CalculatorTests.Subtract", TestCaseCount.AtLeast(1), ["P:Production.Calculator.Factor"], [])
        );

        var parsed = TestSurfaceManifestReader.TryRead(
            SourceText.From(TestSurfaceManifestWriter.Write(manifest)),
            out var roundTripped,
            out var error
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(parsed).IsTrue();
            _ = await Assert.That(error).IsNull();
            _ = await Assert
                .That(Join(roundTripped.BehavioralReferencedMemberIds))
                .IsEqualTo(Join(manifest.BehavioralReferencedMemberIds));
            _ = await Assert
                .That(Join(roundTripped.BehavioralReferencesByTest["M:Tests.CalculatorTests.Add"]))
                .IsEqualTo(Join(manifest.BehavioralReferencesByTest["M:Tests.CalculatorTests.Add"]));
            _ = await Assert
                .That(roundTripped.BehavioralReferencesByTest["M:Tests.CalculatorTests.Subtract"])
                .IsEmpty();
        }
    }

    private static async Task<bool> AssertSameSurface(TestSurfaceManifest expected, TestSurfaceManifest actual)
    {
        using (Assert.Multiple())
        {
            _ = await Assert.That(Join(actual.TestMethodIds)).IsEqualTo(Join(expected.TestMethodIds));
            _ = await Assert.That(Join(actual.ReferencedMemberIds)).IsEqualTo(Join(expected.ReferencedMemberIds));
            _ = await Assert.That(Describe(actual)).IsEqualTo(Describe(expected));
        }

        return true;
    }

    private static string Describe(TestSurfaceManifest manifest) =>
        string.Join(
            ";",
            manifest
                .TestMethodIds.OrderBy(id => id, StringComparer.Ordinal)
                .Select(id =>
                    id + "=" + manifest.TestCaseCounts[id].ToString() + "[" + Join(manifest.ReferencesByTest[id]) + "]"
                )
        );

    private static TestSurfaceManifest Blocks(
        params (string TestMethodId, TestCaseCount Count, string[] ReferencedMemberIds)[] blocks
    )
    {
        var counts = ImmutableDictionary.CreateBuilder<string, TestCaseCount>(StringComparer.Ordinal);
        var references = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(StringComparer.Ordinal);

        foreach (var (testMethodId, count, referencedMemberIds) in blocks)
        {
            counts[testMethodId] = count;
            references[testMethodId] = ImmutableHashSet.Create(StringComparer.Ordinal, referencedMemberIds);
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
            references[testMethodId] = ImmutableHashSet.Create(StringComparer.Ordinal, referencedMemberIds);
            behavioralReferences[testMethodId] = ImmutableHashSet.Create(
                StringComparer.Ordinal,
                behavioralReferencedMemberIds
            );
        }

        return new TestSurfaceManifest(
            counts.ToImmutable(),
            references.ToImmutable(),
            behavioralReferences.ToImmutable()
        );
    }

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
