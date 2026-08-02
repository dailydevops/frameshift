# FrameShift rules

Every diagnostic FrameShift can report, with the file each `helpLinkUri` points to.

| ID                      | Severity          | Description                                                                                         |
| ----------------------- | ----------------- | --------------------------------------------------------------------------------------------------- |
| [FSH0001](FSH0001.md)   | Warning           | Mutation point is not reachable from any test, so a surviving mutant there would go unnoticed.      |
| [FSH0002](FSH0002.md)   | Info              | Mutant cannot change observable behaviour, so the mutation point is not a testing gap.              |
| [FSH0003](FSH0003.md)   | Warning           | Test-surface manifest is missing, malformed or stale, so reachability cannot be determined.         |
| [FSH0004](FSH0004.md)   | Info              | Test method does not reference any production member and cannot contribute to the tested surface.   |
| [FSH0005](FSH0005.md)   | Warning (MSBuild) | No test-surface manifest reached the compiler, so the project has not been set up for the analysis. |
| [FSH0006](FSH0006.md)   | Info              | Mutation point is reached by a single test case, so a mutation that matters for other inputs would go unnoticed. |
| [FSH0007](FSH0007.md)   | Warning           | Mutation point is reachable without a behavioral assertion, so a surviving mutant there would go unnoticed. |

`FSH0001` to `FSH0004`, `FSH0006` and `FSH0007` are analyzer diagnostics in the category `FrameShift`, all enabled by default,
configurable through `.editorconfig`, `[SuppressMessage]` and `#pragma warning`. `FSH0005` is an
MSBuild warning from the build assets of the package and is silenced with the
`FrameShiftSuppressSetupWarning` property instead.

---

> [!NOTE]
> **Made with ❤️ by the NetEvolve Team**
> Visit us at [https://www.daily-devops.net](https://www.daily-devops.net) for more information about our services and solutions.
