using HybridCPU.Platform.Contracts;

namespace HybridCPU.RuntimeKernel;

public sealed partial class DeterministicRuntimeKernelV1
{
    private readonly SortedDictionary<ulong, List<AddressWaiter>> _addressWaiters = [];
    private readonly Dictionary<ulong, ulong> _addressWakeEpochs = [];
    private ulong _addressWaitClock;

    public ulong AddressWakeEpoch(ulong address) =>
        _addressWakeEpochs.TryGetValue(address, out ulong epoch) ? epoch : 0;

    public HybridCpuAddressWaitResultV1 WaitOnAddress(HybridCpuAddressWaitRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Address == 0 || request.Address % 4 != 0 ||
            !TryContext(request.ContextId, out ContextState context) ||
            context.State is not (HybridCpuExecutionContextStateV1.Running or HybridCpuExecutionContextStateV1.Runnable))
            return WaitFailure(HybridCpuKernelStatusV1.InvalidRequest,
                "Address wait requires a live runnable context and a naturally aligned nonzero address.", request.Address);
        ulong epoch = AddressWakeEpoch(request.Address);
        if (request.CancellationRequested)
            return WaitSuccess(HybridCpuAddressWaitDispositionV1.Cancelled, request.Address, epoch, [request.ContextId]);
        if (request.DeadlineTick <= _addressWaitClock)
            return WaitSuccess(HybridCpuAddressWaitDispositionV1.TimedOut, request.Address, epoch, [request.ContextId]);
        if (request.ObservedValue != request.ExpectedValue)
            return WaitSuccess(HybridCpuAddressWaitDispositionV1.ValueChanged, request.Address, epoch, [request.ContextId]);
        if (request.ObservedWakeEpoch != epoch)
            return WaitSuccess(HybridCpuAddressWaitDispositionV1.WakeObserved, request.Address, epoch, [request.ContextId]);
        if (_addressWaiters.Values.Sum(static items => items.Count) >= HybridCpuManagedSynchronizationContractV1.MaximumAddressWaiters)
            return WaitFailure(HybridCpuKernelStatusV1.BudgetExhausted, "Address-wait budget exhausted.", request.Address);
        HybridCpuKernelContextResultV1 parked = ParkContext(request.ContextId,
            $"wait-address:{request.Address:x16}:epoch:{epoch}");
        if (!parked.IsSuccess) return WaitFailure(parked.Status, parked.Reason, request.Address);
        if (!_addressWaiters.TryGetValue(request.Address, out List<AddressWaiter>? waiters))
            _addressWaiters.Add(request.Address, waiters = []);
        waiters.Add(new(request.ContextId, request.ExpectedValue, epoch, request.DeadlineTick));
        waiters.Sort(static (left, right) => left.ContextId.CompareTo(right.ContextId));
        return WaitSuccess(HybridCpuAddressWaitDispositionV1.Registered, request.Address, epoch, [request.ContextId]);
    }

    public HybridCpuAddressWaitResultV1 WakeAddress(ulong address, int count)
    {
        if (address == 0 || address % 4 != 0 || count <= 0)
            return WaitFailure(HybridCpuKernelStatusV1.InvalidRequest,
                "Wake requires a naturally aligned nonzero address and positive count.", address);
        ulong epoch = checked(AddressWakeEpoch(address) + 1);
        _addressWakeEpochs[address] = epoch;
        ulong[] selected = _addressWaiters.TryGetValue(address, out List<AddressWaiter>? waiters)
            ? waiters.OrderBy(static item => item.ContextId).Take(count).Select(static item => item.ContextId).ToArray()
            : [];
        foreach (ulong contextId in selected) WakeRegisteredContext(address, contextId);
        return WaitSuccess(HybridCpuAddressWaitDispositionV1.Woken, address, epoch, selected);
    }

    public HybridCpuAddressWaitResultV1 AdvanceAddressWaitClock(ulong logicalTick)
    {
        if (logicalTick < _addressWaitClock)
            return WaitFailure(HybridCpuKernelStatusV1.InvalidRequest, "Logical wait time cannot move backwards.", 0);
        _addressWaitClock = logicalTick;
        AddressWaiter[] expired = _addressWaiters.Values.SelectMany(static items => items)
            .Where(item => item.DeadlineTick <= logicalTick).OrderBy(static item => item.ContextId).ToArray();
        foreach (AddressWaiter waiter in expired)
        {
            ulong address = _addressWaiters.Single(pair => pair.Value.Contains(waiter)).Key;
            WakeRegisteredContext(address, waiter.ContextId);
        }
        return WaitSuccess(HybridCpuAddressWaitDispositionV1.TimedOut, 0, 0,
            expired.Select(static item => item.ContextId).ToArray());
    }

    public HybridCpuAddressWaitResultV1 CancelAddressWait(ulong contextId)
    {
        (ulong Address, AddressWaiter Waiter)? found = _addressWaiters
            .SelectMany(static pair => pair.Value.Select(waiter => (pair.Key, waiter)))
            .Where(item => item.waiter.ContextId == contextId)
            .Select(item => ((ulong Address, AddressWaiter Waiter)?)(item.Key, item.waiter)).FirstOrDefault();
        if (found is null)
            return WaitFailure(HybridCpuKernelStatusV1.InvalidRequest, "Context has no registered address wait.", 0);
        WakeRegisteredContext(found.Value.Address, contextId);
        return WaitSuccess(HybridCpuAddressWaitDispositionV1.Cancelled, found.Value.Address,
            AddressWakeEpoch(found.Value.Address), [contextId]);
    }

    private void WakeRegisteredContext(ulong address, ulong contextId)
    {
        if (!_addressWaiters.TryGetValue(address, out List<AddressWaiter>? waiters)) return;
        waiters.RemoveAll(item => item.ContextId == contextId);
        if (waiters.Count == 0) _addressWaiters.Remove(address);
        if (TryContext(contextId, out ContextState context) && context.State == HybridCpuExecutionContextStateV1.Parked)
            _contexts[contextId] = Update(context with { WaitReason = string.Empty }, HybridCpuExecutionContextStateV1.Runnable);
    }

    private void RemoveAddressWait(ulong contextId)
    {
        foreach (ulong address in _addressWaiters.Where(pair => pair.Value.Any(item => item.ContextId == contextId))
                     .Select(static pair => pair.Key).ToArray())
        {
            _addressWaiters[address].RemoveAll(item => item.ContextId == contextId);
            if (_addressWaiters[address].Count == 0) _addressWaiters.Remove(address);
        }
    }

    private HybridCpuAddressWaitResultV1 WaitSuccess(HybridCpuAddressWaitDispositionV1 disposition,
        ulong address, ulong epoch, IReadOnlyList<ulong> contextIds) => new(HybridCpuKernelStatusV1.Success,
        disposition, string.Empty, address, epoch, _addressWaitClock, contextIds,
        HybridCpuManagedSynchronizationContractV1.ComputeWaitResultDigest(disposition, address, epoch,
            _addressWaitClock, contextIds));

    private HybridCpuAddressWaitResultV1 WaitFailure(HybridCpuKernelStatusV1 status, string reason, ulong address) =>
        new(status, HybridCpuAddressWaitDispositionV1.Cancelled, reason, address, AddressWakeEpoch(address),
            _addressWaitClock, [], HybridCpuManagedSynchronizationContractV1.ComputeWaitResultDigest(
                HybridCpuAddressWaitDispositionV1.Cancelled, address, AddressWakeEpoch(address), _addressWaitClock, []));

    private sealed record AddressWaiter(ulong ContextId, ulong ExpectedValue, ulong WakeEpoch, ulong DeadlineTick);
}
