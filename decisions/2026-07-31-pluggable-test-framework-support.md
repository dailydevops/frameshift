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
  thin analyzer per framework, registered in a fixed order in TestFrameworkProbeRegistry, with everything
  after detection framework-neutral. Detection is fail-closed — a probe that returns null from
  TryCreateRecognizer shuts its analysis down completely instead of guessing, so a compilation is never
  judged by a framework it does not use.
---

# Decision: Pluggable test framework support with fail-closed detection

FrameShift supports more than one test framework through a single, narrow plug-in seam. Only the question
"what marks a method as a test method?" is framework-specific; everything after that answer — discovery,
the reachability walk inside the test assembly, the collected test surface, the manifest and the
diagnostics — is shared. A framework that is not detected does not fall back to a guess: the analysis for
that framework switches itself off entirely and reports nothing at all.

## Context

TUnit was the first framework FrameShift targeted, but nothing about the test-side analysis is actually
tied to it. The analysis discovers test methods, walks the code reachable from them inside the test
assembly, records the production members those paths reference, and compares that surface against the
test-surface manifest on disk. Only the very first step depends on the framework in use.

Each framework marks its tests differently, and the differences are not superficial:

| Framework | What marks a test method | Shape of the rule |
| --- | --- | --- |
| TUnit | `TUnit.Core.TestAttribute` | The attribute is sealed, so specialised attributes derive from a *different* framework type named `TestAttribute` |
| xUnit | `Xunit.FactAttribute` | A base/derived chain — `TheoryAttribute` and custom test attributes derive from `FactAttribute` |
| NUnit | `TestAttribute`, `TestCaseAttribute`, `TestCaseSourceAttribute` | Three *siblings*, not a base type and its derivations |
| MSTest | `TestMethodAttribute` | Unsealed base type; `DataTestMethodAttribute` and user-defined attributes derive from it |

Two further realities shaped the design:

- **Assembly identities move between major versions.** MSTest ships
  `Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute` in `MSTest.TestFramework` since
  version 4 and in `Microsoft.VisualStudio.TestPlatform.TestFramework` in version 3 and earlier; the
  namespace did not change with the rename, so both identities have to be accepted. xUnit v2 and v3 ship a
  type of the identical metadata name `Xunit.FactAttribute` in `xunit.core` and `xunit.v3.core`, so a
  compilation referencing both cannot resolve that name at all and needs a name-plus-assembly fallback.
- **Attribute names are not unique.** `Microsoft.VisualStudio.TestTools.UnitTesting` is an ordinary
  namespace that any project may declare a look-alike attribute in, and `TestAttribute` or `FactAttribute`
  are plausible names for something entirely unrelated.

The consequence of a wrong answer is asymmetric. A false positive — treating a project as an xUnit project
because a same-named attribute happens to be present — makes FrameShift judge a compilation whose real
tests it cannot see, and every FSH0001 it then reports is wrong. Detection therefore has to be able to say
"not mine" and be believed.

## Decision

**Test framework support is a plug-in seam consisting of exactly three small pieces per framework, plus
one registry line.**

1. **`ITestFrameworkProbe`** — decides whether a compilation uses the framework. Its single method
   `ITestMethodRecognizer? TryCreateRecognizer(Compilation compilation)` returns a recogniser when the
   framework is present and `null` when it is not. The probe is stateless and exposed as a shared
   `Instance`, so it is safe to use from concurrent analyzer callbacks.
2. **`ITestMethodRecognizer`** — answers `bool IsTestMethod(IMethodSymbol method)` for one compilation. It
   is created per compilation by the probe, so the symbols the framework is identified by are resolved
   exactly once. Implementations are immutable and thread-safe.
3. **A thin `DiagnosticAnalyzer`** — `TUnitTestSurfaceAnalyzer`, `XunitTestSurfaceAnalyzer`,
   `NUnitTestSurfaceAnalyzer`, `MSTestTestSurfaceAnalyzer`. Each registers a compilation action whose whole
   body is `TestSurfaceAnalysis.Execute(context, <framework>TestFrameworkProbe.Instance);`.

