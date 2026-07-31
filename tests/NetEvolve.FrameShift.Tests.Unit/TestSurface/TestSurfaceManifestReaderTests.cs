namespace NetEvolve.FrameShift.Tests.Unit;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Pins the grammar of the checked-in manifest. The manifest is the only channel between the test
/// pass and the production pass, so every accepted and every rejected shape is nailed down here,
/// including the exact wording of the error that ends up in <c>FSH0003</c>. The file is read in blocks:
/// an <c>R</c> line belongs to the <c>T</c> line above it, which is what makes the aggregation over the
/// tests reaching a member possible at all.
/// </summary>
public class TestSurfaceManifestReaderTests
{
    private const string Header = "frameshift-test-surface/1";
    private const string TestId = "M:Tests.CalculatorTests.Add_TwoValues_ReturnsSum";
    private const string OtherTestId = "M:Tests.CalculatorTests.Divide_ByZero_Throws";
    private const string ReferenceId = "M:Production.Calculator.Add(System.Int32,System.Int32)";
    private const string OtherReferenceId = "T:Production.Calculator";
    private const string MissingHeaderError =
        "The test-surface manifest does not contain the required header 'frameshift-test-surface/1'.";

    [Test]
    public async Task TryRead_ValidManifest_ParsesTheBlockAndBothUnions()
    {
        var text = Build("\n", Header, "T " + TestId + " 3", "R " + ReferenceId);

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsTrue();
        _ = await Assert.That(error).IsNull();
        _ = await Assert.That(Join(manifest.TestMethodIds)).IsEqualTo(TestId);
        _ = await Assert.That(Join(manifest.ReferencedMemberIds)).IsEqualTo(ReferenceId);
        _ = await Assert.That(manifest.TestCaseCounts[TestId]).IsEqualTo(TestCaseCount.Exact(3));
        _ = await Assert.That(Join(manifest.ReferencesByTest[TestId])).IsEqualTo(ReferenceId);
    }

    [Test]
    public async Task TryRead_ATestWithSeveralReferences_AttributesThemAllToThatTest()
    {
        var text = Build("\n", Header, "T " + TestId + " 1", "R " + ReferenceId, "R " + OtherReferenceId);

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsTrue();
        _ = await Assert.That(error).IsNull();
        _ = await Assert.That(Join(manifest.ReferencesByTest[TestId])).IsEqualTo(ReferenceId + "|" + OtherReferenceId);
    }

    /// <summary>
    /// The whole point of the block format: a member reached by two tests has to be attributed to both,
    /// because a member reached by more than one test is never reported as reached by a single case.
    /// </summary>
    [Test]
    public async Task TryRead_TwoTestsSharingAReference_AttributesItToBoth()
    {
        var text = Build(
            "\n",
            Header,
            "T " + TestId + " 1",
            "R " + ReferenceId,
            "T " + OtherTestId + " 2",
            "R " + ReferenceId,
            "R " + OtherReferenceId
        );

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsTrue();
        _ = await Assert.That(error).IsNull();
        _ = await Assert.That(Join(manifest.TestMethodIds)).IsEqualTo(TestId + "|" + OtherTestId);
        _ = await Assert.That(Join(manifest.ReferencesByTest[TestId])).IsEqualTo(ReferenceId);
        _ = await Assert
            .That(Join(manifest.ReferencesByTest[OtherTestId]))
            .IsEqualTo(ReferenceId + "|" + OtherReferenceId);
        _ = await Assert.That(manifest.TestCaseCounts[TestId]).IsEqualTo(TestCaseCount.Exact(1));
        _ = await Assert.That(manifest.TestCaseCounts[OtherTestId]).IsEqualTo(TestCaseCount.Exact(2));
    }

    [Test]
    public async Task TryRead_ATestWithoutAnyReference_ParsesToAnEmptyBlock()
    {
        var text = Build("\n", Header, "T " + TestId + " 1");

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsTrue();
        _ = await Assert.That(error).IsNull();
        _ = await Assert.That(Join(manifest.TestMethodIds)).IsEqualTo(TestId);
        _ = await Assert.That(manifest.ReferencesByTest[TestId]).IsEmpty();
        _ = await Assert.That(manifest.ReferencedMemberIds).IsEmpty();
    }

    [Test]
    public async Task TryRead_ALowerBoundCount_IsParsedAsALowerBound()
    {
        var text = Build("\n", Header, "T " + TestId + " 1+", "R " + ReferenceId);

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsTrue();
        _ = await Assert.That(error).IsNull();
        _ = await Assert.That(manifest.TestCaseCounts[TestId]).IsEqualTo(TestCaseCount.AtLeast(1));
    }

