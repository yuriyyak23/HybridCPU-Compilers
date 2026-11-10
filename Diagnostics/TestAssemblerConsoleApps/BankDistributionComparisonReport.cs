namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal sealed record BankDistributionComparisonReport(
    string ComparisonId,
    string LatencyHidingArtifactDirectory,
    string BankDistributedArtifactDirectory,
    string LatencyHidingStatus,
    string BankDistributedStatus,
    double IpcDelta,
    long CycleDelta,
    long PartialWidthIssueDelta,
    double RetiredPhysicalLanesPerRetireCycleDelta,
    long BytesTransferredDelta);
