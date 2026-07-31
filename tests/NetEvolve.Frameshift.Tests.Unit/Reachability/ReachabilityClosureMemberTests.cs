namespace NetEvolve.Frameshift.Tests.Unit;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.Frameshift.Reachability;
using NetEvolve.Frameshift.Tests.Infrastructure;
using NetEvolve.Frameshift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the members whose executable code does not live in a method body: properties, indexers,
/// events, field initializers and constructor initializers. A member that is only reachable through
/// one of them must end up in the closure, because otherwise the production side analyzer reports a
/// gap for code that a test does execute.
/// </summary>
public class ReachabilityClosureMemberTests
{
    private const string PropertySource = """
        namespace Production;

        public sealed class Properties
        {
            private int _value;

            public int Expression => Compute();

            public int Block
            {
                get { return Read(); }
                set { Write(value); }
            }

            public int Initialized { get; } = Seed();

            public int Untouched => Unused();

            private int Compute() => 1;

            private int Read() => _value;

            private void Write(int value) => _value = value;

            private static int Seed() => 3;

            private int Unused() => 4;
        }
        """;

    private const string IndexerSource = """
        namespace Production;

        public sealed class Indexers
        {
            private readonly int[] _items = new int[4];

            public int this[int index] => Lookup(index);

            public int this[string key]
            {
                get { return Find(key); }
            }

            public int this[long index]
            {
                get { return Read(index); }
                set { Store(index, value); }
            }

            private int Lookup(int index) => _items[index];

            private int Find(string key) => key.Length;

            private int Read(long index) => _items[(int)index];

            private void Store(long index, int value) => _items[(int)index] = value;
        }
        """;

    private const string EventSource = """
        namespace Production;

        public sealed class Events
        {
            private System.EventHandler? _handler;

            public event System.EventHandler Changed
            {
                add { Attach(value); }
                remove { Detach(value); }
            }

            private void Attach(System.EventHandler handler) => _handler += handler;

            private void Detach(System.EventHandler handler) => _handler -= handler;
        }
        """;

    private const string StaticSource = """
        namespace Production;

        public static class Statics
        {
            public static readonly int Seeded = MakeSeed();

            public static int Configured;

            static Statics() => Configure();

            private static int MakeSeed() => 5;

            private static void Configure() => Configured = 6;
        }
        """;

    private const string ConstructorSource = """
        namespace Production;

        public class Origin
        {
            protected Origin(int value) => Store(value);

            public int Value { get; private set; }

            private void Store(int value) => Value = value;
        }

        public sealed class Derived : Origin
        {
            public Derived()
                : this(1) { }

            public Derived(int value)
                : base(Adjust(value)) { }

            private static int Adjust(int value) => value;
        }
        """;

    private const string ClosureSource = """
        namespace Production;

        public sealed class PropertyClosures
        {
            public int WithLambda
            {
                get
                {
                    System.Func<int> factory = () => FromLambda();

                    return factory();
                }
            }

            public int WithLocalFunction
            {
                get
                {
                    return Inner();

                    int Inner() => FromLocalFunction();
                }
            }

            public int this[int index]
            {
                get
                {
                    return Indexed(index);

                    int Indexed(int value) => FromIndexer(value);
                }
            }

            private int FromLambda() => 1;

            private int FromLocalFunction() => 2;

            private int FromIndexer(int index) => index;
        }
        """;

