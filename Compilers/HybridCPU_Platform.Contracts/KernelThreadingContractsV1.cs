using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Platform.Contracts;

public enum HybridCpuExecutionContextStateV1 : byte
{
    Created = 0,
    Runnable = 1,
    Running = 2,
    Parked = 3,
    Joining = 4,
    GcRendezvous = 5,
    Exited = 6
}

public sealed record HybridCpuExecutionContextCreateRequestV1(
    ulong EntryAddress,
    ulong StackBase,
    ulong StackSize,
    ulong GuardBase,
    ulong GuardSize,
    ulong ContextCarrierAddress,
    ulong TlsBase,
    int PreferredVirtualThreadCarrier = -1);

public sealed record HybridCpuExecutionContextSnapshotV1(
    HybridCpuExecutionContextDescriptorV1 Descriptor,
    HybridCpuExecutionContextStateV1 State,
    ulong GuardBase,
    ulong GuardSize,
    ulong TlsBase,
    int? ExitCode,
    ulong? JoinTargetContextId,
    string WaitReason,
    ulong StateSequence);

public sealed record HybridCpuKernelContextResultV1(
    HybridCpuKernelStatusV1 Status,
    string Reason,
    HybridCpuExecutionContextSnapshotV1? Context)
{
    public bool IsSuccess => Status == HybridCpuKernelStatusV1.Success && Context is not null;
}

public sealed record HybridCpuGcRendezvousSnapshotV1(
    ulong Epoch,
    ulong RequestingContextId,
    IReadOnlyList<HybridCpuExecutionContextSnapshotV1> Contexts,
    string SnapshotDigest);

public sealed record HybridCpuKernelRendezvousResultV1(
    HybridCpuKernelStatusV1 Status,
    string Reason,
    HybridCpuGcRendezvousSnapshotV1? Snapshot)
{
    public bool IsSuccess => Status == HybridCpuKernelStatusV1.Success && Snapshot is not null;
}

public static class HybridCpuKernelThreadingContractV1
{
    public const string SchemaId = "hybridcpu.kernel-threading/v1";
    public const int SchemaVersion = 1;

    public static string ContractDigest { get; } = Hash(string.Join('|', SchemaId, SchemaVersion,
        HybridCpuPlatformContractV1.MaximumExecutionContexts,
        HybridCpuPlatformContractV1.MaximumManagedTlsBytesPerContext,
        "managed-thread!=vt", "x4=opaque-context-carrier", "tls-base=kernel-context-property",
        "scheduler=deterministic-cooperative", "gc=kernel-rendezvous-managed-root-enumeration",
        "stack=rw-with-no-access-guard", "unknown-missing-rejected=fail-closed"));

    public static string ComputeSnapshotDigest(ulong epoch, ulong requester,
        IReadOnlyList<HybridCpuExecutionContextSnapshotV1> contexts) => Hash(string.Join('|',
        SchemaId, epoch, requester,
        string.Join(';', contexts.OrderBy(static item => item.Descriptor.ContextId).Select(static item =>
            $"{item.Descriptor.ContextId}:{item.State}:{item.Descriptor.VirtualThreadCarrier}:" +
            $"{item.Descriptor.ContextCarrierAddress}:{item.TlsBase}:{item.Descriptor.StackBase}:" +
            $"{item.Descriptor.StackSize}:{item.GuardBase}:{item.GuardSize}:{item.StateSequence}"))));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
