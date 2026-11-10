using System.Security.Cryptography;
using System.Text;

namespace HybridCPU.Platform.Contracts;

public enum HybridCpuManagedMemoryOperationV1 : byte
{
    OrdinaryRead = 0,
    OrdinaryWrite = 1,
    VolatileRead = 2,
    VolatileWrite = 3,
    InterlockedCompareExchange = 4,
    InterlockedExchange = 5,
    InterlockedAdd = 6,
    FullFence = 7
}

public enum HybridCpuManagedMemoryOrderingV1 : byte
{
    IntraThread = 0,
    Acquire = 1,
    Release = 2,
    SequentiallyConsistent = 3
}

public sealed record HybridCpuManagedMemoryMappingV1(
    HybridCpuManagedMemoryOperationV1 Operation,
    IReadOnlyList<int> SupportedWidthsBits,
    bool RequiresNaturalAlignment,
    HybridCpuManagedMemoryOrderingV1 SuccessOrdering,
    HybridCpuManagedMemoryOrderingV1 FailureOrdering,
    IReadOnlyList<string> PrimitiveSequence,
    string ObservableGuarantee);

public enum HybridCpuAddressWaitDispositionV1 : byte
{
    Registered = 0,
    ValueChanged = 1,
    WakeObserved = 2,
    Woken = 3,
    TimedOut = 4,
    Cancelled = 5
}

public sealed record HybridCpuAddressWaitRequestV1(
    ulong ContextId,
    ulong Address,
    ulong ExpectedValue,
    ulong ObservedValue,
    ulong ObservedWakeEpoch,
    ulong DeadlineTick,
    bool CancellationRequested);

public sealed record HybridCpuAddressWaitResultV1(
    HybridCpuKernelStatusV1 Status,
    HybridCpuAddressWaitDispositionV1 Disposition,
    string Reason,
    ulong Address,
    ulong WakeEpoch,
    ulong LogicalTick,
    IReadOnlyList<ulong> AffectedContextIds,
    string ResultDigest)
{
    public bool IsSuccess => Status == HybridCpuKernelStatusV1.Success;
}

/// <summary>
/// Normative managed memory-model mapping. It describes architecture-visible primitives and
/// ordering only; compiler lowering, runtime Monitor ownership and ISE execution remain separate.
/// </summary>
public static class HybridCpuManagedSynchronizationContractV1
{
    public const string SchemaId = "hybridcpu.managed-synchronization/v1";
    public const int SchemaVersion = 1;
    public const int MaximumMonitorRecursion = 1024;
    public const int MaximumAddressWaiters = HybridCpuPlatformContractV1.MaximumExecutionContexts;

    public static IReadOnlyList<HybridCpuManagedMemoryMappingV1> Mappings { get; } = Array.AsReadOnly(new[]
    {
        Map(HybridCpuManagedMemoryOperationV1.OrdinaryRead, [8, 16, 32, 64], false,
            HybridCpuManagedMemoryOrderingV1.IntraThread, HybridCpuManagedMemoryOrderingV1.IntraThread,
            ["LB/LH/LW/LD"], "Program-order semantics within one managed thread; cross-thread publication requires synchronization."),
        Map(HybridCpuManagedMemoryOperationV1.OrdinaryWrite, [8, 16, 32, 64], false,
            HybridCpuManagedMemoryOrderingV1.IntraThread, HybridCpuManagedMemoryOrderingV1.IntraThread,
            ["SB/SH/SW/SD"], "Program-order semantics within one managed thread; cross-thread publication requires synchronization."),
        Map(HybridCpuManagedMemoryOperationV1.VolatileRead, [32, 64], true,
            HybridCpuManagedMemoryOrderingV1.Acquire, HybridCpuManagedMemoryOrderingV1.Acquire,
            ["LW/LD", "FENCE"], "The load observes one naturally aligned atomic value and later operations cannot move before it."),
        Map(HybridCpuManagedMemoryOperationV1.VolatileWrite, [32, 64], true,
            HybridCpuManagedMemoryOrderingV1.Release, HybridCpuManagedMemoryOrderingV1.Release,
            ["FENCE", "SW/SD"], "Earlier operations publish before one naturally aligned atomic store becomes visible."),
        Map(HybridCpuManagedMemoryOperationV1.InterlockedCompareExchange, [32, 64], true,
            HybridCpuManagedMemoryOrderingV1.SequentiallyConsistent, HybridCpuManagedMemoryOrderingV1.SequentiallyConsistent,
            ["FENCE", "LR.aq", "SC.rl retry-on-reservation-loss", "FENCE"],
            "Success and value-mismatch failure are full-fence operations; reservation loss retries without publishing a managed result."),
        Map(HybridCpuManagedMemoryOperationV1.InterlockedExchange, [32, 64], true,
            HybridCpuManagedMemoryOrderingV1.SequentiallyConsistent, HybridCpuManagedMemoryOrderingV1.SequentiallyConsistent,
            ["FENCE", "AMOSWAP.aqrl", "FENCE"], "One indivisible exchange participates in the global synchronization order."),
        Map(HybridCpuManagedMemoryOperationV1.InterlockedAdd, [32, 64], true,
            HybridCpuManagedMemoryOrderingV1.SequentiallyConsistent, HybridCpuManagedMemoryOrderingV1.SequentiallyConsistent,
            ["FENCE", "AMOADD.aqrl", "FENCE"], "One indivisible add participates in the global synchronization order."),
        Map(HybridCpuManagedMemoryOperationV1.FullFence, [32, 64], true,
            HybridCpuManagedMemoryOrderingV1.SequentiallyConsistent, HybridCpuManagedMemoryOrderingV1.SequentiallyConsistent,
            ["FENCE"], "All prior memory effects publish before any later memory effect becomes observable.")
    });

    public static string ContractDigest { get; } = Hash(string.Join('|', SchemaId, SchemaVersion,
        "naturally-atomic=aligned-32,64", "ordinary=cross-thread-data-race-unspecified",
        "release=publishes-prior", "acquire=observes-published", "full-fence=global-order",
        "cas-failure=full-fence", "speculation=squashed-effects-invisible",
        "flush-replay=retired-effects-once", "wait=value+epoch-recheck", "wake=epoch-before-selection",
        "timeout=logical-tick", "termination=removes-waiter", MaximumMonitorRecursion, MaximumAddressWaiters,
        string.Join(';', Mappings.Select(static item =>
            $"{item.Operation}:{string.Join(',', item.SupportedWidthsBits)}:{item.RequiresNaturalAlignment}:" +
            $"{item.SuccessOrdering}:{item.FailureOrdering}:{string.Join(',', item.PrimitiveSequence)}:{item.ObservableGuarantee}"))));

    public static HybridCpuManagedMemoryMappingV1 Mapping(HybridCpuManagedMemoryOperationV1 operation) =>
        Mappings.Single(item => item.Operation == operation);

    public static string ComputeWaitResultDigest(HybridCpuAddressWaitDispositionV1 disposition, ulong address,
        ulong epoch, ulong tick, IReadOnlyList<ulong> contextIds) => Hash(string.Join('|', SchemaId,
        disposition, address, epoch, tick, string.Join(',', contextIds.Order())));

    private static HybridCpuManagedMemoryMappingV1 Map(HybridCpuManagedMemoryOperationV1 operation,
        IReadOnlyList<int> widths, bool naturalAlignment, HybridCpuManagedMemoryOrderingV1 success,
        HybridCpuManagedMemoryOrderingV1 failure, IReadOnlyList<string> primitives, string guarantee) =>
        new(operation, widths, naturalAlignment, success, failure, primitives, guarantee);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
