namespace NetEvolve.Frameshift.Tests.Unit;

using Microsoft.CodeAnalysis.Text;
using NetEvolve.Frameshift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Pins the grammar of the checked-in manifest. The manifest is the only channel between the test
/// pass and the production pass, so every accepted and every rejected shape is nailed down here,
/// including the exact wording of the error that ends up in <c>FSH0003</c>.
/// </summary>
public class TestSurfaceManifestReaderTests
{
    private const string Header = "frameshift-test-surface/1";
    private const string TestId = "M:Tests.CalculatorTests.Add_TwoValues_ReturnsSum";
    private const string ReferenceId = "M:Production.Calculator.Add(System.Int32,System.Int32)";
    private const string MissingHeaderError =
        "The test-surface manifest does not contain the required header 'frameshift-test-surface/1'.";

    [Test]
    public async Task TryRead_ValidManifest_ParsesBothIdSets()
    {
        var text = Build("\n", Header, "T " + TestId, "R " + ReferenceId);

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsTrue();
        _ = await Assert.That(error).IsNull();
        _ = await Assert.That(Join(manifest.TestMethodIds)).IsEqualTo(TestId);
        _ = await Assert.That(Join(manifest.ReferencedMemberIds)).IsEqualTo(ReferenceId);
    }

    [Test]
    public async Task TryRead_HeaderOnly_ParsesToEmptyManifest()
    {
        var parsed = TestSurfaceManifestReader.TryRead(Build("\n", Header), out var manifest, out var error);

        _ = await Assert.That(parsed).IsTrue();
        _ = await Assert.That(error).IsNull();
        _ = await Assert.That(manifest.IsEmpty).IsTrue();
    }

    [Test]
    public async Task TryRead_BlankAndCommentLines_AreIgnored()
    {
        var text = Build("\n", "# a leading comment", "", "   ", Header, "", "# another comment", "T " + TestId, "");

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsTrue();
        _ = await Assert.That(error).IsNull();
        _ = await Assert.That(Join(manifest.TestMethodIds)).IsEqualTo(TestId);
        _ = await Assert.That(manifest.ReferencedMemberIds.IsEmpty).IsTrue();
    }

    [Test]
    [Arguments("X " + ReferenceId)]
    [Arguments("RR " + ReferenceId)]
    [Arguments("t " + ReferenceId)]
    [Arguments("frameshift-test-surface/2")]
    public async Task TryRead_UnknownLinePrefix_IsIgnored(string line)
    {
        var text = Build("\n", Header, line, "T " + TestId);

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsTrue();
        _ = await Assert.That(error).IsNull();
        _ = await Assert.That(Join(manifest.TestMethodIds)).IsEqualTo(TestId);
        _ = await Assert.That(manifest.ReferencedMemberIds.IsEmpty).IsTrue();
    }

    [Test]
    public async Task TryRead_HeaderMissing_ReportsTheFirstContentLine()
    {
        var text = Build("\n", "T " + TestId, "R " + ReferenceId);

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsFalse();
        _ = await Assert.That(manifest.IsEmpty).IsTrue();
        _ = await Assert
            .That(error)
            .IsEqualTo(
                "Line 1: expected the test-surface manifest header 'frameshift-test-surface/1', "
                    + "but found 'T M:Tests.CalculatorTests.Add_TwoValues_ReturnsSum'."
            );
    }

    [Test]
    public async Task TryRead_WrongHeader_ReportsTheOneBasedLineNumber()
    {
        var text = Build("\n", "# a comment", "", "frameshift-test-surface/0", "T " + TestId);

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsFalse();
        _ = await Assert.That(manifest.IsEmpty).IsTrue();
        _ = await Assert
            .That(error)
            .IsEqualTo(
                "Line 3: expected the test-surface manifest header 'frameshift-test-surface/1', "
                    + "but found 'frameshift-test-surface/0'."
            );
    }

    [Test]
    public async Task TryRead_EmptyFile_IsMalformed()
    {
        var parsed = TestSurfaceManifestReader.TryRead(SourceText.From(string.Empty), out var manifest, out var error);

        _ = await Assert.That(parsed).IsFalse();
        _ = await Assert.That(manifest.IsEmpty).IsTrue();
        _ = await Assert.That(error).IsEqualTo(MissingHeaderError);
    }

    [Test]
    public async Task TryRead_OnlyCommentsAndBlankLines_IsMalformed()
    {
        var text = Build("\n", "# nothing but a comment", "", "\t");

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsFalse();
        _ = await Assert.That(manifest.IsEmpty).IsTrue();
        _ = await Assert.That(error).IsEqualTo(MissingHeaderError);
    }

    [Test]
    [Arguments("T", 'T')]
    [Arguments("R", 'R')]
    [Arguments("T   ", 'T')]
    [Arguments("\tR\t", 'R')]
    public async Task TryRead_EntryWithoutId_IsMalformed(string entry, char marker)
    {
        var text = Build("\n", Header, entry, "R " + ReferenceId);

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsFalse();
        _ = await Assert.That(manifest.IsEmpty).IsTrue();
        _ = await Assert
            .That(error)
            .IsEqualTo($"Line 2: the '{marker}' entry does not specify a documentation comment id.");
    }

    [Test]
    [Arguments("\n")]
    [Arguments("\r\n")]
    public async Task TryRead_LineEnding_IsAcceptedInBothForms(string newLine)
    {
        var text = Build(newLine, Header, "T " + TestId, "R " + ReferenceId);

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsTrue();
        _ = await Assert.That(error).IsNull();
        _ = await Assert.That(Join(manifest.TestMethodIds)).IsEqualTo(TestId);
        _ = await Assert.That(Join(manifest.ReferencedMemberIds)).IsEqualTo(ReferenceId);
    }

    [Test]
    public async Task TryRead_DuplicateIds_CollapseIntoOneEntry()
    {
        var text = Build("\n", Header, "T " + TestId, "T " + TestId, "R " + ReferenceId, "R  " + ReferenceId + "  ");

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsTrue();
        _ = await Assert.That(error).IsNull();
        _ = await Assert.That(manifest.TestMethodIds.Count).IsEqualTo(1);
        _ = await Assert.That(manifest.ReferencedMemberIds.Count).IsEqualTo(1);
        _ = await Assert.That(Join(manifest.ReferencedMemberIds)).IsEqualTo(ReferenceId);
    }

    [Test]
    public async Task TryRead_IdsDifferingOnlyInCase_AreKeptApart()
    {
        var text = Build("\n", Header, "R M:Production.Calculator.Add", "R M:Production.Calculator.ADD");

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsTrue();
        _ = await Assert.That(error).IsNull();
        _ = await Assert
            .That(Join(manifest.ReferencedMemberIds))
            .IsEqualTo("M:Production.Calculator.ADD|M:Production.Calculator.Add");
    }

    [Test]
    public async Task TryRead_TextIsNull_ThrowsArgumentNullException()
    {
        var threw = ThrowsArgumentNull(() => _ = TestSurfaceManifestReader.TryRead(null!, out _, out _));

        _ = await Assert.That(threw).IsTrue();
    }

    private static SourceText Build(string newLine, params string[] lines) =>
        SourceText.From(string.Join(newLine, lines) + newLine);

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