Everything else is framework-neutral. `TestMethodDiscovery`, `TestSurfaceCollector`,
`TestSurfaceManifest`, its reader and writer, and `TestSurfaceAnalysis` itself take an
`ITestMethodRecognizer` and never learn which framework produced it. `TestSurfaceCollector` exposes
exactly two entry points, `Collect(Compilation, ITestMethodRecognizer, CancellationToken)` and
`FindTestsWithoutProductionReference(Compilation, ITestMethodRecognizer, CancellationToken)`, and both
demand a recogniser. There is no overload that collects a surface without being told what a test is, so
no code path can silently assume a framework — the choice is always made by a probe and always visible in
the call. A fifth framework therefore adds nothing to the collector.

### Every probe accepts either signal

All four probes apply the same rule, and that uniformity is deliberate. A probe returns a recogniser when
the framework's well-known attribute type resolves in the compilation **or** when the compilation
references an assembly belonging to the framework (`TUnit*`, `xunit*`, `nunit*`, `MSTest.TestFramework*`
or `Microsoft.VisualStudio.TestPlatform.TestFramework*`); it returns `null` only when neither holds.

The reason no probe may demand both signals is `GetTypeByMetadataName`. It resolves a metadata name only
when exactly one referenced assembly declares it, and returns `null` on an ambiguity. A compilation
referencing two major versions of the same framework at once is exactly that ambiguity —
`Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute` is declared by
`MSTest.TestFramework` since MSTest 4 and by `Microsoft.VisualStudio.TestPlatform.TestFramework` in
MSTest 3 and earlier, and `Xunit.FactAttribute` by both `xunit.core` and `xunit.v3.core`. A probe that
insisted on a resolved type would hand back `null` there and shut its analysis down completely on a
project that unmistakably uses the framework. The assembly reference is the signal that survives the
ambiguity, so it has to be sufficient on its own.

The recogniser is where the remaining strictness sits, and it is the right place for it, because it
judges an individual attribute instead of a whole compilation. A recogniser accepts an attribute when the
attribute type — or any type in its base chain — is the resolved framework attribute type, **or** when it
carries the framework's simple attribute name *and* is declared in an assembly belonging to the
framework. The name alone is never sufficient. That is what keeps a hand-written look-alike in a
same-named namespace — `Microsoft.VisualStudio.TestTools.UnitTesting` is an ordinary namespace any project
may declare — from marking a method as a test: it fails the assembly half of the name-based rule and is
not in the base chain of the real attribute. The consequence of the aligned probes is only that the
analysis wakes up, discovers no test method, and shuts down under the fail-closed rule below.

The name-based rule exists for the two cases the symbol-based rule cannot cover: a sealed framework
attribute whose specialisations derive from a sibling type, and the ambiguous metadata name of a
compilation referencing two major versions at once.

The assembly-name comparison is ordinal for TUnit and case-insensitive for xUnit, NUnit and MSTest, whose
names are long, dotted and reproduced by hand often enough that insisting on exact casing would only ever
produce a false negative.

`TestClassAttribute` and `TestFixtureAttribute` are deliberately not part of any rule: they mark the
declaring type, not the method, so only an attribute on the method itself makes a method a test.

### The registry has a fixed, documented order

`TestFrameworkProbeRegistry` is the single place the supported frameworks are listed, in the order
**TUnit, xUnit, NUnit, MSTest**. That order is part of the contract, not cosmetic — see the coordination
rule below. `Matching(Compilation)` returns the probes that recognise a compilation, in that same order,
and is empty when no supported framework is present.

### The seam is fail-closed

`TestSurfaceAnalysis.Execute` shuts down as early and as completely as it can. It reports nothing
whatsoever unless the probe returns a recogniser **and** at least one test method is actually discovered.
Discovering no test is treated as "this analysis has no authority over this compilation", not as "this
compilation has no tests" — because a project whose tests cannot be seen must never be judged, as every
judgement would be a false one. In that state not even a diagnostic about the manifest is produced.

That contract is what makes four analyzers side by side harmless: each stays silent on the compilations
that are not its own.

### Coordination rule for a project using several frameworks

A test project may legitimately reference more than one framework, and then more than one analyzer is
awake on the same compilation — *awake* meaning the probe recognised the framework and at least one of its
test methods was found. There is still exactly one test-surface manifest, so:

- **FSH0004** (test method references no production member) is reported **per framework**. It names an
  individual test method, and each analyzer sees a different set of them.
