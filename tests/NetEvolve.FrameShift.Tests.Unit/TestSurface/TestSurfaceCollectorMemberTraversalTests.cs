namespace NetEvolve.FrameShift.Tests.Unit;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Tests.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Pins down which member shapes of a test assembly the collector walks into. A test rarely calls
/// production code straight from its own body: it goes through a property, an indexer, an event
/// accessor, a constructor chain or a local function of the test assembly. Every one of those hops has
/// to be followed, otherwise the production side reports a gap that does not exist.
/// </summary>
/// <remarks>
/// The fixture uses the same two-assembly setup as <see cref="TestSurfaceCollectorTests" />: a
/// production assembly that is only visible as metadata, and a test assembly compiled against it. Each
/// member shape calls its own production signal method, so a single containment assertion proves that
/// exactly that hop was taken. Assertions are containment based on purpose, because several shapes need
/// framework types such as <c>EventHandler</c> whose members are recorded as production references too.
/// </remarks>
public class TestSurfaceCollectorMemberTraversalTests
{
    private const string ProductionAssemblyName = "ProductionAssembly";

    private const string ProductionSource = """
        namespace Production;

        public static class Signals
        {
            public static int FromExpressionProperty() => 1;

            public static int FromBlockProperty() => 2;

            public static int FromPropertySetter() => 3;

            public static int FromPropertyInitializer() => 4;

            public static int FromStaticProperty() => 5;

            public static int FromStaticFieldInitializer() => 6;

            public static int FromExpressionIndexer() => 7;

            public static int FromBlockIndexer() => 8;

            public static int FromIndexerSetter() => 9;

            public static int FromEventAdd() => 10;

            public static int FromEventRemove() => 11;

            public static int FromFieldEventInitializer() => 12;

            public static int FromEventHandler() => 13;

            public static int FromThisInitializer() => 14;

            public static int FromBaseInitializer() => 15;

            public static int FromLocalFunction() => 16;

            public static int FromLambda() => 17;

            public static int FromAnonymousType() => 18;

            public static int FromUnreachableProperty() => 19;
        }

        public static class Extras
        {
            public static int FromReducedExtension(this int value) => 20;
        }
        """;

    private const string InaccessibleProductionAssemblyName = "InaccessibleProductionAssembly";

    private const string TestMethodIdPrefix = "M:Tests.MemberShapeTests.";

    private const string UnreducedExtensionId = "M:Production.Extras.FromReducedExtension(System.Int32)~System.Int32";

    private const string ReducedExtensionId = "M:Production.Extras.FromReducedExtension~System.Int32";

    private const string InaccessibleMemberId = "M:Production.Hidden.FromProtectedMember~System.Int32";

    /// <summary>
    /// A production assembly whose member is protected, so that a call to it from the test assembly does
    /// not bind — CS0122 — while the compiler still offers it as the single candidate of the call.
    /// </summary>
    private const string InaccessibleProductionSource = """
        namespace Production;

        public class Hidden
        {
            protected static int FromProtectedMember() => 21;
        }
        """;

    private const string InaccessibleTestSource = """
        namespace Tests;

        using TUnit.Core;

        public class HiddenAccessTests
        {
            [Test]
            public void CallsProtectedMember() => _ = Production.Hidden.FromProtectedMember();
        }
        """;

