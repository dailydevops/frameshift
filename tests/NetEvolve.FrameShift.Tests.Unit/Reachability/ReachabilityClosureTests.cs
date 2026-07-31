namespace NetEvolve.FrameShift.Tests.Unit;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Reachability;
using NetEvolve.FrameShift.Tests.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the expansion of a recorded test surface into the transitive set of production members a
/// test can reach, including the two approximations the closure deliberately makes: virtual and
/// interface dispatch, and the attribution of code inside a lambda or a local function to the member
/// that encloses it.
/// </summary>
public class ReachabilityClosureTests
{
    private const string ChainSource = """
        namespace Production;

        public sealed class Chain
        {
            public void A() => B();

            private void B() => C();

            private void C()
            {
            }

            public void D()
            {
            }
        }
        """;

    private const string CycleSource = """
        namespace Production;

        public sealed class Cycle
        {
            public void First() => Second();

            private void Second() => First();
        }
        """;

    private const string DispatchSource = """
        namespace Production;

        public interface IGreeter
        {
            string Greet();
        }

        public sealed class Greeter : IGreeter
        {
            public string Greet() => "greeting";
        }

        public abstract class ShapeBase
        {
            public abstract int Area();
        }

        public sealed class Square : ShapeBase
        {
            public override int Area() => 4;
        }

        public static class Consumer
        {
            public static string UseInterface(IGreeter greeter) => greeter.Greet();

            public static int UseAbstract(ShapeBase shape) => shape.Area();
        }
        """;

    private const string EnclosingSource = """
        namespace Production;

        public sealed class Enclosing
        {
            public int WithLambda()
            {
                System.Func<int> factory = () => 7;

                return factory();
            }

            public int WithLocalFunction()
            {
                return Inner();

                static int Inner() => 9;
            }
        }
        """;

