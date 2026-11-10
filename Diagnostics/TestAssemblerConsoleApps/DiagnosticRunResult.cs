namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal sealed record DiagnosticRunResult(
    DiagnosticRunProfile Profile,
    DiagnosticRunStatus Status,
    string ArtifactDirectory,
    int? ExitCode,
    TimeSpan Elapsed,
    DiagnosticArtifactManifest Manifest,
    SimpleAsmAppMetrics? Metrics,
    ReplayPhaseBenchmarkPairReport? ReplayReport,
    DiagnosticExecutionCheckpoint? LastCheckpoint);
