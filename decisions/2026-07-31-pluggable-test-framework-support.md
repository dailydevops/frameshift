---
authors:
  - Martin Stühmer

applyTo:
  - "src/NetEvolve.FrameShift/TestSurface/**/*.cs"
  - "src/NetEvolve.FrameShift/Analyzers/**/*.cs"

created: 2026-07-31

lastModified: 2026-07-31

state: accepted

instructions: |
  Test framework support is a plug-in seam: one ITestFrameworkProbe, one ITestMethodRecognizer and one
  thin analyzer per framework VERSION — not per framework — registered in a fixed order in
  TestFrameworkProbeRegistry, with everything after detection framework-neutral. The registered order is
  TUnit, xUnit v2, xUnit v3, NUnit, MSTest. Detection is fail-closed — a probe that returns null from
  TryCreateRecognizer shuts its analysis down completely instead of guessing, so a compilation is never
  judged by a framework it does not use.
---

# Decision: Pluggable test framework support with fail-closed detection

FrameShift supports more than one test framework through a single, narrow plug-in seam. Only the question
"what marks a method as a test method?" is framework-specific; everything after that answer — discovery,
the reachability walk inside the test assembly, the collected test surface, the manifest and the
diagnostics — is shared. A framework that is not detected does not fall back to a guess: the analysis for
that framework switches itself off entirely and reports nothing at all.

**The unit of the seam is a framework _version_, not a framework.** A probe answers "does this compilation
use *this* version of *this* framework", and two probes may legitimately match the same compilation and
even describe the same test methods. xUnit is the case that forced the distinction to be explicit: v2 and
v3 are separate plug-ins, `xUnit v2` and `xUnit v3`, with their own probe, recogniser and analyzer.

## Context

TUnit was the first framework FrameShift targeted, but nothing about the test-side analysis is actually
tied to it. The analysis discovers test methods, walks the code reachable from them inside the test
assembly, records the production members those paths reference, and compares that surface against the
test-surface manifest on disk. Only the very first step depends on the framework in use.

Each framework marks its tests differently, and the differences are not superficial:

| Framework version | Declaring assembly | What marks a test method | Shape of the rule |
| --- | --- | --- | --- |
| TUnit | `TUnit.Core` | `TUnit.Core.TestAttribute` | The attribute is sealed, so specialised attributes derive from a *different* framework type named `TestAttribute` |
| xUnit v2 | `xunit.core` | `Xunit.FactAttribute` | A base/derived chain — `TheoryAttribute` and custom test attributes derive from `FactAttribute` |
| xUnit v3 | `xunit.v3.core` | `Xunit.FactAttribute` | The same chain, under the same metadata name, from a different assembly |
| NUnit | `nunit.framework` | `TestAttribute`, `TestCaseAttribute`, `TestCaseSourceAttribute` | Three *siblings*, not a base type and its derivations |
| MSTest | `MSTest.TestFramework`, `Microsoft.VisualStudio.TestPlatform.TestFramework` | `TestMethodAttribute` | Unsealed base type; `DataTestMethodAttribute` and user-defined attributes derive from it |

Two further realities shaped the design:

- **A metadata name does not identify a framework version, and sometimes identifies nothing at all.**
  MSTest ships `Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute` in
  `MSTest.TestFramework` since version 4 and in `Microsoft.VisualStudio.TestPlatform.TestFramework` in
  version 3 and earlier; the namespace did not change with the rename, so both identities have to be
  accepted. xUnit is worse: v2 and v3 declare a type of the *identical* metadata name
  `Xunit.FactAttribute`, in `xunit.core` and in `xunit.v3.core`. `Compilation.GetTypeByMetadataName`
  resolves a name only when exactly one referenced assembly declares it and answers `null` on an
  ambiguity, so in a compilation referencing both xUnit versions that name resolves to nothing.
- **Attribute names are not unique.** `Microsoft.VisualStudio.TestTools.UnitTesting` is an ordinary
  namespace that any project may declare a look-alike attribute in, and `TestAttribute` or `FactAttribute`
  are plausible names for something entirely unrelated.

