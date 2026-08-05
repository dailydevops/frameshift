namespace NetEvolve.FrameShift.Tests.Integration.Analyzers;

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using NetEvolve.FrameShift.Analyzers;
using NetEvolve.FrameShift.Diagnostics;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Drives <see cref="TUnitTestSurfaceAnalyzer" /> end to end against a real two-assembly setup: a
/// production assembly that is visible only as a metadata reference, and a test assembly compiled
/// against it that carries genuine <c>[Test]</c> methods.
/// </summary>
/// <remarks>
/// <para>
/// Every expectation here is about the two things the analyzer promises to the developer: a test that
/// cannot possibly contribute to the tested surface is named (<c>FSH0004</c>), and a checked-in
/// manifest that no longer describes the tests is reported before anybody trusts it (<c>FSH0003</c>).
/// The manifest the tests feed back in is always the one <see cref="TestSurfaceManifestWriter" />
/// produces for the very compilation under analysis, so that no expectation depends on a hand-written
/// documentation comment id.
/// </para>
/// <para>
/// <see cref="AnalyzerRunner" /> turns an analyzer exception into a failing run instead of returning
/// <c>AD0001</c>, therefore every test in this class also asserts that the analyzer did not crash.
/// </para>
/// </remarks>
public class TUnitTestSurfaceAnalyzerTests
{
    private const string ProductionAssemblyName = "ProductionAssembly";
    private const string TestAssemblyName = "TestAssembly";
    private const string ProductionPath = "Production.cs";
    private const string TestPath = "CalculatorTests.cs";

    private const string CoveringTestName = "Add_ExercisesProduction";
    private const string LocalOnlyTestName = "LocalStateOnly_TouchesNoProduction";

    private const string PartialTestPath = "PartialCalculatorTests.cs";
    private const string PartialLocalOnlyTestName = "PartialLocalStateOnly_TouchesNoProduction";

    private const string ExoticShapePath = "ExoticShapeTests.cs";
    private const string GenericTestName = "Generic_TouchesNoProduction";
    private const string NestedTestName = "Nested_TouchesNoProduction";
    private const string ExplicitInterfaceTestName = "RunExplicitly_TouchesNoProduction";
    private const string FileLocalTestName = "FileLocal_TouchesNoProduction";

    private const string NoTestsScenario = "framework referenced, no test method";
    private const string ForeignAttributeScenario = "test attribute of an unrelated framework";
    private const string FrameworkLikeAssemblyScenario = "framework-like assembly name, no test method";
    private const string FrameworkLikeAssemblyName = "TUnit.Satellite";

    private const string MalformedManifest = "not-a-test-surface-manifest\n";
    private const string UnrelatedAdditionalFilePath = "Notes.txt";

    private const string MalformedHeaderDetail =
        "Line 1: expected the test-surface manifest header 'frameshift-test-surface/1', "
        + "but found 'not-a-test-surface-manifest'.";

    /// <summary>
    /// A manifest whose header is fine and whose only entry names no id at all, so that the problem is
    /// found by the entry rule rather than by the header rule.
    /// </summary>
    private const string EntryWithoutIdManifest = "frameshift-test-surface/1\nT\n";

    private const string EntryWithoutIdDetail = "Line 2: the 'T' entry does not specify a documentation comment id.";

    private const string GhostReferenceId = "M:Fixture.Ghost.Vanished";
    private const string ReferencePrefix = "R ";

    private const string ProductionSource = """
        namespace Fixture;

        public class Calculator
        {
            public int Add(int left, int right)
            {
                return Doubler.Twice(left) + right;
            }

            public int Subtract(int left, int right)
            {
                return left - right;
            }
        }

        public static class Doubler
        {
            public static int Twice(int value)
            {
                return value + value;
            }
        }
        """;

    /// <summary>
    /// The test assembly under analysis. The local-only test deliberately touches neither the
    /// production assembly nor the framework: a predefined type keyword, a <c>var</c> or an operator
    /// would all bind to a member outside this assembly and would therefore count as a production
    /// reference, which is exactly what the <c>FSH0004</c> expectation is about.
    /// </summary>
    private const string TestSource = """
        namespace Tests;

        using TUnit.Core;

        public class CalculatorTests
        {
            [Test]
            public void Add_ExercisesProduction()
            {
                Fixture.Calculator calculator = new Fixture.Calculator();

                _ = calculator.Add(2, 3);
            }

            [Test]
            public void LocalStateOnly_TouchesNoProduction() => Verify(Compute());

            private static int Compute() => 41;

            private static void Verify(int value)
            {
            }
        }
        """;