    private const string TestSource = """
        namespace Tests;

        using System;
        using Production;
        using TUnit.Core;

        public class MemberShapeTests
        {
            private static readonly int StaticField = Production.Signals.FromStaticFieldInitializer();

            private event EventHandler? FieldEventWithInitializer = (sender, args) =>
                Production.Signals.FromFieldEventInitializer();

            private event EventHandler? FieldEventWithHandler;

            private event EventHandler CustomEvent
            {
                add
                {
                    Production.Signals.FromEventAdd();
                }

                remove
                {
                    Production.Signals.FromEventRemove();
                }
            }

            private int ExpressionProperty => Production.Signals.FromExpressionProperty();

            private int BlockProperty
            {
                get
                {
                    return Production.Signals.FromBlockProperty();
                }
            }

            private int SetOnlyProperty
            {
                set
                {
                    Production.Signals.FromPropertySetter();
                }
            }

            private int InitializedProperty { get; } = Production.Signals.FromPropertyInitializer();

            private static int StaticProperty => Production.Signals.FromStaticProperty();

            private int UnreachableProperty => Production.Signals.FromUnreachableProperty();

            private int AutoProperty { get; set; }

            private int ReadOnlyAutoProperty { get; }

            private int this[int index] => Production.Signals.FromExpressionIndexer();

            private int this[string index]
            {
                get
                {
                    return Production.Signals.FromBlockIndexer();
                }
            }

            private int this[long index]
            {
                set
                {
                    Production.Signals.FromIndexerSetter();
                }
            }

            [Test]
            public void ReadsExpressionBodiedProperty() => _ = ExpressionProperty;

            [Test]
            public void ReadsBlockBodiedProperty() => _ = BlockProperty;

            [Test]
            public void WritesSetOnlyProperty() => SetOnlyProperty = 1;

            [Test]
            public void ReadsPropertyWithInitializer() => _ = InitializedProperty;

            [Test]
            public void ReadsStaticProperty() => _ = StaticProperty;

            [Test]
            public void ReadsStaticField() => _ = StaticField;

            [Test]
            public void ReadsExpressionBodiedIndexer() => _ = this[0];

            [Test]
            public void ReadsBlockBodiedIndexer() => _ = this[""];

            [Test]
            public void WritesSetOnlyIndexer() => this[0L] = 1;

            [Test]
            public void SubscribesToCustomEvent() => CustomEvent += OnEvent;

            [Test]
            public void SubscribesToFieldEventWithInitializer() => FieldEventWithInitializer += OnEvent;

            [Test]
            public void SubscribesToFieldEventWithHandler() => FieldEventWithHandler += OnHandledEvent;

            [Test]
            public void CreatesTypeWithThisInitializer() => _ = new ThisChained();

            [Test]
            public void CreatesTypeWithBaseInitializer() => _ = new BaseChained();

            [Test]
            public void CallsLocalFunction()
            {
                int Local() => Production.Signals.FromLocalFunction();

                _ = Local();
            }

            [Test]
            public void CallsLambda()
            {
                Func<int> lambda = () => Production.Signals.FromLambda();

                _ = lambda();
            }

            [Test]
            public void ReadsAnonymousTypeProperty()
            {
                var anonymous = new { Value = Production.Signals.FromAnonymousType() };

                _ = anonymous.Value;
            }

            [Test]
            public void CallsExtensionMethodInReducedForm() => _ = 7.FromReducedExtension();

            [Test]
            public void OnlyTouchesAutoProperties()
            {
                AutoProperty = 1;
                _ = AutoProperty;
                _ = ReadOnlyAutoProperty;
            }

            private void OnEvent(object? sender, EventArgs args)
            {
            }

            private void OnHandledEvent(object? sender, EventArgs args) =>
                Production.Signals.FromEventHandler();
        }

        public class ThisChained
        {
            public ThisChained()
                : this(0)
            {
            }

            public ThisChained(int seed) => Production.Signals.FromThisInitializer();
        }

        public class ChainedBase
        {
            protected ChainedBase(int seed)
            {
            }
        }

        public class BaseChained : ChainedBase
        {
            public BaseChained()
                : base(Production.Signals.FromBaseInitializer())
            {
            }
        }
        """;

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

    [Test]
    public async Task Collect_ExpressionBodiedProperty_RecordsTheMemberItCalls() =>
        await AssertRecorded("M:Production.Signals.FromExpressionProperty~System.Int32").ConfigureAwait(false);

    [Test]
    public async Task Collect_PropertyWithBlockBodiedGetter_RecordsTheMemberItCalls() =>
        await AssertRecorded("M:Production.Signals.FromBlockProperty~System.Int32").ConfigureAwait(false);

    [Test]
    public async Task Collect_PropertySetter_RecordsTheMemberItCalls() =>
        await AssertRecorded("M:Production.Signals.FromPropertySetter~System.Int32").ConfigureAwait(false);

    /// <summary>
    /// The initializer of a property runs whenever the declaring type is created, so a test that reads
    /// the property does exercise the production call inside that initializer.
    /// </summary>
    [Test]
    public async Task Collect_PropertyWithInitializer_RecordsTheMemberTheInitializerCalls() =>
        await AssertRecorded("M:Production.Signals.FromPropertyInitializer~System.Int32").ConfigureAwait(false);

    [Test]
    public async Task Collect_StaticProperty_RecordsTheMemberItCalls() =>
        await AssertRecorded("M:Production.Signals.FromStaticProperty~System.Int32").ConfigureAwait(false);

