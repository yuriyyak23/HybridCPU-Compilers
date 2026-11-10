using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;

namespace HybridCPU.ManagedRuntime;

public sealed record HybridCpuManagedSynchronizationOptionsV1(bool Enabled, string OptionsDigest)
{
    public static HybridCpuManagedSynchronizationOptionsV1 Production { get; } = Create(false);
    public static HybridCpuManagedSynchronizationOptionsV1 Qualification { get; } = Create(true);
    public static HybridCpuManagedSynchronizationOptionsV1 Create(bool enabled) => new(enabled,
        HybridCpuPlatformContractV1.Hash($"hybridcpu.managed-synchronization-options/v1|{enabled}"));
}

public sealed record HybridCpuManagedAtomicResultV1(
    bool IsSuccess,
    string Reason,
    HybridCpuManagedMemoryOperationV1 Operation,
    ulong ObservedValue,
    ulong ResultValue,
    bool ValueChanged,
    ulong Sequence,
    string ResultDigest);

/// <summary>
/// Runtime-owned executable memory-model oracle for the closed managed V1 mapping. Ordinary
/// stores may remain unpublished per context; release, acquire, Interlocked and fences obey the
/// normative cross-context rules. Speculative effects never enter committed visibility.
/// </summary>
public sealed class HybridCpuManagedMemoryModelV1
{
    private readonly HybridCpuManagedSynchronizationOptionsV1 _options;
    private readonly Dictionary<ulong, ulong> _committed = [];
    private readonly Dictionary<ulong, SortedDictionary<ulong, ulong>> _pendingByContext = [];
    private readonly Dictionary<ulong, SortedDictionary<ulong, ulong>> _speculativeByContext = [];
    private ulong _sequence;

    public HybridCpuManagedMemoryModelV1(HybridCpuManagedSynchronizationOptionsV1? options = null) =>
        _options = options ?? HybridCpuManagedSynchronizationOptionsV1.Production;

    public void Seed(ulong address, ulong value) => _committed[address] = value;

    public HybridCpuManagedAtomicResultV1 OrdinaryStore(ulong contextId, ulong address, ulong value, int widthBits = 64)
    {
        if (!Valid(contextId, address, widthBits, atomic: false, out string reason)) return Fail(HybridCpuManagedMemoryOperationV1.OrdinaryWrite, reason);
        Pending(contextId)[address] = Normalize(value, widthBits);
        return Success(HybridCpuManagedMemoryOperationV1.OrdinaryWrite, 0, value, true);
    }

    public HybridCpuManagedAtomicResultV1 OrdinaryLoad(ulong contextId, ulong address, int widthBits = 64)
    {
        if (!Valid(contextId, address, widthBits, atomic: false, out string reason)) return Fail(HybridCpuManagedMemoryOperationV1.OrdinaryRead, reason);
        ulong value = Pending(contextId).TryGetValue(address, out ulong own) ? own : ReadCommitted(address);
        return Success(HybridCpuManagedMemoryOperationV1.OrdinaryRead, value, value, false);
    }

    public HybridCpuManagedAtomicResultV1 VolatileWrite(ulong contextId, ulong address, ulong value, int widthBits = 64)
    {
        if (!Valid(contextId, address, widthBits, atomic: true, out string reason)) return Fail(HybridCpuManagedMemoryOperationV1.VolatileWrite, reason);
        Publish(contextId);
        _committed[address] = Normalize(value, widthBits);
        return Success(HybridCpuManagedMemoryOperationV1.VolatileWrite, 0, value, true);
    }

    public HybridCpuManagedAtomicResultV1 VolatileRead(ulong contextId, ulong address, int widthBits = 64)
    {
        if (!Valid(contextId, address, widthBits, atomic: true, out string reason)) return Fail(HybridCpuManagedMemoryOperationV1.VolatileRead, reason);
        ulong value = ReadCommitted(address);
        return Success(HybridCpuManagedMemoryOperationV1.VolatileRead, value, value, false);
    }

