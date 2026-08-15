namespace NetEvolve.FrameShift.Tests.Unit.Reachability;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Reachability;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
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
/// <remarks>
/// The attribution the walk carries is covered here as well. Its one dangerous error direction is
/// understating it: a member that is reached by two tests but only remembers one of them makes the
/// aggregated test case count too small, which is exactly the false report the count exists to
/// prevent. The union over independent paths, over paths of different length, and over the dispatch
/// approximation is therefore asserted as an exact set every time.
/// </remarks>
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

    private const string DiamondSource = """
        namespace Production;

        public sealed class Diamond
        {
            public void EnterLeft() => ThroughLeft();

            public void EnterRight() => ThroughRight();

            private void ThroughLeft() => Shared();

            private void ThroughRight() => Shared();

            private void Shared() => Deep();

            private void Deep()
            {
            }
        }
        """;

    private const string LengthsSource = """
        namespace Production;

        public sealed class Lengths
        {
            public void FromShortPath() => Target();

            public void FromLongPath() => Hop();

            private void Hop() => Target();

            private void Target() => Tail();

            private void Tail()
            {
            }
        }
        """;

    private const string LeftTestId = "M:Tests.DiamondTests.EntersLeft";
    private const string RightTestId = "M:Tests.DiamondTests.EntersRight";
    private const string NoTestIds = "<none>";

    [Test]
    [Arguments(ChainSource)]
    [Arguments(CycleSource)]
    [Arguments(DispatchSource)]
    [Arguments(EnclosingSource)]
    [Arguments(DiamondSource)]
    [Arguments(LengthsSource)]
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Chain", "A"))).IsTrue();
            _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Chain", "B"))).IsTrue();
            _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Chain", "C"))).IsTrue();
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Chain", "D"))).IsFalse();
            _ = await Assert.That(reachable.Count).IsEqualTo(3);
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Chain", "A"))).IsTrue();
            _ = await Assert.That(reachable.Count).IsEqualTo(3);
        }
    }

    [Test]
    public async Task Compute_EmptyManifest_ReturnsTheEmptySet()
    {
        var compilation = CompilationFactory.Create(ChainSource);

        var reachable = ReachabilityClosure.Compute(compilation, TestSurfaceManifest.Empty, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.IsEmpty).IsTrue();
            _ = await Assert.That(reachable.Count).IsEqualTo(0);
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Cycle", "First"))).IsTrue();
            _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Cycle", "Second"))).IsTrue();
            _ = await Assert.That(reachable.Count).IsEqualTo(2);
        }
    }

    [Test]
    public async Task Compute_InterfaceMember_AlsoReachesItsImplementation()
    {
        var compilation = CompilationFactory.Create(DispatchSource);
        var manifest = Manifest("M:Production.Consumer.UseInterface(Production.IGreeter)");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Method(compilation, "Production.IGreeter", "Greet"))).IsTrue();
            _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Greeter", "Greet"))).IsTrue();
        }
    }

    [Test]
    public async Task Compute_AbstractMember_AlsoReachesItsOverride()
    {
        var compilation = CompilationFactory.Create(DispatchSource);
        var manifest = Manifest("M:Production.Consumer.UseAbstract(Production.ShapeBase)");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Method(compilation, "Production.ShapeBase", "Area"))).IsTrue();
            _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Square", "Area"))).IsTrue();
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(lambdaSymbol)).IsFalse();
            _ = await Assert.That(reachable.ContainsEnclosing(lambdaSymbol)).IsTrue();
        }
    }

    [Test]
    public async Task ContainsEnclosing_SymbolInsideALocalFunction_IsAttributedToItsEnclosingMember()
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(EnclosingSource);
        var localFunction = SyntaxNodeLocator.FindFirst<LocalFunctionStatementSyntax>(tree);
        var localFunctionSymbol = semanticModel.GetDeclaredSymbol(localFunction)!;
        var reachable = new ReachableSymbolSet([Method(compilation, "Production.Enclosing", "WithLocalFunction")]);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(localFunctionSymbol)).IsFalse();
            _ = await Assert.That(reachable.ContainsEnclosing(localFunctionSymbol)).IsTrue();
        }
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

        await cancellation.CancelAsyncCompat().ConfigureAwait(false);
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

    [Test]
    public async Task ComputeFromReferences_CallChain_AttributesEveryTransitiveMemberToTheSeedingTest()
    {
        var compilation = CompilationFactory.Create(ChainSource);
        var references = References((LeftTestId, ["M:Production.Chain.A"]));

        var reachable = ReachabilityClosure.ComputeFromReferences(compilation, references, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Describe(reachable, Method(compilation, "Production.Chain", "A")))
                .IsEqualTo(LeftTestId);
            _ = await Assert
                .That(Describe(reachable, Method(compilation, "Production.Chain", "B")))
                .IsEqualTo(LeftTestId);
            _ = await Assert
                .That(Describe(reachable, Method(compilation, "Production.Chain", "C")))
                .IsEqualTo(LeftTestId);
        }
    }

    [Test]
    public async Task ComputeFromReferences_CallChain_LeavesTheUntouchedMemberUnattributed()
    {
        var compilation = CompilationFactory.Create(ChainSource);
        var references = References((LeftTestId, ["M:Production.Chain.A"]));

        var reachable = ReachabilityClosure.ComputeFromReferences(compilation, references, CancellationToken.None);

        _ = await Assert.That(Describe(reachable, Method(compilation, "Production.Chain", "D"))).IsEqualTo(NoTestIds);
    }

    /// <summary>
    /// Two tests enter the same call graph through two different members and meet in a third one. The
    /// meeting point and everything below it belong to both tests, while the two entry paths keep the
    /// one test that owns them.
    /// </summary>
    [Test]
    public async Task ComputeFromReferences_DiamondWithTwoSeededTests_UnionsTheAttributionAtTheMeetingPoint()
    {
        var compilation = CompilationFactory.Create(DiamondSource);
        var references = References(
            (LeftTestId, ["M:Production.Diamond.EnterLeft"]),
            (RightTestId, ["M:Production.Diamond.EnterRight"])
        );

        var reachable = ReachabilityClosure.ComputeFromReferences(compilation, references, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Describe(reachable, Method(compilation, "Production.Diamond", "Shared")))
                .IsEqualTo($"{LeftTestId}, {RightTestId}");
            _ = await Assert
                .That(Describe(reachable, Method(compilation, "Production.Diamond", "Deep")))
                .IsEqualTo($"{LeftTestId}, {RightTestId}");
        }
    }

    [Test]
    public async Task ComputeFromReferences_DiamondWithTwoSeededTests_KeepsTheBranchesApart()
    {
        var compilation = CompilationFactory.Create(DiamondSource);
        var references = References(
            (LeftTestId, ["M:Production.Diamond.EnterLeft"]),
            (RightTestId, ["M:Production.Diamond.EnterRight"])
        );

        var reachable = ReachabilityClosure.ComputeFromReferences(compilation, references, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Describe(reachable, Method(compilation, "Production.Diamond", "ThroughLeft")))
                .IsEqualTo(LeftTestId);
            _ = await Assert
                .That(Describe(reachable, Method(compilation, "Production.Diamond", "ThroughRight")))
                .IsEqualTo(RightTestId);
        }
    }

    /// <summary>
    /// The two paths to the shared member have different lengths, so the walk reaches it once, expands
    /// it, and only afterwards learns about the second test. Everything below the shared member has to
    /// be revisited for that, or the attribution of the longer path would stop at it.
    /// </summary>
    [Test]
    public async Task ComputeFromReferences_PathsOfDifferentLength_PropagateTheWidenedAttributionDownwards()
    {
        var compilation = CompilationFactory.Create(LengthsSource);
        var references = References(
            (LeftTestId, ["M:Production.Lengths.FromShortPath"]),
            (RightTestId, ["M:Production.Lengths.FromLongPath"])
        );

        var reachable = ReachabilityClosure.ComputeFromReferences(compilation, references, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Describe(reachable, Method(compilation, "Production.Lengths", "Target")))
                .IsEqualTo($"{LeftTestId}, {RightTestId}");
            _ = await Assert
                .That(Describe(reachable, Method(compilation, "Production.Lengths", "Tail")))
                .IsEqualTo($"{LeftTestId}, {RightTestId}");
        }
    }

    [Test]
    public async Task ComputeFromReferences_TwoTestsSeedingTheSameMember_UnionTheAttributionOfTheSeed()
    {
        var compilation = CompilationFactory.Create(ChainSource);
        var references = References((LeftTestId, ["M:Production.Chain.A"]), (RightTestId, ["M:Production.Chain.A"]));

        var reachable = ReachabilityClosure.ComputeFromReferences(compilation, references, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Describe(reachable, Method(compilation, "Production.Chain", "A")))
                .IsEqualTo($"{LeftTestId}, {RightTestId}");
            _ = await Assert
                .That(Describe(reachable, Method(compilation, "Production.Chain", "C")))
                .IsEqualTo($"{LeftTestId}, {RightTestId}");
        }
    }

    [Test]
    public async Task ComputeFromReferences_CyclicCallGraph_AttributesBothMembersAndTerminates()
    {
        var compilation = CompilationFactory.Create(CycleSource);
        var references = References((LeftTestId, ["M:Production.Cycle.First"]));

        var reachable = ReachabilityClosure.ComputeFromReferences(compilation, references, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Describe(reachable, Method(compilation, "Production.Cycle", "First")))
                .IsEqualTo(LeftTestId);
            _ = await Assert
                .That(Describe(reachable, Method(compilation, "Production.Cycle", "Second")))
                .IsEqualTo(LeftTestId);
        }
    }

    [Test]
    public async Task ComputeFromReferences_UnresolvableId_IsIgnored()
    {
        var compilation = CompilationFactory.Create(ChainSource);
        var references = References((LeftTestId, ["M:Production.Chain.Missing"]));

        var reachable = ReachabilityClosure.ComputeFromReferences(compilation, references, CancellationToken.None);

        _ = await Assert.That(reachable.IsEmpty).IsTrue();
    }

    [Test]
    public async Task ComputeFromReferences_NoReferenceAtAll_ReturnsTheEmptySet()
    {
        var compilation = CompilationFactory.Create(ChainSource);

        var reachable = ReachabilityClosure.ComputeFromReferences(
            compilation,
            ImmutableDictionary.Create<string, ImmutableHashSet<string>>(StringComparer.Ordinal),
            CancellationToken.None
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.IsEmpty).IsTrue();
            _ = await Assert.That(reachable.Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task ComputeFromReferences_CancelledToken_Throws()
    {
        using var cancellation = new CancellationTokenSource();
        var compilation = CompilationFactory.Create(ChainSource);
        var references = References((LeftTestId, ["M:Production.Chain.A"]));

        await cancellation.CancelAsyncCompat().ConfigureAwait(false);
        var threw = ThrowsCancellation(() =>
            _ = ReachabilityClosure.ComputeFromReferences(compilation, references, cancellation.Token)
        );

        _ = await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task ComputeFromReferences_CompilationIsNull_ThrowsArgumentNullException()
    {
        var references = References((LeftTestId, ["M:Production.Chain.A"]));

        var threw = ThrowsArgumentNull(() =>
            _ = ReachabilityClosure.ComputeFromReferences(null!, references, CancellationToken.None)
        );

        _ = await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task ComputeFromReferences_ReferencesAreNull_ThrowsArgumentNullException()
    {
        var compilation = CompilationFactory.Create(ChainSource);

        var threw = ThrowsArgumentNull(() =>
            _ = ReachabilityClosure.ComputeFromReferences(compilation, null!, CancellationToken.None)
        );

        _ = await Assert.That(threw).IsTrue();
    }

    /// <summary>
    /// A manifest that records references without saying which test recorded them still produces the
    /// very same reachable set. The members are reachable, they just carry no test id, and a caller has
    /// to read that as "unknown" instead of as "no test".
    /// </summary>
    [Test]
    public async Task Compute_ManifestWithoutAttribution_KeepsTheMembersReachableAndUnattributed()
    {
        var compilation = CompilationFactory.Create(ChainSource);

        var reachable = ReachabilityClosure.Compute(
            compilation,
            Manifest("M:Production.Chain.A"),
            CancellationToken.None
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Chain", "C"))).IsTrue();
            _ = await Assert
                .That(Describe(reachable, Method(compilation, "Production.Chain", "C")))
                .IsEqualTo(NoTestIds);
        }
    }

    [Test]
    public async Task GetTestIds_SymbolIsNull_ThrowsArgumentNullException()
    {
        var reachable = ReachableSymbolSet.Empty;

        var exception = Assert.Throws<ArgumentNullException>(() => _ = reachable.GetTestIds(null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("symbol");
    }

    [Test]
    public async Task GetEnclosingTestIds_SymbolIsNull_ReturnsNoTestIds()
    {
        var reachable = ReachableSymbolSet.Empty;

        _ = await Assert.That(Describe(reachable.GetEnclosingTestIds(null))).IsEqualTo(NoTestIds);
    }

    /// <summary>
    /// The mutation point inside a lambda has to be aggregated with the tests that reach the member the
    /// lambda lives in, because the lambda itself is never reached by anything else.
    /// </summary>
    [Test]
    public async Task GetEnclosingTestIds_SymbolInsideALambda_InheritsTheAttributionOfItsEnclosingMember()
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(EnclosingSource);
        var lambda = SyntaxNodeLocator.FindFirst<LambdaExpressionSyntax>(tree);
        var lambdaSymbol = semanticModel.GetSymbolInfo(lambda).Symbol!;
        var references = References((LeftTestId, ["M:Production.Enclosing.WithLambda"]));

        var reachable = ReachabilityClosure.ComputeFromReferences(compilation, references, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Describe(reachable, Method(compilation, "Production.Enclosing", "WithLambda")))
                .IsEqualTo(LeftTestId);
            _ = await Assert.That(Describe(reachable.GetEnclosingTestIds(lambdaSymbol))).IsEqualTo(LeftTestId);
        }
    }

    [Test]
    public async Task GetEnclosingTestIds_SymbolInsideAnUnattributedMember_ReturnsNoTestIds()
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(EnclosingSource);
        var lambda = SyntaxNodeLocator.FindFirst<LambdaExpressionSyntax>(tree);
        var lambdaSymbol = semanticModel.GetSymbolInfo(lambda).Symbol!;
        var references = References((LeftTestId, ["M:Production.Enclosing.WithLocalFunction"]));

        var reachable = ReachabilityClosure.ComputeFromReferences(compilation, references, CancellationToken.None);

        _ = await Assert.That(Describe(reachable.GetEnclosingTestIds(lambdaSymbol))).IsEqualTo(NoTestIds);
    }

    /// <summary>
    /// The local function is reached in its own right, because the walk records its declaration, so its
    /// attribution and the one of the member that declares it are the same set instead of one being
    /// inherited from the other.
    /// </summary>
    [Test]
    public async Task GetEnclosingTestIds_LocalFunction_MatchesTheAttributionOfItsEnclosingMember()
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(EnclosingSource);
        var localFunction = SyntaxNodeLocator.FindFirst<LocalFunctionStatementSyntax>(tree);
        var localFunctionSymbol = semanticModel.GetDeclaredSymbol(localFunction)!;
        var references = References((LeftTestId, ["M:Production.Enclosing.WithLocalFunction"]));

        var reachable = ReachabilityClosure.ComputeFromReferences(compilation, references, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Describe(reachable, localFunctionSymbol)).IsEqualTo(LeftTestId);
            _ = await Assert.That(Describe(reachable.GetEnclosingTestIds(localFunctionSymbol))).IsEqualTo(LeftTestId);
        }
    }

    private static TestSurfaceManifest Manifest(params string[] referencedMemberIds) =>
        new TestSurfaceManifest([], ImmutableHashSet.Create(StringComparer.Ordinal, referencedMemberIds));

    private static ImmutableDictionary<string, ImmutableHashSet<string>> References(
        params (string TestId, string[] ReferencedMemberIds)[] tests
    )
    {
        var builder = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(StringComparer.Ordinal);

        foreach (var (testId, referencedMemberIds) in tests)
        {
            builder[testId] = ImmutableHashSet.Create(StringComparer.Ordinal, referencedMemberIds);
        }

        return builder.ToImmutable();
    }

    private static string Describe(ReachableSymbolSet reachable, ISymbol symbol) =>
        Describe(reachable.GetTestIds(symbol));

    private static string Describe(ImmutableHashSet<string> testIds) =>
        testIds.IsEmpty ? NoTestIds : string.Join(", ", testIds.OrderBy(testId => testId, StringComparer.Ordinal));

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