    [Test]
    [Arguments(ChainSource)]
    [Arguments(CycleSource)]
    [Arguments(DispatchSource)]
    [Arguments(EnclosingSource)]
    public async Task Fixtures_EveryCompilation_CompilesWithoutErrors(string source)
    {
        var compilation = CompilationFactory.Create(source);

        _ = await Assert
            .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    public async Task Compute_CallChain_ContainsTheTransitivelyReachedMembers()
    {
        var compilation = CompilationFactory.Create(ChainSource);

        var reachable = ReachabilityClosure.Compute(
            compilation,
            Manifest("M:Production.Chain.A"),
            CancellationToken.None
        );

        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Chain", "A"))).IsTrue();
        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Chain", "B"))).IsTrue();
        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Chain", "C"))).IsTrue();
    }

    [Test]
    public async Task Compute_CallChain_DoesNotContainTheUntouchedMember()
    {
        var compilation = CompilationFactory.Create(ChainSource);

        var reachable = ReachabilityClosure.Compute(
            compilation,
            Manifest("M:Production.Chain.A"),
            CancellationToken.None
        );

        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Chain", "D"))).IsFalse();
        _ = await Assert.That(reachable.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Compute_UnresolvableIds_AreIgnored()
    {
        var compilation = CompilationFactory.Create(ChainSource);
        var manifest = Manifest("M:Production.Chain.Missing", "T:Production.Absent", "P:Production.Chain.Nope");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        _ = await Assert.That(reachable.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Compute_UnresolvableIdNextToAValidOne_KeepsTheValidSeed()
    {
        var compilation = CompilationFactory.Create(ChainSource);
        var manifest = Manifest("M:Production.Chain.Missing", "M:Production.Chain.A");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Chain", "A"))).IsTrue();
        _ = await Assert.That(reachable.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Compute_EmptyManifest_ReturnsTheEmptySet()
    {
        var compilation = CompilationFactory.Create(ChainSource);

        var reachable = ReachabilityClosure.Compute(compilation, TestSurfaceManifest.Empty, CancellationToken.None);

        _ = await Assert.That(reachable.IsEmpty).IsTrue();
        _ = await Assert.That(reachable.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Compute_CyclicCallGraph_Terminates()
    {
        var compilation = CompilationFactory.Create(CycleSource);

        var reachable = ReachabilityClosure.Compute(
            compilation,
            Manifest("M:Production.Cycle.First"),
            CancellationToken.None
        );

        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Cycle", "First"))).IsTrue();
        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Cycle", "Second"))).IsTrue();
        _ = await Assert.That(reachable.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Compute_InterfaceMember_AlsoReachesItsImplementation()
    {
        var compilation = CompilationFactory.Create(DispatchSource);
        var manifest = Manifest("M:Production.Consumer.UseInterface(Production.IGreeter)");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.IGreeter", "Greet"))).IsTrue();
        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Greeter", "Greet"))).IsTrue();
    }

    [Test]
    public async Task Compute_AbstractMember_AlsoReachesItsOverride()
    {
        var compilation = CompilationFactory.Create(DispatchSource);
        var manifest = Manifest("M:Production.Consumer.UseAbstract(Production.ShapeBase)");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.ShapeBase", "Area"))).IsTrue();
        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Square", "Area"))).IsTrue();
    }

    [Test]
    public async Task Compute_UnrelatedImplementation_StaysUnreachable()
    {
        var compilation = CompilationFactory.Create(DispatchSource);
        var manifest = Manifest("M:Production.Consumer.UseInterface(Production.IGreeter)");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Square", "Area"))).IsFalse();
    }

    [Test]
    public async Task ContainsEnclosing_SymbolInsideALambda_IsAttributedToItsEnclosingMember()
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(EnclosingSource);
        var lambda = SyntaxNodeLocator.FindFirst<LambdaExpressionSyntax>(tree);
        var lambdaSymbol = semanticModel.GetSymbolInfo(lambda).Symbol!;
        var reachable = new ReachableSymbolSet([Method(compilation, "Production.Enclosing", "WithLambda")]);

        _ = await Assert.That(reachable.Contains(lambdaSymbol)).IsFalse();
        _ = await Assert.That(reachable.ContainsEnclosing(lambdaSymbol)).IsTrue();
    }

    [Test]
    public async Task ContainsEnclosing_SymbolInsideALocalFunction_IsAttributedToItsEnclosingMember()
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(EnclosingSource);
        var localFunction = SyntaxNodeLocator.FindFirst<LocalFunctionStatementSyntax>(tree);
        var localFunctionSymbol = semanticModel.GetDeclaredSymbol(localFunction)!;
        var reachable = new ReachableSymbolSet([Method(compilation, "Production.Enclosing", "WithLocalFunction")]);

        _ = await Assert.That(reachable.Contains(localFunctionSymbol)).IsFalse();
        _ = await Assert.That(reachable.ContainsEnclosing(localFunctionSymbol)).IsTrue();
    }

    [Test]
    public async Task ContainsEnclosing_MemberOfAReachableType_StaysUnreachable()
    {
        var compilation = CompilationFactory.Create(EnclosingSource);
        var type = compilation.GetTypeByMetadataName("Production.Enclosing")!;
        var reachable = new ReachableSymbolSet([type]);

        var enclosed = Method(compilation, "Production.Enclosing", "WithLambda");

        _ = await Assert.That(reachable.ContainsEnclosing(enclosed)).IsFalse();
    }

    [Test]
    public async Task ContainsEnclosing_SymbolIsNull_ReturnsFalse()
    {
        var reachable = ReachableSymbolSet.Empty;

        _ = await Assert.That(reachable.ContainsEnclosing(null)).IsFalse();
    }

    [Test]
    public async Task Compute_CancelledToken_Throws()
    {
        using var cancellation = new CancellationTokenSource();
        var compilation = CompilationFactory.Create(ChainSource);
        var manifest = Manifest("M:Production.Chain.A");

        await cancellation.CancelAsync().ConfigureAwait(false);
        var threw = ThrowsCancellation(() =>
            _ = ReachabilityClosure.Compute(compilation, manifest, cancellation.Token)
        );

        _ = await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task Compute_CompilationIsNull_ThrowsArgumentNullException()
    {
        var manifest = Manifest("M:Production.Chain.A");

        var threw = ThrowsArgumentNull(() => _ = ReachabilityClosure.Compute(null!, manifest, CancellationToken.None));

        _ = await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task Compute_ManifestIsNull_ThrowsArgumentNullException()
    {
        var compilation = CompilationFactory.Create(ChainSource);

        var threw = ThrowsArgumentNull(() =>
            _ = ReachabilityClosure.Compute(compilation, null!, CancellationToken.None)
        );

        _ = await Assert.That(threw).IsTrue();
    }

    private static TestSurfaceManifest Manifest(params string[] referencedMemberIds) =>
        new TestSurfaceManifest([], ImmutableHashSet.Create(StringComparer.Ordinal, referencedMemberIds));

    private static IMethodSymbol Method(Compilation compilation, string typeName, string methodName) =>
        compilation.GetTypeByMetadataName(typeName)!.GetMembers(methodName).OfType<IMethodSymbol>().First();

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

    private static bool ThrowsCancellation(Action action)
    {
        try
        {
            action();
        }
        catch (OperationCanceledException)
        {
            return true;
        }

        return false;
    }
}