    public HybridCpuManagedAtomicResultV1 CompareExchange(ulong contextId, ulong address, ulong value,
        ulong comparand, int widthBits = 64)
    {
        if (!Valid(contextId, address, widthBits, atomic: true, out string reason)) return Fail(HybridCpuManagedMemoryOperationV1.InterlockedCompareExchange, reason);
        Publish(contextId);
        ulong observed = ReadCommitted(address);
        bool changed = observed == Normalize(comparand, widthBits);
        if (changed) _committed[address] = Normalize(value, widthBits);
        return Success(HybridCpuManagedMemoryOperationV1.InterlockedCompareExchange, observed,
            ReadCommitted(address), changed);
    }

    public HybridCpuManagedAtomicResultV1 Exchange(ulong contextId, ulong address, ulong value, int widthBits = 64)
    {
        if (!Valid(contextId, address, widthBits, atomic: true, out string reason)) return Fail(HybridCpuManagedMemoryOperationV1.InterlockedExchange, reason);
        Publish(contextId);
        ulong observed = ReadCommitted(address);
        _committed[address] = Normalize(value, widthBits);
        return Success(HybridCpuManagedMemoryOperationV1.InterlockedExchange, observed, _committed[address], true);
    }

    public HybridCpuManagedAtomicResultV1 Add(ulong contextId, ulong address, ulong value, int widthBits = 64)
    {
        if (!Valid(contextId, address, widthBits, atomic: true, out string reason)) return Fail(HybridCpuManagedMemoryOperationV1.InterlockedAdd, reason);
        Publish(contextId);
        ulong observed = ReadCommitted(address);
        ulong result = Normalize(unchecked(observed + value), widthBits);
        _committed[address] = result;
        return Success(HybridCpuManagedMemoryOperationV1.InterlockedAdd, observed, result, true);
    }

    public HybridCpuManagedAtomicResultV1 FullFence(ulong contextId)
    {
        if (!_options.Enabled || contextId == 0) return Fail(HybridCpuManagedMemoryOperationV1.FullFence, "Managed synchronization is disabled or the context is invalid.");
        Publish(contextId);
        return Success(HybridCpuManagedMemoryOperationV1.FullFence, 0, 0, false);
    }

    public bool SpeculateStore(ulong contextId, ulong address, ulong value)
    {
        if (!_options.Enabled || contextId == 0 || address == 0) return false;
        if (!_speculativeByContext.TryGetValue(contextId, out SortedDictionary<ulong, ulong>? writes))
            _speculativeByContext.Add(contextId, writes = []);
        writes[address] = value;
        return true;
    }

    public void FlushOrReplay(ulong contextId) => _speculativeByContext.Remove(contextId);
    public ulong CommittedValue(ulong address) => ReadCommitted(address);

    private bool Valid(ulong contextId, ulong address, int widthBits, bool atomic, out string reason)
    {
        reason = string.Empty;
        if (!_options.Enabled || contextId == 0 || address == 0 || widthBits is not (8 or 16 or 32 or 64))
        {
            reason = "Managed synchronization is disabled or the memory request is invalid.";
            return false;
        }
        if (atomic && (widthBits is not (32 or 64) || address % (ulong)(widthBits / 8) != 0))
        {
            reason = "Managed atomic operations require naturally aligned 32-bit or 64-bit storage.";
            return false;
        }
        return true;
    }

    private SortedDictionary<ulong, ulong> Pending(ulong contextId)
    {
        if (!_pendingByContext.TryGetValue(contextId, out SortedDictionary<ulong, ulong>? values))
            _pendingByContext.Add(contextId, values = []);
        return values;
    }

    private void Publish(ulong contextId)
    {
        foreach ((ulong address, ulong value) in Pending(contextId)) _committed[address] = value;
        Pending(contextId).Clear();
    }

