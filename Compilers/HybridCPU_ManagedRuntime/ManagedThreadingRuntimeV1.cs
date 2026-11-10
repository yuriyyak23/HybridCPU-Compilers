using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;

namespace HybridCPU.ManagedRuntime;

public enum HybridCpuManagedThreadingStatusV1 : byte
{
    Success = 0,
    Disabled = 1,
    InvalidRequest = 2,
    BudgetExhausted = 3,
    KernelFailure = 4,
    GcFailure = 5
}

public enum HybridCpuManagedThreadLifecycleStateV1 : byte
{
    Created = 0,
    Runnable = 1,
    Running = 2,
    WaitSleepJoin = 3,
    Stopped = 4,
    TerminatedByUnhandledException = 5
}

public sealed record HybridCpuManagedThreadingOptionsV1(
    bool EnableManagedThreading,
    int MaximumThreads,
    int MaximumTlsBytesPerThread,
    string OptionsDigest)
{
    public static HybridCpuManagedThreadingOptionsV1 Production { get; } = Create(false,
        HybridCpuPlatformContractV1.MaximumExecutionContexts,
        HybridCpuPlatformContractV1.MaximumManagedTlsBytesPerContext);

    public static HybridCpuManagedThreadingOptionsV1 Qualification { get; } = Create(true,
        HybridCpuPlatformContractV1.MaximumExecutionContexts,
        HybridCpuPlatformContractV1.MaximumManagedTlsBytesPerContext);

    public static HybridCpuManagedThreadingOptionsV1 Create(bool enabled, int maximumThreads, int maximumTlsBytes) =>
        new(enabled, maximumThreads, maximumTlsBytes, HybridCpuPlatformContractV1.Hash(string.Join('|',
            "hybridcpu.managed-threading-options/v1", enabled, maximumThreads, maximumTlsBytes)));
}

public sealed record HybridCpuManagedTlsSlotRequestV1(string Identity, bool ContainsObjectReference);

public sealed record HybridCpuManagedTlsSlotV1(
    string Identity,
    int OffsetBytes,
    int SizeBytes,
    bool ContainsObjectReference);

public sealed record HybridCpuManagedTlsLayoutV1(
    IReadOnlyList<HybridCpuManagedTlsSlotV1> Slots,
    int SizeBytes,
    string LayoutDigest)
{
    public static HybridCpuManagedTlsLayoutV1 Create(IReadOnlyList<HybridCpuManagedTlsSlotRequestV1> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Any(static item => item is null || string.IsNullOrWhiteSpace(item.Identity)) ||
            requests.Select(static item => item.Identity).Distinct(StringComparer.Ordinal).Count() != requests.Count)
            throw new ArgumentException("TLS slot identities must be present and unique.", nameof(requests));
        HybridCpuManagedTlsSlotV1[] slots = requests.OrderBy(static item => item.Identity, StringComparer.Ordinal)
            .Select((item, index) => new HybridCpuManagedTlsSlotV1(item.Identity, checked(index * 8), 8,
                item.ContainsObjectReference)).ToArray();
        int size = checked(slots.Length * 8);
        if (size > HybridCpuPlatformContractV1.MaximumManagedTlsBytesPerContext)
            throw new ArgumentException("TLS layout exceeds the platform budget.", nameof(requests));
        return new(slots, size, HybridCpuPlatformContractV1.Hash(string.Join('|',
            "hybridcpu.managed-tls-layout/v1", size,
            string.Join(';', slots.Select(static item =>
                $"{item.Identity}:{item.OffsetBytes}:{item.SizeBytes}:{item.ContainsObjectReference}")))));
    }
}

public sealed record HybridCpuManagedThreadSnapshotV1(
    ulong ManagedThreadId,
    ulong ExecutionContextId,
    HybridCpuManagedThreadLifecycleStateV1 State,
    string Name,
    int? ExitCode,
    string? UnhandledExceptionIdentity,
    ulong TlsBase,
    int VirtualThreadCarrier,
    ulong StateSequence);