The consequence of a wrong answer is asymmetric. A false positive — treating a project as an xUnit project
because a same-named attribute happens to be present — makes FrameShift judge a compilation whose real
tests it cannot see, and every FSH0001 it then reports is wrong. Detection therefore has to be able to say
"not mine" and be believed.

## Decision

**Test framework support is a plug-in seam consisting of exactly three small pieces per framework
version, plus one registry line.**

1. **`ITestFrameworkProbe`** — decides whether a compilation uses this framework version. Its single method
   `ITestMethodRecognizer? TryCreateRecognizer(Compilation compilation)` returns a recogniser when the
   framework version is present and `null` when it is not, and its `FrameworkName` is the display name of
   that version — `"TUnit"`, `"xUnit v2"`, `"xUnit v3"`, `"NUnit"`, `"MSTest"`. The probe is stateless and
   exposed as a shared `Instance`, so it is safe to use from concurrent analyzer callbacks.
2. **`ITestMethodRecognizer`** — answers `bool IsTestMethod(IMethodSymbol method)` for one compilation. It
   is created per compilation by the probe, so the symbols the framework is identified by are resolved
   exactly once. Implementations are immutable and thread-safe.
3. **A thin `DiagnosticAnalyzer`** — `TUnitTestSurfaceAnalyzer`, `XunitV2TestSurfaceAnalyzer`,
   `XunitV3TestSurfaceAnalyzer`, `NUnitTestSurfaceAnalyzer`, `MSTestTestSurfaceAnalyzer`. Each registers a
   compilation action whose whole body is
   `TestSurfaceAnalysis.Execute(context, <FrameworkVersion>TestFrameworkProbe.Instance);`.

With `MutationCoverageAnalyzer` on the production side that makes **six `DiagnosticAnalyzer` types** in the
package: five test-side adapters and one production-side analyzer.

Everything else is framework-neutral. `TestMethodDiscovery`, `TestSurfaceCollector`,
`TestSurfaceManifest`, its reader and writer, and `TestSurfaceAnalysis` itself take an
`ITestMethodRecognizer` and never learn which framework produced it. `TestSurfaceCollector` exposes
exactly two entry points, `Collect(Compilation, ITestMethodRecognizer, CancellationToken)` and
`FindTestsWithoutProductionReference(Compilation, ITestMethodRecognizer, CancellationToken)`, and both
demand a recogniser. There is no overload that collects a surface without being told what a test is, so
no code path can silently assume a framework — the choice is always made by a probe and always visible in
the call. A sixth framework version therefore adds nothing to the collector.

### One probe per version, resolving its type inside its own assembly

Splitting xUnit into two plug-ins is not a rename. It changes how the type is found, and that is the point
of the split.

A probe that covers two major versions at once has only the compilation to resolve its attribute in, and
`Compilation.GetTypeByMetadataName("Xunit.FactAttribute")` answers `null` precisely when both versions are
referenced, because the name is then ambiguous. The combined probe therefore had to fall back to matching
an attribute by its **simple name plus its declaring assembly**, which is a weaker rule: it accepts
anything named `FactAttribute` from anything named like an xUnit assembly, and it cannot tell the two
versions apart at all.

A probe that owns exactly one version does not have that problem. It locates *its own* assembly among the
referenced ones — `xunit.core` for v2, `xunit.v3.core` for v3 — and resolves `Xunit.FactAttribute` through
`IAssemblySymbol.GetTypeByMetadataName` **inside that assembly**. The lookup cannot be ambiguous, because
an assembly declares a metadata name at most once. It answers with the v2 type or the v3 type, never with
"one of the two", and it keeps answering when the other version is referenced as well. The simple-name
fallback that the combined probe needed is therefore gone from both xUnit recognisers: they match strictly
by symbol, walking the base chain against the type their probe resolved. The remaining recognisers keep
their name-plus-assembly rule, which they need for reasons of their own — TUnit because its attribute is
sealed and specialisations derive from a sibling type, MSTest because its own two assemblies can make the
metadata name ambiguous within one compilation.

### Every probe still accepts either signal

Detection stays fail-open and uniform across all five probes, and that uniformity is deliberate. A probe
returns a recogniser when its well-known attribute type resolves **or** when the compilation references an
assembly belonging to that framework version; it returns `null` only when neither holds.

