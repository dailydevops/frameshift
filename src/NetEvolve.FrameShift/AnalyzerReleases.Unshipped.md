; Unshipped analyzer releases are tracked here.
; See https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|------------|----------|-------------------------------------------------------------------------
FSH0001 | FrameShift | Warning | Mutation point is not reachable from any test.
FSH0002 | FrameShift | Info | Mutant cannot change observable behaviour (trivial or equivalent mutant).
FSH0003 | FrameShift | Warning | Test-surface manifest is missing or malformed.
FSH0004 | FrameShift | Info | Test method does not reference any production member.
FSH0006 | FrameShift | Info | Mutation point is reached by a single test case.
FSH0007 | FrameShift | Warning | Mutation point is reachable without a behavioral assertion.