- **FSH0003** (manifest missing, malformed or stale) is reported **exactly once**, by the first awake
  framework in registry order. Every other awake framework skips the manifest entirely, so the same
  problem is not reported once per referenced framework. A probe that is not registered at all leads by
  itself, because nothing else would look at the manifest on its behalf and staying silent would be worse.
- The manifest is compared against the **union of the test surfaces of all awake frameworks**, never
  against the leading framework's surface alone. A mixed project records the tests of every framework in
  its single manifest, so judging it from one framework's view would report it stale purely because the
  other framework's tests are invisible from there.

### Adding a fifth framework

Three small files and one line:

1. `src/NetEvolve.FrameShift/TestSurface/<Framework>TestFrameworkProbe.cs` — the framework name, the
   attribute metadata name(s), the accepted assembly-name prefix(es), and a `TryCreateRecognizer` that
   returns `null` when neither the attribute type resolves nor a framework assembly is referenced. Follow
   the existing four and accept either signal; do not require both. Requiring both makes the probe blind
   to a compilation that references two major versions of the framework, because the metadata name is then
   ambiguous and does not resolve. The recogniser, not the probe, is where a look-alike attribute is
   rejected.
2. `src/NetEvolve.FrameShift/TestSurface/<Framework>TestMethodRecognizer.cs` — the attribute rule for that
   framework, walking the base chain and applying the name-plus-assembly fallback. The recogniser has to
   tolerate a `null` attribute type, because the probe may have woken it on the assembly reference alone.
3. `src/NetEvolve.FrameShift/Analyzers/<Framework>TestSurfaceAnalyzer.cs` — a `DiagnosticAnalyzer`
   declaring FSH0003 and FSH0004 and delegating to `TestSurfaceAnalysis.Execute`.
4. One entry in the registration region of `TestFrameworkProbeRegistry`, appended at the end so the
   existing order — and therefore the existing FSH0003 leadership in mixed projects — does not change.

No framework-neutral code, no diagnostic descriptor, no MSBuild asset and no manifest format change is
involved.

## Consequences

**Positive**

- Framework support is additive. A new framework cannot break an existing one, because the only shared
  thing it touches is one line of a list.
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

- **One more analyzer instance is loaded per framework.** All four test-side analyzers are loaded into
  every consuming build, and each one runs a compilation action even on projects it will immediately shut
  down on. The cost of that shutdown is one probe call, but it is not zero.
- **The registry order is now observable behaviour.** Which framework reports FSH0003 in a mixed project
  follows from the list order, tests pin it (`TestFrameworkProbeRegistryTests`,
  `MixedFrameworkTestSurfaceTests`), and reordering the list is a behaviour change rather than a
  refactoring.
- **A referenced-but-unused framework wakes its probe.** Because either signal is enough, a project that
  references a framework package without writing a single test of it gets a recogniser, and the framework's
  analyzer runs a discovery pass before finding nothing. The fail-closed rule makes that harmless — no test
  discovered means no diagnostic, including none about the manifest — but the discovery walk is paid for.
  The repository's own test projects are exactly this case: they reference all four frameworks as
  compile-time metadata and run on TUnit.
- **A framework's attribute or assembly rename between major versions is a standing maintenance
  obligation.** MSTest 4 renaming its framework assembly already forced the probe to accept two identities;
  a future rename in any framework means a silent false negative until the probe is updated, and the
  fail-closed design turns that into "no diagnostics" rather than a visible error.
- **`FindAwakeFrameworks` probes every registered framework on every awake compilation** and discovers its
  test methods to decide whether it is awake, so the mixed-project coordination costs a full probe walk in
  the one case where it matters.
- Four probes plus four recognisers plus four analyzers is more types than a single detector would need,
  and the per-framework attribute rules necessarily repeat a similar base-chain walk.

## Alternatives Considered

**A single analyzer consulting all probes.** One `TestSurfaceAnalyzer` that asks the registry which
frameworks match and handles them all in one run. Fewer types, no duplicated analyzer boilerplate, and no
per-framework compilation action. Rejected because the two properties that matter most become implicit:
the per-framework diagnostic (FSH0004 for exactly this framework's tests) turns into a loop inside one
analyzer, and the shut-down contract stops being a visible property of an analyzer — "this analyzer
reports nothing on a non-MSTest project" is checkable and testable in a way that "this loop iteration
contributed nothing" is not. The coordination rule for mixed projects would also become invisible instead
of being an explicit, documented registry order.

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