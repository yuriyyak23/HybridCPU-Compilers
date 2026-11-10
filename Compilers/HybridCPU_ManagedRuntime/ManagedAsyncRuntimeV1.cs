using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;

namespace HybridCPU.ManagedRuntime;

public enum HybridCpuManagedTaskStatusV1 : byte
{
    Pending = 0,
    Succeeded = 1,
    Faulted = 2,
    Cancelled = 3
}

public enum HybridCpuManagedExecutionContextPolicyV1 : byte
{
    Suppress = 0,
    FlowImmutableCarrier = 1
}

public enum HybridCpuManagedAsyncStatusV1 : byte
{
    Success = 0,
    Disabled = 1,
    InvalidRequest = 2,
    BudgetExhausted = 3,
    NoWork = 4,
    KernelFailure = 5
}

public sealed record HybridCpuManagedAsyncOptionsV1(
    bool Enabled,
    int MaximumTasks,
    int MaximumContinuations,
    int MaximumWorkers,
    HybridCpuManagedExecutionContextPolicyV1 ExecutionContextPolicy,
    string OptionsDigest)
{
    public static HybridCpuManagedAsyncOptionsV1 Production { get; } = Create(false, 0, 0, 0,
        HybridCpuManagedExecutionContextPolicyV1.Suppress);
    public static HybridCpuManagedAsyncOptionsV1 Qualification { get; } = Create(true, 1024, 4096, 4,
        HybridCpuManagedExecutionContextPolicyV1.FlowImmutableCarrier);

    public static HybridCpuManagedAsyncOptionsV1 Create(bool enabled, int tasks, int continuations, int workers,
        HybridCpuManagedExecutionContextPolicyV1 policy) => new(enabled, tasks, continuations, workers, policy,
            HybridCpuPlatformContractV1.Hash($"{HybridCpuAsyncRuntimeContractV1.ContractDigest}|options|{enabled}|{tasks}|{continuations}|{workers}|{policy}"));
}

public sealed record HybridCpuManagedTaskSnapshotV1(
    ulong TaskId,
    ulong TaskObjectReference,
    HybridCpuManagedTaskStatusV1 Status,
    ulong ResultValue,
    string ExceptionIdentity,
    bool CancellationRequested,
    ulong StateSequence);

public sealed record HybridCpuManagedContinuationWorkV1(
    ulong ContinuationId,
    ulong AntecedentTaskId,
    ulong TargetTaskId,
    ulong ContinuationObjectReference,
    ulong ExecutionContextObjectReference,
    string ExecutionContextIdentity,
    HybridCpuManagedTaskStatusV1 AntecedentStatus);

public sealed record HybridCpuManagedContinuationOutcomeV1(
    HybridCpuManagedTaskStatusV1 Status,
    ulong ResultValue = 0,
    string ExceptionIdentity = "");

public sealed record HybridCpuManagedAsyncResultV1(
    HybridCpuManagedAsyncStatusV1 Status,
    string Reason,
    HybridCpuManagedTaskSnapshotV1? Task,
    HybridCpuManagedContinuationWorkV1? Work,
    string ResultDigest)
{
    public bool IsSuccess => Status == HybridCpuManagedAsyncStatusV1.Success;
}

/// <summary>
/// Bounded cooperative Task/continuation library. C# state machines remain ordinary CIL; this
/// runtime owns only task state, continuation roots, deterministic queueing and timer completion.
/// </summary>
public sealed class HybridCpuManagedAsyncRuntimeV1
{
    private readonly IHybridCpuRuntimeKernelV1 _kernel;
    private readonly HybridCpuManagedAsyncOptionsV1 _options;
    private readonly SortedDictionary<ulong, TaskState> _tasks = [];
    private readonly SortedDictionary<ulong, ContinuationState> _continuations = [];
    private readonly Queue<ulong> _ready = [];
    private readonly SortedDictionary<ulong, ulong> _deadlineTasks = [];
    private ulong _nextTaskId = 1;
    private ulong _nextContinuationId = 1;
    private ulong _sequence;