    [Test]
    [Arguments(PropertySource)]
    [Arguments(IndexerSource)]
    [Arguments(EventSource)]
    [Arguments(StaticSource)]
    [Arguments(ConstructorSource)]
    [Arguments(ClosureSource)]
    public async Task Fixtures_EveryCompilation_CompilesWithoutErrors(string source)
    {
        var compilation = CompilationFactory.Create(source);

        _ = await Assert
            .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    public async Task Compute_ExpressionBodiedProperty_ReachesTheHelperItCalls()
    {
        var compilation = CompilationFactory.Create(PropertySource);
        var manifest = Manifest(Property(compilation, "Production.Properties", "Expression"));

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Properties", "Compute"))).IsTrue();
    }

    [Test]
    public async Task Compute_PropertyWithBlockAccessors_ReachesTheHelperOfEachAccessor()
    {
        var compilation = CompilationFactory.Create(PropertySource);
        var manifest = Manifest(Property(compilation, "Production.Properties", "Block"));

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Properties", "Read"))).IsTrue();
        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Properties", "Write"))).IsTrue();
    }

    [Test]
    public async Task Compute_PropertyInitializer_ReachesTheHelperItCalls()
    {
        var compilation = CompilationFactory.Create(PropertySource);
        var manifest = Manifest(Property(compilation, "Production.Properties", "Initialized"));

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Properties", "Seed"))).IsTrue();
    }

    [Test]
    public async Task Compute_UnseededProperty_KeepsItsHelperOutOfTheClosure()
    {
        var compilation = CompilationFactory.Create(PropertySource);
        var manifest = Manifest(Property(compilation, "Production.Properties", "Expression"));

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        _ = await Assert
            .That(reachable.Contains(Property(compilation, "Production.Properties", "Untouched")))
            .IsFalse();
        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Properties", "Unused"))).IsFalse();
        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Properties", "Seed"))).IsFalse();
        _ = await Assert.That(reachable.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Compute_ExpressionBodiedIndexer_ReachesTheHelperItCalls()
    {
        var compilation = CompilationFactory.Create(IndexerSource);
        var manifest = Manifest(Indexer(compilation, "Production.Indexers", "Int32"));

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Indexers", "Lookup"))).IsTrue();
    }

    [Test]
    public async Task Compute_IndexerWithoutSetter_ReachesTheHelperOfItsGetter()
    {
        var compilation = CompilationFactory.Create(IndexerSource);
        var manifest = Manifest(Indexer(compilation, "Production.Indexers", "String"));

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Indexers", "Find"))).IsTrue();
        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Indexers", "Store"))).IsFalse();
    }

    [Test]
    public async Task Compute_IndexerWithSetter_ReachesTheHelperOfBothAccessors()
    {
        var compilation = CompilationFactory.Create(IndexerSource);
        var manifest = Manifest(Indexer(compilation, "Production.Indexers", "Int64"));

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Indexers", "Read"))).IsTrue();
        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Indexers", "Store"))).IsTrue();
    }

    [Test]
    public async Task Compute_ExplicitEventAccessors_ReachTheirHelpers()
    {
        var compilation = CompilationFactory.Create(EventSource);
        var manifest = Manifest(Event(compilation, "Production.Events", "Changed"));

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Events", "Attach"))).IsTrue();
        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Events", "Detach"))).IsTrue();
    }

    [Test]
    public async Task Compute_StaticFieldInitializer_ReachesTheHelperItCalls()
    {
        var compilation = CompilationFactory.Create(StaticSource);
        var manifest = Manifest(Field(compilation, "Production.Statics", "Seeded"));

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Statics", "MakeSeed"))).IsTrue();
        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Statics", "Configure"))).IsFalse();
    }

    [Test]
    public async Task Compute_StaticConstructor_ReachesTheHelperItCalls()
    {
        var compilation = CompilationFactory.Create(StaticSource);
        var manifest = Manifest(StaticConstructor(compilation, "Production.Statics"));

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Statics", "Configure"))).IsTrue();
        _ = await Assert.That(reachable.Contains(Field(compilation, "Production.Statics", "Configured"))).IsTrue();
    }

    [Test]
    public async Task Compute_ThisConstructorInitializer_ReachesTheChainedConstructor()
    {
        var compilation = CompilationFactory.Create(ConstructorSource);
        var manifest = Manifest(Constructor(compilation, "Production.Derived", 0));

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        _ = await Assert.That(reachable.Contains(Constructor(compilation, "Production.Derived", 1))).IsTrue();
        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Derived", "Adjust"))).IsTrue();
    }

    [Test]
    public async Task Compute_BaseConstructorInitializer_ReachesTheBaseConstructorAndItsCallees()
    {
        var compilation = CompilationFactory.Create(ConstructorSource);
        var manifest = Manifest(Constructor(compilation, "Production.Derived", 1));

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        _ = await Assert.That(reachable.Contains(Constructor(compilation, "Production.Origin", 1))).IsTrue();
        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Origin", "Store"))).IsTrue();
        _ = await Assert.That(reachable.Contains(Constructor(compilation, "Production.Derived", 0))).IsFalse();
    }

    [Test]
    public async Task Compute_LambdaInsideAPropertyBody_ReachesTheHelperItCalls()
    {
        var compilation = CompilationFactory.Create(ClosureSource);
        var manifest = Manifest(Property(compilation, "Production.PropertyClosures", "WithLambda"));

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        _ = await Assert
            .That(reachable.Contains(Method(compilation, "Production.PropertyClosures", "FromLambda")))
            .IsTrue();
    }

    [Test]
    public async Task Compute_LocalFunctionInsideAPropertyBody_ReachesTheHelperItCalls()
    {
        var compilation = CompilationFactory.Create(ClosureSource);
        var manifest = Manifest(Property(compilation, "Production.PropertyClosures", "WithLocalFunction"));

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        _ = await Assert
            .That(reachable.Contains(Method(compilation, "Production.PropertyClosures", "FromLocalFunction")))
            .IsTrue();
    }

    [Test]
    public async Task Compute_LocalFunctionInsideAnIndexerBody_ReachesTheHelperItCalls()
    {
        var compilation = CompilationFactory.Create(ClosureSource);
        var manifest = Manifest(Indexer(compilation, "Production.PropertyClosures", "Int32"));

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        _ = await Assert
            .That(reachable.Contains(Method(compilation, "Production.PropertyClosures", "FromIndexer")))
            .IsTrue();
    }

    [Test]
    public async Task ContainsEnclosing_LocalFunctionInAPropertyAccessor_IsAttributedToTheProperty()
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(ClosureSource);
        var localFunction = LocalFunction(tree, "Inner");
        var localFunctionSymbol = semanticModel.GetDeclaredSymbol(localFunction)!;
        var property = Property(compilation, "Production.PropertyClosures", "WithLocalFunction");
        var reachable = new ReachableSymbolSet([property]);

        _ = await Assert.That(reachable.Contains(localFunctionSymbol)).IsFalse();
        _ = await Assert.That(reachable.ContainsEnclosing(localFunctionSymbol)).IsTrue();
    }

    [Test]
    public async Task ContainsEnclosing_LocalFunctionInAnIndexerAccessor_IsAttributedToTheIndexer()
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(ClosureSource);
        var localFunction = LocalFunction(tree, "Indexed");
        var localFunctionSymbol = semanticModel.GetDeclaredSymbol(localFunction)!;
        var indexer = Indexer(compilation, "Production.PropertyClosures", "Int32");
        var reachable = new ReachableSymbolSet([indexer]);

        _ = await Assert.That(reachable.Contains(localFunctionSymbol)).IsFalse();
        _ = await Assert.That(reachable.ContainsEnclosing(localFunctionSymbol)).IsTrue();
    }

    [Test]
    public async Task ContainsEnclosing_LambdaInAPropertyAccessor_IsAttributedToTheProperty()
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(ClosureSource);
        var lambda = SyntaxNodeLocator.FindFirst<LambdaExpressionSyntax>(tree);
        var lambdaSymbol = semanticModel.GetSymbolInfo(lambda).Symbol!;
        var property = Property(compilation, "Production.PropertyClosures", "WithLambda");
        var reachable = new ReachableSymbolSet([property]);

        _ = await Assert.That(reachable.Contains(lambdaSymbol)).IsFalse();
        _ = await Assert.That(reachable.ContainsEnclosing(lambdaSymbol)).IsTrue();
    }

