namespace NetEvolve.Frameshift.Tests.Unit;

using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

public class ScaffoldingTests
{
    [Test]
    public async Task Assembly_UnderTest_IsLoadable()
    {
        var assembly = typeof(Placeholder).Assembly;

        _ = await Assert.That(assembly.GetName().Name).IsEqualTo("NetEvolve.Frameshift");
    }

    [Test]
    public async Task Assembly_Categorisation_IsUnitTest()
    {
        var attributes = Assembly.GetExecutingAssembly().GetCustomAttributes(inherit: false);

        _ = await Assert.That(attributes.Select(attribute => attribute.GetType().Name)).Contains("UnitTestAttribute");
    }
}