    public HybridCpuManagedAsyncRuntimeV1(IHybridCpuRuntimeKernelV1 kernel,
        HybridCpuManagedAsyncOptionsV1? options = null)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _options = options ?? HybridCpuManagedAsyncOptionsV1.Production;
    }

    public bool HasCompilerBackendSpecialCase => false;
    public bool HasIseExecutionAuthority => false;

    public HybridCpuManagedAsyncResultV1 CreateTask(ulong taskObjectReference)
    {
        if (!Enabled(out HybridCpuManagedAsyncResultV1? failure)) return failure;
        if (taskObjectReference == 0 || taskObjectReference % 8 != 0)
            return Fail(HybridCpuManagedAsyncStatusV1.InvalidRequest, "Task requires an aligned managed object reference.");
        if (_tasks.Count >= _options.MaximumTasks)
            return Fail(HybridCpuManagedAsyncStatusV1.BudgetExhausted, "Task budget exhausted.");
        ulong id = _nextTaskId++;
        TaskState task = new(id, taskObjectReference, HybridCpuManagedTaskStatusV1.Pending, 0, string.Empty,
            false, ++_sequence);
        _tasks.Add(id, task);
        return Success(task);
    }

    public HybridCpuManagedAsyncResultV1 ContinueWith(ulong antecedentTaskId, ulong targetTaskId,
        ulong continuationObjectReference, ulong executionContextObjectReference = 0,
        string executionContextIdentity = "")
    {
        if (!Enabled(out HybridCpuManagedAsyncResultV1? failure)) return failure;
        if (!_tasks.TryGetValue(antecedentTaskId, out TaskState? antecedent) ||
            !_tasks.TryGetValue(targetTaskId, out TaskState? target) || target.Status != HybridCpuManagedTaskStatusV1.Pending ||
            continuationObjectReference == 0 || continuationObjectReference % 8 != 0 ||
            _continuations.Count >= _options.MaximumContinuations)
            return Fail(_continuations.Count >= _options.MaximumContinuations ? HybridCpuManagedAsyncStatusV1.BudgetExhausted : HybridCpuManagedAsyncStatusV1.InvalidRequest,
                "Continuation requires live pending tasks, an aligned object reference and available budget.");
        if (_options.ExecutionContextPolicy == HybridCpuManagedExecutionContextPolicyV1.Suppress)
        {
            executionContextObjectReference = 0;
            executionContextIdentity = string.Empty;
        }
        else if (executionContextObjectReference != 0 &&
                 (executionContextObjectReference % 8 != 0 || string.IsNullOrWhiteSpace(executionContextIdentity)))
            return Fail(HybridCpuManagedAsyncStatusV1.InvalidRequest, "A flowed execution context requires an aligned immutable carrier and stable identity.");
        ulong id = _nextContinuationId++;
        ContinuationState continuation = new ContinuationState(id, antecedentTaskId, targetTaskId,
            continuationObjectReference, executionContextObjectReference, executionContextIdentity, ++_sequence, false);
        _continuations.Add(id, continuation);
        if (antecedent.Status != HybridCpuManagedTaskStatusV1.Pending) Enqueue(continuation);
        return Result(HybridCpuManagedAsyncStatusV1.Success, string.Empty, target, Work(continuation, antecedent.Status));
    }

    public HybridCpuManagedAsyncResultV1 Complete(ulong taskId, ulong resultValue = 0) =>
        Transition(taskId, HybridCpuManagedTaskStatusV1.Succeeded, resultValue, string.Empty);

    public HybridCpuManagedAsyncResultV1 Fault(ulong taskId, string exceptionIdentity) =>
        Transition(taskId, HybridCpuManagedTaskStatusV1.Faulted, 0, exceptionIdentity);

    public HybridCpuManagedAsyncResultV1 Cancel(ulong taskId)
    {
        if (!_tasks.TryGetValue(taskId, out TaskState? task) || task.Status != HybridCpuManagedTaskStatusV1.Pending)
            return Fail(HybridCpuManagedAsyncStatusV1.InvalidRequest, "Only a pending task can be cancelled.");
        _tasks[taskId] = task with { CancellationRequested = true };
        ulong timer = _deadlineTasks.FirstOrDefault(pair => pair.Value == taskId).Key;
        if (timer != 0)
        {
            _ = _kernel.CancelDeadline(timer);
            _deadlineTasks.Remove(timer);
        }
        return Transition(taskId, HybridCpuManagedTaskStatusV1.Cancelled, 0, string.Empty);
    }

    public HybridCpuManagedAsyncResultV1 Delay(ulong taskId, ulong contextId, ulong deadlineTick)
    {
        if (!Enabled(out HybridCpuManagedAsyncResultV1? failure)) return failure;
        if (!_tasks.TryGetValue(taskId, out TaskState? task) || task.Status != HybridCpuManagedTaskStatusV1.Pending)
            return Fail(HybridCpuManagedAsyncStatusV1.InvalidRequest, "Delay requires a pending task.");
        ulong token = taskId;
        HybridCpuDeadlineResultV1 timer = _kernel.SleepUntil(new(contextId, deadlineTick, token));
        if (!timer.IsSuccess)
            return Fail(HybridCpuManagedAsyncStatusV1.KernelFailure, timer.Reason);
        if (timer.Disposition == HybridCpuDeadlineDispositionV1.Completed) return Complete(taskId);
        _deadlineTasks.Add(token, taskId);
        return Success(task);
    }

    public HybridCpuManagedAsyncResultV1 AdvanceVirtualTime(ulong tick)
    {
        if (!Enabled(out HybridCpuManagedAsyncResultV1? failure)) return failure;
        HybridCpuDeadlineResultV1 advanced = _kernel.AdvanceMonotonicTime(tick);
        if (!advanced.IsSuccess) return Fail(HybridCpuManagedAsyncStatusV1.KernelFailure, advanced.Reason);
        HybridCpuManagedAsyncResultV1 result = Result(HybridCpuManagedAsyncStatusV1.Success, string.Empty, null, null);
        foreach (ulong token in advanced.CompletedTokens)
            if (_deadlineTasks.Remove(token, out ulong taskId)) result = Complete(taskId);
        return result;
    }

    public HybridCpuManagedAsyncResultV1 RunNext(int workerId,
        Func<HybridCpuManagedContinuationWorkV1, HybridCpuManagedContinuationOutcomeV1> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        if (!Enabled(out HybridCpuManagedAsyncResultV1? failure)) return failure;
        if (workerId < 0 || workerId >= _options.MaximumWorkers)
            return Fail(HybridCpuManagedAsyncStatusV1.InvalidRequest, "Worker id exceeds the bounded cooperative pool.");
        if (_ready.Count == 0) return Fail(HybridCpuManagedAsyncStatusV1.NoWork, "No continuation is ready.");
        ulong id = _ready.Dequeue();
        ContinuationState continuation = _continuations[id];
        TaskState antecedent = _tasks[continuation.AntecedentTaskId];
        HybridCpuManagedContinuationWorkV1 work = Work(continuation, antecedent.Status);
        HybridCpuManagedContinuationOutcomeV1 outcome;
        try
        {
            outcome = execute(work) ?? new(HybridCpuManagedTaskStatusV1.Faulted, 0, "System.InvalidOperationException");
        }
        catch (Exception exception)
        {
            outcome = new(HybridCpuManagedTaskStatusV1.Faulted, 0, exception.GetType().FullName ?? exception.GetType().Name);
        }
        _continuations.Remove(id);
        HybridCpuManagedAsyncResultV1 transitioned = outcome.Status switch
        {
            HybridCpuManagedTaskStatusV1.Succeeded => Complete(continuation.TargetTaskId, outcome.ResultValue),
            HybridCpuManagedTaskStatusV1.Cancelled => Cancel(continuation.TargetTaskId),
            HybridCpuManagedTaskStatusV1.Faulted => Fault(continuation.TargetTaskId,
                string.IsNullOrWhiteSpace(outcome.ExceptionIdentity) ? "System.Exception" : outcome.ExceptionIdentity),
            _ => Fault(continuation.TargetTaskId, "System.InvalidOperationException")
        };
        return transitioned with { Work = work };
    }

    public IReadOnlyList<HybridCpuManagedTaskSnapshotV1> Tasks() => _tasks.Values
        .OrderBy(static task => task.TaskId).Select(Snapshot).ToArray();

    public IReadOnlyList<HybridCpuManagedGcRootV1> EnumerateRoots()
    {
        SortedDictionary<string, HybridCpuManagedGcRootV1> roots = new SortedDictionary<string, HybridCpuManagedGcRootV1>(StringComparer.Ordinal);
        foreach (TaskState task in _tasks.Values.Where(static task => task.Status == HybridCpuManagedTaskStatusV1.Pending))
            roots[$"async:task:{task.TaskId}"] = new($"async:task:{task.TaskId}", HybridCpuManagedGcRootSourceV1.Handle, task.TaskObjectReference);
        foreach (ContinuationState item in _continuations.Values)
        {
            roots[$"async:continuation:{item.ContinuationId}"] = new($"async:continuation:{item.ContinuationId}", HybridCpuManagedGcRootSourceV1.Handle, item.ContinuationObjectReference);
            if (item.ExecutionContextObjectReference != 0)
                roots[$"async:execution-context:{item.ContinuationId}"] = new($"async:execution-context:{item.ContinuationId}", HybridCpuManagedGcRootSourceV1.Handle, item.ExecutionContextObjectReference);
        }
        return roots.Values.ToArray();
    }

    private HybridCpuManagedAsyncResultV1 Transition(ulong taskId, HybridCpuManagedTaskStatusV1 status,
        ulong value, string exception)
    {
        if (!Enabled(out HybridCpuManagedAsyncResultV1? failure)) return failure;
        if (!_tasks.TryGetValue(taskId, out TaskState? task) || task.Status != HybridCpuManagedTaskStatusV1.Pending ||
            status == HybridCpuManagedTaskStatusV1.Pending || status == HybridCpuManagedTaskStatusV1.Faulted && string.IsNullOrWhiteSpace(exception))
            return Fail(HybridCpuManagedAsyncStatusV1.InvalidRequest, "Task terminal transition is invalid or repeated.");
        task = task with { Status = status, ResultValue = value, ExceptionIdentity = exception, StateSequence = ++_sequence };
        _tasks[taskId] = task;
        foreach (ContinuationState continuation in _continuations.Values
                     .Where(item => item.AntecedentTaskId == taskId && !item.Enqueued)
                     .OrderBy(static item => item.RegistrationSequence).ThenBy(static item => item.ContinuationId).ToArray())
            Enqueue(continuation);
        return Success(task);
    }

    private void Enqueue(ContinuationState continuation)
    {
        _continuations[continuation.ContinuationId] = continuation with { Enqueued = true };
        _ready.Enqueue(continuation.ContinuationId);
    }

    private bool Enabled(out HybridCpuManagedAsyncResultV1 failure)
    {
        if (_options.Enabled && _options.MaximumTasks is > 0 and <= HybridCpuAsyncRuntimeContractV1.MaximumTasks &&
            _options.MaximumContinuations is > 0 and <= HybridCpuAsyncRuntimeContractV1.MaximumContinuations &&
            _options.MaximumWorkers is > 0 and <= HybridCpuAsyncRuntimeContractV1.MaximumWorkers)
        {
            failure = null!;
            return true;
        }
        failure = Fail(HybridCpuManagedAsyncStatusV1.Disabled, "Managed async libraries are default-disabled or options exceed contract budgets.");
        return false;
    }

    private HybridCpuManagedAsyncResultV1 Success(TaskState task) =>
        Result(HybridCpuManagedAsyncStatusV1.Success, string.Empty, task, null);
    private HybridCpuManagedAsyncResultV1 Fail(HybridCpuManagedAsyncStatusV1 status, string reason) =>
        Result(status, reason, null, null);
    private HybridCpuManagedAsyncResultV1 Result(HybridCpuManagedAsyncStatusV1 status, string reason,
        TaskState? task, HybridCpuManagedContinuationWorkV1? work)
    {
        HybridCpuManagedTaskSnapshotV1? snapshot = task is null ? null : Snapshot(task);
        string digest = HybridCpuPlatformContractV1.Hash($"{HybridCpuAsyncRuntimeContractV1.ContractDigest}|{_options.OptionsDigest}|{status}|{reason}|{snapshot}|{work}|{_sequence}");
        return new(status, reason, snapshot, work, digest);
    }

    private static HybridCpuManagedTaskSnapshotV1 Snapshot(TaskState task) => new(task.TaskId,
        task.TaskObjectReference, task.Status, task.ResultValue, task.ExceptionIdentity,
        task.CancellationRequested, task.StateSequence);
    private HybridCpuManagedContinuationWorkV1 Work(ContinuationState continuation,
        HybridCpuManagedTaskStatusV1 status) => new(continuation.ContinuationId,
        continuation.AntecedentTaskId, continuation.TargetTaskId, continuation.ContinuationObjectReference,
        continuation.ExecutionContextObjectReference, continuation.ExecutionContextIdentity, status);

    private sealed record TaskState(ulong TaskId, ulong TaskObjectReference,
        HybridCpuManagedTaskStatusV1 Status, ulong ResultValue, string ExceptionIdentity,
        bool CancellationRequested, ulong StateSequence);
    private sealed record ContinuationState(ulong ContinuationId, ulong AntecedentTaskId,
        ulong TargetTaskId, ulong ContinuationObjectReference, ulong ExecutionContextObjectReference,
        string ExecutionContextIdentity, ulong RegistrationSequence, bool Enqueued);
}
