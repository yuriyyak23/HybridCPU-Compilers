namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal enum DiagnosticTelemetryLogMode
{
    Minimal = 0,
    Extended = 1
}

internal sealed record DiagnosticRunProfile(
    string Id,
    string DisplayName,
    DiagnosticWorkloadKind WorkloadKind,
    FrontendMode FrontendMode,
    int WallClockTimeoutMs,
    SimpleAsmAppMode? SimpleAsmMode = null,
    int HeartbeatIntervalMs = 1000,
    ulong HeartbeatCycleStride = 2048,
    ulong WorkloadIterations = 1_000_000UL,
    DiagnosticTelemetryLogMode TelemetryLogMode = DiagnosticTelemetryLogMode.Minimal,
    ShadowFrontendMode ShadowFrontendMode = ShadowFrontendMode.Off)
{
    public const ulong DefaultWorkloadIterations = 1_000_000UL;

    public static DiagnosticRunProfile CreateSimple(
        string id,
        string displayName,
        SimpleAsmAppMode mode,
        int wallClockTimeoutMs = 30000,
        int heartbeatIntervalMs = 1000,
        ulong heartbeatCycleStride = 2048,
        FrontendMode frontendMode = FrontendMode.NativeVLIW,
        ulong workloadIterations = DefaultWorkloadIterations)
    {
        return new DiagnosticRunProfile(
            id,
            displayName,
            DiagnosticWorkloadKind.SimpleAsmMode,
            frontendMode,
            wallClockTimeoutMs,
            mode,
            heartbeatIntervalMs,
            heartbeatCycleStride,
            workloadIterations);
    }

    public static DiagnosticRunProfile CreateReplayPair(
        string id = "replay",
        string displayName = "Replay Phase Pair",
        int wallClockTimeoutMs = 15000,
        int heartbeatIntervalMs = 1000,
        ulong heartbeatCycleStride = 2048,
        FrontendMode frontendMode = FrontendMode.NativeVLIW,
        ulong workloadIterations = DefaultWorkloadIterations)
    {
        return new DiagnosticRunProfile(
            id,
            displayName,
            DiagnosticWorkloadKind.ReplayPhasePair,
            frontendMode,
            wallClockTimeoutMs,
            SimpleAsmMode: null,
            heartbeatIntervalMs,
            heartbeatCycleStride,
            workloadIterations);
    }

    public static DiagnosticRunProfile CreateArchitectural(
        string id,
        string displayName,
        DiagnosticWorkloadKind workloadKind,
        int wallClockTimeoutMs = 15000,
        int heartbeatIntervalMs = 1000,
        ulong heartbeatCycleStride = 2048,
        FrontendMode frontendMode = FrontendMode.NativeVLIW,
        ulong workloadIterations = DefaultWorkloadIterations)
    {
        if (workloadKind == DiagnosticWorkloadKind.SimpleAsmMode)
        {
            throw new ArgumentException("Use CreateSimple for SimpleAsmMode profiles.", nameof(workloadKind));
        }

        return new DiagnosticRunProfile(
            id,
            displayName,
            workloadKind,
            frontendMode,
            wallClockTimeoutMs,
            SimpleAsmMode: null,
            heartbeatIntervalMs,
            heartbeatCycleStride,
            workloadIterations);
    }
}