    /// <summary>
    /// A test method written as a partial one: the attribute sits on the defining declaration and the
    /// body on the implementing one. Both parts report the merged attributes, so a project written this
    /// way is exactly the shape in which the very same test could be named twice.
    /// </summary>
    private const string PartialTestSource = """
        namespace Tests;

        using TUnit.Core;

        public partial class PartialCalculatorTests
        {
            [Test]
            public partial void PartialLocalStateOnly_TouchesNoProduction();

            public partial void PartialLocalStateOnly_TouchesNoProduction() => Verify(Compute());

            private static int Compute() => 41;

            private static void Verify(int value)
            {
            }
        }
        """;

    /// <summary>
    /// The four declaration shapes a test method can take in which the two identities the analysis
    /// depends on are least obvious: a generic method, a method of a type nested in a generic one, an
    /// explicit interface implementation - whose symbol carries the interface in its name - and a method
    /// of a file-local type, whose metadata name the compiler mangles per file. Each of them exercises
    /// nothing but its own assembly, so each of them is an <c>FSH0004</c> report.
    /// </summary>
    /// <remarks>
    /// <c>ExoticCases</c> is declared before <c>IRunnable</c> on purpose: both declare a method of the
    /// same name, and the fixture needs the identifier of the implementation rather than the one of the
    /// interface member.
    /// </remarks>
    private const string ExoticShapeSource = """
        namespace Tests;

        using TUnit.Core;

        public class ExoticCases : IRunnable
        {
            [Test]
            public void Generic_TouchesNoProduction<TValue>() => Verify(Compute());

            [Test]
            void IRunnable.RunExplicitly_TouchesNoProduction() => Verify(Compute());

            private static int Compute() => 41;

            private static void Verify(int value)
            {
            }
        }

        public interface IRunnable
        {
            void RunExplicitly_TouchesNoProduction();
        }

        public class Outer<TValue>
        {
            public class Inner
            {
                [Test]
                public void Nested_TouchesNoProduction() => Verify(Compute());

                private static int Compute() => 41;

                private static void Verify(int value)
                {
                }
            }
        }

        file class FileLocalCases
        {
            [Test]
            public void FileLocal_TouchesNoProduction() => Verify(Compute());

            private static int Compute() => 41;

            private static void Verify(int value)
            {
            }
        }
        """;

    /// <summary>
    /// A compilation that carries the framework reference but declares no test method at all.
    /// </summary>
    private const string WithoutTestsSource = """
        namespace Tests;

        public class NotATestClass
        {
            public int Compute() => 41;
        }
        """;

    /// <summary>
    /// A compilation whose <c>[Test]</c> attribute belongs to an unrelated framework, so that the probe
    /// has to reject it on the declaring assembly rather than on the attribute name.
    /// </summary>
    private const string ForeignAttributeSource = """
        namespace Tests;

        using System;

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class TestAttribute : Attribute
        {
        }

        public class ForeignCases
        {
            [Test]
            public void LooksLikeATest()
            {
            }
        }
        """;

    /// <summary>
    /// A satellite assembly whose name matches the framework prefix while declaring nothing a test could
    /// be recognised by.
    /// </summary>
    private const string FrameworkLikeSatelliteSource = """
        namespace Satellite;

        public static class Marker
        {
        }
        """;

    /// <summary>
    /// A test compilation of a framework no probe in the registry knows: the attribute is declared by the
    /// compilation itself, so not a single registered framework can be awake on it.
    /// </summary>
    private const string UnregisteredFrameworkSource = """
        namespace Tests;

        using System;

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class CaseAttribute : Attribute
        {
        }

        public class UnregisteredCases
        {
            [Case]
            public void LocalOnly_TouchesNoProduction() => Verify(Compute());

            private static int Compute() => 41;

            private static void Verify(int value)
            {
            }
        }
        """;

    private const string CaseAttributeMetadataName = "Tests.CaseAttribute";
    private const string CaseAttributeName = "CaseAttribute";
    private const string UnregisteredFrameworkName = "Fixture";

    /// <summary>
    /// The names of the four unusually shaped test methods of <see cref="ExoticShapeSource" />, which is
    /// at the same time the exact set of tests the analyzer has to name on that fixture.
    /// </summary>
    private static readonly string[] _exoticTestNames =
    [
        GenericTestName,
        ExplicitInterfaceTestName,
        NestedTestName,
        FileLocalTestName,
    ];

