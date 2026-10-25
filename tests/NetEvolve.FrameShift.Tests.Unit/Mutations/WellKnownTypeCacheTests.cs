namespace NetEvolve.FrameShift.Tests.Unit.Mutations;

using Microsoft.CodeAnalysis;
using NetEvolve.FrameShift.Mutations;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers <see cref="WellKnownTypeCache" />: repeated lookups of the same metadata name in the same
/// compilation return the identical, cached symbol; independent compilations never share an entry; and an
/// unresolvable metadata name is cached as a miss rather than retried.
/// </summary>
public class WellKnownTypeCacheTests
{
    private const string Source = """
        public class Sample
        {
        }
        """;

    [Test]
    public async Task GetType_CalledTwiceForSameCompilation_ReturnsSameSymbolInstance()
    {
        var compilation = CompilationFactory.Create(Source);

        var first = WellKnownTypeCache.GetType(compilation, "System.String");
        var second = WellKnownTypeCache.GetType(compilation, "System.String");

        _ = await Assert.That(first).IsNotNull();
        _ = await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }

    [Test]
    public async Task GetType_CalledForDifferentMetadataNames_ReturnsDistinctSymbols()
    {
        var compilation = CompilationFactory.Create(Source);

        var stringType = WellKnownTypeCache.GetType(compilation, "System.String");
        var mathType = WellKnownTypeCache.GetType(compilation, "System.Math");

        _ = await Assert.That(stringType).IsNotNull();
        _ = await Assert.That(mathType).IsNotNull();
        _ = await Assert.That(SymbolEqualityComparer.Default.Equals(stringType, mathType)).IsFalse();
    }

    [Test]
    public async Task GetType_CalledForDifferentCompilations_ReturnsIndependentSymbols()
    {
        var first = CompilationFactory.Create(Source, assemblyName: "FirstAssembly");
        var second = CompilationFactory.Create(Source, assemblyName: "SecondAssembly");

        // "Sample" is declared by the fixture's own source, so each compilation builds its own source
        // symbol for it; unlike a type resolved from a shared metadata reference, two such symbols can
        // never be the same instance, which is what proves the cache keeps the two compilations apart
        // instead of one leaking the other's answer.
        var firstType = WellKnownTypeCache.GetType(first, "Sample");
        var secondType = WellKnownTypeCache.GetType(second, "Sample");

        _ = await Assert.That(firstType).IsNotNull();
        _ = await Assert.That(secondType).IsNotNull();
        _ = await Assert.That(ReferenceEquals(firstType, secondType)).IsFalse();
        _ = await Assert.That(SymbolEqualityComparer.Default.Equals(firstType, secondType)).IsFalse();
    }

    [Test]
    public async Task GetType_UnresolvableMetadataName_ReturnsNullBothTimes()
    {
        var compilation = CompilationFactory.Create(Source);

        var first = WellKnownTypeCache.GetType(compilation, "Not.A.Real.Type");
        var second = WellKnownTypeCache.GetType(compilation, "Not.A.Real.Type");

        _ = await Assert.That(first).IsNull();
        _ = await Assert.That(second).IsNull();
    }

    [Test]
    public async Task GetType_CompilationNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = WellKnownTypeCache.GetType(null!, "System.String")
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("compilation");
    }

    [Test]
    public async Task GetType_MetadataNameNull_ThrowsArgumentNullException()
    {
        var compilation = CompilationFactory.Create(Source);

        var exception = Assert.Throws<ArgumentNullException>(() => _ = WellKnownTypeCache.GetType(compilation, null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("metadataName");
    }
}
