namespace NetEvolve.FrameShift.Execution.Tests.Unit;

/// <summary>
/// Pins the constructor-to-property contract of <see cref="TestHostRunResult" />: every value passed in
/// comes back out unchanged, which is all this plain result carrier promises.
/// </summary>
public class TestHostRunResultTests
{
    [Test]
    public async Task Constructor_ExitedProcess_ExposesEveryValueUnchanged()
    {
        var result = new TestHostRunResult(exitCode: 1, timedOut: false, "out", "err");

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.ExitCode).IsEqualTo(1);
            _ = await Assert.That(result.TimedOut).IsFalse();
            _ = await Assert.That(result.StandardOutput).IsEqualTo("out");
            _ = await Assert.That(result.StandardError).IsEqualTo("err");
        }
    }

    [Test]
    public async Task Constructor_TimedOutProcess_ExposesANullExitCode()
    {
        var result = new TestHostRunResult(exitCode: null, timedOut: true, string.Empty, string.Empty);

        using (Assert.Multiple())
        {
            _ = await Assert.That(result.ExitCode).IsNull();
            _ = await Assert.That(result.TimedOut).IsTrue();
        }
    }
}