    /// <summary>
    /// A static readonly field is initialized before the first test touches it, so the production call
    /// inside its initializer belongs to the surface of every test that reads the field.
    /// </summary>
    [Test]
    public async Task Collect_StaticReadOnlyFieldInitializer_RecordsTheMemberItCalls() =>
        await AssertRecorded("M:Production.Signals.FromStaticFieldInitializer~System.Int32").ConfigureAwait(false);

    [Test]
    public async Task Collect_ExpressionBodiedIndexer_RecordsTheMemberItCalls() =>
        await AssertRecorded("M:Production.Signals.FromExpressionIndexer~System.Int32").ConfigureAwait(false);

    [Test]
    public async Task Collect_IndexerWithBlockBodiedGetter_RecordsTheMemberItCalls() =>
        await AssertRecorded("M:Production.Signals.FromBlockIndexer~System.Int32").ConfigureAwait(false);

    [Test]
    public async Task Collect_IndexerSetter_RecordsTheMemberItCalls() =>
        await AssertRecorded("M:Production.Signals.FromIndexerSetter~System.Int32").ConfigureAwait(false);

    [Test]
    [Arguments("M:Production.Signals.FromEventAdd~System.Int32")]
    [Arguments("M:Production.Signals.FromEventRemove~System.Int32")]
    public async Task Collect_EventWithExplicitAccessors_RecordsTheMembersBothAccessorsCall(string expectedId) =>
        await AssertRecorded(expectedId).ConfigureAwait(false);

    [Test]
    public async Task Collect_FieldLikeEventWithInitializer_RecordsTheMemberTheInitializerCalls() =>
        await AssertRecorded("M:Production.Signals.FromFieldEventInitializer~System.Int32").ConfigureAwait(false);

    [Test]
    public async Task Collect_FieldLikeEventHandler_RecordsTheMemberTheHandlerCalls() =>
        await AssertRecorded("M:Production.Signals.FromEventHandler~System.Int32").ConfigureAwait(false);

    [Test]
    public async Task Collect_ConstructorWithThisInitializer_RecordsTheMemberTheChainedConstructorCalls() =>
        await AssertRecorded("M:Production.Signals.FromThisInitializer~System.Int32").ConfigureAwait(false);

    [Test]
    public async Task Collect_ConstructorWithBaseInitializer_RecordsTheMemberTheArgumentCalls() =>
        await AssertRecorded("M:Production.Signals.FromBaseInitializer~System.Int32").ConfigureAwait(false);

    [Test]
    public async Task Collect_LocalFunction_RecordsTheMemberItCalls() =>
        await AssertRecorded("M:Production.Signals.FromLocalFunction~System.Int32").ConfigureAwait(false);

    [Test]
    public async Task Collect_Lambda_RecordsTheMemberItCalls() =>
        await AssertRecorded("M:Production.Signals.FromLambda~System.Int32").ConfigureAwait(false);

    /// <summary>
    /// The accessors of an anonymous type have no declaring syntax at all, which is the case the
    /// traversal guard exists for: the accessor is skipped instead of throwing, while the production
    /// call in the member initializer is still recorded.
    /// </summary>
    [Test]
    public async Task Collect_AccessorWithoutDeclaringSyntax_IsSkippedWithoutThrowing() =>
        await AssertRecorded("M:Production.Signals.FromAnonymousType~System.Int32").ConfigureAwait(false);

