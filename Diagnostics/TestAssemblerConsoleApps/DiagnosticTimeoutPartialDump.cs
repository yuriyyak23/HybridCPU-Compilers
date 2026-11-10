namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal sealed record DiagnosticTimeoutPartialDump
{
    public string SchemaVersion { get; init; } = "diagnostic-timeout-partial-dump/v1";
    public string CommandId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string ArtifactDirectory { get; init; } = string.Empty;
    public string Status { get; init; } = DiagnosticRunStatus.TimedOut.ToString();
    public DateTimeOffset CapturedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DiagnosticArtifactManifest? Manifest { get; init; }
    public DiagnosticExecutionCheckpoint? LastCheckpoint { get; init; }
    public string? StdoutTail { get; init; }
    public string? StderrTail { get; init; }
    public Dictionary<string, long>? ArtifactFileSizes { get; init; }
}
