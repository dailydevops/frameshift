namespace NetEvolve.Frameshift.Tests.Unit;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.Frameshift.Tests.Infrastructure;
using NetEvolve.Frameshift.TestSurface;
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
        """;

    private const string TestSource = """
        namespace Tests;

        using System;
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

        _ = await Assert
            .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(production)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        _ = await Assert
            .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(test)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
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
    /// Auto property accessors have a declaring syntax but no body, and a get-only property has no
    /// setter to walk at all. Neither may throw, and neither may invent a production reference.
    /// </summary>
    [Test]
    public async Task Collect_TestTouchingOnlyAutoProperties_HasNoProductionReference()
    {
        var withoutReference = TestSurfaceCollector.FindTestsWithoutProductionReference(
            CreateTest(CreateProduction()),
            CancellationToken.None
        );

        var names = withoutReference.Select(method => method.Name).ToList();

        _ = await Assert.That(names.Contains("OnlyTouchesAutoProperties", StringComparer.Ordinal)).IsTrue();
        _ = await Assert.That(names.Contains("ReadsBlockBodiedProperty", StringComparer.Ordinal)).IsFalse();
    }

    /// <summary>
    /// The traversal must follow the members a test actually reaches, not every member of the test
    /// assembly. A property nothing calls therefore contributes nothing.
    /// </summary>
    [Test]
    public async Task Collect_PropertyNoTestReaches_DoesNotContributeItsReferences()
    {
        var manifest = TestSurfaceCollector.Collect(CreateTest(CreateProduction()), CancellationToken.None);

        _ = await Assert
            .That(manifest.ReferencedMemberIds.Contains("M:Production.Signals.FromUnreachableProperty~System.Int32"))
            .IsFalse();
    }

    [Test]
    public async Task Collect_RecordedIds_ResolveBackToProductionSymbols()
    {
        var production = CreateProduction();
        var manifest = TestSurfaceCollector.Collect(CreateTest(production), CancellationToken.None);

        var unresolved = manifest
            .ReferencedMemberIds.Where(id => id.Contains("Production.Signals", StringComparison.Ordinal))
            .Where(id => DocumentationCommentId.GetSymbolsForDeclarationId(id, production).IsEmpty);

        _ = await Assert.That(Join(unresolved)).IsEqualTo(string.Empty);
    }

    private static async Task AssertRecorded(string expectedId)
    {
        var manifest = TestSurfaceCollector.Collect(CreateTest(CreateProduction()), CancellationToken.None);

        _ = await Assert.That(manifest.ReferencedMemberIds.Contains(expectedId)).IsTrue();
    }

    private static CSharpCompilation CreateProduction() =>
        CompilationFactory.Create(ProductionSource, ProductionAssemblyName, filePath: "Signals.cs");

    private static CSharpCompilation CreateTest(Compilation production) =>
        CompilationFactory.Create(
            TestSource,
            includeTUnit: true,
            additionalReferences: [production.ToMetadataReference()],
            filePath: "MemberShapeTests.cs"
        );

    private static string Join(IEnumerable<string> values) =>
        string.Join("|", values.OrderBy(value => value, StringComparer.Ordinal));
}