    /// <summary>
    /// Pins the reduced side of the symbol normalisation: an extension method called in reduced form is
    /// recorded under the id of the static method it really is, never under the reduced signature.
    /// </summary>
    /// <remarks>
    /// The reduced symbol has no parameter for the receiver, so recording it would produce
    /// <c>M:Production.Extras.FromReducedExtension~System.Int32</c>. That id names no member of the
    /// production assembly, so the production side would resolve nothing and report the whole method as
    /// unreachable.
    /// </remarks>
    [Test]
    public async Task Collect_ExtensionMethodCalledInReducedForm_RecordsTheUnreducedDefinition()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        using (Assert.Multiple())
        {
            _ = await Assert.That(manifest.ReferencedMemberIds.Contains(UnreducedExtensionId)).IsTrue();
            _ = await Assert.That(manifest.ReferencedMemberIds.Contains(ReducedExtensionId)).IsFalse();
        }
    }

    /// <summary>
    /// Pins the candidate side of the symbol lookup: a call that does not bind, but that the compiler can
    /// name exactly one candidate for, still contributes that member to the surface.
    /// </summary>
    /// <remarks>
    /// The collector runs inside an analyzer, so it constantly sees code that does not compile yet. A
    /// call to an inaccessible member is the reproducible shape of that state: dropping it would make the
    /// manifest shrink while the developer is typing, and the production side would report gaps that
    /// disappear again on the next keystroke.
    /// </remarks>
    [Test]
    public async Task Collect_CallDoesNotBindButHasExactlyOneCandidate_RecordsTheCandidate()
    {
        var test = CreateInaccessibleTest();

        var manifest = CollectSurface(test);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(DiagnosticAssertions.Ids(CompilationFactory.GetCompileErrors(test)))
                .Contains("CS0122");
            _ = await Assert.That(manifest.ReferencedMemberIds.Contains(InaccessibleMemberId)).IsTrue();
        }
    }

    /// <summary>
    /// Auto property accessors have a declaring syntax but no body, and a get-only property has no
    /// setter to walk at all. Neither may throw, and neither may invent a production reference.
    /// </summary>
    [Test]
    public async Task Collect_TestTouchingOnlyAutoProperties_HasNoProductionReference()
    {
        var test = CreateTest(CreateProduction());

        var withoutReference = TestSurfaceCollector.FindTestsWithoutProductionReference(
            test,
            CreateRecognizer(test),
            CancellationToken.None
        );

        var names = withoutReference.Select(method => method.Name).ToList();

        using (Assert.Multiple())
        {
            _ = await Assert.That(names.Contains("OnlyTouchesAutoProperties", StringComparer.Ordinal)).IsTrue();
            _ = await Assert.That(names.Contains("ReadsBlockBodiedProperty", StringComparer.Ordinal)).IsFalse();
        }
    }

    /// <summary>
    /// The traversal must follow the members a test actually reaches, not every member of the test
    /// assembly. A property nothing calls therefore contributes nothing.
    /// </summary>
    [Test]
    public async Task Collect_PropertyNoTestReaches_DoesNotContributeItsReferences()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        _ = await Assert
            .That(manifest.ReferencedMemberIds.Contains("M:Production.Signals.FromUnreachableProperty~System.Int32"))
            .IsFalse();
    }

    [Test]
    public async Task Collect_RecordedIds_ResolveBackToProductionSymbols()
    {
        var production = CreateProduction();
        var manifest = CollectSurface(CreateTest(production));

        var unresolved = manifest
            .ReferencedMemberIds.Where(id => id.Contains("Production.Signals", StringComparison.Ordinal))
            .Where(id => DocumentationCommentId.GetSymbolsForDeclarationId(id, production).IsEmpty);

        _ = await Assert.That(Join(unresolved)).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// Every hop the traversal takes has to end up attributed to the test it started from, otherwise the
    /// production side knows the member is reached but not by how many input combinations.
    /// </summary>
    [Test]
    [Arguments("ReadsExpressionBodiedProperty", "M:Production.Signals.FromExpressionProperty~System.Int32")]
    [Arguments("ReadsBlockBodiedProperty", "M:Production.Signals.FromBlockProperty~System.Int32")]
    [Arguments("WritesSetOnlyProperty", "M:Production.Signals.FromPropertySetter~System.Int32")]
    [Arguments("ReadsPropertyWithInitializer", "M:Production.Signals.FromPropertyInitializer~System.Int32")]
    [Arguments("ReadsStaticField", "M:Production.Signals.FromStaticFieldInitializer~System.Int32")]
    [Arguments("ReadsExpressionBodiedIndexer", "M:Production.Signals.FromExpressionIndexer~System.Int32")]
    [Arguments("SubscribesToCustomEvent", "M:Production.Signals.FromEventAdd~System.Int32")]
    [Arguments("SubscribesToCustomEvent", "M:Production.Signals.FromEventRemove~System.Int32")]
    [Arguments("SubscribesToFieldEventWithInitializer", "M:Production.Signals.FromFieldEventInitializer~System.Int32")]
    [Arguments("SubscribesToFieldEventWithHandler", "M:Production.Signals.FromEventHandler~System.Int32")]
    [Arguments("CreatesTypeWithThisInitializer", "M:Production.Signals.FromThisInitializer~System.Int32")]
    [Arguments("CallsLocalFunction", "M:Production.Signals.FromLocalFunction~System.Int32")]
    [Arguments("CallsLambda", "M:Production.Signals.FromLambda~System.Int32")]
    public async Task Collect_MemberReachedThroughAMemberShape_IsAttributedToTheReachingTest(
        string testMethodName,
        string expectedId
    )
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        _ = await Assert.That(ReferencesOf(manifest, testMethodName).Contains(expectedId)).IsTrue();
    }

    /// <summary>
    /// The member shapes stay apart from each other: the signal of one property must not appear under the
    /// test that reads a different property, however similar the two declarations look.
    /// </summary>
    [Test]
    public async Task Collect_MemberReachedByAnotherTest_IsNotAttributed()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        var references = ReferencesOf(manifest, "ReadsExpressionBodiedProperty");

        using (Assert.Multiple())
        {
            _ = await Assert.That(references.Contains("M:Production.Signals.FromBlockProperty~System.Int32")).IsFalse();
            _ = await Assert.That(references.Contains("M:Production.Signals.FromEventAdd~System.Int32")).IsFalse();
        }
    }

    [Test]
    public async Task Collect_TestTouchingOnlyAutoProperties_IsAttributedAnEmptySet()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        _ = await Assert.That(Join(ReferencesOf(manifest, "OnlyTouchesAutoProperties"))).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// Both maps are keyed by the same set of tests, so that the production side can look a test up in
    /// either one without ever missing an entry.
    /// </summary>
    [Test]
    public async Task Collect_BothMaps_AreKeyedByEveryDiscoveredTest()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        using (Assert.Multiple())
        {
            _ = await Assert.That(Join(manifest.ReferencesByTest.Keys)).IsEqualTo(Join(manifest.TestMethodIds));
            _ = await Assert.That(Join(manifest.TestCaseCounts.Keys)).IsEqualTo(Join(manifest.TestMethodIds));
        }
    }

    /// <summary>
    /// Every test of this fixture is a parameterless one, which is exactly one case each.
    /// </summary>
    [Test]
    public async Task Collect_ParameterlessTests_AreCountedAsExactlyOneCase()
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        var counts = manifest.TestCaseCounts.Values.Select(count => count.ToString()).Distinct(StringComparer.Ordinal);

        _ = await Assert.That(Join(counts)).IsEqualTo("1");
    }

    private static async Task AssertRecorded(string expectedId)
    {
        var manifest = CollectSurface(CreateTest(CreateProduction()));

        _ = await Assert.That(manifest.ReferencedMemberIds.Contains(expectedId)).IsTrue();
    }

    /// <summary>
    /// Collects the test surface of <paramref name="test" /> with a TUnit recogniser, which is how every
    /// production caller reaches the collector.
    /// </summary>
    /// <param name="test">The compilation to inspect.</param>
    /// <returns>The collected manifest.</returns>
    private static TestSurfaceManifest CollectSurface(Compilation test) =>
        TestSurfaceCollector.Collect(test, CreateRecognizer(test), CancellationToken.None);

    /// <summary>
    /// Looks the attribution of the test <paramref name="testMethodName" /> of the fixture up.
    /// </summary>
    /// <param name="manifest">The collected manifest.</param>
    /// <param name="testMethodName">The simple name of the test method.</param>
    /// <returns>The production members attributed to that test.</returns>
    private static ImmutableHashSet<string> ReferencesOf(TestSurfaceManifest manifest, string testMethodName) =>
        manifest.ReferencesByTest[TestMethodIdPrefix + testMethodName];

    private static TUnitTestMethodRecognizer CreateRecognizer(Compilation compilation) =>
        new TUnitTestMethodRecognizer(TUnitTestFrameworkProbe.GetTestAttributeType(compilation));

    private static CSharpCompilation CreateProduction() =>
        CompilationFactory.Create(ProductionSource, ProductionAssemblyName, filePath: "Signals.cs");

    private static CSharpCompilation CreateTest(Compilation production) =>
        CompilationFactory.Create(
            TestSource,
            includeTUnit: true,
            additionalReferences: [production.ToMetadataReference()],
            filePath: "MemberShapeTests.cs"
        );

    private static CSharpCompilation CreateInaccessibleTest() =>
        CompilationFactory.Create(
            InaccessibleTestSource,
            includeTUnit: true,
            additionalReferences:
            [
                CompilationFactory
                    .Create(InaccessibleProductionSource, InaccessibleProductionAssemblyName, filePath: "Hidden.cs")
                    .ToMetadataReference(),
            ],
            filePath: "HiddenAccessTests.cs"
        );

    private static string Join(IEnumerable<string> values) =>
        string.Join("|", values.OrderBy(value => value, StringComparer.Ordinal));
}
