# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repository is

`NetEvolve.FrameShift` is a Roslyn analyzer package that reports mutation-testing gaps at build time
without executing a single test. It rewrites the syntax tree of production code to generate mutants,
checks whether any discovered test can reach the mutated member, and reports surviving mutants as
ordinary compiler diagnostics. `NetEvolve.FrameShift.Execution` (`src/NetEvolve.FrameShift.Execution`)
is a companion .NET tool (`frameshift`) that goes further: it recompiles a production file with one
mutation applied, runs the already-built test project's own test host against it as a real subprocess,
and reports whether the suite actually kills it — reusing the same mutation operators as the analyzer.

Read the root `README.md` before making non-trivial changes — it documents the two-pass architecture,
the project layout, and the quality gates in full detail; this file only summarizes what a session
needs to get oriented and stay consistent.

## The two-pass design (core architecture)

The analysis runs in two passes because neither compilation alone knows enough:

- A **test compilation** references the production assembly as metadata only — it can name the
  production members its tests touch but owns no production syntax tree to mutate.
- A **production compilation** owns every syntax tree and the full call graph, but cannot see a
  single test.

**Pass 1 (test side)**: a test-framework-specific analyzer (`src/NetEvolve.FrameShift/Analyzers`,
one per framework version — TUnit, xUnit v2, xUnit v3, NUnit, MSTest) discovers test methods, walks
code reachable from them, and records every production member referenced. A source generator
(`Generation/`) serializes that list into a *test-surface manifest* and emits it as generated source;
the packaged MSBuild target turns it into `<ProjectName>.<TargetFramework>.frameshift` next to the
test project — a checked-in, diffable, line-based text file (`frameshift-test-surface/1` header, `T`
= test method, `R` = referenced member).

**Pass 2 (production side)**: `MutationCoverageAnalyzer` consumes the manifest via `AdditionalFiles`,
seeds the reachable set from the recorded member ids, closes it transitively over the production call
graph (`Reachability/`), walks every syntax tree, generates candidate mutants (`Mutations/`,
`Mutations/Operators/`), verifies each compiles via in-memory recompilation (`MutantCompiler`),
classifies mutants that cannot change observable behavior (`Equivalence/`), and reports what remains.

Diagnostics (`Diagnostics/` is the single source of truth for ids/messages, documented one-per-file
under `docs/rules/`):

- `FSH0001` — meaningful mutant in an unreachable member
- `FSH0002` — mutant without observable effect
- `FSH0003` — manifest missing, malformed or stale
- `FSH0004` — test method references no production member at all
- `FSH0005` — MSBuild warning: project opted in but has no manifest set up
- `FSH0006` — mutation point reached only by tests contributing a single input combination
- `FSH0007` — mutation point reachable but no discovered test asserts on its behavior

A project without a manifest stays silent (it hasn't opted in) except for `FSH0005`.

**Errors have a preferred direction.** A false "not trivial" or false "viable mutant" verdict costs
a dismissible diagnostic; a false "trivial" verdict silently hides a real testing gap. Every
classification only claims what it can prove. The analysis stays a pure compile-time operation — no
test execution, file system access, or network from the analyzers/generator; anything undecidable
from the compilation is a documented limitation, not a workaround.

## Project layout

- `src/NetEvolve.FrameShift` (`netstandard2.0`, single-target on purpose — only one assembly may sit
  in `analyzers/dotnet/cs`) — analyzers, generator, packaged MSBuild assets under `build/`.
- `src/NetEvolve.FrameShift.Execution` (`net10.0`, ships as a .NET tool) — the `frameshift` CLI:
  `MutationExecutionEngine`, `MutantAssemblyBuilder`, `IsolatedAssemblyRunner`/`ProcessTestHostRunner`
  (collectible `AssemblyLoadContext`), and report writers under `Reports/` (console/HTML/Markdown/
  GitHub Actions job summary).
- `tests/NetEvolve.Frameshift.Tests.Unit` and `tests/NetEvolve.FrameShift.Tests.Integration` — TUnit
  tests for the analyzer package (unit: operators, equivalence, reachability, manifest format, options;
  integration: whole compilations driven end to end, including the manifest round trip).
- `tests/NetEvolve.FrameShift.Execution.Tests.Unit` / `...Execution.Tests.Integration` — tests for the
  execution tool.
- `docs/rules/` — one document per diagnostic id, target of the analyzer help links.
- `templates/` — documentation templates (READMEs, ADRs).

Both `src/NetEvolve.FrameShift` test projects multi-target one shared list defined once in
`Directory.Build.props`: `net6.0`–`net10.0` everywhere, plus `net472`/`net48`/`net481` on Windows only
(no .NET Framework targeting packs elsewhere). `xunit.v3.core` has no assets for `net6.0`/`net7.0`;
every reference to an xUnit v3 type is guarded by `#if FRAMESHIFT_XUNIT_V3`. Keep such guards as
narrow as the reference itself — per-framework `Compile` excludes are not used in this repository.

## Commands

```bash
# Restore / build / test the whole solution
dotnet restore
dotnet build ./FrameShift.slnx -c Release   # must be free of errors AND warnings (TreatWarningsAsErrors + .editorconfig)
dotnet test ./FrameShift.slnx               # must be green on every target framework of every test project

# Run a single test project on a single target framework (iterate with this, not the full matrix)
dotnet test ./tests/NetEvolve.Frameshift.Tests.Unit/NetEvolve.Frameshift.Tests.Unit.csproj -f net10.0
dotnet test ./tests/NetEvolve.FrameShift.Tests.Integration/NetEvolve.FrameShift.Tests.Integration.csproj -f net481   # Windows only

# Format (also enforced by the build: reformats in Debug, checks in Release)
csharpier format .

# Coverage (measure each test project separately, one target framework at a time — same output file otherwise)
dotnet test ./tests/NetEvolve.Frameshift.Tests.Unit/NetEvolve.Frameshift.Tests.Unit.csproj -f net10.0 -- --coverage --coverage-output-format cobertura --coverage-output unit.cobertura.xml
```

Run the full-matrix build and test before opening a pull request — CI runs the same commands. `net472`-only
breaks are invisible on Linux/macOS; build on Windows or expect CI to catch them.

## Working conventions specific to this repo

- Package versions live only in `Directory.Packages.props`; never add a `Version` attribute to a
  `PackageReference` in a project file.
- Never add target frameworks to `src/NetEvolve.FrameShift` — Roslyn only loads one assembly from
  `analyzers/dotnet/cs`, so a second target produces something nothing can load. Multi-targeting
  belongs to the test projects.
- Snapshot tests (`Verify.TUnit`) live in `_snapshots/` mirroring the test that owns them. A failing
  snapshot writes a `.received.` file next to the accepted one — review the diff, and if correct,
  replace the accepted file and commit it together with the code change. Never accept a snapshot
  you haven't read.
- Don't leave repository-wide config (`.editorconfig`, `Directory.Build.props`,
  `Directory.Packages.props` structure) unchanged unless explicitly asked to touch it.
- Commit messages and PR summaries: list the tests run and their outcome; avoid drive-by reformatting
  of unrelated code.