No probe may demand both signals, because a resolved type is not something detection can rely on. For
MSTest the metadata name can be ambiguous inside a single compilation, and any project may declare a type
under a framework's exact full name. A probe that insisted on a resolved type would hand back `null` there
and shut its analysis down completely on a project that unmistakably uses the framework. The assembly
reference is the signal that survives, so it has to be sufficient on its own — and a recogniser has to
tolerate a `null` attribute type, because it may have been woken by the assembly reference alone.

The recogniser is where the remaining strictness sits, and it is the right place for it, because it
judges an individual attribute instead of a whole compilation. A recogniser accepts an attribute when the
attribute type — or any type in its base chain — is the resolved framework attribute type; the TUnit,
NUnit and MSTest recognisers additionally accept an attribute that carries the framework's simple
attribute name *and* is declared in an assembly belonging to the framework. The name alone is never
sufficient. That is what keeps a hand-written look-alike in a same-named namespace —
`Microsoft.VisualStudio.TestTools.UnitTesting` is an ordinary namespace any project may declare — from
marking a method as a test: it fails the assembly half of the name-based rule and is not in the base chain
of the real attribute. The consequence of the fail-open probes is only that the analysis wakes up,
discovers no test method, and shuts down under the fail-closed rule below.

The assembly-name comparison is ordinal for TUnit and case-insensitive for xUnit, NUnit and MSTest, whose
names are long, dotted and reproduced by hand often enough that insisting on exact casing would only ever
produce a false negative.

`TestClassAttribute` and `TestFixtureAttribute` are deliberately not part of any rule: they mark the
declaring type, not the method, so only an attribute on the method itself makes a method a test.

### The registry has a fixed, documented order

`TestFrameworkProbeRegistry` is the single place the supported framework versions are listed, in the order
**TUnit, xUnit v2, xUnit v3, NUnit, MSTest**. That order is part of the contract, not cosmetic — see the
coordination rule below — and a test pins it, so changing it is a deliberate act.
`Matching(Compilation)` returns the probes that recognise a compilation, in that same order, and is empty
when no supported framework is present. It can return two probes for one compilation, and for a project
that references both xUnit versions it does.

### The seam is fail-closed

`TestSurfaceAnalysis.Execute` shuts down as early and as completely as it can. It reports nothing
whatsoever unless the probe returns a recogniser **and** at least one test method is actually discovered.
Discovering no test is treated as "this analysis has no authority over this compilation", not as "this
compilation has no tests" — because a project whose tests cannot be seen must never be judged, as every
judgement would be a false one. In that state not even a diagnostic about the manifest is produced.

That contract is what makes five analyzers side by side harmless: each stays silent on the compilations
that are not its own.

### Coordination rule for a project using several framework versions

A test project may legitimately reference more than one framework, or two versions of one framework, and
then more than one analyzer is awake on the same compilation — *awake* meaning the probe recognised the
framework version and at least one of its test methods was found. There is still exactly one test-surface
manifest, so:

- **FSH0004** (test method references no production member) is reported **per framework version**. It names
  an individual test method, and each analyzer sees its own set of them. Nothing in the seam forbids two
  adapters from describing the same test method, and a method both recognise is named by both.
- **FSH0003** (manifest missing, malformed or stale) is reported **exactly once**, by the first awake
  framework version in registry order. Every other awake one skips the manifest entirely, so the same
  problem is not reported once per referenced framework. In a project on xUnit v2 and v3 at once, v2 leads,
  because it comes first in the registry. A probe that is not registered at all leads by itself, because
  nothing else would look at the manifest on its behalf and staying silent would be worse.
- The manifest is compared against the **union of the test surfaces of all awake framework versions**, never
  against the leading one's surface alone. A mixed project records the tests of every framework in
  its single manifest, so judging it from one framework's view would report it stale purely because the
  other framework's tests are invisible from there.

### Adding a sixth framework version

Three small files and one line — and "version" is the unit: a new major version of an already supported
framework costs exactly the same as a framework nobody has heard of.