    [Test]
    public async Task ContainsEnclosing_LocalFunctionOfAnotherProperty_IsNotAttributed()
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(ClosureSource);
        var localFunction = LocalFunction(tree, "Inner");
        var localFunctionSymbol = semanticModel.GetDeclaredSymbol(localFunction)!;
        var property = Property(compilation, "Production.PropertyClosures", "WithLambda");
        var reachable = new ReachableSymbolSet([property]);

        _ = await Assert.That(reachable.ContainsEnclosing(localFunctionSymbol)).IsFalse();
    }

    private static TestSurfaceManifest Manifest(params ISymbol[] seeds)
    {
        string[] referencedMemberIds = [.. seeds.Select(seed => DocumentationCommentId.CreateDeclarationId(seed)!)];

        return new TestSurfaceManifest([], ImmutableHashSet.Create(StringComparer.Ordinal, referencedMemberIds));
    }

    private static LocalFunctionStatementSyntax LocalFunction(SyntaxTree tree, string name) =>
        SyntaxNodeLocator.FindFirst<LocalFunctionStatementSyntax>(
            tree,
            localFunction => string.Equals(localFunction.Identifier.ValueText, name, StringComparison.Ordinal)
        );

    private static IMethodSymbol Method(Compilation compilation, string typeName, string methodName) =>
        compilation.GetTypeByMetadataName(typeName)!.GetMembers(methodName).OfType<IMethodSymbol>().First();

    private static IPropertySymbol Property(Compilation compilation, string typeName, string propertyName) =>
        compilation.GetTypeByMetadataName(typeName)!.GetMembers(propertyName).OfType<IPropertySymbol>().First();

    private static IEventSymbol Event(Compilation compilation, string typeName, string eventName) =>
        compilation.GetTypeByMetadataName(typeName)!.GetMembers(eventName).OfType<IEventSymbol>().First();

    private static IFieldSymbol Field(Compilation compilation, string typeName, string fieldName) =>
        compilation.GetTypeByMetadataName(typeName)!.GetMembers(fieldName).OfType<IFieldSymbol>().First();

    private static IPropertySymbol Indexer(Compilation compilation, string typeName, string parameterTypeName) =>
        compilation
            .GetTypeByMetadataName(typeName)!
            .GetMembers()
            .OfType<IPropertySymbol>()
            .First(property =>
                property.IsIndexer
                && string.Equals(property.Parameters[0].Type.Name, parameterTypeName, StringComparison.Ordinal)
            );

    private static IMethodSymbol Constructor(Compilation compilation, string typeName, int parameterCount) =>
        compilation
            .GetTypeByMetadataName(typeName)!
            .InstanceConstructors.First(constructor => constructor.Parameters.Length == parameterCount);

    private static IMethodSymbol StaticConstructor(Compilation compilation, string typeName) =>
        compilation.GetTypeByMetadataName(typeName)!.StaticConstructors.First();
}
