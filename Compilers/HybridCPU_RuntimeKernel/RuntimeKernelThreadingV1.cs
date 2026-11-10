using HybridCPU.Platform.Contracts;

namespace HybridCPU.RuntimeKernel;

public sealed partial class DeterministicRuntimeKernelV1
{
    private readonly SortedDictionary<ulong, ContextState> _contexts = [];
    private Dictionary<ulong, HybridCpuExecutionContextStateV1>? _rendezvousPriorStates;
    private ulong _currentContextId;
    private ulong _nextContextId = 2;
    private ulong _stateSequence;
    private ulong _rendezvousEpoch;

    public HybridCpuKernelContextResultV1 CreateContext(HybridCpuExecutionContextCreateRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!HasLiveContext()) return ContextFailure(HybridCpuKernelStatusV1.NoCurrentContext, "No live execution context.");
        if (_contexts.Count >= HybridCpuPlatformContractV1.MaximumExecutionContexts)
            return ContextFailure(HybridCpuKernelStatusV1.BudgetExhausted, "Execution-context budget exhausted.");
        if (!ValidPageRange(request.StackBase, request.StackSize) ||
            !ValidPageRange(request.GuardBase, request.GuardSize) ||
            request.GuardBase > ulong.MaxValue - request.GuardSize ||
            request.GuardBase + request.GuardSize != request.StackBase ||
            request.ContextCarrierAddress == 0 || request.TlsBase == 0 ||
            request.EntryAddress == 0 || request.PreferredVirtualThreadCarrier is < -1 or > 3)
            return ContextFailure(HybridCpuKernelStatusV1.InvalidRequest,
                "A context requires an adjacent no-access guard, page-aligned stack, entry, TLS and opaque context carrier.");
        if (_mappings.Count > HybridCpuPlatformContractV1.MaximumVmMappings - 2 ||
            _mappings.Any(item => RangesOverlap(item.Address, item.Size, request.GuardBase,
                checked(request.GuardSize + request.StackSize))))
            return ContextFailure(HybridCpuKernelStatusV1.AddressConflict,
                "Context stack or guard overlaps an existing mapping or mapping budget.");
        if (request.PreferredVirtualThreadCarrier >= 0 && CarrierInUse(request.PreferredVirtualThreadCarrier))
            return ContextFailure(HybridCpuKernelStatusV1.AddressConflict, "The requested VT carrier is already bound.");
        if (_contexts.Values.Any(item => item.Descriptor.ContextCarrierAddress == request.ContextCarrierAddress ||
                item.TlsBase == request.TlsBase))
            return ContextFailure(HybridCpuKernelStatusV1.AddressConflict, "Context-carrier and TLS bases must be unique.");

