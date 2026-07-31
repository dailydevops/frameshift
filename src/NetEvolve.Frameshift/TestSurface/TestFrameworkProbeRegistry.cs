namespace NetEvolve.Frameshift.TestSurface;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

/// <summary>
/// The single place where the supported test frameworks are listed. Adding a framework means adding one
/// probe, one <see cref="ITestMethodRecognizer" />, one thin analyzer and one line in the registration
/// region below.
/// </summary>
/// <remarks>
/// <para>
/// The order of the registrations is fixed — TUnit, xUnit, NUnit, MSTest — and is part of the contract
/// rather than cosmetic. When a test project references several frameworks at once, every matching
/// analyzer would judge the one shared test-surface manifest and report the same problem several times,
/// so exactly one of them has to be in charge, and this order is what picks it.
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
        XunitTestFrameworkProbe.Instance,
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
    /// when no supported test framework is present.
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