    private ulong ReadCommitted(ulong address) => _committed.TryGetValue(address, out ulong value) ? value : 0;
    private static ulong Normalize(ulong value, int widthBits) => widthBits == 64 ? value : value & ((1UL << widthBits) - 1);
    private HybridCpuManagedAtomicResultV1 Success(HybridCpuManagedMemoryOperationV1 operation, ulong observed, ulong result, bool changed)
    {
        ulong sequence = ++_sequence;
        return new(true, string.Empty, operation, observed, result, changed, sequence,
            HybridCpuPlatformContractV1.Hash($"{HybridCpuManagedSynchronizationContractV1.ContractDigest}|{operation}|{observed}|{result}|{changed}|{sequence}"));
    }
    private HybridCpuManagedAtomicResultV1 Fail(HybridCpuManagedMemoryOperationV1 operation, string reason) =>
        new(false, reason, operation, 0, 0, false, _sequence,
            HybridCpuPlatformContractV1.Hash($"{HybridCpuManagedSynchronizationContractV1.ContractDigest}|{operation}|failure|{reason}|{_sequence}"));
}

public enum HybridCpuManagedMonitorDispositionV1 : byte
{
    Acquired = 0,
    Recursive = 1,
    Contended = 2,
    Released = 3,
    HandedOff = 4,
    TimedOut = 5,
    Cancelled = 6,
    Invalid = 7,
    Disabled = 8
}

public sealed record HybridCpuManagedMonitorResultV1(
    HybridCpuManagedMonitorDispositionV1 Disposition,
    string Reason,
    ulong ObjectAddress,
    ulong OwnerManagedThreadId,
    int Recursion,
    IReadOnlyList<ulong> WokenManagedThreadIds,
    string ResultDigest)
{
    public bool IsSuccess => Disposition is not (HybridCpuManagedMonitorDispositionV1.Invalid or HybridCpuManagedMonitorDispositionV1.Disabled);
}

/// <summary>ManagedRuntime-owned Monitor identity, recursion and handoff over kernel address waits.</summary>
public sealed class HybridCpuManagedMonitorRuntimeV1
{
    private readonly HybridCpuManagedThreadingRuntimeV1 _threads;
    private readonly IHybridCpuRuntimeKernelV1 _kernel;
    private readonly HybridCpuManagedSynchronizationOptionsV1 _options;
    private readonly SortedDictionary<ulong, MonitorState> _monitors = [];

    public HybridCpuManagedMonitorRuntimeV1(HybridCpuManagedThreadingRuntimeV1 threads,
        IHybridCpuRuntimeKernelV1 kernel, HybridCpuManagedSynchronizationOptionsV1? options = null)
    {
        _threads = threads ?? throw new ArgumentNullException(nameof(threads));
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _options = options ?? HybridCpuManagedSynchronizationOptionsV1.Production;
    }

    public HybridCpuManagedMonitorResultV1 Enter(ulong managedThreadId, ulong objectAddress,
        ulong deadlineTick = ulong.MaxValue, bool cancellationRequested = false)
    {
        if (!Valid(managedThreadId, objectAddress, out ulong contextId, out HybridCpuManagedMonitorResultV1? failure)) return failure;
        MonitorState state = _monitors.TryGetValue(objectAddress, out MonitorState? found) ? found : new(0, 0);
        if (state.OwnerManagedThreadId == 0)
        {
            _monitors[objectAddress] = new(managedThreadId, 1);
            return Result(HybridCpuManagedMonitorDispositionV1.Acquired, objectAddress, managedThreadId, 1, []);
        }
        if (state.OwnerManagedThreadId == managedThreadId)
        {
            if (state.Recursion >= HybridCpuManagedSynchronizationContractV1.MaximumMonitorRecursion)
                return Invalid("Monitor recursion budget exhausted.", objectAddress);
            _monitors[objectAddress] = state with { Recursion = state.Recursion + 1 };
            return Result(HybridCpuManagedMonitorDispositionV1.Recursive, objectAddress, managedThreadId, state.Recursion + 1, []);
        }
        ulong epoch = _kernel.AddressWakeEpoch(objectAddress);
        HybridCpuAddressWaitResultV1 wait = _kernel.WaitOnAddress(new(contextId, objectAddress,
            state.OwnerManagedThreadId, state.OwnerManagedThreadId, epoch, deadlineTick, cancellationRequested));
        HybridCpuManagedMonitorDispositionV1 disposition = wait.Disposition switch
        {
            HybridCpuAddressWaitDispositionV1.Registered => HybridCpuManagedMonitorDispositionV1.Contended,
            HybridCpuAddressWaitDispositionV1.TimedOut => HybridCpuManagedMonitorDispositionV1.TimedOut,
            HybridCpuAddressWaitDispositionV1.Cancelled => HybridCpuManagedMonitorDispositionV1.Cancelled,
            _ => HybridCpuManagedMonitorDispositionV1.Contended
        };
        return wait.IsSuccess ? Result(disposition, objectAddress, state.OwnerManagedThreadId, state.Recursion, [])
            : Invalid(wait.Reason, objectAddress);
    }

