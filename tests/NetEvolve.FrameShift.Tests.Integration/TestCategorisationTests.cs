namespace NetEvolve.FrameShift.Tests.Integration;

using System.Reflection;
using NetEvolve.FrameShift.Analyzers;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Guards two deliberate conventions of this project: the assembly is categorised as an integration
/// test assembly from the project file, and the analyzer assembly under test is loadable from here.
/// </summary>
public class TestCategorisationTests
{
    [Test]
    public async Task Assembly_Categorisation_IsIntegrationTest()
    {
        var attributes = Assembly.GetExecutingAssembly().GetCustomAttributes(inherit: false);

        _ = await Assert
            .That(attributes.Select(attribute => attribute.GetType().Name))
            .Contains("IntegrationTestAttribute");
    }

    [Test]
    public async Task Assembly_UnderTest_IsLoadable()
    {
        var assembly = typeof(MutationCoverageAnalyzer).Assembly;

        _ = await Assert.That(assembly.GetName().Name).IsEqualTo("NetEvolve.FrameShift");
    }
}
