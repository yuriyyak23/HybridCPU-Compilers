namespace HybridCPU.Platform.Contracts;

/// <summary>
/// Private compiler/runtime encoding for final-PC finally continuations. This is image metadata,
/// not an ISA extension. Token zero is reserved for exceptional unwind and never names a normal leave.
/// </summary>
public static class HybridCpuManagedFinallyContinuationEncodingV1
{
    public const uint Magic = 0x46434348; // HCCF
    public const ushort Version = 2;
    public const int ExceptionalToken = 0;
    public const int HeaderSizeBytes = 24;
    public const int ContinuationCountOffset = 8;
    public const int StepCountOffset = 12;
    public const int TokenSlotOffset = 16;
    public const int ContinuationRowSizeBytes = 24;
    public const int TokenOffset = 0;
    public const int LeaveOffset = 4;
    public const int TargetOffset = 8;
    public const int FirstStepOffset = 12;
    public const int ContinuationStepCountOffset = 16;
    public const int ReleaseBeforeTargetOffset = 20;
    public const int StepRowSizeBytes = 24;
    public const int StepClauseOrdinalOffset = 0;
    public const int StepHandlerOffset = 4;
    public const int StepReleaseBeforeEntryOffset = 8;
    public const int StepNextClauseOrdinalOffset = 12;
    public const int StepNextOffset = 16;
    public const int StepHandlerEndOffset = 20;
}
