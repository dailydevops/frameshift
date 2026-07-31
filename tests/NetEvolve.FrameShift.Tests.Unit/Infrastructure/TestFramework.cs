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
    /// xUnit.net v3, recognised by <c>Xunit.FactAttribute</c> of the <c>xunit.v3.core</c> assembly.
    /// </summary>
    XunitV3,

    /// <summary>
    /// xUnit.net v2, recognised by <c>Xunit.FactAttribute</c> of the <c>xunit.core</c> assembly.
    /// </summary>
    XunitV2,

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
    /// Both xUnit.net versions declare the very same type names, so a fixture built against this value
    /// must not spell out an ambiguous name such as <c>Xunit.FactAttribute</c>; it would not compile.
    /// </remarks>
    All,
}