    [Test]
    public async Task Fixtures_BothAssemblies_CompileWithoutErrors()
    {
        var production = CreateProduction();
        var test = CreateTest(production);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(production)))
                .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
            _ = await Assert
                .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(test)))
                .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    /// <summary>
    /// A production project must never see a single diagnostic of the test-side analyzer, not even the
    /// manifest complaint that the very same additional file provokes on a test project.
    /// </summary>
    [Test]
    public async Task Analyzer_CompilationWithoutTestFramework_ReportsNothing()
    {
        var diagnostics = await RunAllAsync(CreateProduction(), MalformedManifest).ConfigureAwait(false);

        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    public async Task Analyzer_TestExercisingProduction_IsNotReportedAsWithoutProductionReference()
    {
        var diagnostics = await RunAsync(CreateTest(), DiagnosticIds.TestWithoutProductionReference)
            .ConfigureAwait(false);

        var namesTheCoveringTest = diagnostics.Any(diagnostic =>
            GetMessage(diagnostic).Contains(CoveringTestName, StringComparison.Ordinal)
        );

        _ = await Assert.That(namesTheCoveringTest).IsFalse();
    }

    [Test]
    public async Task Analyzer_TestWithoutProductionReference_IsReportedOnceAtItsIdentifier()
    {
        var test = CreateTest();
        var identifier = FindMethod(test, LocalOnlyTestName).Identifier;

        var diagnostics = await RunAsync(test, DiagnosticIds.TestWithoutProductionReference).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(diagnostics).Count().IsEqualTo(1);
            _ = await Assert.That(diagnostics[0].Location.SourceSpan).IsEqualTo(identifier.Span);
            _ = await Assert
                .That(GetMessage(diagnostics[0]).Contains(LocalOnlyTestName, StringComparison.Ordinal))
                .IsTrue();
        }
    }

    [Test]
    public async Task Analyzer_WithoutAnyManifest_ReportsNoManifestProblem()
    {
        var diagnostics = await RunAsync(CreateTest(), DiagnosticIds.InvalidTestSurfaceManifest).ConfigureAwait(false);

        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    public async Task Analyzer_MalformedManifest_ReportsTheParseProblem()
    {
        var diagnostics = await RunAsync(CreateTest(), DiagnosticIds.InvalidTestSurfaceManifest, MalformedManifest)
            .ConfigureAwait(false);

        _ = await Assert
            .That(DiagnosticAssertions.Describe(diagnostics))
            .IsEqualTo(DescribeManifestProblem(MalformedHeaderDetail));
    }

    /// <summary>
    /// A manifest that fails on one of its entries is reported with that entry's line and reason, not
    /// with a generic complaint about the file. The reader names a reason for every rejection it makes,
    /// which is why the analyzer's own fallback wording never reaches a developer.
    /// </summary>
    [Test]
    public async Task Analyzer_ManifestEntryWithoutAnId_ReportsTheOffendingLine()
    {
        var diagnostics = await RunAsync(CreateTest(), DiagnosticIds.InvalidTestSurfaceManifest, EntryWithoutIdManifest)
            .ConfigureAwait(false);

        _ = await Assert
            .That(DiagnosticAssertions.Describe(diagnostics))
            .IsEqualTo(DescribeManifestProblem(EntryWithoutIdDetail));
    }

    /// <summary>
    /// A partial test method is one test, so it is named once and at the identifier of its defining
    /// declaration - the declaration its symbol is bound to. Reporting it once per part would name the
    /// same test twice, and falling back to another location would point the developer at the body
    /// instead of at the test.
    /// </summary>
    [Test]
    public async Task Analyzer_PartialTestWithoutProductionReference_IsReportedOnceAtTheDefiningDeclaration()
    {
        var test = CreatePartialTest();
        var identifier = FindMethod(test, PartialLocalOnlyTestName).Identifier;

        var diagnostics = await RunAsync(test, DiagnosticIds.TestWithoutProductionReference).ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(test)))
                .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
            _ = await Assert.That(diagnostics).Count().IsEqualTo(1);
            _ = await Assert.That(diagnostics[0].Location.SourceSpan).IsEqualTo(identifier.Span);
            _ = await Assert
                .That(GetMessage(diagnostics[0]).Contains(PartialLocalOnlyTestName, StringComparison.Ordinal))
                .IsTrue();
        }
    }

    [Test]
    public async Task Fixtures_ExoticallyShapedTests_CompileWithoutErrors()
    {
        var test = CreateExoticShapeTest();

        _ = await Assert
            .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(test)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    /// <summary>
    /// Every one of the four unusual declaration shapes is named exactly once and anchored at its own
    /// name, which is the outcome the location fallbacks of the analysis exist for and never have to
    /// supply: a generic method, a method of a type nested in a generic one, an explicit interface
    /// implementation and a method of a file-local type all resolve to their identifier like any other.
    /// </summary>
    [Test]
    public async Task Analyzer_ExoticallyShapedTestsWithoutProductionReference_ReportsEachOneAtItsIdentifier()
    {
        var test = CreateExoticShapeTest();
        var expected = _exoticTestNames.Select(name => FindMethod(test, name).Identifier.Span);

        var diagnostics = await RunAsync(test, DiagnosticIds.TestWithoutProductionReference).ConfigureAwait(false);

        var messages = diagnostics.Select(diagnostic => GetMessage(diagnostic)).ToImmutableArray();
        var mentions = _exoticTestNames.Select(name => CountMentions(messages, name));

        using (Assert.Multiple())
        {
            _ = await Assert.That(diagnostics.Length).IsEqualTo(_exoticTestNames.Length);
            _ = await Assert.That(Describe(mentions)).IsEqualTo("1, 1, 1, 1");
            _ = await Assert
                .That(Describe(diagnostics.Select(diagnostic => diagnostic.Location.SourceSpan)))
                .IsEqualTo(Describe(expected));
        }
    }

    /// <summary>
    /// The invariant both fallbacks of the reporting path rest on, asserted on the shapes most likely to
    /// break it: a discovered test method is always declared by a <see cref="MethodDeclarationSyntax" />
    /// and always has a documentation comment id. As long as that holds, neither the location fallback
    /// nor the display-string fallback of the report key can be reached, and a shape that ever broke it
    /// would fail here instead of silently changing what a developer reads.
    /// </summary>
    [Test]
    public async Task Discovery_ExoticallyShapedTests_YieldAMethodDeclarationAndADeclarationIdForEachOfThem()
    {
        var test = CreateExoticShapeTest();
        var recognizer = TUnitTestFrameworkProbe.Instance.TryCreateRecognizer(test)!;

        var methods = TestMethodDiscovery.FindTestMethods(test, recognizer, CancellationToken.None);

        var withoutDeclaration = methods.Where(method => !HasMethodDeclaration(method));
        var withoutDeclarationId = methods.Where(method =>
            string.IsNullOrEmpty(DocumentationCommentId.CreateDeclarationId(method.OriginalDefinition))
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(methods.Length).IsEqualTo(_exoticTestNames.Length);
            _ = await Assert.That(Describe(withoutDeclaration.Select(method => method.Name))).IsEqualTo(string.Empty);
            _ = await Assert.That(Describe(withoutDeclarationId.Select(method => method.Name))).IsEqualTo(string.Empty);
        }
    }

    private static bool HasMethodDeclaration(IMethodSymbol method) =>
        method
            .DeclaringSyntaxReferences.Select(reference => reference.GetSyntax(CancellationToken.None))
            .OfType<MethodDeclarationSyntax>()
            .Any();

    [Test]
    public async Task Analyzer_ManifestMatchingTheCollectedSurface_ReportsNoManifestProblem()
    {
        var test = CreateTest();

        var diagnostics = await RunAsync(test, DiagnosticIds.InvalidTestSurfaceManifest, CreateManifest(test))
            .ConfigureAwait(false);

        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    public async Task Analyzer_ManifestWithAnIdTooMany_ReportsOneRemovedId()
    {
        var test = CreateTest();
        var manifest = WithGhostReference(CreateManifest(test));

        var diagnostics = await RunAsync(test, DiagnosticIds.InvalidTestSurfaceManifest, manifest)
            .ConfigureAwait(false);

        _ = await Assert
            .That(DiagnosticAssertions.Describe(diagnostics))
            .IsEqualTo(DescribeStale(added: 0, removed: 1));
    }

    [Test]
    public async Task Analyzer_ManifestWithAnIdMissing_ReportsOneAddedId()
    {
        var test = CreateTest();
        var manifest = WithoutFirstReference(CreateManifest(test));

        var diagnostics = await RunAsync(test, DiagnosticIds.InvalidTestSurfaceManifest, manifest)
            .ConfigureAwait(false);

        _ = await Assert
            .That(DiagnosticAssertions.Describe(diagnostics))
            .IsEqualTo(DescribeStale(added: 1, removed: 0));
    }

    [Test]
    public async Task Analyzer_ManifestWithAnIdMissingAndOneTooMany_ReportsBothCounts()
    {
        var test = CreateTest();
        var manifest = WithGhostReference(WithoutFirstReference(CreateManifest(test)));

        var diagnostics = await RunAsync(test, DiagnosticIds.InvalidTestSurfaceManifest, manifest)
            .ConfigureAwait(false);

        _ = await Assert
            .That(DiagnosticAssertions.Describe(diagnostics))
            .IsEqualTo(DescribeStale(added: 1, removed: 1));
    }

    /// <summary>
    /// The comparison is on id sets, never on text, so a manifest that a merge or a developer reordered,
    /// commented or padded still describes the same recorded surface.
    /// </summary>
    [Test]
    public async Task Analyzer_ManifestDifferingOnlyInFormatting_ReportsNoManifestProblem()
    {
        var test = CreateTest();
        var manifest = CreateManifest(test);
        var reformatted = Reformat(manifest);

        var diagnostics = await RunAsync(test, DiagnosticIds.InvalidTestSurfaceManifest, reformatted)
            .ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reformatted).IsNotEqualTo(manifest);
            _ = await Assert
                .That(DiagnosticAssertions.Describe(diagnostics))
                .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        }
    }

    [Test]
    public async Task Analyzer_Disabled_ReportsNothing()
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["build_property.FrameShiftEnabled"] = "false",
        };

        var diagnostics = await RunAllAsync(CreateTest(), MalformedManifest, options).ConfigureAwait(false);

        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    /// <summary>
    /// <c>FrameShiftTestAnalyzers</c> naming a framework other than TUnit must silence this analyzer
    /// completely, exactly like <see cref="Analyzer_Disabled_ReportsNothing" /> - even though the
    /// compilation genuinely carries TUnit tests and an additional file that would otherwise be reported
    /// as a malformed manifest.
    /// </summary>
    [Test]
    public async Task Analyzer_TestAnalyzersNamesADifferentFramework_ReportsNothing()
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["build_property.FrameShiftTestAnalyzers"] = "XunitV2",
        };

        var diagnostics = await RunAllAsync(CreateTest(), MalformedManifest, options).ConfigureAwait(false);

        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    /// <summary>
    /// <c>FrameShiftTestAnalyzers</c> naming TUnit itself - alongside frameworks that are not in play - must
    /// leave the analyzer exactly as active as it would be without the property at all.
    /// </summary>
    [Test]
    public async Task Analyzer_TestAnalyzersNamesItsOwnFramework_StillReports()
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["build_property.FrameShiftTestAnalyzers"] = "NUnit;TUnit;MSTest",
        };

        var diagnostics = await RunAllAsync(CreateTest(), MalformedManifest, options).ConfigureAwait(false);

        _ = await Assert
            .That(
                DiagnosticAssertions
                    .Ids(diagnostics)
                    .Contains(DiagnosticIds.InvalidTestSurfaceManifest, StringComparer.Ordinal)
            )
            .IsTrue();
    }

    /// <summary>
    /// The analyzer must be down whenever it recognises no test of its own framework, and being down has
    /// to mean absolute silence: not a single diagnostic, not even the manifest complaint that the very
    /// same additional file provokes on a compilation whose tests it does recognise. Judging a
    /// compilation whose tests are invisible could only ever produce false findings.
    /// </summary>
    /// <param name="scenario">The name of the compilation shape under test.</param>
    [Test]
    [Arguments(NoTestsScenario)]
    [Arguments(ForeignAttributeScenario)]
    [Arguments(FrameworkLikeAssemblyScenario)]
    public async Task Analyzer_NoTestOfItsFrameworkIsRecognised_ReportsNothing(string scenario)
    {
        var compilation = CreateCompilationWithoutRecognisableTests(scenario);

        var diagnostics = await RunAllAsync(compilation, MalformedManifest).ConfigureAwait(false);

        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    /// <summary>
    /// Guards the fixtures of <see cref="Analyzer_NoTestOfItsFrameworkIsRecognised_ReportsNothing(string)" />:
    /// each of them must compile, so that the silence of the analyzer is caused by the absence of
    /// recognisable tests rather than by a broken compilation.
    /// </summary>
    /// <param name="scenario">The name of the compilation shape under test.</param>
    [Test]
    [Arguments(NoTestsScenario)]
    [Arguments(ForeignAttributeScenario)]
    [Arguments(FrameworkLikeAssemblyScenario)]
    public async Task Fixtures_WithoutRecognisableTests_CompileWithoutErrors(string scenario)
    {
        var compilation = CreateCompilationWithoutRecognisableTests(scenario);

        _ = await Assert
            .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    private static CSharpCompilation CreateCompilationWithoutRecognisableTests(string scenario) =>
        scenario switch
        {
            NoTestsScenario => CompilationFactory.Create(WithoutTestsSource, TestAssemblyName, includeTUnit: true),
            ForeignAttributeScenario => CompilationFactory.Create(ForeignAttributeSource, TestAssemblyName),
            FrameworkLikeAssemblyScenario => CompilationFactory.Create(
                WithoutTestsSource,
                TestAssemblyName,
                additionalReferences:
                [
                    CompilationFactory
                        .Create(FrameworkLikeSatelliteSource, FrameworkLikeAssemblyName, filePath: "Satellite.cs")
                        .ToMetadataReference(),
                ]
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario."),
        };

    /// <summary>
    /// Runs every manifest shape the other tests use through the analyzer once more and proves that none
    /// of them makes it throw, which Roslyn would otherwise hide behind an <c>AD0001</c> diagnostic.
    /// </summary>
    [Test]
    public async Task Analyzer_EveryManifestShape_NeverCrashes()
    {
        var test = CreateTest();
        var reported = new List<string>();

        foreach (var shape in GetManifestShapes(CreateManifest(test)))
        {
            reported.AddRange(DiagnosticAssertions.Ids(await RunAllAsync(test, shape).ConfigureAwait(false)));
        }

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(reported.Contains(AnalyzerRunner.AnalyzerFailureId, StringComparer.Ordinal))
                .IsFalse();
            _ = await Assert
                .That(reported.Contains(DiagnosticIds.TestWithoutProductionReference, StringComparer.Ordinal))
                .IsTrue();
        }
    }

    private static IEnumerable<string?> GetManifestShapes(string manifest) =>
        [
            null,
            manifest,
            MalformedManifest,
            Reformat(manifest),
            WithGhostReference(manifest),
            WithoutFirstReference(manifest),
        ];

    [Test]
    public async Task Initialize_ContextIsNull_ThrowsArgumentNullException()
    {
        var analyzer = new TUnitTestSurfaceAnalyzer();

        var exception = Assert.Throws<ArgumentNullException>(() => analyzer.Initialize(null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("context");
    }

    /// <summary>
    /// A manifest whose content cannot be read at all is reported like an unparseable one. Staying silent
    /// would let the production side keep trusting a file nobody was able to look at.
    /// </summary>
    [Test]
    public async Task Analyzer_ManifestWithoutReadableContent_ReportsTheUnreadableFile()
    {
        var diagnostics = await RunWithFilesAsync(
                CreateTest(),
                DiagnosticIds.InvalidTestSurfaceManifest,
                InMemoryAdditionalText.WithoutContent()
            )
            .ConfigureAwait(false);

        _ = await Assert
            .That(DiagnosticAssertions.Describe(diagnostics))
            .IsEqualTo(DescribeManifestProblem("the content of the file is not available."));
    }

    /// <summary>
    /// Additional files are shared by everything the build hands to the analyzers, so a file that is not
    /// a manifest has to be walked past rather than mistaken for one.
    /// </summary>
    [Test]
    public async Task Analyzer_AdditionalFileThatIsNotAManifest_ReportsNoManifestProblem()
    {
        var diagnostics = await RunWithFilesAsync(
                CreateTest(),
                DiagnosticIds.InvalidTestSurfaceManifest,
                new InMemoryAdditionalText(UnrelatedAdditionalFilePath, MalformedManifest)
            )
            .ConfigureAwait(false);

        _ = await Assert.That(DiagnosticAssertions.Describe(diagnostics)).IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    public async Task Analyzer_ManifestBehindAnUnrelatedAdditionalFile_IsStillFound()
    {
        var diagnostics = await RunWithFilesAsync(
                CreateTest(),
                DiagnosticIds.InvalidTestSurfaceManifest,
                new InMemoryAdditionalText(UnrelatedAdditionalFilePath, MalformedManifest),
                new InMemoryAdditionalText(MalformedManifest)
            )
            .ConfigureAwait(false);

        _ = await Assert
            .That(DiagnosticAssertions.Describe(diagnostics))
            .IsEqualTo(DescribeManifestProblem(MalformedHeaderDetail));
    }

    /// <summary>
    /// Pins the side of the leadership decision none of the registered frameworks can reach: a probe that
    /// is not in <see cref="TestFrameworkProbeRegistry.All" /> at all leads the manifest comparison by
    /// itself, because nothing else would ever look at the manifest on its behalf.
    /// </summary>
    /// <remarks>
    /// The fixture references no test framework whatsoever, so no registered probe is awake and the list
    /// of awake frameworks is empty. Skipping the manifest here — the behaviour of every framework that is
    /// awake but does not lead — would leave a broken manifest completely unreported.
    /// </remarks>
    [Test]
    public async Task Analyzer_ProbeIsNotRegistered_LeadsTheManifestComparisonItself()
    {
        var compilation = CompilationFactory.Create(UnregisteredFrameworkSource, TestAssemblyName);

        var diagnostics = await AnalyzerRunner
            .RunAsync(
                new UnregisteredFrameworkAnalyzer(),
                compilation,
                DiagnosticIds.InvalidTestSurfaceManifest,
                AdditionalFiles(MalformedManifest)
            )
            .ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation)))
                .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
            _ = await Assert
                .That(DiagnosticAssertions.Describe(diagnostics))
                .IsEqualTo(DescribeManifestProblem(MalformedHeaderDetail));
        }
    }

    /// <summary>
    /// The counterpart of the manifest expectation above: the shared analysis reports the tests of the
    /// unregistered framework too, so the framework really is fully awake and not merely leading.
    /// </summary>
    [Test]
    public async Task Analyzer_ProbeIsNotRegistered_StillReportsItsTestsWithoutProductionReference()
    {
        var compilation = CompilationFactory.Create(UnregisteredFrameworkSource, TestAssemblyName);

        var diagnostics = await AnalyzerRunner
            .RunAsync(new UnregisteredFrameworkAnalyzer(), compilation, DiagnosticIds.TestWithoutProductionReference)
            .ConfigureAwait(false);

        using (Assert.Multiple())
        {
            _ = await Assert.That(diagnostics).Count().IsEqualTo(1);
            _ = await Assert.That(GetMessage(diagnostics[0]).Contains("LocalOnly", StringComparison.Ordinal)).IsTrue();
        }
    }

    private static Task<ImmutableArray<Diagnostic>> RunWithFilesAsync(
        Compilation compilation,
        string diagnosticId,
        params AdditionalText[] additionalFiles
    ) => AnalyzerRunner.RunAsync(new TUnitTestSurfaceAnalyzer(), compilation, diagnosticId, additionalFiles);

    private static CSharpCompilation CreateProduction() =>
        CompilationFactory.Create(ProductionSource, ProductionAssemblyName, filePath: ProductionPath);

    private static CSharpCompilation CreateTest() => CreateTest(CreateProduction());

    private static CSharpCompilation CreateTest(Compilation production) =>
        CompilationFactory.Create(
            TestSource,
            TestAssemblyName,
            includeTUnit: true,
            additionalReferences: [production.ToMetadataReference()],
            filePath: TestPath
        );

    private static CSharpCompilation CreatePartialTest() =>
        CompilationFactory.Create(
            PartialTestSource,
            TestAssemblyName,
            includeTUnit: true,
            additionalReferences: [CreateProduction().ToMetadataReference()],
            filePath: PartialTestPath
        );

    private static CSharpCompilation CreateExoticShapeTest() =>
        CompilationFactory.Create(
            ExoticShapeSource,
            TestAssemblyName,
            includeTUnit: true,
            additionalReferences: [CreateProduction().ToMetadataReference()],
            filePath: ExoticShapePath
        );

    private static string CreateManifest(Compilation test)
    {
        var recognizer = TUnitTestFrameworkProbe.Instance.TryCreateRecognizer(test)!;

        return TestSurfaceManifestWriter.Write(TestSurfaceCollector.Collect(test, recognizer, CancellationToken.None));
    }

    private static MethodDeclarationSyntax FindMethod(Compilation compilation, string name) =>
        SyntaxNodeLocator.FindFirst<MethodDeclarationSyntax>(
            compilation.SyntaxTrees.First(),
            method => string.Equals(method.Identifier.ValueText, name, StringComparison.Ordinal)
        );

    private static Task<ImmutableArray<Diagnostic>> RunAsync(
        Compilation compilation,
        string diagnosticId,
        string? manifest = null
    ) => AnalyzerRunner.RunAsync(new TUnitTestSurfaceAnalyzer(), compilation, diagnosticId, AdditionalFiles(manifest));

    private static Task<ImmutableArray<Diagnostic>> RunAllAsync(
        Compilation compilation,
        string? manifest = null,
        IReadOnlyDictionary<string, string>? globalOptions = null
    ) => AnalyzerRunner.RunAsync(new TUnitTestSurfaceAnalyzer(), compilation, AdditionalFiles(manifest), globalOptions);

    private static ImmutableArray<AdditionalText> AdditionalFiles(string? manifest) =>
        manifest is null ? [] : [new InMemoryAdditionalText(manifest)];

    private static string GetMessage(Diagnostic diagnostic) => diagnostic.GetMessage(CultureInfo.InvariantCulture);

    private static int CountMentions(ImmutableArray<string> messages, string name) =>
        messages.Count(message => message.Contains(name, StringComparison.Ordinal));

    private static string Describe(IEnumerable<string> values) => DescribeValues(values);

    private static string Describe(IEnumerable<int> values) =>
        DescribeValues(values.Select(value => value.ToString(CultureInfo.InvariantCulture)));

    private static string Describe(IEnumerable<TextSpan> spans) =>
        DescribeValues(spans.Select(span => span.ToString()));

    /// <summary>
    /// Renders a set of values as one comparable, order-independent string, so that an assertion can
    /// state the exact expected set instead of a count.
    /// </summary>
    /// <param name="values">The values to render.</param>
    /// <returns>The rendered set.</returns>
    private static string DescribeValues(IEnumerable<string> values) =>
        string.Join(", ", values.OrderBy(value => value, StringComparer.Ordinal));

    private static string WithGhostReference(string manifest) => manifest + ReferencePrefix + GhostReferenceId + "\n";

    private static string WithoutFirstReference(string manifest)
    {
        var lines = SplitLines(manifest);
        var dropped = lines.First(line => line.StartsWith(ReferencePrefix, StringComparison.Ordinal));

        return Join(lines.Where(line => !string.Equals(line, dropped, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Rewrites a manifest so that its text differs as much as the format allows while its id sets stay
    /// exactly the same: the entries are reversed and comment and blank lines are interleaved.
    /// </summary>
    /// <param name="manifest">The canonical manifest text.</param>
    /// <returns>The reformatted manifest.</returns>
    private static string Reformat(string manifest)
    {
        var lines = SplitLines(manifest);
        var builder = new StringBuilder();

        _ = builder.Append("# a comment in front of the header\n\n").Append(lines[0]).Append("\n\n");

        foreach (var entry in lines.Skip(1).Reverse())
        {
            _ = builder.Append("# an entry follows\n").Append(entry).Append("\n\n");
        }

        return builder.ToString();
    }

    private static ImmutableArray<string> SplitLines(string manifest) =>
        [.. manifest.Split('\n').Where(line => line.Length > 0)];

    private static string Join(IEnumerable<string> lines) => string.Join("\n", lines) + "\n";

    private static string DescribeStale(int added, int removed) =>
        DescribeManifestProblem(
            "the recorded test surface no longer matches the tests of this project, so the manifest is "
                + "stale and must be regenerated ("
                + added.ToString(CultureInfo.InvariantCulture)
                + " id(s) added, "
                + removed.ToString(CultureInfo.InvariantCulture)
                + " id(s) removed)."
        );

    private static string DescribeManifestProblem(string detail) =>
        DiagnosticIds.InvalidTestSurfaceManifest
        + " "
        + InMemoryAdditionalText.DefaultPath
        + "(1,1): Test-surface manifest '"
        + InMemoryAdditionalText.DefaultPath
        + "' could not be read: "
        + detail;

    /// <summary>
    /// An analyzer for a test framework that is deliberately absent from
    /// <see cref="TestFrameworkProbeRegistry.All" />, so that the shared analysis can be driven with a
    /// probe nothing else knows about.
    /// </summary>
    [SuppressMessage(
        "MicrosoftCodeAnalysisCorrectness",
        "RS1001:Missing diagnostic analyzer attribute",
        Justification = "A test-only analyzer that is never shipped; the attribute would declare this test assembly a compiler extension."
    )]
    private sealed class UnregisteredFrameworkAnalyzer : DiagnosticAnalyzer
    {
        private static readonly ImmutableArray<DiagnosticDescriptor> _supportedDiagnostics =
        [
            Descriptors.InvalidTestSurfaceManifest,
            Descriptors.TestWithoutProductionReference,
        ];

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => _supportedDiagnostics;

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationAction(compilationContext =>
                TestSurfaceAnalysis.Execute(compilationContext, new UnregisteredFrameworkProbe())
            );
        }
    }

    /// <summary>
    /// Recognises the framework by the attribute type the fixture declares itself.
    /// </summary>
    private sealed class UnregisteredFrameworkProbe : ITestFrameworkProbe
    {
        public string FrameworkName => UnregisteredFrameworkName;

        public string ConfigurationToken => UnregisteredFrameworkName;

        public ITestMethodRecognizer? TryCreateRecognizer(Compilation compilation) =>
            compilation.GetTypeByMetadataName(CaseAttributeMetadataName) is null ? null : new CaseAttributeRecognizer();
    }

    /// <summary>
    /// Treats every method carrying the fixture's own attribute as a test method.
    /// </summary>
    private sealed class CaseAttributeRecognizer : ITestMethodRecognizer
    {
        public string FrameworkName => UnregisteredFrameworkName;

        public bool IsTestMethod(IMethodSymbol method) =>
            method.GetAttributes().Any(attribute => IsCaseAttribute(attribute.AttributeClass));

        /// <summary>
        /// Counts every recognised method as exactly one case, which is what the fixture's attribute
        /// expresses: it carries no data of its own, so the inputs are hardcoded in the body.
        /// </summary>
        /// <param name="method">The counted method.</param>
        /// <returns>An exact count of one.</returns>
        public TestCaseCount GetTestCaseCount(IMethodSymbol method) => TestCaseCount.Exact(1);

        private static bool IsCaseAttribute(INamedTypeSymbol? attributeClass) =>
            attributeClass is not null
            && string.Equals(attributeClass.Name, CaseAttributeName, StringComparison.Ordinal);
    }
}