public sealed record HybridCpuManagedThreadResultV1(
    HybridCpuManagedThreadingStatusV1 Status,
    string Reason,
    HybridCpuManagedThreadSnapshotV1? Thread)
{
    public bool IsSuccess => Status == HybridCpuManagedThreadingStatusV1.Success && Thread is not null;
}

public sealed record HybridCpuManagedMultiThreadGcResultV1(
    HybridCpuManagedThreadingStatusV1 Status,
    string Reason,
    HybridCpuManagedNonMovingGcResultV1? Collection,
    string EnterSnapshotDigest,
    string LeaveSnapshotDigest)
{
    public bool IsSuccess => Status == HybridCpuManagedThreadingStatusV1.Success && Collection?.IsSuccess == true;
}

/// <summary>
/// ManagedRuntime owns managed Thread identity, TLS layout/storage, lifecycle and all-thread
/// root enumeration. RuntimeKernel supplies only language-neutral execution contexts, VT binding,
/// wait states and rendezvous. A VT is never used as managed Thread semantic identity.
/// </summary>
public sealed partial class HybridCpuManagedThreadingRuntimeV1
{
    public const string SchemaId = "hybridcpu.managed-threading/v1";
    private readonly IHybridCpuRuntimeKernelV1 _kernel;
    private readonly HybridCpuManagedThreadingOptionsV1 _options;
    private readonly HybridCpuManagedTlsLayoutV1 _tlsLayout;
    private readonly SortedDictionary<ulong, ManagedThreadState> _threads = [];
    private ulong _nextManagedThreadId = 1;
    private ulong _stateSequence;