    public HybridCpuManagedMonitorResultV1 Exit(ulong managedThreadId, ulong objectAddress)
    {
        if (!Valid(managedThreadId, objectAddress, out _, out HybridCpuManagedMonitorResultV1? failure)) return failure;
        if (!_monitors.TryGetValue(objectAddress, out MonitorState? state) || state.OwnerManagedThreadId != managedThreadId)
            return Invalid("Only the owning managed thread may exit a monitor.", objectAddress);
        if (state.Recursion > 1)
        {
            _monitors[objectAddress] = state with { Recursion = state.Recursion - 1 };
            return Result(HybridCpuManagedMonitorDispositionV1.Released, objectAddress, managedThreadId, state.Recursion - 1, []);
        }
        HybridCpuAddressWaitResultV1 wake = _kernel.WakeAddress(objectAddress, 1);
        ulong[] woken = wake.AffectedContextIds.Select(contextId =>
            _threads.TryResolveManagedThread(contextId, out ulong threadId) ? threadId : 0)
            .Where(static id => id != 0).ToArray();
        if (woken.Length == 1)
        {
            _monitors[objectAddress] = new(woken[0], 1);
            return Result(HybridCpuManagedMonitorDispositionV1.HandedOff, objectAddress, woken[0], 1, woken);
        }
        _monitors.Remove(objectAddress);
        return Result(HybridCpuManagedMonitorDispositionV1.Released, objectAddress, 0, 0, []);
    }

    public (ulong OwnerManagedThreadId, int Recursion) Snapshot(ulong objectAddress) =>
        _monitors.TryGetValue(objectAddress, out MonitorState? state) ? (state.OwnerManagedThreadId, state.Recursion) : (0, 0);

    private bool Valid(ulong threadId, ulong address, out ulong contextId, out HybridCpuManagedMonitorResultV1 failure)
    {
        contextId = 0;
        if (!_options.Enabled)
        {
            failure = Result(HybridCpuManagedMonitorDispositionV1.Disabled, address, 0, 0, [], "Managed synchronization is default-disabled.");
            return false;
        }
        if (address == 0 || address % 8 != 0 || !_threads.TryResolveContext(threadId, out contextId))
        {
            failure = Invalid("Monitor requires an aligned object and a live managed thread.", address);
            return false;
        }
        failure = null!;
        return true;
    }

    private static HybridCpuManagedMonitorResultV1 Invalid(string reason, ulong address) =>
        Result(HybridCpuManagedMonitorDispositionV1.Invalid, address, 0, 0, [], reason);
    private static HybridCpuManagedMonitorResultV1 Result(HybridCpuManagedMonitorDispositionV1 disposition,
        ulong address, ulong owner, int recursion, IReadOnlyList<ulong> woken, string reason = "") =>
        new(disposition, reason, address, owner, recursion, woken,
            HybridCpuPlatformContractV1.Hash($"{HybridCpuManagedSynchronizationContractV1.ContractDigest}|{disposition}|{address}|{owner}|{recursion}|{string.Join(',', woken)}|{reason}"));
    private sealed record MonitorState(ulong OwnerManagedThreadId, int Recursion);
}
