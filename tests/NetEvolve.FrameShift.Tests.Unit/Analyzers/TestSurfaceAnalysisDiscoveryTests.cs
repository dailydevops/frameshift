namespace NetEvolve.FrameShift.Tests.Unit.Analyzers;

using System;
using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NetEvolve.FrameShift.Analyzers;
using NetEvolve.FrameShift.Configuration;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Pins down that <see cref="TestSurfaceAnalysis" /> discovers the current framework's test methods at
/// most once per <c>Execute</c> call, instead of once directly and once more while collecting the awake
/// frameworks.
/// </summary>
/// <remarks>
/// The private <c>FindAwakeFrameworks</c> helper is reached through reflection because it is the exact
/// seam the fix lives in: it either reuses the recogniser <c>Execute</c> already built and already used to
/// discover the current framework's test methods, or it builds and discovers with a fresh one. Reference
/// equality of the recogniser is what tells the two cases apart without instrumenting
/// <see cref="TestMethodDiscovery.FindTestMethods" /> itself.
/// </remarks>
public class TestSurfaceAnalysisDiscoveryTests
{
    private const string TestSource = """
        namespace Tests;

        using TUnit.Core;

        public class SampleTests
        {
            [Test]
            public void Alpha()
            {
            }
        }
        """;

    /// <summary>
    /// For the registry entry matching the framework <c>Execute</c> is already running for,
    /// <c>FindAwakeFrameworks</c> must reuse the recogniser (and therefore the discovery result) it is
    /// handed, rather than creating a new recogniser and rediscovering the same test methods a second
    /// time.
    /// </summary>
    [Test]
    public async Task FindAwakeFrameworks_EntryOfTheCurrentFramework_ReusesTheAlreadyKnownRecognizer()
    {
        var compilation = CompilationFactory.Create(TestSource, includeTUnit: true);
        var probe = TUnitTestFrameworkProbe.Instance;
        var recognizer = probe.TryCreateRecognizer(compilation)!;
        var testMethods = TestMethodDiscovery.FindTestMethods(compilation, recognizer, CancellationToken.None);

        _ = await Assert.That(testMethods.IsEmpty).IsFalse();

        var awake = InvokeFindAwakeFrameworks(compilation, probe, recognizer, testMethods);
        var entry = FindEntryFor(awake, probe);

        using (Assert.Multiple())
        {
            _ = await Assert.That(entry).IsNotNull();
            _ = await Assert.That(ReferenceEquals(GetRecognizer(entry!), recognizer)).IsTrue();
        }
    }

    /// <summary>
    /// The reused recogniser also means the reused discovery result: a framework with no test methods of
    /// its own is not reported as awake, exactly like a freshly discovered empty result would not be.
    /// </summary>
    [Test]
    public async Task FindAwakeFrameworks_CurrentFrameworkHasNoTestMethods_IsNotReportedAsAwake()
    {
        const string withoutTests = """
            namespace Tests;

            public class NotATestClass
            {
                public int Compute() => 41;
            }
            """;

        var compilation = CompilationFactory.Create(withoutTests, includeTUnit: true);
        var probe = TUnitTestFrameworkProbe.Instance;
        var recognizer = probe.TryCreateRecognizer(compilation)!;
        var testMethods = TestMethodDiscovery.FindTestMethods(compilation, recognizer, CancellationToken.None);

        _ = await Assert.That(testMethods.IsEmpty).IsTrue();

        var awake = InvokeFindAwakeFrameworks(compilation, probe, recognizer, testMethods);
        var entry = FindEntryFor(awake, probe);

        _ = await Assert.That(entry).IsNull();
    }

    private static IEnumerable InvokeFindAwakeFrameworks(
        Compilation compilation,
        ITestFrameworkProbe probe,
        ITestMethodRecognizer recognizer,
        ImmutableArray<IMethodSymbol> testMethods
    )
    {
#pragma warning disable CS0618 // Obsolete: this constructor is the only way to build the context by hand for a targeted, reflection-driven test of a private helper.
        var context = new CompilationAnalysisContext(
            compilation,
            new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty),
            static _ => { },
            static _ => true,
            CancellationToken.None
        );
#pragma warning restore CS0618

        var method =
            typeof(TestSurfaceAnalysis).GetMethod("FindAwakeFrameworks", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("TestSurfaceAnalysis.FindAwakeFrameworks was not found.");

        var result = method.Invoke(null, [context, probe, recognizer, testMethods, FrameShiftOptions.Default]);

        return (IEnumerable)result!;
    }

    private static object? FindEntryFor(IEnumerable awake, ITestFrameworkProbe probe)
    {
        foreach (var candidate in awake)
        {
            var candidateProbe = candidate
                .GetType()
                .GetProperty("Probe", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(candidate);

            if (ReferenceEquals(candidateProbe, probe))
            {
                return candidate;
            }
        }

        return null;
    }

    private static ITestMethodRecognizer GetRecognizer(object entry) =>
        (ITestMethodRecognizer)(
            entry.GetType().GetProperty("Recognizer", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry)
            ?? throw new InvalidOperationException("AwakeFramework.Recognizer was not found.")
        );
}