1. `src/NetEvolve.FrameShift/TestSurface/<FrameworkVersion>TestFrameworkProbe.cs` — the display name, the
   attribute metadata name(s), the accepted assembly identities, and a `TryCreateRecognizer` that
   returns `null` when neither the attribute type resolves nor a framework assembly is referenced. Follow
   the existing five and accept either signal; do not require both. Prefer resolving the attribute type
   inside the version's own assembly, through `IAssemblySymbol.GetTypeByMetadataName`, as both xUnit probes
   do: that lookup cannot be made ambiguous by another version of the same framework, and it is what lets a
   recogniser match strictly by symbol. The recogniser, not the probe, is where a look-alike attribute is
   rejected.
2. `src/NetEvolve.FrameShift/TestSurface/<FrameworkVersion>TestMethodRecognizer.cs` — the attribute rule for
   that version, walking the base chain. Add the name-plus-assembly fallback only if the version's type
   genuinely cannot be resolved exactly. The recogniser has to tolerate a `null` attribute type, because the
   probe may have woken it on the assembly reference alone.
3. `src/NetEvolve.FrameShift/Analyzers/<FrameworkVersion>TestSurfaceAnalyzer.cs` — a `DiagnosticAnalyzer`
   declaring FSH0003 and FSH0004 and delegating to `TestSurfaceAnalysis.Execute`.
4. One entry in the registration region of `TestFrameworkProbeRegistry`. Append it at the end unless the
   new entry is a version of an already registered framework, which belongs next to its siblings in version
   order; either way, moving an existing entry changes the FSH0003 leadership in mixed projects and is a
   behaviour change.

No framework-neutral code, no diagnostic descriptor, no MSBuild asset and no manifest format change is
involved.

## Consequences

### How the adapters are covered

The test projects reference every supported framework as compile-time-only metadata, so each probe and
recogniser is exercised against the real attribute types. One package does not cover the whole target-framework
matrix: `xunit.v3.core` ships no assets for `net6.0` and `net7.0`, so `FRAMESHIFT_XUNIT_V3` is defined on every
target framework except those two and every reference to an xUnit v3 type sits behind it.

That is a second, practical benefit of the split. While one probe covered both versions, the whole xUnit
adapter had to live with the narrowest package in the pair. Now the guard is only around the v3 side:

| Adapter | Covered on |
| --- | --- |
| TUnit, xUnit v2, NUnit, MSTest | every target framework of the matrix |
| xUnit v3 | every target framework except `net6.0` and `net7.0` |

Both gates — a `Release` build free of errors and warnings, and a green test run — apply to every target
framework of the matrix; a test that genuinely cannot apply to one is guarded by a conditional compilation
symbol rather than dropped.

**Positive**

- Framework support is additive. A new framework version cannot break an existing one, because the only
  shared thing it touches is one line of a list.
- **A version is identified exactly.** Because each xUnit probe resolves `Xunit.FactAttribute` inside its own
  assembly, a compilation referencing both versions is described by two adapters that each see their own
  attribute type, instead of by one adapter that had to guess from a simple name.
- **The v2 adapter is no longer held back by the v3 package.** Splitting the plug-in split the conditional
  compilation with it, so only the v3 side is narrowed to the target frameworks its package supports.
- The framework-neutral analysis is exercised identically by every framework, so a fix in discovery,
  collection or manifest handling benefits all of them at once.
- The fail-closed contract makes a false positive structurally hard: the failure mode of a detection bug is
  "FrameShift reports nothing", which a developer notices as missing output rather than acting on wrong
  warnings.
- Per-framework analyzers give per-framework diagnostics for free. FSH0004 naming the framework's own test
  methods needs no extra plumbing.
- Each recogniser is created once per compilation and is immutable, so symbol resolution is not repeated
  per method and the analyzers remain safe under `EnableConcurrentExecution`.

**Negative**

- **One more analyzer instance is loaded per framework version.** All five test-side analyzers are loaded
  into every consuming build, and each one runs a compilation action even on projects it will immediately
  shut down on. The cost of that shutdown is one probe call, but it is not zero, and splitting xUnit added
  one such analyzer to every build in the world for the benefit of the projects that reference both
  versions.
- **The registry order is now observable behaviour.** Which framework version reports FSH0003 in a mixed
  project follows from the list order, tests pin it (`TestFrameworkProbeRegistryTests`,
  `MixedFrameworkTestSurfaceTests`), and reordering the list is a behaviour change rather than a
  refactoring.
