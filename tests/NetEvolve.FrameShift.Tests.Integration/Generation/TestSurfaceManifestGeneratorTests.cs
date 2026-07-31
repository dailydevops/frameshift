namespace NetEvolve.FrameShift.Tests.Integration.Generation;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using NetEvolve.FrameShift.Generation;
using NetEvolve.FrameShift.Tests.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Drives <see cref="TestSurfaceManifestGenerator" /> exactly the way the compiler does and pins the
/// contract between the generator and the MSBuild target that turns its output back into a manifest file.
/// </summary>
/// <remarks>
/// <para>
/// The two halves of the manifest pipeline can only be tested together: the generator emits the manifest
/// wrapped in a block comment, and the target recreates the file by dropping the first and the last line.
/// If either side moves a single character, the production project reads a manifest that is either
/// malformed or silently truncated, so the shape of the emitted file is asserted literally here rather
/// than through the constants of the generator.
/// </para>
/// <para>
/// The questions split into two kinds. What the generator emitted for a given compilation is a snapshot:
/// the manifest of each of the four frameworks, the union a compilation referencing two of them produces,
/// and the documentation comment ids of the most awkward members a C# API can offer. A snapshot states
/// the answer in full and lets a reviewer read it, which no assertion over counts and prefixes can.
/// </para>
/// <para>
/// Everything a snapshot would weaken into "something changed" stays an explicit assertion: the contract
/// with the MSBuild target that the first line is <c>/*</c> and the last one <c>*/</c>, that a production
/// compilation generates nothing at all, that <c>FrameShiftEnabled=false</c> switches the generator off,
/// that two runs are byte identical, and that every emitted id resolves back to a symbol. None of those
/// is a statement about one particular output, so none of them belongs in a file that is regenerated
/// whenever the output legitimately changes.
/// </para>
/// <para>
/// The snapshots are identical on all eight target frameworks of the matrix, because everything in them
/// comes from the fixtures rather than from the executing runtime. The xUnit fixture is the single
/// exception, and only in that it does not exist on net6.0 and net7.0, where xUnit.net v3 ships no assets
/// at all; the snapshot it compares against on the other six is the same file for all of them.
/// </para>
/// </remarks>
public class TestSurfaceManifestGeneratorTests
{
    private const string ProductionAssemblyName = "ProductionAssembly";
    private const string TestAssemblyName = "TestAssembly";
    private const string ProductionPath = "Production.cs";
    private const string TestPath = "CalculatorTests.cs";

    private const string CommentStart = "/*";
    private const string CommentEnd = "*/";
    private const string Header = "frameshift-test-surface/1";

    private const char LineFeed = '\n';
    private const char CarriageReturn = '\r';
    private const string LineFeedText = "\n";

    private const string ProductionSource = """
        namespace Fixture;

        public class Calculator
        {
            public int Add(int left, int right)
            {
                return left + right;
            }

            public int Subtract(int left, int right)
            {
                return left - right;
            }
        }
        """;

    private const string TUnitSource = """
        namespace Tests;

        public class CalculatorTests
        {
            [TUnit.Core.Test]
            public void Add_ReturnsTheSum()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Add(2, 3);
            }
        }
        """;

#if FRAMESHIFT_XUNIT_V3
    private const string XunitSource = """
        namespace Tests;

        public class CalculatorTests
        {
            [Xunit.Fact]
            public void Add_ReturnsTheSum()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Add(2, 3);
            }
        }
        """;
#endif

    private const string NUnitSource = """
        namespace Tests;

        public class CalculatorTests
        {
            [NUnit.Framework.Test]
            public void Add_ReturnsTheSum()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Add(2, 3);
            }
        }
        """;

    private const string MSTestSource = """
        namespace Tests;

        public class CalculatorTests
        {
            [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
            public void Add_ReturnsTheSum()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Add(2, 3);
            }
        }
        """;

    /// <summary>
    /// One TUnit test and one NUnit test in the same compilation, each one touching a different
    /// production method, so that the union of the two surfaces is visible in the manifest. Neither test
    /// is recognised by the probe of the other framework, therefore a manifest that named only one of the
    /// two would prove that one framework had been preferred over the other.
    /// </summary>
    private const string MixedFrameworkSource = """
        namespace Tests;

        public class CalculatorTests
        {
            [TUnit.Core.Test]
            public void Add_ReturnsTheSum()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Add(2, 3);
            }

            [NUnit.Framework.Test]
            public void Subtract_ReturnsTheDifference()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Subtract(5, 2);
            }
        }
        """;

