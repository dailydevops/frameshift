# NetEvolve.FrameShift.Execution

[![NuGet Version](https://img.shields.io/nuget/v/NetEvolve.FrameShift.Execution.svg)](https://www.nuget.org/packages/NetEvolve.FrameShift.Execution/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/NetEvolve.FrameShift.Execution.svg)](https://www.nuget.org/packages/NetEvolve.FrameShift.Execution/)
[![License](https://img.shields.io/github/license/dailydevops/frameshift.svg)](https://github.com/dailydevops/frameshift/blob/main/LICENSE)

`frameshift` is a .NET tool that runs execution-based mutation verification: it recompiles a
production source file with one mutation applied at a time, runs the already-built test project
against each mutant, and reports whether the test suite actually kills it. Where the
[`NetEvolve.FrameShift`](https://www.nuget.org/packages/NetEvolve.FrameShift/) analyzer answers
"is this mutation point reachable from any test" at build time without executing anything, this
tool answers the stronger question - "does the test suite actually fail" - by running the tests
for real, against every generated mutant.

## Features

- Real execution, not static reachability - every mutant is recompiled and its already-built test
  project's own test host is run against it as a real subprocess, so the verdict is "did a test
  actually fail", not an approximation.
- Test-framework agnostic - the test host is run exactly as its own runner would, and only its
  process exit code is read, so any framework the test project already uses works unmodified.
- No project re-evaluation - only the production source files given on the command line are
  recompiled, referencing whatever already sits in the test project's build output, so a full run
  needs no MSBuild or SDK resolution step of its own.
- Reuses the same mutation operators as the [`NetEvolve.FrameShift`](https://www.nuget.org/packages/NetEvolve.FrameShift/)
  analyzer, so a mutant produced here is the same mutant that analyzer would have reported as a
  gap.
- A mutant that fails to recompile is reported as its own outcome (`Build failed`) instead of being
  silently skipped or counted as killed.
- A configurable per-mutant timeout reports a hung test host as `Timed out` instead of blocking the
  run indefinitely.
- Aggregates every mutant's verdict into a single mutation score, alongside the per-mutant
  breakdown that produced it.

## Installation

### .NET CLI (global tool)

```bash
dotnet tool install --global NetEvolve.FrameShift.Execution
```

### .NET CLI (local tool)

```bash
dotnet tool install --local NetEvolve.FrameShift.Execution
```

## Quick Start

1. Build the test project whose test host should run against every mutant, for example
   `dotnet build tests/Calculator.Tests`.
2. Run `frameshift` against that build output, naming the production source file to mutate:

   ```bash
   frameshift \
     --test-output tests/Calculator.Tests/bin/Debug/net10.0 \
     --production-dll Calculator.dll \
     --test-dll Calculator.Tests.dll \
     --source src/Calculator/Rates.cs
   ```

3. Read the per-mutant verdicts and the aggregated mutation score from the console output.

## Usage

The test project must already be built - `frameshift` recompiles only the production source files
it is given, against the test assembly and its dependencies already present in that build output.

```text
Usage: frameshift --test-output <dir> --production-dll <file.dll> --test-dll <file.dll> --source <file.cs> [--source <file.cs> ...] [--timeout-seconds <seconds>]

  --test-output         The build output directory of the test project (contains the test
                           assembly, the production assembly and every dependency of both).
  --production-dll       The file name of the production assembly inside that directory,
                           e.g. MyApp.dll. Recompiled fresh from --source; the copy already in
                           the output directory is never read.
  --test-dll             The file name of the test assembly inside that directory.
  --source              A production source file to compile and generate mutations from.
                           Repeatable.
  --timeout-seconds    How long to wait for the test host of a single mutant before it is
                           killed and the mutant is reported as timed out. Defaults to 60.
```

### Basic Example

Run after `dotnet build` has produced `bin/Debug/net10.0` for the test project:

```bash
frameshift \
  --test-output tests/Calculator.Tests/bin/Debug/net10.0 \
  --production-dll Calculator.dll \
  --test-dll Calculator.Tests.dll \
  --source src/Calculator/Rates.cs
```

### Advanced Example

Mutating several source files in one run and lowering the per-mutant timeout for a fast-failing
test suite:

```bash
frameshift \
  --test-output tests/Calculator.Tests/bin/Debug/net10.0 \
  --production-dll Calculator.dll \
  --test-dll Calculator.Tests.dll \
  --source src/Calculator/Rates.cs \
  --source src/Calculator/Discounts.cs \
  --timeout-seconds 15
```

The exit code answers "did the run complete", not "did the code pass mutation testing": `0` means
a score was produced, whatever it is, and a non-zero code means the invocation itself was wrong or
the run was interrupted. Gating a build on a minimum mutation score is a policy decision this tool
deliberately does not make on a caller's behalf.

## Requirements

- .NET 10.0 or higher - the tool itself targets `net10.0` and needs a collectible, unloadable
  `AssemblyLoadContext` to isolate every mutant it runs.
- A test project already built with `dotnet build`, whose output directory contains the test
  assembly, the production assembly, and every dependency of both.

## Related Packages

- [**NetEvolve.FrameShift**](https://www.nuget.org/packages/NetEvolve.FrameShift/) - the Roslyn
  analyzer that reports mutation-testing gaps at build time, without executing a single test.

## Documentation

For complete documentation, please visit the [official documentation](https://github.com/dailydevops/frameshift/blob/main/README.md).

## Contributing

Contributions are welcome! Please read the [Contributing Guidelines](https://github.com/dailydevops/frameshift/blob/main/CONTRIBUTING.md) before submitting a pull request.

## Support

- **Issues**: Report bugs or request features on [GitHub Issues](https://github.com/dailydevops/frameshift/issues)
- **Documentation**: Read the full documentation at [https://github.com/dailydevops/frameshift](https://github.com/dailydevops/frameshift)

## License

This project is licensed under the MIT License - see the [LICENSE](https://github.com/dailydevops/frameshift/blob/main/LICENSE) file for details.

---

> [!NOTE]
> **Made with ❤️ by the NetEvolve Team**
> Visit us at [https://www.daily-devops.net](https://www.daily-devops.net) for more information about our services and solutions.
