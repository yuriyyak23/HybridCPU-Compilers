using HybridCPU_ISE;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Diagnostics;

namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal sealed record DiagnosticExecutionCheckpoint
{
    public string SchemaVersion { get; init; } = "diagnostic-execution-checkpoint/v1";
    public string ProfileId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public string FrontendProfile { get; init; } = string.Empty;
    public string ProgramVariant { get; init; } = string.Empty;
    public string ObservationSourceProvenance { get; init; } = string.Empty;
    public string Stage { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public long Sequence { get; init; }
    public DateTimeOffset CapturedUtc { get; init; } = DateTimeOffset.UtcNow;
    public ulong? RetirementTarget { get; init; }
    public ulong? HardCycleLimit { get; init; }
    public ulong? ObservedCycleCount { get; init; }
    public ulong? ObservedInstructionsRetired { get; init; }
    public ulong? ObservedStallCycles { get; init; }
    public int? ActiveVirtualThreadId { get; init; }
    public ulong? ActiveLivePc { get; init; }
    public string? CurrentState { get; init; }
    public CoreStateSnapshot? CoreState { get; init; }
    public StackFlagsSnapshot? StackFlags { get; init; }
    public PerformanceReport? Performance { get; init; }
    public ReplayPhaseMetrics? ReplayPhaseMetrics { get; init; }
    public SchedulerPhaseMetrics? SchedulerPhaseMetrics { get; init; }
    public TypedSlotTelemetryProfile? TelemetryProfile { get; init; }
    public string? ReplayTokenJson { get; init; }
    public string? ReplayTokenCaptureFailure { get; init; }
}