        _mappings.Add(new(request.GuardBase, request.GuardSize, HybridCpuVmProtectionV1.None));
        _mappings.Add(new(request.StackBase, request.StackSize,
            HybridCpuVmProtectionV1.Read | HybridCpuVmProtectionV1.Write));
        ulong id = _nextContextId++;
        HybridCpuExecutionContextDescriptorV1 descriptor = new HybridCpuExecutionContextDescriptorV1(id, request.PreferredVirtualThreadCarrier,
            request.ContextCarrierAddress, request.EntryAddress, request.StackBase, request.StackSize,
            checked(request.StackBase + request.StackSize), SnapshotMappings());
        ContextState state = new ContextState(descriptor, HybridCpuExecutionContextStateV1.Created,
            request.GuardBase, request.GuardSize, request.TlsBase, null, null, string.Empty, NextSequence());
        _contexts.Add(id, state);
        return ContextSuccess(state);
    }

    public HybridCpuKernelContextResultV1 StartContext(ulong contextId)
    {
        if (!TryContext(contextId, out ContextState state)) return MissingContext();
        if (state.State != HybridCpuExecutionContextStateV1.Created)
            return ContextFailure(HybridCpuKernelStatusV1.InvalidRequest, "Only a created context can be started.");
        int carrier = state.Descriptor.VirtualThreadCarrier;
        if (carrier < 0)
        {
            carrier = Enumerable.Range(0, 4).FirstOrDefault(id => !CarrierInUse(id), -1);
            if (carrier < 0) return ContextFailure(HybridCpuKernelStatusV1.BudgetExhausted, "No VT execution carrier is available.");
            state = state with { Descriptor = state.Descriptor with { VirtualThreadCarrier = carrier } };
        }
        state = Update(state, HybridCpuExecutionContextStateV1.Runnable);
        _contexts[contextId] = state;
        return ContextSuccess(state);
    }

    public HybridCpuKernelContextResultV1 ExitContext(ulong contextId, int exitCode)
    {
        if (!TryContext(contextId, out ContextState state)) return MissingContext();
        if (state.State is HybridCpuExecutionContextStateV1.Created or HybridCpuExecutionContextStateV1.Exited)
            return ContextFailure(HybridCpuKernelStatusV1.InvalidRequest, "Only a live started context can exit.");
        RemoveAddressWait(contextId);
        state = Update(state, HybridCpuExecutionContextStateV1.Exited) with
        {
            ExitCode = exitCode,
            JoinTargetContextId = null,
            WaitReason = string.Empty,
            Descriptor = state.Descriptor with { VirtualThreadCarrier = -1 }
        };
        _contexts[contextId] = state;
        foreach ((ulong id, ContextState waiter) in _contexts.Where(pair => pair.Value.JoinTargetContextId == contextId).ToArray())
            _contexts[id] = Update(waiter with { JoinTargetContextId = null, WaitReason = string.Empty },
                HybridCpuExecutionContextStateV1.Runnable);
        if (_currentContextId == contextId) SelectNextRunnable(contextId);
        return ContextSuccess(state);
    }

    public HybridCpuKernelContextResultV1 JoinContext(ulong waitingContextId, ulong targetContextId)
    {
        if (waitingContextId == targetContextId) return ContextFailure(HybridCpuKernelStatusV1.InvalidRequest, "A context cannot join itself.");
        if (!TryContext(waitingContextId, out ContextState waiter) || !TryContext(targetContextId, out ContextState target))
            return MissingContext();
        if (waiter.State is not (HybridCpuExecutionContextStateV1.Running or HybridCpuExecutionContextStateV1.Runnable) ||
            target.State == HybridCpuExecutionContextStateV1.Created)
            return ContextFailure(HybridCpuKernelStatusV1.InvalidRequest, "Join requires live started contexts.");
        if (target.State == HybridCpuExecutionContextStateV1.Exited) return ContextSuccess(waiter);
        waiter = Update(waiter with { JoinTargetContextId = targetContextId, WaitReason = $"join:{targetContextId}" },
            HybridCpuExecutionContextStateV1.Joining);
        _contexts[waitingContextId] = waiter;
        if (_currentContextId == waitingContextId) SelectNextRunnable(waitingContextId);
        return ContextSuccess(waiter);
    }

    public HybridCpuKernelContextResultV1 ParkContext(ulong contextId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return ContextFailure(HybridCpuKernelStatusV1.InvalidRequest, "A stable park reason is required.");
        if (!TryContext(contextId, out ContextState state)) return MissingContext();
        if (state.State is not (HybridCpuExecutionContextStateV1.Running or HybridCpuExecutionContextStateV1.Runnable))
            return ContextFailure(HybridCpuKernelStatusV1.InvalidRequest, "Only running or runnable contexts can park.");
        state = Update(state with { WaitReason = reason }, HybridCpuExecutionContextStateV1.Parked);
        _contexts[contextId] = state;
        if (_currentContextId == contextId) SelectNextRunnable(contextId);
        return ContextSuccess(state);
    }

    public HybridCpuKernelContextResultV1 UnparkContext(ulong contextId)
    {
        if (!TryContext(contextId, out ContextState state)) return MissingContext();
        if (state.State != HybridCpuExecutionContextStateV1.Parked)
            return ContextFailure(HybridCpuKernelStatusV1.InvalidRequest, "Only a parked context can be unparked.");
        state = Update(state with { WaitReason = string.Empty }, HybridCpuExecutionContextStateV1.Runnable);
        _contexts[contextId] = state;
        return ContextSuccess(state);
    }

    public HybridCpuKernelContextResultV1 BindContext(ulong contextId, int virtualThreadCarrier)
    {
        if (virtualThreadCarrier is < 0 or > 3) return ContextFailure(HybridCpuKernelStatusV1.InvalidRequest, "VT carrier must be in range 0..3.");
        if (!TryContext(contextId, out ContextState state)) return MissingContext();
        if (state.State is HybridCpuExecutionContextStateV1.Running or HybridCpuExecutionContextStateV1.GcRendezvous or HybridCpuExecutionContextStateV1.Exited ||
            CarrierInUse(virtualThreadCarrier, contextId))
            return ContextFailure(HybridCpuKernelStatusV1.InvalidRequest, "The context or VT carrier cannot be rebound in its current state.");
        state = Touch(state with { Descriptor = state.Descriptor with { VirtualThreadCarrier = virtualThreadCarrier } });
        _contexts[contextId] = state;
        return ContextSuccess(state);
    }

    public HybridCpuKernelContextResultV1 UnbindContext(ulong contextId)
    {
        if (!TryContext(contextId, out ContextState state)) return MissingContext();
        if (state.State is HybridCpuExecutionContextStateV1.Running or HybridCpuExecutionContextStateV1.GcRendezvous or HybridCpuExecutionContextStateV1.Exited)
            return ContextFailure(HybridCpuKernelStatusV1.InvalidRequest, "A running, rendezvous or exited context cannot be unbound.");
        state = Touch(state with { Descriptor = state.Descriptor with { VirtualThreadCarrier = -1 } });
        _contexts[contextId] = state;
        return ContextSuccess(state);
    }

    public HybridCpuKernelContextResultV1 ScheduleNext()
    {
        if (_rendezvousPriorStates is not null) return ContextFailure(HybridCpuKernelStatusV1.InvalidRequest, "Scheduling is stopped during GC rendezvous.");
        if (_trapOrder.Count != 0) return ContextFailure(HybridCpuKernelStatusV1.TrapStateMismatch, "Cannot switch contexts with an attached trap.");
        if (TryContext(_currentContextId, out ContextState current) && current.State == HybridCpuExecutionContextStateV1.Running)
            _contexts[_currentContextId] = Update(current, HybridCpuExecutionContextStateV1.Runnable);
        ContextState? next = SelectNextRunnable(_currentContextId);
        return next is null
            ? ContextFailure(HybridCpuKernelStatusV1.NoCurrentContext, "No runnable context exists.")
            : ContextSuccess(next);
    }

    public IReadOnlyList<HybridCpuExecutionContextSnapshotV1> Contexts() =>
        _contexts.Values.Select(Snapshot).ToArray();

    public HybridCpuKernelRendezvousResultV1 GcRendezvousEnter(ulong requestingContextId)
    {
        if (_rendezvousPriorStates is not null)
            return RendezvousFailure(HybridCpuKernelStatusV1.InvalidRequest, "A GC rendezvous is already active.");
        if (!TryContext(requestingContextId, out ContextState requester) ||
            requester.State is HybridCpuExecutionContextStateV1.Created or HybridCpuExecutionContextStateV1.Exited)
            return RendezvousFailure(HybridCpuKernelStatusV1.InvalidRequest, "The requesting context is not live.");
        if (_trapOrder.Count != 0) return RendezvousFailure(HybridCpuKernelStatusV1.TrapStateMismatch, "Cannot rendezvous with an attached trap.");

        _rendezvousPriorStates = _contexts.ToDictionary(static pair => pair.Key, static pair => pair.Value.State);
        foreach ((ulong id, ContextState state) in _contexts.ToArray())
            if (state.State is HybridCpuExecutionContextStateV1.Running or HybridCpuExecutionContextStateV1.Runnable or HybridCpuExecutionContextStateV1.Parked)
                _contexts[id] = Update(state, HybridCpuExecutionContextStateV1.GcRendezvous);
        ulong epoch = ++_rendezvousEpoch;
        HybridCpuExecutionContextSnapshotV1[] contexts = Contexts().ToArray();
        return new(HybridCpuKernelStatusV1.Success, string.Empty,
            new(epoch, requestingContextId, contexts,
                HybridCpuKernelThreadingContractV1.ComputeSnapshotDigest(epoch, requestingContextId, contexts)));
    }

    public HybridCpuKernelRendezvousResultV1 GcRendezvousLeave(ulong requestingContextId, ulong epoch)
    {
        if (_rendezvousPriorStates is null || epoch != _rendezvousEpoch)
            return RendezvousFailure(HybridCpuKernelStatusV1.InvalidRequest, "Rendezvous epoch is absent or stale.");
        foreach ((ulong id, HybridCpuExecutionContextStateV1 prior) in _rendezvousPriorStates.OrderBy(static pair => pair.Key))
            if (_contexts.TryGetValue(id, out ContextState? state) && state.State == HybridCpuExecutionContextStateV1.GcRendezvous)
                _contexts[id] = Update(state, prior);
        _rendezvousPriorStates = null;
        if (!_contexts.TryGetValue(_currentContextId, out ContextState? current) || current.State != HybridCpuExecutionContextStateV1.Running)
            SelectNextRunnable(requestingContextId == 0 ? 0 : requestingContextId - 1);
        HybridCpuExecutionContextSnapshotV1[] contexts = Contexts().ToArray();
        return new(HybridCpuKernelStatusV1.Success, string.Empty,
            new(epoch, requestingContextId, contexts,
                HybridCpuKernelThreadingContractV1.ComputeSnapshotDigest(epoch, requestingContextId, contexts)));
    }

    private void InitializeInitialContext(HybridCpuExecutionContextDescriptorV1 descriptor)
    {
        _currentContextId = descriptor.ContextId;
        _contexts.Add(descriptor.ContextId, new(descriptor, HybridCpuExecutionContextStateV1.Running,
            0, 0, descriptor.ContextCarrierAddress, null, null, string.Empty, NextSequence()));
    }

    private void TerminateProcessContexts(int exitCode)
    {
        foreach ((ulong id, ContextState state) in _contexts.ToArray())
        {
            RemoveAddressWait(id);
            _contexts[id] = Update(state, HybridCpuExecutionContextStateV1.Exited) with
            {
                ExitCode = exitCode,
                JoinTargetContextId = null,
                WaitReason = string.Empty,
                Descriptor = state.Descriptor with { VirtualThreadCarrier = -1 }
            };
        }

        _rendezvousPriorStates = null;
        _currentContextId = 0;
    }

    private void RefreshRegisteredContextMappings(IReadOnlyList<HybridCpuVmRangeV1> mappings)
    {
        foreach ((ulong id, ContextState state) in _contexts.ToArray())
            _contexts[id] = state with { Descriptor = state.Descriptor with { VmMappings = mappings } };
    }

    private ContextState? SelectNextRunnable(ulong afterContextId)
    {
        ContextState? next = _contexts.Values.Where(static item => item.State == HybridCpuExecutionContextStateV1.Runnable)
            .OrderBy(item => item.Descriptor.ContextId > afterContextId ? 0 : 1)
            .ThenBy(static item => item.Descriptor.ContextId).FirstOrDefault();
        if (next is null) return null;
        next = Update(next, HybridCpuExecutionContextStateV1.Running);
        _contexts[next.Descriptor.ContextId] = next;
        _currentContextId = next.Descriptor.ContextId;
        _context = next.Descriptor with { VmMappings = SnapshotMappings() };
        return next;
    }

    private bool CarrierInUse(int carrier, ulong exceptContextId = 0) => _contexts.Values.Any(item =>
        item.Descriptor.ContextId != exceptContextId && item.Descriptor.VirtualThreadCarrier == carrier &&
        item.State != HybridCpuExecutionContextStateV1.Exited);

    private bool TryContext(ulong id, out ContextState state) => _contexts.TryGetValue(id, out state!);
    private ulong NextSequence() => ++_stateSequence;
    private ContextState Touch(ContextState state) => state with { StateSequence = NextSequence() };
    private ContextState Update(ContextState state, HybridCpuExecutionContextStateV1 next) =>
        state with { State = next, StateSequence = NextSequence() };
    private static bool ValidPageRange(ulong address, ulong size) =>
        size != 0 && address % 4096 == 0 && size % 4096 == 0 && address <= ulong.MaxValue - size;

    private HybridCpuKernelContextResultV1 ContextSuccess(ContextState state) =>
        new(HybridCpuKernelStatusV1.Success, string.Empty, Snapshot(state));
    private static HybridCpuExecutionContextSnapshotV1 Snapshot(ContextState state) => new(
        state.Descriptor, state.State, state.GuardBase, state.GuardSize, state.TlsBase,
        state.ExitCode, state.JoinTargetContextId, state.WaitReason, state.StateSequence);
    private static HybridCpuKernelContextResultV1 ContextFailure(HybridCpuKernelStatusV1 status, string reason) => new(status, reason, null);
    private static HybridCpuKernelContextResultV1 MissingContext() => ContextFailure(HybridCpuKernelStatusV1.InvalidRequest, "Execution context does not exist.");
    private static HybridCpuKernelRendezvousResultV1 RendezvousFailure(HybridCpuKernelStatusV1 status, string reason) => new(status, reason, null);

    private sealed record ContextState(
        HybridCpuExecutionContextDescriptorV1 Descriptor,
        HybridCpuExecutionContextStateV1 State,
        ulong GuardBase,
        ulong GuardSize,
        ulong TlsBase,
        int? ExitCode,
        ulong? JoinTargetContextId,
        string WaitReason,
        ulong StateSequence);
}
