namespace NetEvolve.FrameShift.Execution.Tests.Integration;

/// <summary>
/// Exercises every parsing branch of <see cref="ExecutionCliOptions" />: the four required flags, the
/// optional timeout, and every way the command line can be malformed.
/// </summary>
public class ExecutionCliOptionsTests
{
    private static string[] ValidArgs(string sourcePath, string testOutputDirectory) =>
        [
            "--test-output",
            testOutputDirectory,
            "--production-dll",
            "Production.dll",
            "--test-dll",
            "TestHost.dll",
            "--source",
            sourcePath,
        ];

    [Test]
    public async Task TryParse_EveryRequiredFlagPresent_SucceedsWithDefaultTimeout()
    {
        var directory = Directory.CreateTempSubdirectory("frameshift-cli-options-");
        var sourcePath = Path.Combine(directory.FullName, "Source.cs");

        try
        {
            await File.WriteAllTextAsync(sourcePath, "class C;").ConfigureAwait(false);

            var parsed = ExecutionCliOptions.TryParse(
                ValidArgs(sourcePath, directory.FullName),
                out var options,
                out var error
            );

            using (Assert.Multiple())
            {
                _ = await Assert.That(parsed).IsTrue();
                _ = await Assert.That(error).IsNull();
                _ = await Assert.That(options).IsNotNull();
                _ = await Assert.That(options!.TestOutputDirectory).IsEqualTo(directory.FullName);
                _ = await Assert.That(options.ProductionAssemblyFileName).IsEqualTo("Production.dll");
                _ = await Assert.That(options.TestAssemblyFileName).IsEqualTo("TestHost.dll");
                _ = await Assert.That(options.SourceFilePaths).IsEquivalentTo([sourcePath]);
                _ = await Assert.That(options.Timeout).IsEqualTo(TimeSpan.FromSeconds(60));
            }
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task TryParse_ExplicitTimeout_OverridesTheDefault()
    {
        var directory = Directory.CreateTempSubdirectory("frameshift-cli-options-");
        var sourcePath = Path.Combine(directory.FullName, "Source.cs");

        try
        {
            await File.WriteAllTextAsync(sourcePath, "class C;").ConfigureAwait(false);

            var parsed = ExecutionCliOptions.TryParse(
                [.. ValidArgs(sourcePath, directory.FullName), "--timeout-seconds", "15"],
                out var options,
                out var error
            );

            using (Assert.Multiple())
            {
                _ = await Assert.That(parsed).IsTrue();
                _ = await Assert.That(error).IsNull();
                _ = await Assert.That(options!.Timeout).IsEqualTo(TimeSpan.FromSeconds(15));
            }
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task TryParse_RepeatedSourceFlag_CollectsEveryPath()
    {
        var directory = Directory.CreateTempSubdirectory("frameshift-cli-options-");
        var firstSourcePath = Path.Combine(directory.FullName, "First.cs");
        var secondSourcePath = Path.Combine(directory.FullName, "Second.cs");

        try
        {
            await File.WriteAllTextAsync(firstSourcePath, "class First;").ConfigureAwait(false);
            await File.WriteAllTextAsync(secondSourcePath, "class Second;").ConfigureAwait(false);

            var parsed = ExecutionCliOptions.TryParse(
                [
                    "--test-output",
                    directory.FullName,
                    "--production-dll",
                    "Production.dll",
                    "--test-dll",
                    "TestHost.dll",
                    "--source",
                    firstSourcePath,
                    "--source",
                    secondSourcePath,
                ],
                out var options,
                out var error
            );

            using (Assert.Multiple())
            {
                _ = await Assert.That(parsed).IsTrue();
                _ = await Assert.That(error).IsNull();
                _ = await Assert.That(options!.SourceFilePaths).IsEquivalentTo([firstSourcePath, secondSourcePath]);
            }
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Test]
    [Arguments("--test-output")]
    [Arguments("--production-dll")]
    [Arguments("--test-dll")]
    [Arguments("--source")]
    [Arguments("--timeout-seconds")]
    [Arguments("--unknown-flag")]
    public async Task TryParse_FlagWithoutAValue_Fails(string flag)
    {
        var parsed = ExecutionCliOptions.TryParse([flag], out var options, out var error);

        using (Assert.Multiple())
        {
            _ = await Assert.That(parsed).IsFalse();
            _ = await Assert.That(options).IsNull();
            _ = await Assert.That(error).IsEqualTo($"The flag '{flag}' requires a value.");
        }
    }

    [Test]
    public async Task TryParse_UnrecognisedFlag_Fails()
    {
        var parsed = ExecutionCliOptions.TryParse(["--unrecognised", "value"], out var options, out var error);

        using (Assert.Multiple())
        {
            _ = await Assert.That(parsed).IsFalse();
            _ = await Assert.That(options).IsNull();
            _ = await Assert.That(error).IsEqualTo("Unrecognised argument '--unrecognised'.");
        }
    }

    [Test]
    [Arguments("0")]
    [Arguments("-1")]
    [Arguments("not-a-number")]
    public async Task TryParse_InvalidTimeoutValue_Fails(string timeoutValue)
    {
        var parsed = ExecutionCliOptions.TryParse(["--timeout-seconds", timeoutValue], out var options, out var error);

        using (Assert.Multiple())
        {
            _ = await Assert.That(parsed).IsFalse();
            _ = await Assert.That(options).IsNull();
            _ = await Assert.That(error).IsEqualTo($"'{timeoutValue}' is not a positive number of seconds.");
        }
    }

    [Test]
    public async Task TryParse_MissingTestOutput_Fails()
    {
        var parsed = ExecutionCliOptions.TryParse(
            ["--production-dll", "Production.dll", "--test-dll", "TestHost.dll", "--source", "Source.cs"],
            out var options,
            out var error
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(parsed).IsFalse();
            _ = await Assert.That(options).IsNull();
            _ = await Assert.That(error).IsEqualTo("Missing required argument '--test-output'.");
        }
    }

    [Test]
    public async Task TryParse_MissingProductionAssembly_Fails()
    {
        var directory = Directory.CreateTempSubdirectory("frameshift-cli-options-");

        try
        {
            var parsed = ExecutionCliOptions.TryParse(
                ["--test-output", directory.FullName, "--test-dll", "TestHost.dll", "--source", "Source.cs"],
                out var options,
                out var error
            );

            using (Assert.Multiple())
            {
                _ = await Assert.That(parsed).IsFalse();
                _ = await Assert.That(options).IsNull();
                _ = await Assert.That(error).IsEqualTo("Missing required argument '--production-dll'.");
            }
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task TryParse_MissingTestAssembly_Fails()
    {
        var directory = Directory.CreateTempSubdirectory("frameshift-cli-options-");

        try
        {
            var parsed = ExecutionCliOptions.TryParse(
                ["--test-output", directory.FullName, "--production-dll", "Production.dll", "--source", "Source.cs"],
                out var options,
                out var error
            );

            using (Assert.Multiple())
            {
                _ = await Assert.That(parsed).IsFalse();
                _ = await Assert.That(options).IsNull();
                _ = await Assert.That(error).IsEqualTo("Missing required argument '--test-dll'.");
            }
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task TryParse_NoSourceFlag_Fails()
    {
        var directory = Directory.CreateTempSubdirectory("frameshift-cli-options-");

        try
        {
            var parsed = ExecutionCliOptions.TryParse(
                [
                    "--test-output",
                    directory.FullName,
                    "--production-dll",
                    "Production.dll",
                    "--test-dll",
                    "TestHost.dll",
                ],
                out var options,
                out var error
            );

            using (Assert.Multiple())
            {
                _ = await Assert.That(parsed).IsFalse();
                _ = await Assert.That(options).IsNull();
                _ = await Assert.That(error).IsEqualTo("At least one '--source' is required.");
            }
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task TryParse_TestOutputDirectoryDoesNotExist_Fails()
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), "frameshift-cli-options-missing-" + Guid.NewGuid());

        var parsed = ExecutionCliOptions.TryParse(
            [
                "--test-output",
                missingDirectory,
                "--production-dll",
                "Production.dll",
                "--test-dll",
                "TestHost.dll",
                "--source",
                "Source.cs",
            ],
            out var options,
            out var error
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(parsed).IsFalse();
            _ = await Assert.That(options).IsNull();
            _ = await Assert.That(error).IsEqualTo($"The test output directory '{missingDirectory}' does not exist.");
        }
    }

    [Test]
    public async Task TryParse_SourceFileDoesNotExist_Fails()
    {
        var directory = Directory.CreateTempSubdirectory("frameshift-cli-options-");
        var missingSourcePath = Path.Combine(directory.FullName, "Missing.cs");

        try
        {
            var parsed = ExecutionCliOptions.TryParse(
                ValidArgs(missingSourcePath, directory.FullName),
                out var options,
                out var error
            );

            using (Assert.Multiple())
            {
                _ = await Assert.That(parsed).IsFalse();
                _ = await Assert.That(options).IsNull();
                _ = await Assert.That(error).IsEqualTo($"The source file '{missingSourcePath}' does not exist.");
            }
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task Usage_MatchesTheSnapshot() => await Verify(ExecutionCliOptions.Usage).ConfigureAwait(false);
}
