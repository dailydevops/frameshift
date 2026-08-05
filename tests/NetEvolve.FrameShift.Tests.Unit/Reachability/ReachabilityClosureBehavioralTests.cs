namespace NetEvolve.FrameShift.Tests.Unit.Reachability;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using NetEvolve.FrameShift.Reachability;
using NetEvolve.FrameShift.TestSurface;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers <see cref="ReachabilityClosure.ComputeBehavioral" /> directly: it re-uses the very same
/// transitive walk <see cref="ReachabilityClosureTests" /> already covers for
/// <see cref="ReachabilityClosure.Compute(Compilation, TestSurfaceManifest, CancellationToken)" />, seeded
/// from a narrower set, so this file does not repeat every dispatch and attribution scenario of that
/// walk. It instead proves the one thing that is unique to the behavioral variant: the seed is
/// <see cref="TestSurfaceManifest.BehavioralReferencedMemberIds" />, never
/// <see cref="TestSurfaceManifest.ReferencedMemberIds" />, so a manifest with plain references and no
/// behavioral ones produces an empty behavioral closure even though the plain closure of the very same
/// manifest is not empty at all.
/// </summary>
public class ReachabilityClosureBehavioralTests
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
        """;

    private const string DiamondSource = """
        namespace Production;

        public sealed class Diamond
        {
            public void EnterLeft() => Shared();

            public void EnterRight() => Shared();

            private void Shared()
            {
            }
        }
        """;

    private const string AnonymousTestId = "M:Tests.Behavioral.Reaches";
    private const string LeftTestId = "M:Tests.DiamondTests.EntersLeft";
    private const string RightTestId = "M:Tests.DiamondTests.EntersRight";
    private const string NoTestIds = "<none>";

    [Test]
    [Arguments(ChainSource)]
    [Arguments(CycleSource)]
    [Arguments(DispatchSource)]
    [Arguments(DiamondSource)]
    public async Task Fixtures_EveryCompilation_CompilesWithoutErrors(string source)
    {
        var compilation = CompilationFactory.Create(source);

        _ = await Assert
            .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    /// <summary>
    /// The acceptance criterion of the behavioral closure at the reachability layer: a member that is a
    /// plain reference of the manifest, but never a behavioral one, is reachable through
    /// <see cref="ReachabilityClosure.Compute(Compilation, TestSurfaceManifest, CancellationToken)" /> and
    /// unreachable through <see cref="ReachabilityClosure.ComputeBehavioral" />. This is exactly the shape
    /// a manifest recorded for a test that only takes a method reference and asserts <c>IsNotNull</c> has:
    /// the reference exists, the behavioral one does not.
    /// </summary>
    [Test]
    public async Task ComputeBehavioral_ManifestWithOnlyPlainReferences_ReturnsTheEmptySet()
    {
        var compilation = CompilationFactory.Create(ChainSource);
        var manifest = PlainManifest("M:Production.Chain.A");

        var plain = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);
        var behavioral = ReachabilityClosure.ComputeBehavioral(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(plain.IsEmpty).IsFalse();
            _ = await Assert.That(behavioral.IsEmpty).IsTrue();
        }
    }

    [Test]
    public async Task ComputeBehavioral_CallChain_ContainsTheTransitivelyReachedMembers()
    {
        var compilation = CompilationFactory.Create(ChainSource);
        var manifest = BehavioralManifest("M:Production.Chain.A");

        var reachable = ReachabilityClosure.ComputeBehavioral(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Chain", "A"))).IsTrue();
            _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Chain", "B"))).IsTrue();
            _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Chain", "C"))).IsTrue();
        }
    }

    [Test]
    public async Task ComputeBehavioral_CallChain_DoesNotContainTheUntouchedMember()
    {
        var compilation = CompilationFactory.Create(ChainSource);
        var manifest = BehavioralManifest("M:Production.Chain.A");

        var reachable = ReachabilityClosure.ComputeBehavioral(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Chain", "D"))).IsFalse();
            _ = await Assert.That(reachable.Count).IsEqualTo(3);
        }
    }

    [Test]
    public async Task ComputeBehavioral_EmptyManifest_ReturnsTheEmptySet()
    {
        var reachable = ReachabilityClosure.ComputeBehavioral(
            CompilationFactory.Create(ChainSource),
            TestSurfaceManifest.Empty,
            CancellationToken.None
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.IsEmpty).IsTrue();
            _ = await Assert.That(reachable.Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task ComputeBehavioral_UnresolvableIds_AreIgnored()
    {
        var compilation = CompilationFactory.Create(ChainSource);
        var manifest = BehavioralManifest("M:Production.Chain.Missing", "T:Production.Absent");

        var reachable = ReachabilityClosure.ComputeBehavioral(compilation, manifest, CancellationToken.None);

        _ = await Assert.That(reachable.IsEmpty).IsTrue();
    }

    [Test]
    public async Task ComputeBehavioral_UnresolvableIdNextToAValidOne_KeepsTheValidSeed()
    {
        var compilation = CompilationFactory.Create(ChainSource);
        var manifest = BehavioralManifest("M:Production.Chain.Missing", "M:Production.Chain.A");

        var reachable = ReachabilityClosure.ComputeBehavioral(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Chain", "A"))).IsTrue();
            _ = await Assert.That(reachable.Count).IsEqualTo(3);
        }
    }

    [Test]
    public async Task ComputeBehavioral_CyclicCallGraph_Terminates()
    {
        var compilation = CompilationFactory.Create(CycleSource);
        var manifest = BehavioralManifest("M:Production.Cycle.First");

        var reachable = ReachabilityClosure.ComputeBehavioral(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Cycle", "First"))).IsTrue();
            _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Cycle", "Second"))).IsTrue();
            _ = await Assert.That(reachable.Count).IsEqualTo(2);
        }
    }

    /// <summary>
    /// The dispatch approximation of the shared walker applies to the behavioral closure exactly like it
    /// does to the plain one: a behaviorally referenced interface member also behaviorally reaches its
    /// implementation declared in this compilation.
    /// </summary>
    [Test]
    public async Task ComputeBehavioral_InterfaceMember_AlsoReachesItsImplementation()
    {
        var compilation = CompilationFactory.Create(DispatchSource);
        var manifest = BehavioralManifest("M:Production.IGreeter.Greet");

        var reachable = ReachabilityClosure.ComputeBehavioral(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Method(compilation, "Production.IGreeter", "Greet"))).IsTrue();
            _ = await Assert.That(reachable.Contains(Method(compilation, "Production.Greeter", "Greet"))).IsTrue();
        }
    }

    /// <summary>
    /// A member reached behaviorally by two different tests is attributed to both, exactly like the plain
    /// closure attributes a member reached by two tests to both of them.
    /// </summary>
    [Test]
    public async Task ComputeBehavioral_MemberReachedByTwoTests_IsAttributedToBoth()
    {
        var compilation = CompilationFactory.Create(DiamondSource);
        var manifest = new TestSurfaceManifest(
            ImmutableDictionary<string, TestCaseCount>.Empty,
            ImmutableDictionary<string, ImmutableHashSet<string>>.Empty,
            BehavioralReferences(
                (LeftTestId, ["M:Production.Diamond.EnterLeft"]),
                (RightTestId, ["M:Production.Diamond.EnterRight"])
            )
        );

        var reachable = ReachabilityClosure.ComputeBehavioral(compilation, manifest, CancellationToken.None);
        var shared = Method(compilation, "Production.Diamond", "Shared");

        _ = await Assert.That(Describe(reachable.GetTestIds(shared))).IsEqualTo(LeftTestId + ", " + RightTestId);
    }

    [Test]
    public async Task ComputeBehavioral_CompilationIsNull_ThrowsArgumentNullException()
    {
        var threw = ThrowsArgumentNull(() =>
            _ = ReachabilityClosure.ComputeBehavioral(null!, TestSurfaceManifest.Empty, CancellationToken.None)
        );

        _ = await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task ComputeBehavioral_ManifestIsNull_ThrowsArgumentNullException()
    {
        var compilation = CompilationFactory.Create(ChainSource);

        var threw = ThrowsArgumentNull(() =>
            _ = ReachabilityClosure.ComputeBehavioral(compilation, null!, CancellationToken.None)
        );

        _ = await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task ComputeBehavioral_CancelledToken_ThrowsOperationCanceledException()
    {
        var compilation = CompilationFactory.Create(ChainSource);
        var manifest = BehavioralManifest("M:Production.Chain.A");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var threw = ThrowsCancellation(() =>
            _ = ReachabilityClosure.ComputeBehavioral(compilation, manifest, cancellation.Token)
        );

        _ = await Assert.That(threw).IsTrue();
    }

    private static TestSurfaceManifest PlainManifest(params string[] referencedMemberIds) =>
        new TestSurfaceManifest([], ImmutableHashSet.Create(StringComparer.Ordinal, referencedMemberIds));

    private static TestSurfaceManifest BehavioralManifest(params string[] behavioralReferencedMemberIds) =>
        new TestSurfaceManifest(
            ImmutableDictionary<string, TestCaseCount>.Empty,
            ImmutableDictionary<string, ImmutableHashSet<string>>.Empty,
            BehavioralReferences((AnonymousTestId, behavioralReferencedMemberIds))
        );

    private static ImmutableDictionary<string, ImmutableHashSet<string>> BehavioralReferences(
        params (string TestId, string[] BehavioralReferencedMemberIds)[] tests
    )
    {
        var builder = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(StringComparer.Ordinal);

        foreach (var (testId, behavioralReferencedMemberIds) in tests)
        {
            builder[testId] = ImmutableHashSet.Create(StringComparer.Ordinal, behavioralReferencedMemberIds);
        }

        return builder.ToImmutable();
    }

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