    private const string TwoTestsSource = """
        namespace Tests;

        public class CalculatorTests
        {
            [TUnit.Core.Test]
            public void Add_ReturnsTheSum()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Add(2, 3);
            }

            [TUnit.Core.Test]
            public void Subtract_ReturnsTheDifference()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Subtract(5, 2);
            }
        }
        """;

    private const string WithoutAnyTestSource = """
        namespace Tests;

        public class NothingIsTestedHere
        {
            public int Value { get; set; }
        }
        """;

    /// <summary>
    /// A production fixture whose documentation comment ids are as unpleasant as they get: an arity
    /// suffix, a generic method with its own arity, an indexer with a parameter list, an operator, an
    /// explicitly implemented interface member and a nested type.
    /// </summary>
    private const string AwkwardProductionSource = """
        namespace Fixture;

        public interface IShape
        {
            int Corners { get; }
        }

        public class Box<TItem> : IShape
        {
            int IShape.Corners => 4;

            public TItem? Value { get; set; }

            public int this[int index] => index;

            public static TResult Convert<TResult>(TItem item, TResult fallback) => fallback;

            public static Box<TItem> operator +(Box<TItem> left, Box<TItem> right) => left;

            public sealed class Nested
            {
                public int Depth() => 1;
            }
        }
        """;

    private const string AwkwardTestSource = """
        namespace Tests;

        public class BoxTests
        {
            [TUnit.Core.Test]
            public void Box_ExercisesTheAwkwardMembers()
            {
                Fixture.Box<int> box = new Fixture.Box<int>();

                box.Value = 1;

                Fixture.Box<int> sum = box + box;
                Fixture.IShape shape = box;
                Fixture.Box<int>.Nested nested = new Fixture.Box<int>.Nested();

                _ = box[0];
                _ = shape.Corners;
                _ = nested.Depth();
                _ = Fixture.Box<int>.Convert<string>(1, "x");
                _ = sum;
            }
        }
        """;

