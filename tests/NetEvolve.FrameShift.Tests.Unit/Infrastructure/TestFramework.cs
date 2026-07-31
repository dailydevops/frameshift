namespace NetEvolve.FrameShift.Tests.Infrastructure;

/// <summary>
/// The test framework whose assemblies a test compilation references.
/// </summary>
/// <remarks>
/// The value decides which reference set <see cref="ReferenceAssemblies.For(TestFramework)" /> hands out,
/// and therefore whether a fixture can declare test methods of that framework at all. A compilation that
/// references no test framework is a production assembly for the analyzers under test.
/// </remarks>
internal enum TestFramework
{
    /// <summary>
    /// No test framework, which makes the compilation a plain production assembly.
    /// </summary>
    None = 0,

    /// <summary>
    /// TUnit, recognised by <c>TUnit.Core.TestAttribute</c>.
    /// </summary>
    TUnit,

    /// <summary>
    /// xUnit.net v2, recognised by <c>Xunit.FactAttribute</c> of the <c>xunit.core</c> assembly, and
    /// reported as <c>xUnit v2</c>.
    /// </summary>
    /// <remarks>
    /// <c>xunit.core</c> has assets for every target framework of this suite, so this value is usable on all
    /// of them and a test built on it never needs a guard. That is the whole asymmetry between the two
    /// versions: only <see cref="XunitV3" /> is conditional.
    /// </remarks>
    XunitV2,

    /// <summary>
    /// xUnit.net v3, recognised by <c>Xunit.FactAttribute</c> of the <c>xunit.v3.core</c> assembly, and
    /// reported as <c>xUnit v3</c>.
    /// </summary>
    /// <remarks>
    /// The member is declared on every target framework, so that no call site needs a guard of its own, and
    /// it keeps the same ordinal everywhere. <c>xunit.v3.core</c> ships no assets for net6.0 and net7.0
    /// however, therefore <c>FRAMESHIFT_XUNIT_V3</c> is undefined there and
    /// <see cref="ReferenceAssemblies.For(TestFramework)" /> cannot build a reference set for this value at
    /// all; it throws instead of handing out a set without a single xUnit.net v3 assembly. A test that
    /// asserts xUnit.net v3 recognition therefore has to be guarded by <c>FRAMESHIFT_XUNIT_V3</c> itself.
    /// </remarks>
    XunitV3,

    /// <summary>
    /// NUnit, recognised by <c>NUnit.Framework.TestAttribute</c>.
    /// </summary>
    NUnit,

    /// <summary>
    /// MSTest, recognised by <c>Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute</c>.
    /// </summary>
    MSTest,

    /// <summary>
    /// Every supported test framework at once, which is what the cross-framework tests need.
    /// </summary>
    /// <remarks>
    /// This is the one value that references both xUnit.net versions at once, which is what a test proving
    /// that the v2 and the v3 adapter each answer only for their own assembly needs. Both versions declare
    /// the very same type names, so a fixture built against this value must not spell out an ambiguous name
    /// such as <c>Xunit.FactAttribute</c>; it would not compile. On net6.0 and net7.0, where
    /// <c>FRAMESHIFT_XUNIT_V3</c> is undefined, the value covers every supported framework except
    /// xUnit.net v3, and the names of xUnit.net v2 are therefore unambiguous there.
    /// </remarks>
    All,
}
