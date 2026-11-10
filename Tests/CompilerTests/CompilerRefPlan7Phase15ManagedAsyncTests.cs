using System.Reflection;
using System.Runtime.CompilerServices;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.NativeAot;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;
using HybridCPU_ISE.Tests.TestHelpers;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan7Phase15ManagedAsyncTests
{
    [Fact]
    public void Contract_IsDefaultOffBoundedAndAddsNoAsyncOpcodeOrBackend()
    {
        Assert.Equal("hybridcpu.async-runtime/v1", HybridCpuAsyncRuntimeContractV1.SchemaId);
        Assert.Matches("^[0-9a-f]{64}$", HybridCpuAsyncRuntimeContractV1.ContractDigest);
        Assert.False(HybridCpuManagedAsyncOptionsV1.Production.Enabled);
        Assert.Contains(RestrictedCilSupportMatrixV1.Default.Features, static feature =>
            feature.Feature == "async-state-machines" && feature.Support == RestrictedCilMatrixSupportV1.Supported);
        Assert.Contains(RestrictedCilSupportMatrixV1.Default.Features, static feature =>
            feature.Feature == "async-preemption" && feature.Support == RestrictedCilMatrixSupportV1.Unsupported);
        Assert.DoesNotContain(Enum.GetNames<HybridCpuOpcode>(), static name =>
            name.Contains("ASYNC", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("TASK", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("AWAIT", StringComparison.OrdinalIgnoreCase));
        Assert.False(new HybridCpuManagedAsyncRuntimeV1(BootKernel(),
            HybridCpuManagedAsyncOptionsV1.Qualification).HasCompilerBackendSpecialCase);
        Assert.DoesNotContain("async-runtime-libraries",
            HybridCpuSdkPackContractV1.CreateManifest().PublishQualifiedWorkstreams);
    }

    [Fact]
    public void KernelMonotonicDeadlineQueue_IsOrderedVirtualAndRejectsBackwardTime()
    {
        DeterministicRuntimeKernelV1 kernel = BootKernel();
        ulong main = kernel.CurrentContext()!.ContextId;
        HybridCpuKernelContextResultV1 worker = kernel.CreateContext(WorkerRequest(1));
        Assert.True(worker.IsSuccess, worker.Reason);
        Assert.True(kernel.StartContext(worker.Context!.Descriptor.ContextId).IsSuccess);

        Assert.True(kernel.SleepUntil(new(main, 20, 2)).IsSuccess);
        Assert.True(kernel.SleepUntil(new(worker.Context.Descriptor.ContextId, 10, 1)).IsSuccess);
        Assert.Equal([1UL], kernel.AdvanceMonotonicTime(10).CompletedTokens);
        Assert.Equal([2UL], kernel.AdvanceMonotonicTime(20).CompletedTokens);
        Assert.Equal(HybridCpuDeadlineDispositionV1.Invalid,
            kernel.AdvanceMonotonicTime(19).Disposition);
        Assert.Equal(20UL, kernel.MonotonicTicks());
        Assert.Empty(kernel.Deadlines());
    }

    [Fact]
    public void ContinuationSuccess_CompletesTargetAndReleasesQueuedRoots()
    {
        HybridCpuManagedAsyncRuntimeV1 runtime = Runtime();
        ulong source = Create(runtime, 0x1000);
        ulong target = Create(runtime, 0x2000);
        Assert.True(runtime.ContinueWith(source, target, 0x3000, 0x4000, "tenant:7").IsSuccess);
        Assert.Equal([0x1000UL, 0x2000UL, 0x3000UL, 0x4000UL],
            runtime.EnumerateRoots().Select(static root => root.ObjectReference).Order().ToArray());

        Assert.True(runtime.Complete(source, 41).IsSuccess);
        HybridCpuManagedAsyncResultV1 result = runtime.RunNext(0, work =>
        {
            Assert.Equal(HybridCpuManagedTaskStatusV1.Succeeded, work.AntecedentStatus);
            Assert.Equal("tenant:7", work.ExecutionContextIdentity);
            return new(HybridCpuManagedTaskStatusV1.Succeeded, 42);
        });

        Assert.True(result.IsSuccess, result.Reason);
        Assert.Equal(HybridCpuManagedTaskStatusV1.Succeeded, result.Task!.Status);
        Assert.Equal(42UL, result.Task.ResultValue);
        Assert.Empty(runtime.EnumerateRoots());
    }

    [Fact]
    public void ContinuationException_IsContainedAndFaultsOnlyTargetTask()
    {
        HybridCpuManagedAsyncRuntimeV1 runtime = Runtime();
        ulong source = Create(runtime, 0x1000);
        ulong target = Create(runtime, 0x2000);
        Assert.True(runtime.ContinueWith(source, target, 0x3000).IsSuccess);
        Assert.True(runtime.Complete(source).IsSuccess);

        HybridCpuManagedAsyncResultV1 result = runtime.RunNext(0, _ =>
            throw new InvalidOperationException("contained"));

        Assert.Equal(HybridCpuManagedTaskStatusV1.Faulted, result.Task!.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName, result.Task.ExceptionIdentity);
        Assert.Equal(HybridCpuManagedTaskStatusV1.Succeeded,
            runtime.Tasks().Single(task => task.TaskId == source).Status);
    }

    [Fact]
    public void Cancellation_CancelsDeadlineAndPropagatesToContinuationByLibraryPolicy()
    {
        DeterministicRuntimeKernelV1 kernel = BootKernel();
        HybridCpuManagedAsyncRuntimeV1 runtime = new HybridCpuManagedAsyncRuntimeV1(kernel, HybridCpuManagedAsyncOptionsV1.Qualification);
        ulong delay = Create(runtime, 0x1000);
        ulong dependent = Create(runtime, 0x2000);
        Assert.True(runtime.ContinueWith(delay, dependent, 0x3000).IsSuccess);
        Assert.True(runtime.Delay(delay, kernel.CurrentContext()!.ContextId, 50).IsSuccess);
        Assert.Single(kernel.Deadlines());

        Assert.True(runtime.Cancel(delay).IsSuccess);
        Assert.Empty(kernel.Deadlines());
        HybridCpuManagedAsyncResultV1 result = runtime.RunNext(0, work =>
            new(work.AntecedentStatus == HybridCpuManagedTaskStatusV1.Cancelled
                ? HybridCpuManagedTaskStatusV1.Cancelled : HybridCpuManagedTaskStatusV1.Faulted));

        Assert.Equal(HybridCpuManagedTaskStatusV1.Cancelled, result.Task!.Status);
        Assert.True(runtime.AdvanceVirtualTime(49).IsSuccess);
        Assert.Equal(HybridCpuManagedTaskStatusV1.Cancelled,
            runtime.Tasks().Single(task => task.TaskId == delay).Status);
    }

    [Fact]
    public void VirtualTimer_CompletesTaskOnlyAtDeadline()
    {
        DeterministicRuntimeKernelV1 kernel = BootKernel();
        HybridCpuManagedAsyncRuntimeV1 runtime = new HybridCpuManagedAsyncRuntimeV1(kernel, HybridCpuManagedAsyncOptionsV1.Qualification);
        ulong task = Create(runtime, 0x1000);
        Assert.True(runtime.Delay(task, kernel.CurrentContext()!.ContextId, 10).IsSuccess);
        Assert.True(runtime.AdvanceVirtualTime(9).IsSuccess);
        Assert.Equal(HybridCpuManagedTaskStatusV1.Pending, runtime.Tasks().Single().Status);
        Assert.True(runtime.AdvanceVirtualTime(10).IsSuccess);
        Assert.Equal(HybridCpuManagedTaskStatusV1.Succeeded, runtime.Tasks().Single().Status);
    }

    [Fact]
    public void QueuedTasksContinuationsAndExecutionContext_AreRealGcRoots()
    {
        DeterministicRuntimeKernelV1 kernel = BootKernel();
        HybridCpuManagedAsyncRuntimeV1 runtime = new(kernel, HybridCpuManagedAsyncOptionsV1.Qualification);
        HybridCpuManagedTypeSystemV1 types = Types();
        HybridCpuManagedHeapAllocatorV1 heap = new HybridCpuManagedHeapAllocatorV1(kernel, types,
            HybridCpuManagedHeapOptionsV1.Create(0x7000_0000, 4096, 4096, -3));
        Assert.True(heap.Initialize().IsSuccess);
        ulong handle = types.TypeHandle(types.Descriptors.Single().TypeId)!.Value;
        ulong taskObject = heap.Allocate(handle).ObjectReference;
        ulong continuationObject = heap.Allocate(handle).ObjectReference;
        ulong contextObject = heap.Allocate(handle).ObjectReference;
        ulong dead = heap.Allocate(handle).ObjectReference;
        ulong source = Create(runtime, taskObject);
        ulong target = Create(runtime, heap.Allocate(handle).ObjectReference);
        Assert.True(runtime.ContinueWith(source, target, continuationObject, contextObject, "flow:1").IsSuccess);
        HybridCpuManagedAbiFamilyV1 abi = HybridCpuManagedAbiFamilyV1.Default;
        HybridCpuManagedNonMovingGcV1 collector = new HybridCpuManagedNonMovingGcV1(types, heap, abi.ContractDigest,
            abi.TargetContractDigest, abi.NativeAbiDigest, HybridCpuManagedAbiFamilyV1.RuntimePackRevision);

        HybridCpuManagedNonMovingGcResultV1 collection = collector.Collect(new([], [], runtime.EnumerateRoots()),
            HybridCpuManagedNonMovingGcOptionsV1.Qualification);

        Assert.True(collection.IsSuccess, collection.Reason);
        Assert.Contains(taskObject, collection.ReachableObjects);
        Assert.Contains(continuationObject, collection.ReachableObjects);
        Assert.Contains(contextObject, collection.ReachableObjects);
        Assert.Contains(dead, collection.ReclaimedObjects);
    }

    [Fact]
    public void WorkerAndQueueBudgets_FailClosedAndExecutionContextCanBeSuppressed()
    {
        DeterministicRuntimeKernelV1 kernel = BootKernel();
        HybridCpuManagedAsyncOptionsV1 options = HybridCpuManagedAsyncOptionsV1.Create(true, 2, 1, 1,
            HybridCpuManagedExecutionContextPolicyV1.Suppress);
        HybridCpuManagedAsyncRuntimeV1 runtime = new HybridCpuManagedAsyncRuntimeV1(kernel, options);
        ulong source = Create(runtime, 0x1000);
        ulong target = Create(runtime, 0x2000);
        Assert.Equal(HybridCpuManagedAsyncStatusV1.BudgetExhausted, runtime.CreateTask(0x3000).Status);
        HybridCpuManagedAsyncResultV1 continuation = runtime.ContinueWith(source, target, 0x4000, 0x5000, "ignored");
        Assert.True(continuation.IsSuccess);
        Assert.Equal(0UL, continuation.Work!.ExecutionContextObjectReference);
        Assert.Equal(string.Empty, continuation.Work.ExecutionContextIdentity);
        Assert.Equal(HybridCpuManagedAsyncStatusV1.BudgetExhausted,
            runtime.ContinueWith(source, target, 0x6000).Status);
        Assert.True(runtime.Complete(source).IsSuccess);
        Assert.Equal(HybridCpuManagedAsyncStatusV1.InvalidRequest, runtime.RunNext(1, _ => new(HybridCpuManagedTaskStatusV1.Succeeded)).Status);
        Assert.True(runtime.RunNext(0, _ => new(HybridCpuManagedTaskStatusV1.Succeeded)).IsSuccess);
    }

    [Fact]
    public void CooperativeSchedulerTrace_IsDeterministicAcrossIndependentRuntimes()
    {
        string Run()
        {
            HybridCpuManagedAsyncRuntimeV1 runtime = Runtime();
            ulong source = Create(runtime, 0x1000);
            ulong first = Create(runtime, 0x2000);
            ulong second = Create(runtime, 0x3000);
            Assert.True(runtime.ContinueWith(source, first, 0x4000).IsSuccess);
            Assert.True(runtime.ContinueWith(source, second, 0x5000).IsSuccess);
            Assert.True(runtime.Complete(source, 7).IsSuccess);
            HybridCpuManagedAsyncResultV1 a = runtime.RunNext(0, work => new(HybridCpuManagedTaskStatusV1.Succeeded, work.ContinuationId));
            HybridCpuManagedAsyncResultV1 b = runtime.RunNext(1, work => new(HybridCpuManagedTaskStatusV1.Succeeded, work.ContinuationId));
            return $"{a.Work!.ContinuationId}:{a.Task!.ResultValue}|{b.Work!.ContinuationId}:{b.Task!.ResultValue}|{string.Join(';', runtime.Tasks())}";
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public async Task CSharpAsyncStateMachine_UsesOrdinaryCilAndNoKernelOrIseAsyncAuthority()
    {
        MethodInfo asyncMethod = typeof(Phase15AsyncCorpus).GetMethod(nameof(Phase15AsyncCorpus.AddOneAsync))!;
        AsyncStateMachineAttribute attribute = asyncMethod.GetCustomAttribute<AsyncStateMachineAttribute>()!;
        Assert.NotNull(attribute.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
        Assert.Equal(42, await Phase15AsyncCorpus.AddOneAsync(41));

        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2);
        RestrictedCilImportResultV1 ordinary = importer.ImportImage(
            File.ReadAllBytes(typeof(Phase15AsyncCorpus).Assembly.Location),
            new(typeof(Phase15AsyncCorpus).FullName!, nameof(Phase15AsyncCorpus.OrdinaryMoveNext)),
            "phase15-ordinary-state-machine");
        Assert.Equal(RestrictedCilImportStatusV1.Success, ordinary.Status);
        ManagedEhImportResultV1 moveNext = importer.ImportManagedEhPlan(
            File.ReadAllBytes(typeof(Phase15AsyncCorpus).Assembly.Location),
            new(attribute.StateMachineType.Name, "MoveNext"));
        Assert.True(moveNext.Status == ManagedEhImportStatusV1.Success,
            $"{moveNext.Code}: {moveNext.Reason}");
        Assert.Contains(moveNext.Plan!.Clauses, static clause =>
            clause.Kind == HybridCpuManagedEhClauseKindV1.Catch);

        string root = CompatFreezeScanner.FindRepoRoot();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "HybridCPU_ISE", "CloseToHSL", "Core"), "*.cs", SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(path);
            Assert.DoesNotContain("HybridCpuManagedAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain("AsyncStateMachine", source, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("AsyncStateMachine", File.ReadAllText(Path.Combine(CompatFreezeScanner.FindCompilerRepoRoot(),
            "Compilers", "HybridCPU_RuntimeKernel", "RuntimeKernelTimersV1.cs")), StringComparison.Ordinal);
    }

    private static ulong Create(HybridCpuManagedAsyncRuntimeV1 runtime, ulong reference)
    {
        HybridCpuManagedAsyncResultV1 result = runtime.CreateTask(reference);
        Assert.True(result.IsSuccess, result.Reason);
        return result.Task!.TaskId;
    }

    private static HybridCpuManagedAsyncRuntimeV1 Runtime() => new(BootKernel(),
        HybridCpuManagedAsyncOptionsV1.Qualification);

    private static DeterministicRuntimeKernelV1 BootKernel()
    {
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Assert.True(kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
            0x0010_0000, 4096, 0x0010_0000, 0x0020_0000, 4096, 0)).IsSuccess);
        return kernel;
    }

    private static HybridCpuExecutionContextCreateRequestV1 WorkerRequest(int carrier) => new(
        0x0010_0000, 0x5000_1000, 4096, 0x5000_0000, 4096, 0x6000_0000, 0x6100_0000, carrier);

    private static HybridCpuManagedTypeSystemV1 Types()
    {
        HybridCpuManagedTypeSystemBuildV1 build = new HybridCpuManagedTypeSystemBuilderV1().Build(
        [
            new("AsyncNode", HybridCpuManagedTypeKindV1.Class, null, [], [])
        ]);
        Assert.True(build.IsSuccess, build.Reason);
        return build.TypeSystem!;
    }
}

public static class Phase15AsyncCorpus
{
    public static async Task<int> AddOneAsync(int value)
    {
        await Task.Yield();
        return value + 1;
    }

    public static int OrdinaryMoveNext(int state) => state switch
    {
        0 => 1,
        1 => 2,
        _ => -1
    };
}