    /// <summary>
    /// A fixture that does not compile makes every assertion around it meaningless, so all of them are
    /// proven to be clean C# first.
    /// </summary>
    [Test]
    public async Task Fixtures_EveryCompilation_CompileWithoutErrors()
    {
        var production = CreateProduction();
        List<string> described = [Describe(production), Describe(CreateAwkwardProduction())];

        described.Add(Describe(CreateTest(TestFramework.TUnit, TUnitSource, production)));
#if FRAMESHIFT_XUNIT_V3
        described.Add(Describe(CreateTest(TestFramework.XunitV3, XunitSource, production)));
#endif
        described.Add(Describe(CreateTest(TestFramework.NUnit, NUnitSource, production)));
        described.Add(Describe(CreateTest(TestFramework.MSTest, MSTestSource, production)));
        described.Add(Describe(CreateTest(TestFramework.All, MixedFrameworkSource, production)));
        described.Add(Describe(CreateTest(TestFramework.TUnit, TwoTestsSource, production)));
        described.Add(Describe(CreateTest(TestFramework.TUnit, WithoutAnyTestSource, production)));
        described.Add(Describe(CreateAwkwardTest()));

        _ = await Assert
            .That(string.Join("|", described.Distinct(StringComparer.Ordinal)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    /// <summary>
    /// A production project references no test framework, so no probe matches and the generator must not
    /// contribute a file at all — an empty file would already be a file the MSBuild target picks up.
    /// </summary>
    [Test]
    public async Task Generate_ProductionCompilationWithoutAnyTestFramework_GeneratesNoSourceAtAll()
    {
        var output = Run(CreateProduction());

        _ = await Assert.That(output.HintNames).IsEqualTo(string.Empty);
        _ = await Assert.That(output.Sources.Length).IsEqualTo(0);

        var diagnostics = DiagnosticAssertions.Describe(output.Diagnostics);

        _ = await Assert.That(diagnostics).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    /// <summary>
    /// Exactly one source under exactly the hint name the MSBuild target searches for.
    /// </summary>
    [Test]
    public async Task Generate_TUnitCompilation_GeneratesExactlyOneSourceWithTheAgreedHintName()
    {
        var output = Run(CreateTest(TestFramework.TUnit, TUnitSource, CreateProduction()));

        _ = await Assert.That(output.Sources.Length).IsEqualTo(1);
        _ = await Assert.That(output.HintNames).IsEqualTo("TestSurfaceManifest.g.cs");
        _ = await Assert.That(TestSurfaceManifestGenerator.HintName).IsEqualTo("TestSurfaceManifest.g.cs");
    }

    /// <summary>
    /// The contract with the MSBuild target: the first line is <c>/*</c>, the last line is <c>*/</c>,
    /// each of them occurs exactly once, and the manifest header follows immediately.
    /// </summary>
    [Test]
    public async Task Generate_TUnitCompilation_WrapsTheManifestInOneBlockCommentAndNothingElse()
    {
        var text = Generate(CreateTest(TestFramework.TUnit, TUnitSource, CreateProduction()));
        var lines = Lines(text);

        _ = await Assert.That(lines[0]).IsEqualTo(CommentStart);
        _ = await Assert.That(lines[^1]).IsEqualTo(CommentEnd);
        _ = await Assert.That(lines[1]).IsEqualTo(Header);
        _ = await Assert.That(Occurrences(text, CommentStart)).IsEqualTo(1);
        _ = await Assert.That(Occurrences(text, CommentEnd)).IsEqualTo(1);
        _ = await Assert.That(text.EndsWith(CommentEnd + LineFeed, StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// Everything the generator emits for a TUnit test project, verbatim.
    /// </summary>
    [Test]
    public async Task Generate_TUnitCompilation_MatchesTheSnapshot() =>
        await VerifyGeneratedSourcesAsync(CreateTest(TestFramework.TUnit, TUnitSource, CreateProduction()))
            .ConfigureAwait(false);

#if FRAMESHIFT_XUNIT_V3
    /// <summary>
    /// The same for xUnit, which reaches the generator through a different probe. The fixture and this
    /// test are compiled out on net6.0 and net7.0, where xUnit.net v3 ships no assets at all; the other
    /// six frameworks of the matrix compare the very same snapshot.
    /// </summary>
    [Test]
    public async Task Generate_XunitCompilation_MatchesTheSnapshot() =>
        await VerifyGeneratedSourcesAsync(CreateTest(TestFramework.XunitV3, XunitSource, CreateProduction()))
            .ConfigureAwait(false);
#endif

    /// <summary>
    /// The same for NUnit.
    /// </summary>
    [Test]
    public async Task Generate_NUnitCompilation_MatchesTheSnapshot() =>
        await VerifyGeneratedSourcesAsync(CreateTest(TestFramework.NUnit, NUnitSource, CreateProduction()))
            .ConfigureAwait(false);

    /// <summary>
    /// The same for MSTest.
    /// </summary>
    [Test]
    public async Task Generate_MSTestCompilation_MatchesTheSnapshot() =>
        await VerifyGeneratedSourcesAsync(CreateTest(TestFramework.MSTest, MSTestSource, CreateProduction()))
            .ConfigureAwait(false);

    /// <summary>
    /// A project in the middle of a migration references two frameworks at once. Preferring one of them
    /// would drop half of the tests from the manifest and make the production side report gaps for code
    /// that is covered, so the single manifest carries the union of both surfaces.
    /// </summary>
    /// <remarks>
    /// The snapshot is what makes the union visible: the TUnit test, the NUnit test and both of the
    /// production methods they exercise stand in one file, so a manifest that silently preferred one of
    /// the two frameworks shows up as the missing half rather than as a changed count.
    /// </remarks>
    [Test]
    public async Task Generate_CompilationUsingTwoFrameworks_MatchesTheSnapshot() =>
        await VerifyGeneratedSourcesAsync(CreateTest(TestFramework.All, MixedFrameworkSource, CreateProduction()))
            .ConfigureAwait(false);

    /// <summary>
    /// Switching FrameShift off has to switch the generator off as well, otherwise the promised escape
    /// hatch from the cost of the whole-compilation analysis does not exist.
    /// </summary>
    [Test]
    public async Task Generate_FrameShiftDisabled_GeneratesNoSourceAtAll()
    {
        var test = CreateTest(TestFramework.TUnit, TUnitSource, CreateProduction());
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["build_property.FrameShiftEnabled"] = "false",
        };

        var disabled = Run(test, options);
        var enabled = Run(test);

        _ = await Assert.That(disabled.Sources.Length).IsEqualTo(0);
        _ = await Assert.That(enabled.Sources.Length).IsEqualTo(1);
    }

    /// <summary>
    /// A test project without a single test still belongs to its framework, so it gets a manifest — an
    /// empty one. Emitting nothing would leave a stale manifest on disk behind and make the production
    /// side judge the code against tests that no longer exist.
    /// </summary>
    [Test]
    public async Task Generate_TestCompilationWithoutAnyTest_GeneratesTheHeaderWithoutAnyEntry()
    {
        var test = CreateTest(TestFramework.TUnit, WithoutAnyTestSource, CreateProduction());
        var text = Generate(test);
        var (success, error, manifest) = Read(text);

        _ = await Assert.That(string.Join("|", Lines(text))).IsEqualTo(CommentStart + "|" + Header + "|" + CommentEnd);
        _ = await Assert.That(success).IsTrue();
        _ = await Assert.That(error).IsEqualTo(string.Empty);
        _ = await Assert.That(manifest.IsEmpty).IsTrue();
        _ = await Assert.That(Canonical(manifest)).IsEqualTo(Header + LineFeed);
    }

    /// <summary>
    /// The manifest is written next to the project and lands in version control, so an unchanged test
    /// project must never produce a diff. Two runs over the same compilation, and a run over a freshly
    /// built but equal compilation, all have to yield the very same bytes.
    /// </summary>
    [Test]
    public async Task Generate_SameCompilationTwice_ProducesByteIdenticalOutput()
    {
        var test = CreateTest(TestFramework.TUnit, TwoTestsSource, CreateProduction());

        var first = Generate(test);
        var second = Generate(test);
        var third = Generate(CreateTest(TestFramework.TUnit, TwoTestsSource, CreateProduction()));

        _ = await Assert.That(second).IsEqualTo(first);
        _ = await Assert.That(third).IsEqualTo(first);
        _ = await Assert.That(first.Length).IsGreaterThan(Header.Length);
    }

    /// <summary>
    /// Determinism rests on the ordering: the test entries come first, the referenced members second, and
    /// both groups are sorted ordinally rather than in the order the walk happened to find them.
    /// </summary>
    [Test]
    public async Task Generate_ManifestEntries_AreGroupedAndSortedOrdinally()
    {
        var text = Generate(CreateTest(TestFramework.TUnit, TwoTestsSource, CreateProduction()));
        var (_, _, manifest) = Read(text);
        var entries = Lines(text).Skip(2).SkipLast(1).ToImmutableArray();

        var markers = string.Concat(entries.Select(entry => entry[0]));
        var expectedMarkers =
            new string('T', manifest.TestMethodIds.Count) + new string('R', manifest.ReferencedMemberIds.Count);

        var (actualTests, expectedTests) = Sorted(entries, 'T');
        var (actualReferences, expectedReferences) = Sorted(entries, 'R');

        _ = await Assert.That(manifest.TestMethodIds.Count).IsEqualTo(2);
        _ = await Assert.That(manifest.ReferencedMemberIds.Count).IsGreaterThan(2);
        _ = await Assert.That(markers).IsEqualTo(expectedMarkers);
        _ = await Assert.That(string.Join("|", actualTests)).IsEqualTo(string.Join("|", expectedTests));
        _ = await Assert.That(string.Join("|", actualReferences)).IsEqualTo(string.Join("|", expectedReferences));
    }

    /// <summary>
    /// Documentation comment ids are the currency between the two passes, so the ids of the nastiest
    /// members a C# API can offer have to resolve back to a symbol. An id that does not resolve would
    /// silently shrink the reachable set on the production side.
    /// </summary>
    [Test]
    public async Task Generate_AwkwardMemberNames_ProducesIdsThatResolveBackToSymbols()
    {
        var test = CreateAwkwardTest();
        var (_, _, manifest) = Read(Generate(test));
        var ids = manifest.TestMethodIds.Union(manifest.ReferencedMemberIds);
        var unresolved = ids.Where(id => !Resolves(id, test)).OrderBy(id => id, StringComparer.Ordinal);

        _ = await Assert.That(string.Join("|", unresolved)).IsEqualTo(string.Empty);
        _ = await Assert.That(Contains(ids, "M:Fixture.Box`1.op_Addition")).IsTrue();
        _ = await Assert.That(Contains(ids, "M:Fixture.Box`1.Convert``1")).IsTrue();
        _ = await Assert.That(Contains(ids, "P:Fixture.Box`1.Item(System.Int32)")).IsTrue();
        _ = await Assert.That(Contains(ids, "P:Fixture.IShape.Corners")).IsTrue();
        _ = await Assert.That(Contains(ids, "M:Fixture.Box`1.Nested.Depth")).IsTrue();
    }

    /// <summary>
    /// Every documentation comment id the awkward fixture produces, verbatim: the arity suffix of a
    /// generic type, the double backtick arity of a generic method with its type parameters in the
    /// signature, the parameter list of an indexer, the mangled name of an operator, the interface member
    /// behind an explicit implementation and the dotted name of a nested type.
    /// </summary>
    /// <remarks>
    /// This snapshot is the readable counterpart of
    /// <see cref="Generate_AwkwardMemberNames_ProducesIdsThatResolveBackToSymbols" />: that test proves
    /// every id resolves, which a snapshot cannot, and this one states which ids there are, which the
    /// resolution test cannot. Both are needed — an id that silently disappeared from the walk would
    /// still resolve for the ones that remain.
    /// </remarks>
    [Test]
    public async Task Generate_AwkwardMemberNames_MatchesTheSnapshot() =>
        await VerifyGeneratedSourcesAsync(CreateAwkwardTest()).ConfigureAwait(false);

    /// <summary>
    /// The defensive guard of the generator: not one emitted line may contain the sequence that closes
    /// the block comment, because such a line would end the comment in the middle of the file and turn
    /// the rest of the manifest into code that cannot compile.
    /// </summary>
    /// <remarks>
    /// A documentation comment id cannot contain <c>*/</c> — <c>/</c> is not part of the character set
    /// the format uses, not even for nested types or pointers — so this test proves the invariant on the
    /// most hostile fixture available instead of forcing the guard with a symbol name that cannot exist.
    /// </remarks>
    [Test]
    public async Task Generate_AwkwardMemberNames_EmitsNoLineThatClosesTheBlockCommentEarly()
    {
        var output = Run(CreateAwkwardTest());
        var text = output.TextOf(TestSurfaceManifestGenerator.HintName);
        var inner = Lines(text).Skip(1).SkipLast(1);
        var offenders = inner.Where(line => line.Contains(CommentEnd, StringComparison.Ordinal));

        _ = await Assert.That(string.Join("|", offenders)).IsEqualTo(string.Empty);
        _ = await Assert.That(Occurrences(text, CommentEnd)).IsEqualTo(1);
        _ = await Assert.That(Describe(output.Compilation)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    /// <summary>
    /// Runs the generator over <paramref name="compilation" /> and verifies everything it produced —
    /// the driver diagnostics and the generated sources — against the snapshot of the calling test.
    /// </summary>
    /// <param name="compilation">The compilation the generator sees.</param>
    /// <returns>A task that completes when the snapshot has been compared.</returns>
    /// <remarks>
    /// <para>
    /// Both collections are sorted before they are handed to the snapshot. Neither the driver nor the
    /// generator promises an order — a source output callback may run concurrently with the callbacks of
    /// other generators — so an unsorted snapshot would fail at random instead of when the output
    /// changes. The manifest text inside a generated source is already ordered by the writer.
    /// </para>
    /// <para>
    /// The diagnostics are rendered into one string rather than snapshotted as a collection. Verify ships
    /// a different assembly per target framework and they do not agree on how an empty collection is
    /// written — some omit the member, others write <c>null</c> — which would make the eight runs of the
    /// matrix disagree about a snapshot none of them changed. A rendered string is the same everywhere and
    /// still fails the day the generator starts reporting something.
    /// </para>
    /// <para>
    /// The generated file still has to compile, which no snapshot can express, so that stays an explicit
    /// assertion next to the snapshot.
    /// </para>
    /// </remarks>
    private static async Task VerifyGeneratedSourcesAsync(Compilation compilation)
    {
        var output = Run(compilation);

        var diagnostics = output.Diagnostics.IsEmpty
            ? DiagnosticAssertions.NoDiagnostics
            : string.Join(
                "\n",
                output
                    .Diagnostics.OrderBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
                    .ThenBy(diagnostic => diagnostic.Location.SourceSpan.Start)
                    .Select(DiagnosticAssertions.Describe)
            );

        var sources = output
            .Sources.OrderBy(source => source.HintName, StringComparer.Ordinal)
            .Select(source => source.Text.ToString())
            .ToImmutableArray();

        _ = await Assert.That(Describe(output.Compilation)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        _ = await Verify(new { diagnostics, sources }).ConfigureAwait(false);
    }

    private static GeneratorRunner.Output Run(
        Compilation compilation,
        IReadOnlyDictionary<string, string>? globalOptions = null
    ) => GeneratorRunner.Run(new TestSurfaceManifestGenerator(), compilation, globalOptions);

    private static string Generate(Compilation compilation) =>
        Run(compilation).TextOf(TestSurfaceManifestGenerator.HintName);

    private static CSharpCompilation CreateProduction() =>
        CompilationFactory.Create(
            ProductionSource,
            TestFramework.None,
            ProductionAssemblyName,
            filePath: ProductionPath
        );

    private static CSharpCompilation CreateAwkwardProduction() =>
        CompilationFactory.Create(
            AwkwardProductionSource,
            TestFramework.None,
            ProductionAssemblyName,
            filePath: ProductionPath
        );

    private static CSharpCompilation CreateAwkwardTest() =>
        CreateTest(TestFramework.TUnit, AwkwardTestSource, CreateAwkwardProduction());

    private static CSharpCompilation CreateTest(TestFramework framework, string source, Compilation production) =>
        CompilationFactory.Create(
            source,
            framework,
            TestAssemblyName,
            additionalReferences: [production.ToMetadataReference()],
            filePath: TestPath
        );

    /// <summary>
    /// Parses the generated file the way the MSBuild target does: drop the first and the last line, hand
    /// the rest to the reader.
    /// </summary>
    /// <param name="generated">The content of the generated source file.</param>
    /// <returns>Whether the text parsed, the reported error and the parsed manifest.</returns>
    private static (bool Success, string Error, TestSurfaceManifest Manifest) Read(string generated)
    {
        var inner = string.Join(LineFeedText, Lines(generated).Skip(1).SkipLast(1)) + LineFeedText;
        var success = TestSurfaceManifestReader.TryRead(SourceText.From(inner), out var manifest, out var error);

        return (success, error ?? string.Empty, manifest);
    }

    private static string Canonical(TestSurfaceManifest manifest) => TestSurfaceManifestWriter.Write(manifest);

    /// <summary>
    /// Splits the generated text into its lines, dropping the empty remainder behind the trailing line
    /// feed, which is the end of the last line and not a line of its own.
    /// </summary>
    /// <param name="text">The generated text.</param>
    /// <returns>The lines, without their line endings.</returns>
    private static ImmutableArray<string> Lines(string text)
    {
        var lines = text.Split(LineFeed).Select(line => line.TrimEnd(CarriageReturn)).ToList();

        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return [.. lines];
    }

    private static (ImmutableArray<string> Actual, ImmutableArray<string> Expected) Sorted(
        ImmutableArray<string> entries,
        char marker
    )
    {
        var actual = entries.Where(entry => entry[0] == marker).ToImmutableArray();

        return (actual, [.. actual.OrderBy(entry => entry, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Determines whether a documentation comment id can be turned back into the symbol it names.
    /// </summary>
    /// <param name="id">The documentation comment id.</param>
    /// <param name="compilation">The compilation the id is resolved in.</param>
    /// <returns><see langword="true" /> if at least one symbol matches; otherwise <see langword="false" />.</returns>
    private static bool Resolves(string id, Compilation compilation) =>
        !DocumentationCommentId.GetSymbolsForDeclarationId(id, compilation).IsDefaultOrEmpty;

    private static bool Contains(IEnumerable<string> ids, string prefix) =>
        ids.Any(id => id.StartsWith(prefix, StringComparison.Ordinal));

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);

        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static string Describe(Compilation compilation) =>
        DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation));
}
