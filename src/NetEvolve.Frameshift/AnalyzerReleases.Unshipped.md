; Unshipped analyzer releases are tracked here.
; See https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|------------|----------|-------------------------------------------------------------------------
FSH0001 | Frameshift | Warning | Mutation point is not reachable from any test.
FSH0002 | Frameshift | Info | Mutant cannot change observable behaviour (trivial or equivalent mutant).
FSH0003 | Frameshift | Warning | Test-surface manifest is missing or malformed.
FSH0004 | Frameshift | Info | Test method does not reference any production member.