    public HybridCpuManagedThreadingRuntimeV1(IHybridCpuRuntimeKernelV1 kernel,
        HybridCpuManagedTlsLayoutV1 tlsLayout,
        HybridCpuManagedThreadingOptionsV1? options = null)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _tlsLayout = tlsLayout ?? throw new ArgumentNullException(nameof(tlsLayout));
        _options = options ?? HybridCpuManagedThreadingOptionsV1.Production;
        ContractDigest = HybridCpuPlatformContractV1.Hash(string.Join('|', SchemaId,
            HybridCpuPlatformContractV1.ContractDigest, HybridCpuKernelThreadingContractV1.ContractDigest,
            _tlsLayout.LayoutDigest, _options.OptionsDigest,
            "thread-identity=managed-runtime", "vt=execution-carrier", "roots=managed-runtime",
            "rendezvous=runtime-kernel", "ise-authority=false"));
    }

    public string ContractDigest { get; }
    public bool HasManagedThreadIdentityAuthority => true;
    public bool HasTlsLayoutAuthority => true;
    public bool HasRootEnumerationAuthority => true;
    public bool HasIseExecutionAuthority => false;

    public HybridCpuManagedThreadResultV1 AttachInitialThread(string name)
    {
        if (!Enabled(out HybridCpuManagedThreadResultV1? failure)) return failure;
        HybridCpuExecutionContextDescriptorV1? context = _kernel.CurrentContext();
        if (context is null || _threads.Count != 0 || string.IsNullOrWhiteSpace(name))
            return Fail("An initial kernel context and a unique managed main thread are required.");
        return AddThread(context, context.ContextCarrierAddress, HybridCpuManagedThreadLifecycleStateV1.Running, name);
    }

    public HybridCpuManagedThreadResultV1 CreateThread(string name,
        HybridCpuExecutionContextCreateRequestV1 request)
    {
        if (!Enabled(out HybridCpuManagedThreadResultV1? failure)) return failure;
        if (string.IsNullOrWhiteSpace(name) || _threads.Count >= _options.MaximumThreads)
            return new(HybridCpuManagedThreadingStatusV1.BudgetExhausted,
                "Managed thread identity or budget is invalid.", null);
        HybridCpuKernelContextResultV1 created = _kernel.CreateContext(request);
        return created.IsSuccess
            ? AddThread(created.Context!.Descriptor, created.Context.TlsBase, HybridCpuManagedThreadLifecycleStateV1.Created, name)
            : KernelFail(created.Reason);
    }

    public HybridCpuManagedThreadResultV1 Start(ulong managedThreadId) => Transition(managedThreadId,
        static (kernel, contextId) => kernel.StartContext(contextId));

    public HybridCpuManagedThreadResultV1 Park(ulong managedThreadId, string reason) => Transition(managedThreadId,
        (kernel, contextId) => kernel.ParkContext(contextId, reason));

    public HybridCpuManagedThreadResultV1 Unpark(ulong managedThreadId) => Transition(managedThreadId,
        static (kernel, contextId) => kernel.UnparkContext(contextId));

    public HybridCpuManagedThreadResultV1 Join(ulong waitingManagedThreadId, ulong targetManagedThreadId)
    {
        if (!TryThread(waitingManagedThreadId, out ManagedThreadState waiter) ||
            !TryThread(targetManagedThreadId, out ManagedThreadState target)) return Fail("Managed thread does not exist.");
        HybridCpuKernelContextResultV1 result = _kernel.JoinContext(waiter.ContextId, target.ContextId);
        if (!result.IsSuccess) return KernelFail(result.Reason);
        RefreshStates();
        return Success(_threads[waitingManagedThreadId]);
    }

    public HybridCpuManagedThreadResultV1 Exit(ulong managedThreadId, int exitCode)
    {
        if (!TryThread(managedThreadId, out ManagedThreadState thread)) return Fail("Managed thread does not exist.");
        HybridCpuKernelContextResultV1 result = _kernel.ExitContext(thread.ContextId, exitCode);
        if (!result.IsSuccess) return KernelFail(result.Reason);
        _threads[managedThreadId] = Touch(thread with { State = HybridCpuManagedThreadLifecycleStateV1.Stopped, ExitCode = exitCode });
        RefreshStates();
        return Success(_threads[managedThreadId]);
    }

    public HybridCpuManagedThreadResultV1 TerminateByUnhandledException(ulong managedThreadId,
        string exceptionIdentity, int exitCode = -1)
    {
        if (string.IsNullOrWhiteSpace(exceptionIdentity) || !TryThread(managedThreadId, out ManagedThreadState thread))
            return Fail("A managed thread and exact exception identity are required.");
        HybridCpuKernelContextResultV1 result = _kernel.ExitContext(thread.ContextId, exitCode);
        if (!result.IsSuccess) return KernelFail(result.Reason);
        _threads[managedThreadId] = Touch(thread with
        {
            State = HybridCpuManagedThreadLifecycleStateV1.TerminatedByUnhandledException,
            ExitCode = exitCode,
            UnhandledExceptionIdentity = exceptionIdentity
        });
        RefreshStates();
        return Success(_threads[managedThreadId]);
    }

    public HybridCpuManagedThreadResultV1 ScheduleNext()
    {
        HybridCpuKernelContextResultV1 scheduled = _kernel.ScheduleNext();
        if (!scheduled.IsSuccess) return KernelFail(scheduled.Reason);
        RefreshStates();
        ManagedThreadState thread = _threads.Values.Single(item => item.ContextId == scheduled.Context!.Descriptor.ContextId);
        return Success(thread);
    }

    public HybridCpuManagedThreadResultV1 WriteTls(ulong managedThreadId, string slotIdentity, ulong value)
    {
        if (!TryThread(managedThreadId, out ManagedThreadState thread)) return Fail("Managed thread does not exist.");
        if (thread.State is HybridCpuManagedThreadLifecycleStateV1.Stopped or HybridCpuManagedThreadLifecycleStateV1.TerminatedByUnhandledException)
            return Fail("A terminated managed thread has no writable TLS state.");
        HybridCpuManagedTlsSlotV1? slot = _tlsLayout.Slots.SingleOrDefault(item =>
            string.Equals(item.Identity, slotIdentity, StringComparison.Ordinal));
        if (slot is null) return Fail("TLS slot does not exist.");
        thread.TlsValues[slot.OffsetBytes] = value;
        _threads[managedThreadId] = Touch(thread);
        return Success(_threads[managedThreadId]);
    }

    public bool TryReadTls(ulong managedThreadId, string slotIdentity, out ulong value)
    {
        value = 0;
        if (!TryThread(managedThreadId, out ManagedThreadState thread)) return false;
        HybridCpuManagedTlsSlotV1? slot = _tlsLayout.Slots.SingleOrDefault(item =>
            string.Equals(item.Identity, slotIdentity, StringComparison.Ordinal));
        return slot is not null && thread.TlsValues.TryGetValue(slot.OffsetBytes, out value);
    }

    public HybridCpuManagedThreadResultV1 PublishGcFrames(ulong managedThreadId,
        IReadOnlyList<HybridCpuManagedGcFrameSnapshotV1> frames)
    {
        if (!TryThread(managedThreadId, out ManagedThreadState thread) || frames is null || frames.Any(static item => item is null))
            return Fail("A managed thread and exact frame snapshots are required.");
        _threads[managedThreadId] = Touch(thread with { Frames = frames.ToArray() });
        return Success(_threads[managedThreadId]);
    }

    public IReadOnlyList<HybridCpuManagedGcRootV1> EnumerateThreadRoots()
    {
        List<HybridCpuManagedGcRootV1> roots = new List<HybridCpuManagedGcRootV1>();
        foreach (ManagedThreadState thread in _threads.Values
            .Where(static item => item.State is not (HybridCpuManagedThreadLifecycleStateV1.Stopped or HybridCpuManagedThreadLifecycleStateV1.TerminatedByUnhandledException))
            .OrderBy(static item => item.ManagedThreadId))
            foreach (HybridCpuManagedTlsSlotV1 slot in _tlsLayout.Slots.Where(static item => item.ContainsObjectReference))
                if (thread.TlsValues.TryGetValue(slot.OffsetBytes, out ulong value) && value != 0)
                    roots.Add(new($"thread:{thread.ManagedThreadId}:tls:{slot.Identity}",
                        HybridCpuManagedGcRootSourceV1.ThreadStatic, value));
        return roots;
    }

    public HybridCpuManagedMultiThreadGcResultV1 CollectAllThreads(ulong requestingManagedThreadId,
        HybridCpuManagedNonMovingGcV1 collector,
        IReadOnlyList<HybridCpuManagedStackMapRegistrationV1> registrations,
        IReadOnlyList<HybridCpuManagedGcRootV1> additionalRoots,
        HybridCpuManagedStringRuntimeV1? strings = null,
        HybridCpuManagedNonMovingGcOptionsV1? gcOptions = null)
    {
        ArgumentNullException.ThrowIfNull(collector);
        if (!TryThread(requestingManagedThreadId, out ManagedThreadState requester) ||
            registrations is null || additionalRoots is null)
            return MultiFail(HybridCpuManagedThreadingStatusV1.InvalidRequest,
                "The requesting thread and root inputs are required.");
        HybridCpuKernelRendezvousResultV1 enter = _kernel.GcRendezvousEnter(requester.ContextId);
        if (!enter.IsSuccess) return MultiFail(HybridCpuManagedThreadingStatusV1.KernelFailure, enter.Reason);
        HybridCpuManagedNonMovingGcResultV1? collection = null;
        HybridCpuKernelRendezvousResultV1 leave;
        try
        {
            HybridCpuManagedGcFrameSnapshotV1[] frames = _threads.Values
                .Where(static item => item.State is not (HybridCpuManagedThreadLifecycleStateV1.Stopped or HybridCpuManagedThreadLifecycleStateV1.TerminatedByUnhandledException))
                .OrderBy(static item => item.ManagedThreadId).SelectMany(static item => item.Frames).ToArray();
            HybridCpuManagedGcRootV1[] roots = additionalRoots.Concat(EnumerateThreadRoots())
                .OrderBy(static item => item.Source).ThenBy(static item => item.Identity, StringComparer.Ordinal).ToArray();
            collection = collector.Collect(new(registrations, frames, roots, strings),
                gcOptions ?? HybridCpuManagedNonMovingGcOptionsV1.Qualification);
        }
        finally
        {
            leave = _kernel.GcRendezvousLeave(requester.ContextId, enter.Snapshot!.Epoch);
            RefreshStates();
        }
        if (!leave.IsSuccess)
            return new(HybridCpuManagedThreadingStatusV1.KernelFailure, leave.Reason, collection,
                enter.Snapshot!.SnapshotDigest, string.Empty);
        return new(collection!.IsSuccess ? HybridCpuManagedThreadingStatusV1.Success : HybridCpuManagedThreadingStatusV1.GcFailure,
            collection.Reason, collection, enter.Snapshot!.SnapshotDigest, leave.Snapshot!.SnapshotDigest);
    }

    public IReadOnlyList<HybridCpuManagedThreadSnapshotV1> Threads()
    {
        RefreshStates();
        return _threads.Values.Select(Snapshot).ToArray();
    }

    private HybridCpuManagedThreadResultV1 AddThread(HybridCpuExecutionContextDescriptorV1 context,
        ulong tlsBase, HybridCpuManagedThreadLifecycleStateV1 state, string name)
    {
        ulong id = _nextManagedThreadId++;
        ManagedThreadState thread = new ManagedThreadState(id, context.ContextId, state, name, null, null,
            tlsBase, context.VirtualThreadCarrier, NextSequence(), [], []);
        _threads.Add(id, thread);
        return Success(thread);
    }

    private HybridCpuManagedThreadResultV1 Transition(ulong managedThreadId,
        Func<IHybridCpuRuntimeKernelV1, ulong, HybridCpuKernelContextResultV1> transition)
    {
        if (!TryThread(managedThreadId, out ManagedThreadState thread)) return Fail("Managed thread does not exist.");
        HybridCpuKernelContextResultV1 result = transition(_kernel, thread.ContextId);
        if (!result.IsSuccess) return KernelFail(result.Reason);
        RefreshStates();
        return Success(_threads[managedThreadId]);
    }

    private void RefreshStates()
    {
        Dictionary<ulong, HybridCpuExecutionContextSnapshotV1> contexts = _kernel.Contexts()
            .ToDictionary(static item => item.Descriptor.ContextId);
        foreach ((ulong id, ManagedThreadState thread) in _threads.ToArray())
        {
            if (!contexts.TryGetValue(thread.ContextId, out HybridCpuExecutionContextSnapshotV1? context)) continue;
            HybridCpuManagedThreadLifecycleStateV1 state = thread.State is HybridCpuManagedThreadLifecycleStateV1.Stopped or HybridCpuManagedThreadLifecycleStateV1.TerminatedByUnhandledException
                ? thread.State
                : context.State switch
                {
                    HybridCpuExecutionContextStateV1.Created => HybridCpuManagedThreadLifecycleStateV1.Created,
                    HybridCpuExecutionContextStateV1.Running => HybridCpuManagedThreadLifecycleStateV1.Running,
                    HybridCpuExecutionContextStateV1.Runnable => HybridCpuManagedThreadLifecycleStateV1.Runnable,
                    HybridCpuExecutionContextStateV1.Parked or HybridCpuExecutionContextStateV1.Joining or HybridCpuExecutionContextStateV1.GcRendezvous => HybridCpuManagedThreadLifecycleStateV1.WaitSleepJoin,
                    HybridCpuExecutionContextStateV1.Exited => HybridCpuManagedThreadLifecycleStateV1.Stopped,
                    _ => thread.State
                };
            if (state != thread.State || context.Descriptor.VirtualThreadCarrier != thread.VirtualThreadCarrier)
                _threads[id] = Touch(thread with { State = state, VirtualThreadCarrier = context.Descriptor.VirtualThreadCarrier });
        }
    }

    private bool Enabled(out HybridCpuManagedThreadResultV1 failure)
    {
        if (_options.EnableManagedThreading && _options.MaximumThreads is > 0 and <= HybridCpuPlatformContractV1.MaximumExecutionContexts &&
            _options.MaximumTlsBytesPerThread is > 0 and <= HybridCpuPlatformContractV1.MaximumManagedTlsBytesPerContext &&
            _tlsLayout.SizeBytes <= _options.MaximumTlsBytesPerThread)
        {
            failure = null!;
            return true;
        }
        failure = new(HybridCpuManagedThreadingStatusV1.Disabled,
            "Managed threading is default-disabled or its deterministic budgets are invalid.", null);
        return false;
    }

    private bool TryThread(ulong id, out ManagedThreadState thread) => _threads.TryGetValue(id, out thread!);
    internal bool TryResolveContext(ulong managedThreadId, out ulong contextId)
    {
        contextId = 0;
        if (!TryThread(managedThreadId, out ManagedThreadState thread) ||
            thread.State is HybridCpuManagedThreadLifecycleStateV1.Stopped or HybridCpuManagedThreadLifecycleStateV1.TerminatedByUnhandledException)
            return false;
        contextId = thread.ContextId;
        return true;
    }

    internal bool TryResolveManagedThread(ulong contextId, out ulong managedThreadId)
    {
        ManagedThreadState? thread = _threads.Values.SingleOrDefault(item => item.ContextId == contextId &&
            item.State is not (HybridCpuManagedThreadLifecycleStateV1.Stopped or HybridCpuManagedThreadLifecycleStateV1.TerminatedByUnhandledException));
        managedThreadId = thread?.ManagedThreadId ?? 0;
        return thread is not null;
    }
    private ulong NextSequence() => ++_stateSequence;
    private ManagedThreadState Touch(ManagedThreadState thread) => thread with { StateSequence = NextSequence() };
    private static HybridCpuManagedThreadSnapshotV1 Snapshot(ManagedThreadState thread) => new(
        thread.ManagedThreadId, thread.ContextId, thread.State, thread.Name, thread.ExitCode,
        thread.UnhandledExceptionIdentity, thread.TlsBase, thread.VirtualThreadCarrier, thread.StateSequence);
    private static HybridCpuManagedThreadResultV1 Success(ManagedThreadState thread) =>
        new(HybridCpuManagedThreadingStatusV1.Success, string.Empty, Snapshot(thread));
    private static HybridCpuManagedThreadResultV1 Fail(string reason) =>
        new(HybridCpuManagedThreadingStatusV1.InvalidRequest, reason, null);
    private static HybridCpuManagedThreadResultV1 KernelFail(string reason) =>
        new(HybridCpuManagedThreadingStatusV1.KernelFailure, reason, null);
    private static HybridCpuManagedMultiThreadGcResultV1 MultiFail(HybridCpuManagedThreadingStatusV1 status, string reason) =>
        new(status, reason, null, string.Empty, string.Empty);

    private sealed record ManagedThreadState(
        ulong ManagedThreadId,
        ulong ContextId,
        HybridCpuManagedThreadLifecycleStateV1 State,
        string Name,
        int? ExitCode,
        string? UnhandledExceptionIdentity,
        ulong TlsBase,
        int VirtualThreadCarrier,
        ulong StateSequence,
        Dictionary<int, ulong> TlsValues,
        IReadOnlyList<HybridCpuManagedGcFrameSnapshotV1> Frames);
}
