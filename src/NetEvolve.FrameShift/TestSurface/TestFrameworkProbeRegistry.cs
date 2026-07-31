namespace NetEvolve.FrameShift.TestSurface;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

/// <summary>
/// The single place where the supported test frameworks are listed. Adding a framework means adding one
/// probe, one <see cref="ITestMethodRecognizer" />, one thin analyzer and one line in the registration
/// region below.
/// </summary>
/// <remarks>
/// <para>
/// The order of the registrations is fixed — TUnit, xUnit v2, xUnit v3, NUnit, MSTest — and is part of
/// the contract rather than cosmetic. When a test project references several frameworks at once, every
/// matching analyzer would judge the one shared test-surface manifest and report the same problem several
/// times, so exactly one of them has to be in charge, and this order is what picks it.
/// </para>
/// <para>
/// The two major versions of xUnit are two entries, not one. Both declare their test attribute under the
/// identical metadata name <c>Xunit.FactAttribute</c>, version 2 in <c>xunit.core</c> and version 3 in
/// <c>xunit.v3.core</c>, so a compilation referencing both makes that name ambiguous and
/// <see cref="Compilation.GetTypeByMetadataName(string)" /> answers <see langword="null" /> for it. A
/// probe bound to one major version resolves the attribute inside its own assembly instead, through
/// <see cref="IAssemblySymbol.GetTypeByMetadataName(string)" />, which is exact. Separate entries are
/// what make that possible, and they are also what lets version 2 be analysed on every target framework
/// while version 3 is only available where its package ships assets.
/// </para>
/// <para>
/// For a compilation that references both versions this means: both probes match, each recogniser sees
/// only the tests of its own version, and each of the two analyzers reports <c>FSH0004</c> for those
/// tests alone — so no test is judged twice and none is missed. The manifest, of which there is only one
/// per project, is reported on by the first matching probe in the order above, which for the two xUnit
/// versions is version 2; it compares the manifest against the union of the test surfaces of every
/// matching framework, so tests of the other version never make the manifest look stale.
/// </para>
/// <para>
/// Every registered probe is stateless and its instance is shared, which keeps the registry safe to use
/// from concurrent analyzer callbacks.
/// </para>
/// </remarks>
internal static class TestFrameworkProbeRegistry
{
    private static readonly ImmutableArray<ITestFrameworkProbe> _all =
    [
        // >>> probe registrations
        TUnitTestFrameworkProbe.Instance,
        XunitV2TestFrameworkProbe.Instance,
        XunitV3TestFrameworkProbe.Instance,
        NUnitTestFrameworkProbe.Instance,
        MSTestTestFrameworkProbe.Instance,
        // <<< probe registrations
    ];

    /// <summary>
    /// Gets every registered probe, in a fixed order so that the outcome of a probe walk never depends
    /// on anything but the compilation itself.
    /// </summary>
    public static ImmutableArray<ITestFrameworkProbe> All => _all;

    /// <summary>
    /// Determines which of the registered frameworks <paramref name="compilation" /> actually uses.
    /// </summary>
    /// <param name="compilation">The compilation to probe.</param>
    /// <returns>
    /// The probes that recognise the compilation, in the order of <see cref="All" />, which is empty
    /// when no supported test framework is present. A compilation referencing both major versions of
    /// xUnit yields both xUnit entries.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="compilation" /> is <see langword="null" />.</exception>
    public static ImmutableArray<ITestFrameworkProbe> Matching(Compilation compilation)
    {
        if (compilation is null)
        {
            throw new ArgumentNullException(nameof(compilation));
        }

        return _all.Where(probe => probe.TryCreateRecognizer(compilation) is not null).ToImmutableArray();
    }
}
