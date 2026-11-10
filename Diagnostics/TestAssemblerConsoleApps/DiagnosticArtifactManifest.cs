using System.Collections.Generic;

namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal sealed record DiagnosticArtifactManifest
{
    public string SchemaVersion { get; init; } = "diagnostic-run-manifest/v1";

    public string RunKind { get; init; } = "single";

    public string CommandId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Status { get; init; } = DiagnosticRunStatus.Pending.ToString();

    public string ArtifactDirectory { get; init; } = string.Empty;

    public string? WorkerProfileId { get; init; }

    public string? WorkloadKind { get; init; }

    public string? FrontendProfile { get; init; }

    public string? ShadowFrontendMode { get; init; }

    public int? ProcessId { get; init; }

    public int? ExitCode { get; init; }

    public string? FailureMessage { get; init; }

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedUtc { get; init; }

    public DateTimeOffset? FinishedUtc { get; init; }

    public double? ElapsedSeconds { get; init; }

    public Dictionary<string, string>? GeneratedFiles { get; init; }

    public Dictionary<string, string>? ChildArtifacts { get; init; }
}
