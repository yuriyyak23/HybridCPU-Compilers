using HybridCPU.Platform.Contracts;

namespace HybridCPU.RuntimeKernel;

/// <summary>
/// Language-neutral monotonic/deadline service. Time advances only through the explicit virtual
/// clock seam, so tests and simulator integrations can reproduce every wake decision.
/// </summary>
public sealed partial class DeterministicRuntimeKernelV1
{
    private readonly SortedDictionary<ulong, DeadlineState> _deadlines = [];
    private ulong _monotonicTick;
    private ulong _deadlineSequence;

    public ulong MonotonicTicks() => _monotonicTick;

    public HybridCpuDeadlineResultV1 SleepUntil(HybridCpuDeadlineRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContextId == 0 || request.Token == 0 || request.DeadlineTick == ulong.MaxValue ||
            !TryContext(request.ContextId, out ContextState context) ||
            context.State is HybridCpuExecutionContextStateV1.Created or HybridCpuExecutionContextStateV1.Exited or HybridCpuExecutionContextStateV1.GcRendezvous)
            return Deadline(HybridCpuDeadlineDispositionV1.Invalid, request.Token, [], "A live schedulable context, finite deadline and nonzero token are required.");
        if (_deadlines.ContainsKey(request.Token))
            return Deadline(HybridCpuDeadlineDispositionV1.Invalid, request.Token, [], "Deadline token is already registered.");
        if (request.DeadlineTick <= _monotonicTick)
            return Deadline(HybridCpuDeadlineDispositionV1.Completed, request.Token, [request.Token], string.Empty);
        if (_deadlines.Count >= HybridCpuAsyncRuntimeContractV1.MaximumPendingDeadlines)
            return Deadline(HybridCpuDeadlineDispositionV1.BudgetExhausted, request.Token, [], "Deadline budget exhausted.");

        HybridCpuKernelContextResultV1 parked = ParkContext(request.ContextId, $"deadline:{request.Token}:{request.DeadlineTick}");
        if (!parked.IsSuccess)
            return Deadline(HybridCpuDeadlineDispositionV1.Invalid, request.Token, [], parked.Reason);
        _deadlines.Add(request.Token, new(request.ContextId, request.DeadlineTick, ++_deadlineSequence));
        return Deadline(HybridCpuDeadlineDispositionV1.Scheduled, request.Token, [], string.Empty);
    }

    public HybridCpuDeadlineResultV1 AdvanceMonotonicTime(ulong tick)
    {
        if (tick < _monotonicTick)
            return Deadline(HybridCpuDeadlineDispositionV1.Invalid, 0, [], "Monotonic time cannot move backwards.");
        _monotonicTick = tick;
        ulong[] completed = _deadlines
            .Where(pair => pair.Value.DeadlineTick <= tick)
            .OrderBy(static pair => pair.Value.DeadlineTick)
            .ThenBy(static pair => pair.Value.Sequence)
            .ThenBy(static pair => pair.Key)
            .Select(static pair => pair.Key).ToArray();
        foreach (ulong token in completed)
        {
            DeadlineState state = _deadlines[token];
            _deadlines.Remove(token);
            if (TryContext(state.ContextId, out ContextState context) && context.State == HybridCpuExecutionContextStateV1.Parked)
                _ = UnparkContext(state.ContextId);
        }
        return Deadline(HybridCpuDeadlineDispositionV1.Completed, 0, completed, string.Empty);
    }

    public HybridCpuDeadlineResultV1 CancelDeadline(ulong token)
    {
        if (token == 0 || !_deadlines.Remove(token, out DeadlineState? state))
            return Deadline(HybridCpuDeadlineDispositionV1.Invalid, token, [], "Deadline token is not registered.");
        if (TryContext(state.ContextId, out ContextState context) && context.State == HybridCpuExecutionContextStateV1.Parked)
            _ = UnparkContext(state.ContextId);
        return Deadline(HybridCpuDeadlineDispositionV1.Cancelled, token, [], string.Empty);
    }

    public IReadOnlyList<(ulong Token, ulong ContextId, ulong DeadlineTick)> Deadlines() => _deadlines
        .OrderBy(static pair => pair.Value.DeadlineTick).ThenBy(static pair => pair.Value.Sequence)
        .ThenBy(static pair => pair.Key)
        .Select(static pair => (pair.Key, pair.Value.ContextId, pair.Value.DeadlineTick)).ToArray();

    private HybridCpuDeadlineResultV1 Deadline(HybridCpuDeadlineDispositionV1 disposition, ulong token,
        IReadOnlyList<ulong> completed, string reason) => new(disposition, _monotonicTick, token, completed, reason,
            HybridCpuAsyncRuntimeContractV1.ResultDigest(disposition, _monotonicTick, token, completed, reason));

    private sealed record DeadlineState(ulong ContextId, ulong DeadlineTick, ulong Sequence);
}
