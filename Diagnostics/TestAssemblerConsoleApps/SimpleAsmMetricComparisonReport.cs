namespace YAKSys_Hybrid_CPU.DiagnosticsConsole;

internal sealed record SimpleAsmMetricComparisonReport(
    string ComparisonId,
    string LeftProfileId,
    string RightProfileId,
    string LeftArtifactDirectory,
    string RightArtifactDirectory,
    string LeftStatus,
    string RightStatus,
    double IpcDelta,
    long CycleDelta,
    long PartialWidthIssueDelta,
    double RetiredPhysicalLanesPerRetireCycleDelta,
    long BytesTransferredDelta);