- **A referenced-but-unused framework wakes its probe.** Because either signal is enough, a project that
  references a framework package without writing a single test of it gets a recogniser, and the framework's
  analyzer runs a discovery pass before finding nothing. The fail-closed rule makes that harmless — no test
  discovered means no diagnostic, including none about the manifest — but the discovery walk is paid for.
  The repository's own test projects are exactly this case: they reference every supported framework as
  compile-time metadata and run on TUnit.
- **A framework's attribute or assembly rename between major versions is a standing maintenance
  obligation.** MSTest 4 renaming its framework assembly already forced the probe to accept two identities,
  and a new xUnit major version means a new plug-in rather than a widened one. A rename that goes unnoticed
  is a silent false negative, which the fail-closed design turns into "no diagnostics" rather than a visible
  error.
- **`FindAwakeFrameworks` probes every registered framework version on every awake compilation** and
  discovers its test methods to decide whether it is awake, so the mixed-project coordination costs a full
  probe walk in the one case where it matters — and that walk grew by one entry with the split.
- Five probes plus five recognisers plus five analyzers is more types than a single detector would need,
  and the per-version attribute rules necessarily repeat a similar base-chain walk. The two xUnit plug-ins
  are near-identical twins, and a fix to one has to be carried to the other by hand.

## Alternatives Considered

**A single analyzer consulting all probes.** One `TestSurfaceAnalyzer` that asks the registry which
frameworks match and handles them all in one run. Fewer types, no duplicated analyzer boilerplate, and no
per-framework compilation action. Rejected because the two properties that matter most become implicit:
the per-framework diagnostic (FSH0004 for exactly this framework's tests) turns into a loop inside one
analyzer, and the shut-down contract stops being a visible property of an analyzer — "this analyzer
reports nothing on a non-MSTest project" is checkable and testable in a way that "this loop iteration
contributed nothing" is not. The coordination rule for mixed projects would also become invisible instead
of being an explicit, documented registry order.

**One probe covering both xUnit major versions.** This is what the seam started with: a single
`xUnit` plug-in whose probe accepted any referenced assembly named like xUnit and whose recogniser fell back
to matching an attribute by simple name plus declaring assembly. Fewer types, one analyzer less in every
build, and no near-duplicate pair to keep in step. Rejected because the fallback was not an implementation
detail but a loss of information: the probe could not resolve `Xunit.FactAttribute` at all when both versions
were referenced, so the one case the fallback existed for was also the case in which the adapter could no
longer say *which* version a test belonged to. It also tied the whole adapter's test coverage to the narrower
of the two packages, since `xunit.v3.core` has no `net6.0` or `net7.0` assets. Two probes resolving their type
inside their own assembly are exact, need no fallback, and are conditionally compiled independently.

**Detecting tests by attribute name alone, without an assembly guard.** Matching any attribute named
`TestAttribute`, `FactAttribute` or `TestMethodAttribute` would be shorter and would need no assembly
prefixes to maintain across renames. Rejected: a same-named attribute from an unrelated namespace would
match. Names like `TestAttribute` are not owned by anyone, and
`Microsoft.VisualStudio.TestTools.UnitTesting` can be declared by any project. A hand-written look-alike
must never turn a project into a test project of a framework it does not use.

**Requiring the user to configure the framework through an MSBuild property.** A property such as a
framework name handed to the analyzer through `AnalyzerConfigOptions` would remove all detection code.
Rejected: detection from the compilation's referenced assemblies and resolvable attribute types is
reliable and needs no user action, whereas a required property is one more thing to get wrong — misspelled,
forgotten in a new project, or left behind after a framework migration — and a wrong value would silently
either disable the analysis or point it at the wrong framework. Detection also handles the mixed-framework
project correctly without asking the user to enumerate anything.

## Related Decisions

- [Two-Pass Mutation Analysis](./2026-07-31-two-pass-mutation-analysis.md) — the plug-in seam described
  here lives entirely in the first of the two passes; it decides what the test side considers a test, and
  therefore what ends up in the manifest the production side consumes.