namespace NetEvolve.Frameshift.Tests.Integration;

using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

public class ScaffoldingTests
{
    [Test]
    public async Task Assembly_Categorisation_IsIntegrationTest()
    {
        var attributes = Assembly.GetExecutingAssembly().GetCustomAttributes(inherit: false);

        _ = await Assert
            .That(attributes.Select(attribute => attribute.GetType().Name))
            .Contains("IntegrationTestAttribute");
    }
}