    /// <summary>
    /// A data source that turns out to be an empty sequence contributes no case at all, which is a
    /// statement the format has to be able to carry.
    /// </summary>
    [Test]
    public async Task TryRead_ACountOfZero_IsAccepted()
    {
        var text = Build("\n", Header, "T " + TestId + " 0");

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsTrue();
        _ = await Assert.That(error).IsNull();
        _ = await Assert.That(manifest.TestCaseCounts[TestId]).IsEqualTo(TestCaseCount.Exact(0));
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
    public async Task TryRead_BlankAndCommentLines_AreIgnoredBetweenBlocksToo()
    {
        var text = Build(
            "\n",
            "# a leading comment",
            "",
            "   ",
            Header,
            "",
            "# another comment",
            "T " + TestId + " 1",
            "",
            "# a comment inside the block",
            "R " + ReferenceId,
            "",
            "T " + OtherTestId + " 1",
            ""
        );

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsTrue();
        _ = await Assert.That(error).IsNull();
        _ = await Assert.That(Join(manifest.TestMethodIds)).IsEqualTo(TestId + "|" + OtherTestId);
        _ = await Assert.That(Join(manifest.ReferencesByTest[TestId])).IsEqualTo(ReferenceId);
        _ = await Assert.That(manifest.ReferencesByTest[OtherTestId]).IsEmpty();
    }

    [Test]
    [Arguments("X " + ReferenceId)]
    [Arguments("RR " + ReferenceId)]
    [Arguments("t " + ReferenceId)]
    [Arguments("frameshift-test-surface/2")]
    public async Task TryRead_UnknownLinePrefix_IsIgnored(string line)
    {
        var text = Build("\n", Header, line, "T " + TestId + " 1");

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsTrue();
        _ = await Assert.That(error).IsNull();
        _ = await Assert.That(Join(manifest.TestMethodIds)).IsEqualTo(TestId);
        _ = await Assert.That(manifest.ReferencedMemberIds.IsEmpty).IsTrue();
    }

    [Test]
    public async Task TryRead_HeaderMissing_ReportsTheFirstContentLine()
    {
        var text = Build("\n", "T " + TestId + " 1", "R " + ReferenceId);

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsFalse();
        _ = await Assert.That(manifest.IsEmpty).IsTrue();
        _ = await Assert
            .That(error)
            .IsEqualTo(
                "Line 1: expected the test-surface manifest header 'frameshift-test-surface/1', "
                    + "but found 'T M:Tests.CalculatorTests.Add_TwoValues_ReturnsSum 1'."
            );
    }

    [Test]
    public async Task TryRead_WrongHeader_ReportsTheOneBasedLineNumber()
    {
        var text = Build("\n", "# a comment", "", "frameshift-test-surface/0", "T " + TestId + " 1");

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

    /// <summary>
    /// The count is not optional: a <c>T</c> line without one would silently claim an unknown number of
    /// cases, which is precisely the information the heuristic depends on.
    /// </summary>
    [Test]
    public async Task TryRead_TestEntryWithoutACount_IsMalformed()
    {
        var text = Build("\n", Header, "T " + TestId, "R " + ReferenceId);

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsFalse();
        _ = await Assert.That(manifest.IsEmpty).IsTrue();
        _ = await Assert
            .That(error)
            .IsEqualTo(
                "Line 2: the 'T' entry for 'M:Tests.CalculatorTests.Add_TwoValues_ReturnsSum' does not "
                    + "specify a test case count."
            );
    }

    [Test]
    [Arguments("x", "x")]
    [Arguments("-1", "-1")]
    [Arguments("+1", "+1")]
    [Arguments("1++", "1++")]
    [Arguments("1.5", "1.5")]
    [Arguments("3 extra", "3 extra")]
    public async Task TryRead_TestEntryWithAMalformedCount_IsMalformed(string count, string reported)
    {
        var text = Build("\n", Header, "T " + TestId + " " + count, "R " + ReferenceId);

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsFalse();
        _ = await Assert.That(manifest.IsEmpty).IsTrue();
        _ = await Assert.That(error).IsEqualTo($"Line 2: '{reported}' is not a valid test case count.");
    }

    /// <summary>
    /// A reference without an enclosing block belongs to nothing, and guessing an owner for it would
    /// invent an attribution the writer never produced.
    /// </summary>
    [Test]
    public async Task TryRead_ReferenceBeforeAnyTest_IsMalformed()
    {
        var text = Build("\n", Header, "R " + ReferenceId, "T " + TestId + " 1");

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsFalse();
        _ = await Assert.That(manifest.IsEmpty).IsTrue();
        _ = await Assert.That(error).IsEqualTo("Line 2: the 'R' entry appears before any 'T' entry.");
    }

    [Test]
    public async Task TryRead_ReferenceAfterACommentButBeforeAnyTest_ReportsTheReferenceLine()
    {
        var text = Build("\n", Header, "# a comment", "", "R " + ReferenceId);

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsFalse();
        _ = await Assert.That(manifest.IsEmpty).IsTrue();
        _ = await Assert.That(error).IsEqualTo("Line 4: the 'R' entry appears before any 'T' entry.");
    }

    [Test]
    [Arguments("\n")]
    [Arguments("\r\n")]
    public async Task TryRead_LineEnding_IsAcceptedInBothForms(string newLine)
    {
        var text = Build(newLine, Header, "T " + TestId + " 3", "R " + ReferenceId);

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsTrue();
        _ = await Assert.That(error).IsNull();
        _ = await Assert.That(Join(manifest.TestMethodIds)).IsEqualTo(TestId);
        _ = await Assert.That(Join(manifest.ReferencedMemberIds)).IsEqualTo(ReferenceId);
        _ = await Assert.That(manifest.TestCaseCounts[TestId]).IsEqualTo(TestCaseCount.Exact(3));
    }

    /// <summary>
    /// A test method appears exactly once in a manifest. Merging two blocks would have to invent a count
    /// for the merged test, so a repeated test is reported instead of being fixed up silently.
    /// </summary>
    [Test]
    public async Task TryRead_TheSameTestDeclaredTwice_IsMalformed()
    {
        var text = Build("\n", Header, "T " + TestId + " 1", "T " + TestId + " 2");

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsFalse();
        _ = await Assert.That(manifest.IsEmpty).IsTrue();
        _ = await Assert
            .That(error)
            .IsEqualTo(
                "Line 3: the 'T' entry for 'M:Tests.CalculatorTests.Add_TwoValues_ReturnsSum' is "
                    + "declared more than once."
            );
    }

    [Test]
    public async Task TryRead_DuplicateReferencesInOneBlock_CollapseIntoOneEntry()
    {
        var text = Build(
            "\n",
            Header,
            "T " + TestId + " 1",
            "R " + ReferenceId,
            "R  " + ReferenceId + "  ",
            "R " + OtherReferenceId
        );

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsTrue();
        _ = await Assert.That(error).IsNull();
        _ = await Assert.That(manifest.ReferencesByTest[TestId].Count).IsEqualTo(2);
        _ = await Assert.That(Join(manifest.ReferencedMemberIds)).IsEqualTo(ReferenceId + "|" + OtherReferenceId);
    }

    [Test]
    public async Task TryRead_IdsDifferingOnlyInCase_AreKeptApart()
    {
        var text = Build(
            "\n",
            Header,
            "T " + TestId + " 1",
            "R M:Production.Calculator.Add",
            "R M:Production.Calculator.ADD"
        );

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsTrue();
        _ = await Assert.That(error).IsNull();
        _ = await Assert
            .That(Join(manifest.ReferencedMemberIds))
            .IsEqualTo("M:Production.Calculator.ADD|M:Production.Calculator.Add");
    }

    /// <summary>
    /// The counts and the references describe the same tests, so every lookup a consumer performs on one
    /// of them succeeds on the other one too.
    /// </summary>
    [Test]
    public async Task TryRead_ParsedManifest_KeysBothDictionariesByTheSameTests()
    {
        var text = Build("\n", Header, "T " + TestId + " 1", "R " + ReferenceId, "T " + OtherTestId + " 2+");

        var parsed = TestSurfaceManifestReader.TryRead(text, out var manifest, out var error);

        _ = await Assert.That(parsed).IsTrue();
        _ = await Assert.That(error).IsNull();
        _ = await Assert.That(Keys(manifest.TestCaseCounts)).IsEqualTo(TestId + "|" + OtherTestId);
        _ = await Assert.That(Keys(manifest.ReferencesByTest)).IsEqualTo(TestId + "|" + OtherTestId);
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

    private static string Keys<TValue>(ImmutableDictionary<string, TValue> dictionary) => Join(dictionary.Keys);

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
