using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan7Phase12ManagedSynchronizationTests
{
    private static readonly HybridCpuManagedTlsLayoutV1 EmptyTls = HybridCpuManagedTlsLayoutV1.Create([]);

    [Fact]
    public void NormativeContract_IsClosedDefaultOffAndAddsNoManagedOpcode()
    {
        Assert.Equal(8, HybridCpuManagedSynchronizationContractV1.Mappings.Count);
        Assert.Equal(64, HybridCpuManagedSynchronizationContractV1.ContractDigest.Length);
        Assert.False(HybridCpuManagedSynchronizationOptionsV1.Production.Enabled);
        Assert.True(HybridCpuManagedSynchronizationOptionsV1.Qualification.Enabled);
        Assert.Equal(13, HybridCpuPlatformContractV1.SchemaMinor);
        Assert.Equal((1, 63), (HybridCpuManagedAbiFamilyV1.SchemaMajor, HybridCpuManagedAbiFamilyV1.SchemaMinor));
        Assert.Equal(HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff,
            HybridCpuManagedFeatureSetV1.Default.Workstreams.Single(static item =>
                item.Identity == "synchronization-memory-model").Support);
        Assert.DoesNotContain(Enum.GetNames<HybridCpuOpcode>(), static name =>
            name.Contains("MONITOR", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("INTERLOCKED", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("VOLATILE", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(InstructionClass.Atomic, InstructionClassifier.GetClass(InstructionsEnum.LR_W));
        Assert.Equal(SerializationClass.MemoryOrdered, InstructionClassifier.GetSerializationClass(InstructionsEnum.FENCE));
    }

    [Fact]
    public void VolatileLowering_GoldenSequenceUsesOrdinaryAccessAndExistingFence()
    {
        HybridCpuManagedSynchronizationPlanV1 read = HybridCpuManagedSynchronizationLoweringV1.Plan(
            HybridCpuManagedMemoryOperationV1.VolatileRead, 64, 8);
        HybridCpuManagedSynchronizationPlanV1 write = HybridCpuManagedSynchronizationLoweringV1.Plan(
            HybridCpuManagedMemoryOperationV1.VolatileWrite, 32, 4);

        Assert.True(read.IsSupported);
        Assert.Equal([HybridCpuOpcode.LD, HybridCpuOpcode.FENCE], read.SuccessPath.Select(static step => step.Opcode));
        Assert.Equal(["volatile-read", "acquire-fence"], read.SuccessPath.Select(static step => step.Role));
        Assert.Equal([HybridCpuOpcode.FENCE, HybridCpuOpcode.SW], write.SuccessPath.Select(static step => step.Opcode));
        Assert.Equal(["release-fence", "volatile-write"], write.SuccessPath.Select(static step => step.Role));
        Assert.Equal(read.ContractDigest, write.ContractDigest);
    }

    [Fact]
    public void InterlockedLowering_MapsCasSuccessFailureAndRmwToExactExistingPrimitives()
    {
        HybridCpuManagedSynchronizationPlanV1 cas = HybridCpuManagedSynchronizationLoweringV1.Plan(
            HybridCpuManagedMemoryOperationV1.InterlockedCompareExchange, 64, 8);
        HybridCpuManagedSynchronizationPlanV1 add = HybridCpuManagedSynchronizationLoweringV1.Plan(
            HybridCpuManagedMemoryOperationV1.InterlockedAdd, 32, 4);

        Assert.Equal([HybridCpuOpcode.FENCE, HybridCpuOpcode.LR_D, HybridCpuOpcode.SC_D, HybridCpuOpcode.FENCE],
            cas.SuccessPath.Select(static step => step.Opcode));
        Assert.Equal([HybridCpuOpcode.FENCE, HybridCpuOpcode.LR_D, HybridCpuOpcode.FENCE],
            cas.FailurePath.Select(static step => step.Opcode));
        Assert.True(cas.SuccessPath.Single(static step => step.Opcode == HybridCpuOpcode.LR_D).AcquireOrdering);
        Assert.True(cas.SuccessPath.Single(static step => step.Opcode == HybridCpuOpcode.SC_D).ReleaseOrdering);
        Assert.True(cas.RetryOnReservationLoss);
        Assert.Equal(HybridCpuOpcode.AMOADD_W, add.SuccessPath[1].Opcode);
        Assert.True(add.SuccessPath[1].AcquireOrdering && add.SuccessPath[1].ReleaseOrdering);
        Assert.Equal(HybridCpuManagedSynchronizationLoweringStatusV1.InvalidAlignment,
            HybridCpuManagedSynchronizationLoweringV1.Plan(
                HybridCpuManagedMemoryOperationV1.InterlockedExchange, 64, 4).Status);
    }

    [Fact]
    public void CrossContextMessagePassing_ReleasePublishesAndAcquireObserves()
    {
        HybridCpuManagedMemoryModelV1 memory = MemoryModel();
        const ulong data = 0x1000;
        const ulong flag = 0x2000;
        memory.Seed(data, 0);
        memory.Seed(flag, 0);

        Assert.True(memory.OrdinaryStore(1, data, 42).IsSuccess);
        Assert.Equal(0UL, memory.OrdinaryLoad(2, data).ObservedValue);
        Assert.True(memory.VolatileWrite(1, flag, 1).IsSuccess);
        Assert.Equal(1UL, memory.VolatileRead(2, flag).ObservedValue);
        Assert.Equal(42UL, memory.OrdinaryLoad(2, data).ObservedValue);
    }

    [Fact]
    public void StoreBuffering_IsAllowedForOrdinaryRacesAndForbiddenAcrossFullFences()
    {
        HybridCpuManagedMemoryModelV1 ordinary = MemoryModel();
        ordinary.Seed(0x1000, 0);
        ordinary.Seed(0x2000, 0);
        ordinary.OrdinaryStore(1, 0x1000, 1);
        ordinary.OrdinaryStore(2, 0x2000, 1);
        Assert.Equal((0UL, 0UL), (ordinary.OrdinaryLoad(1, 0x2000).ObservedValue,
            ordinary.OrdinaryLoad(2, 0x1000).ObservedValue));

        HybridCpuManagedMemoryModelV1 fenced = MemoryModel();
        fenced.Seed(0x1000, 0);
        fenced.Seed(0x2000, 0);
        fenced.OrdinaryStore(1, 0x1000, 1);
        fenced.FullFence(1);
        fenced.OrdinaryStore(2, 0x2000, 1);
        fenced.FullFence(2);
        Assert.Equal((1UL, 1UL), (fenced.OrdinaryLoad(1, 0x2000).ObservedValue,
            fenced.OrdinaryLoad(2, 0x1000).ObservedValue));
    }

    [Fact]
    public void InterlockedOperations_AreAtomicAndFailedCasStillPublishesPriorWrites()
    {
        HybridCpuManagedMemoryModelV1 memory = MemoryModel();
        memory.Seed(0x1000, 7);
        memory.Seed(0x2000, 0);
        Assert.True(memory.OrdinaryStore(1, 0x2000, 99).IsSuccess);
        HybridCpuManagedAtomicResultV1 failed = memory.CompareExchange(1, 0x1000, 8, 6);
        Assert.Equal((7UL, false), (failed.ObservedValue, failed.ValueChanged));
        Assert.Equal(99UL, memory.VolatileRead(2, 0x2000).ObservedValue);
        HybridCpuManagedAtomicResultV1 success = memory.CompareExchange(2, 0x1000, 8, 7);
        Assert.Equal((7UL, 8UL, true), (success.ObservedValue, success.ResultValue, success.ValueChanged));
        Assert.Equal((8UL, 11UL), (memory.Add(1, 0x1000, 3).ObservedValue, memory.CommittedValue(0x1000)));
        Assert.Equal((11UL, 5UL), (memory.Exchange(2, 0x1000, 5).ObservedValue, memory.CommittedValue(0x1000)));
    }

    [Fact]
    public void SpeculationFlushReplay_NeverPublishesSquashedEffectsAndResultsAreDeterministic()
    {
        string Run()
        {
            HybridCpuManagedMemoryModelV1 memory = MemoryModel();
            memory.Seed(0x1000, 3);
            Assert.True(memory.SpeculateStore(1, 0x1000, 99));
            memory.FlushOrReplay(1);
            Assert.Equal(3UL, memory.CommittedValue(0x1000));
            return memory.Add(2, 0x1000, 4).ResultDigest;
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void KernelAddressWait_ClosesLostWakeTimeoutCancellationAndTerminationRaces()
    {
        (DeterministicRuntimeKernelV1 kernel, HybridCpuManagedThreadingRuntimeV1 runtime) = Runtime();
        HybridCpuManagedThreadSnapshotV1 main = runtime.Threads().Single();
        HybridCpuManagedThreadSnapshotV1 worker = CreateAndStart(runtime, "worker", WorkerRequest(1));
        const ulong address = 0x9000;
        ulong staleEpoch = kernel.AddressWakeEpoch(address);
        Assert.True(kernel.WakeAddress(address, 1).IsSuccess);
        HybridCpuAddressWaitResultV1 lostWakeClosed = kernel.WaitOnAddress(new(main.ExecutionContextId,
            address, 7, 7, staleEpoch, 10, false));
        Assert.Equal(HybridCpuAddressWaitDispositionV1.WakeObserved, lostWakeClosed.Disposition);
        Assert.Equal(HybridCpuExecutionContextStateV1.Running, kernel.Contexts().Single(item =>
            item.Descriptor.ContextId == main.ExecutionContextId).State);

        HybridCpuAddressWaitResultV1 registered = kernel.WaitOnAddress(new(main.ExecutionContextId,
            address, 7, 7, kernel.AddressWakeEpoch(address), 10, false));
        Assert.Equal(HybridCpuAddressWaitDispositionV1.Registered, registered.Disposition);
        Assert.Contains(main.ExecutionContextId, kernel.AdvanceAddressWaitClock(10).AffectedContextIds);
        Assert.Equal(HybridCpuAddressWaitDispositionV1.Cancelled,
            kernel.WaitOnAddress(new(worker.ExecutionContextId, address, 7, 7,
                kernel.AddressWakeEpoch(address), 20, true)).Disposition);
        Assert.Equal(HybridCpuAddressWaitDispositionV1.Registered,
            kernel.WaitOnAddress(new(worker.ExecutionContextId, address, 7, 7,
                kernel.AddressWakeEpoch(address), 20, false)).Disposition);
        Assert.True(kernel.ExitContext(worker.ExecutionContextId, -1).IsSuccess);
        Assert.Empty(kernel.WakeAddress(address, 4).AffectedContextIds);
    }

    [Fact]
    public void Monitor_RecursesContendsAndHandsOffInStableContextOrder()
    {
        (DeterministicRuntimeKernelV1 kernel, HybridCpuManagedThreadingRuntimeV1 runtime) = Runtime();
        HybridCpuManagedThreadSnapshotV1 main = runtime.Threads().Single();
        HybridCpuManagedThreadSnapshotV1 worker = CreateAndStart(runtime, "worker", WorkerRequest(1));
        HybridCpuManagedMonitorRuntimeV1 monitor = new HybridCpuManagedMonitorRuntimeV1(runtime, kernel,
            HybridCpuManagedSynchronizationOptionsV1.Qualification);
        const ulong syncObject = 0x8000;

        Assert.Equal(HybridCpuManagedMonitorDispositionV1.Acquired,
            monitor.Enter(main.ManagedThreadId, syncObject).Disposition);
        Assert.Equal(HybridCpuManagedMonitorDispositionV1.Recursive,
            monitor.Enter(main.ManagedThreadId, syncObject).Disposition);
        Assert.Equal(HybridCpuManagedMonitorDispositionV1.Contended,
            monitor.Enter(worker.ManagedThreadId, syncObject).Disposition);
        Assert.Equal(HybridCpuManagedMonitorDispositionV1.Released,
            monitor.Exit(main.ManagedThreadId, syncObject).Disposition);
        HybridCpuManagedMonitorResultV1 handoff = monitor.Exit(main.ManagedThreadId, syncObject);
        Assert.Equal(HybridCpuManagedMonitorDispositionV1.HandedOff, handoff.Disposition);
        Assert.Equal([worker.ManagedThreadId], handoff.WokenManagedThreadIds);
        Assert.Equal((worker.ManagedThreadId, 1), monitor.Snapshot(syncObject));
    }

    [Fact]
    public void GcRendezvous_CollectsWhileMonitorWaiterIsBlockedAndRestoresWaitState()
    {
        (DeterministicRuntimeKernelV1 kernel, HybridCpuManagedThreadingRuntimeV1 runtime) = Runtime();
        HybridCpuManagedThreadSnapshotV1 main = runtime.Threads().Single();
        HybridCpuManagedThreadSnapshotV1 worker = CreateAndStart(runtime, "worker", WorkerRequest(1));
        HybridCpuManagedMonitorRuntimeV1 monitor = new HybridCpuManagedMonitorRuntimeV1(runtime, kernel,
            HybridCpuManagedSynchronizationOptionsV1.Qualification);
        monitor.Enter(main.ManagedThreadId, 0x8000);
        monitor.Enter(worker.ManagedThreadId, 0x8000);
        Assert.Equal(HybridCpuExecutionContextStateV1.Parked, kernel.Contexts().Single(item =>
            item.Descriptor.ContextId == worker.ExecutionContextId).State);

        HybridCpuManagedTypeSystemV1 types = Types();
        HybridCpuManagedHeapAllocatorV1 heap = new HybridCpuManagedHeapAllocatorV1(kernel, types,
            HybridCpuManagedHeapOptionsV1.Create(0x7000_0000, 4096, 4096, -3));
        Assert.True(heap.Initialize().IsSuccess);
        HybridCpuManagedAbiFamilyV1 abi = HybridCpuManagedAbiFamilyV1.Default;
        HybridCpuManagedNonMovingGcV1 collector = new HybridCpuManagedNonMovingGcV1(types, heap, abi.ContractDigest,
            abi.TargetContractDigest, abi.NativeAbiDigest, HybridCpuManagedAbiFamilyV1.RuntimePackRevision);
        HybridCpuManagedMultiThreadGcResultV1 result = runtime.CollectAllThreads(main.ManagedThreadId,
            collector, [], []);

        Assert.True(result.IsSuccess, result.Reason);
        Assert.Equal(HybridCpuExecutionContextStateV1.Parked, kernel.Contexts().Single(item =>
            item.Descriptor.ContextId == worker.ExecutionContextId).State);
        Assert.Equal(HybridCpuManagedMonitorDispositionV1.HandedOff,
            monitor.Exit(main.ManagedThreadId, 0x8000).Disposition);
    }

    private static HybridCpuManagedMemoryModelV1 MemoryModel() =>
        new(HybridCpuManagedSynchronizationOptionsV1.Qualification);

    private static (DeterministicRuntimeKernelV1 Kernel, HybridCpuManagedThreadingRuntimeV1 Runtime) Runtime()
    {
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Assert.True(kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
            0x0010_0000, 4096, 0x0010_0000, 0x0020_0000, 4096, 0)).IsSuccess);
        HybridCpuManagedThreadingRuntimeV1 runtime = new HybridCpuManagedThreadingRuntimeV1(kernel, EmptyTls,
            HybridCpuManagedThreadingOptionsV1.Qualification);
        Assert.True(runtime.AttachInitialThread("main").IsSuccess);
        return (kernel, runtime);
    }

    private static HybridCpuExecutionContextCreateRequestV1 WorkerRequest(int carrier) => new(
        0x0010_0000, 0x5000_1000, 4096, 0x5000_0000, 4096, 0x6000_0000, 0x6100_0000, carrier);

    private static HybridCpuManagedThreadSnapshotV1 CreateAndStart(HybridCpuManagedThreadingRuntimeV1 runtime,
        string name, HybridCpuExecutionContextCreateRequestV1 request)
    {
        HybridCpuManagedThreadResultV1 created = runtime.CreateThread(name, request);
        Assert.True(created.IsSuccess, created.Reason);
        Assert.True(runtime.Start(created.Thread!.ManagedThreadId).IsSuccess);
        return runtime.Threads().Single(item => item.ManagedThreadId == created.Thread.ManagedThreadId);
    }

    private static HybridCpuManagedTypeSystemV1 Types()
    {
        HybridCpuManagedTypeSystemBuildV1 build = new HybridCpuManagedTypeSystemBuilderV1().Build(
        [
            new("Node", HybridCpuManagedTypeKindV1.Class, null, [], [])
        ]);
        Assert.True(build.IsSuccess, build.Reason);
        return build.TypeSystem!;
    }
}
