using System.Security.Cryptography;
using System.Text;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public enum HybridCpuManagedSynchronizationLoweringStatusV1 : byte
{
    Supported = 0,
    UnsupportedWidth = 1,
    InvalidAlignment = 2,
    InvalidRequest = 3
}

public sealed record HybridCpuManagedSynchronizationStepV1(
    int Sequence,
    HybridCpuOpcode Opcode,
    bool AcquireOrdering,
    bool ReleaseOrdering,
    string Role);

public sealed record HybridCpuManagedSynchronizationPlanV1(
    HybridCpuManagedSynchronizationLoweringStatusV1 Status,
    string Reason,
    HybridCpuManagedMemoryOperationV1 Operation,
    int WidthBits,
    int AlignmentBytes,
    IReadOnlyList<HybridCpuManagedSynchronizationStepV1> SuccessPath,
    IReadOnlyList<HybridCpuManagedSynchronizationStepV1> FailurePath,
    bool RetryOnReservationLoss,
    string ContractDigest,
    string PlanDigest)
{
    public bool IsSupported => Status == HybridCpuManagedSynchronizationLoweringStatusV1.Supported;
}

/// <summary>
/// Compiler-owned lowering selection for the normative managed synchronization contract.
/// It selects existing generic HybridCPU instructions and never grants execution authority.
/// </summary>
public static class HybridCpuManagedSynchronizationLoweringV1
{
    public const string SchemaId = "hybridcpu.managed-synchronization-lowering/v1";

    public static HybridCpuManagedSynchronizationPlanV1 Plan(HybridCpuManagedMemoryOperationV1 operation,
        int widthBits, int alignmentBytes)
    {
        HybridCpuManagedMemoryMappingV1 mapping = HybridCpuManagedSynchronizationContractV1.Mapping(operation);
        if (!mapping.SupportedWidthsBits.Contains(widthBits))
            return Failure(HybridCpuManagedSynchronizationLoweringStatusV1.UnsupportedWidth,
                "The managed synchronization width is outside the closed V1 mapping.", operation, widthBits, alignmentBytes);
        if (alignmentBytes <= 0 || mapping.RequiresNaturalAlignment &&
            (alignmentBytes < widthBits / 8 || alignmentBytes % (widthBits / 8) != 0))
            return Failure(HybridCpuManagedSynchronizationLoweringStatusV1.InvalidAlignment,
                "The operation requires natural 32-bit or 64-bit alignment.", operation, widthBits, alignmentBytes);

        HybridCpuManagedSynchronizationStepV1[] success = operation switch
        {
            HybridCpuManagedMemoryOperationV1.OrdinaryRead => [Step(0, Load(widthBits), false, false, "ordinary-read")],
            HybridCpuManagedMemoryOperationV1.OrdinaryWrite => [Step(0, Store(widthBits), false, false, "ordinary-write")],
            HybridCpuManagedMemoryOperationV1.VolatileRead =>
                [Step(0, Load(widthBits), false, false, "volatile-read"), Step(1, HybridCpuOpcode.FENCE, false, false, "acquire-fence")],
            HybridCpuManagedMemoryOperationV1.VolatileWrite =>
                [Step(0, HybridCpuOpcode.FENCE, false, false, "release-fence"), Step(1, Store(widthBits), false, false, "volatile-write")],
            HybridCpuManagedMemoryOperationV1.InterlockedCompareExchange =>
                [Step(0, HybridCpuOpcode.FENCE, false, false, "full-fence-before"),
                 Step(1, widthBits == 32 ? HybridCpuOpcode.LR_W : HybridCpuOpcode.LR_D, true, false, "compare-load"),
                 Step(2, widthBits == 32 ? HybridCpuOpcode.SC_W : HybridCpuOpcode.SC_D, false, true, "conditional-store"),
                 Step(3, HybridCpuOpcode.FENCE, false, false, "full-fence-after")],
            HybridCpuManagedMemoryOperationV1.InterlockedExchange => Atomic(widthBits,
                widthBits == 32 ? HybridCpuOpcode.AMOSWAP_W : HybridCpuOpcode.AMOSWAP_D, "exchange"),
            HybridCpuManagedMemoryOperationV1.InterlockedAdd => Atomic(widthBits,
                widthBits == 32 ? HybridCpuOpcode.AMOADD_W : HybridCpuOpcode.AMOADD_D, "add"),
            HybridCpuManagedMemoryOperationV1.FullFence => [Step(0, HybridCpuOpcode.FENCE, false, false, "full-fence")],
            _ => []
        };
        if (success.Length == 0)
            return Failure(HybridCpuManagedSynchronizationLoweringStatusV1.InvalidRequest,
                "The managed synchronization operation is unknown.", operation, widthBits, alignmentBytes);
        HybridCpuManagedSynchronizationStepV1[] failure = operation == HybridCpuManagedMemoryOperationV1.InterlockedCompareExchange
            ? [success[0], success[1], Step(2, HybridCpuOpcode.FENCE, false, false, "failed-compare-full-fence")]
            : success;
        string digest = Hash(string.Join('|', SchemaId, HybridCpuManagedSynchronizationContractV1.ContractDigest,
            operation, widthBits, alignmentBytes, Text(success), Text(failure),
            operation == HybridCpuManagedMemoryOperationV1.InterlockedCompareExchange));
        return new(HybridCpuManagedSynchronizationLoweringStatusV1.Supported, string.Empty, operation,
            widthBits, alignmentBytes, success, failure,
            operation == HybridCpuManagedMemoryOperationV1.InterlockedCompareExchange,
            HybridCpuManagedSynchronizationContractV1.ContractDigest, digest);
    }

