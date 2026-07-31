; Shipped analyzer releases are tracked here.
; See https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md
;
; Deliberately empty: nothing has been released yet, so every rule lives in
; AnalyzerReleases.Unshipped.md. On the first release its entries move here under a version heading.
;
; FSH0005 is absent from both files on purpose. It is an MSBuild warning emitted by
; NetEvolve.FrameShift.targets, not an analyzer rule, so it has no DiagnosticDescriptor. Release
; tracking only covers analyzer rules, and an entry without a descriptor would fail RS2001.
