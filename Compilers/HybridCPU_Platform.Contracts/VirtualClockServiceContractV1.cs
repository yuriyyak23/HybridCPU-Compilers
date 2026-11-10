namespace HybridCPU.Platform.Contracts;

/// <summary>Kernel-owned read of the explicit virtual clock; its source frequency is a loader profile input.</summary>
public static class HybridCpuVirtualClockServiceContractV1
{
    public const string SchemaId = "hybridcpu.virtual-clock-service/v1";
    public const ulong ReadTicksOperation = 0x4d4f4e4f; // MONO, distinct from embedder-defined low operations.
    public const ulong ReadDoomTicsOperation = 0x444f4f4d; // DOOM, exact 35 Hz projection.
    public const ulong WaitUntilDoomTicOperation = 0x5741_4954; // WAIT
    public static string ContractDigest { get; } = HybridCpuPlatformContractV1.Hash(
        $"{SchemaId}|clock:{ReadTicksOperation}|doom:{ReadDoomTicsOperation}:rate=35|wait:{WaitUntilDoomTicOperation}:arg=i32-target:round=ceil:park-until-wake|buffers=none|result=u64|source=kernel-explicit-virtual-time|units=boot-required");
    public static HybridCpuManagedInteropSignatureV1 Signature { get; } = new(
        "hybridcpu.runtime", "monotonic_ticks", HybridCpuInteropCallingConventionV1.HybridCpuNativeV2,
        [], new(HybridCpuInteropValueKindV1.UnsignedInteger, 8, 8));
}