    private static HybridCpuManagedSynchronizationStepV1[] Atomic(int widthBits, HybridCpuOpcode opcode, string role) =>
        [Step(0, HybridCpuOpcode.FENCE, false, false, "full-fence-before"),
         Step(1, opcode, true, true, role),
         Step(2, HybridCpuOpcode.FENCE, false, false, "full-fence-after")];

    private static HybridCpuOpcode Load(int widthBits) => widthBits switch
    {
        8 => HybridCpuOpcode.LB,
        16 => HybridCpuOpcode.LH,
        32 => HybridCpuOpcode.LW,
        64 => HybridCpuOpcode.LD,
        _ => throw new ArgumentOutOfRangeException(nameof(widthBits))
    };

    private static HybridCpuOpcode Store(int widthBits) => widthBits switch
    {
        8 => HybridCpuOpcode.SB,
        16 => HybridCpuOpcode.SH,
        32 => HybridCpuOpcode.SW,
        64 => HybridCpuOpcode.SD,
        _ => throw new ArgumentOutOfRangeException(nameof(widthBits))
    };

    private static HybridCpuManagedSynchronizationStepV1 Step(int sequence, HybridCpuOpcode opcode,
        bool acquire, bool release, string role) => new(sequence, opcode, acquire, release, role);
    private static string Text(IEnumerable<HybridCpuManagedSynchronizationStepV1> steps) =>
        string.Join(',', steps.Select(static step => $"{step.Sequence}:{step.Opcode}:{step.AcquireOrdering}:{step.ReleaseOrdering}:{step.Role}"));
    private static HybridCpuManagedSynchronizationPlanV1 Failure(HybridCpuManagedSynchronizationLoweringStatusV1 status,
        string reason, HybridCpuManagedMemoryOperationV1 operation, int widthBits, int alignmentBytes) =>
        new(status, reason, operation, widthBits, alignmentBytes, [], [], false,
            HybridCpuManagedSynchronizationContractV1.ContractDigest,
            Hash($"{SchemaId}|{status}|{reason}|{operation}|{widthBits}|{alignmentBytes}"));
    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
