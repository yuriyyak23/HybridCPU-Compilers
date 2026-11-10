using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Compiler.Core.Target.Runtime;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using HybridCPU.RuntimeKernel;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan7Phase11ManagedThreadingTests
{
    private static readonly HybridCpuManagedTlsLayoutV1 TlsLayout = HybridCpuManagedTlsLayoutV1.Create(
    [
        new("counter", false),
        new("root", true)
    ]);

    [Fact]
    public void Contracts_AreVersionedDefaultOffAndKeepAuthoritiesSeparated()
    {
        DeterministicRuntimeKernelV1 kernel = BootKernel();
        HybridCpuManagedThreadingRuntimeV1 disabled = new HybridCpuManagedThreadingRuntimeV1(kernel, TlsLayout);

        Assert.Equal(HybridCpuManagedThreadingStatusV1.Disabled, disabled.AttachInitialThread("main").Status);
        Assert.Equal(13, HybridCpuPlatformContractV1.SchemaMinor);
        Assert.Equal((1, 63), (HybridCpuManagedAbiFamilyV1.SchemaMajor, HybridCpuManagedAbiFamilyV1.SchemaMinor));
        Assert.Equal("hybridcpu.runtime-pack/managed-contract-v1.25", HybridCpuManagedAbiFamilyV1.RuntimePackRevision);
        Assert.Equal(HybridCpuManagedAbiSupportV1.Supported,
            HybridCpuManagedAbiFamilyV1.Default.Subcontracts["ThreadContextAbi"].Support);
        Assert.Equal(HybridCpuManagedAbiSupportV1.Supported,
            HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper("__hybridcpu_managed_poll")!.Support);
        Assert.Equal(HybridCpuManagedWorkstreamSupportV1.QualifiedDefaultOff,
            HybridCpuManagedFeatureSetV1.Default.Workstreams.Single(static item =>
                item.Identity == "tls-thread-runtime-state").Support);
        Assert.True(new HybridCpuManagedThreadingRuntimeV1(kernel, TlsLayout,
            HybridCpuManagedThreadingOptionsV1.Qualification).HasManagedThreadIdentityAuthority);
        Assert.False(new HybridCpuManagedThreadingRuntimeV1(kernel, TlsLayout,
            HybridCpuManagedThreadingOptionsV1.Qualification).HasIseExecutionAuthority);
        Assert.Equal(64, HybridCpuKernelThreadingContractV1.ContractDigest.Length);
    }

    [Fact]
    public void CreateStartAndTls_KeepManagedThreadIdentityDistinctFromVtCarrier()
    {
        (DeterministicRuntimeKernelV1 kernel, HybridCpuManagedThreadingRuntimeV1 runtime) = Runtime();
        HybridCpuManagedThreadSnapshotV1 main = runtime.Threads().Single();
        HybridCpuManagedThreadResultV1 created = runtime.CreateThread("worker", WorkerRequest(1));
        Assert.True(created.IsSuccess, created.Reason);
        HybridCpuManagedThreadSnapshotV1 worker = created.Thread!;

        Assert.NotEqual(worker.ManagedThreadId, (ulong)worker.VirtualThreadCarrier);
        Assert.NotEqual(main.ExecutionContextId, worker.ExecutionContextId);
        Assert.NotEqual(main.TlsBase, worker.TlsBase);
        Assert.Equal(HybridCpuManagedThreadLifecycleStateV1.Created, worker.State);
        Assert.True(runtime.Start(worker.ManagedThreadId).IsSuccess);
        Assert.True(runtime.WriteTls(main.ManagedThreadId, "counter", 11).IsSuccess);
        Assert.True(runtime.WriteTls(worker.ManagedThreadId, "counter", 22).IsSuccess);
        Assert.True(runtime.TryReadTls(main.ManagedThreadId, "counter", out ulong mainValue));
        Assert.True(runtime.TryReadTls(worker.ManagedThreadId, "counter", out ulong workerValue));
        Assert.Equal((11UL, 22UL), (mainValue, workerValue));

        HybridCpuExecutionContextSnapshotV1 workerContext = kernel.Contexts().Single(item =>
            item.Descriptor.ContextId == worker.ExecutionContextId);
        Assert.Equal(workerContext.GuardBase + workerContext.GuardSize, workerContext.Descriptor.StackBase);
        Assert.Equal(HybridCpuVmProtectionV1.None, workerContext.Descriptor.VmMappings.Single(item =>
            item.Address == workerContext.GuardBase).Protection);
        Assert.Equal(HybridCpuVmProtectionV1.Read | HybridCpuVmProtectionV1.Write,
            workerContext.Descriptor.VmMappings.Single(item => item.Address == workerContext.Descriptor.StackBase).Protection);
    }

    [Fact]
    public void ParkUnparkJoinExitAndUnhandledException_HavePerThreadLifecycle()
    {
        (_, HybridCpuManagedThreadingRuntimeV1 runtime) = Runtime();
        HybridCpuManagedThreadSnapshotV1 main = runtime.Threads().Single();
        HybridCpuManagedThreadSnapshotV1 first = CreateAndStart(runtime, "first", WorkerRequest(1));
        HybridCpuManagedThreadSnapshotV1 second = CreateAndStart(runtime, "second", WorkerRequest(2,
            guardBase: 0x5100_0000, contextCarrier: 0x6200_0000, tlsBase: 0x6300_0000));

        Assert.True(runtime.Park(first.ManagedThreadId, "wait:test").IsSuccess);
        Assert.Equal(HybridCpuManagedThreadLifecycleStateV1.WaitSleepJoin,
            runtime.Threads().Single(item => item.ManagedThreadId == first.ManagedThreadId).State);
        Assert.True(runtime.Unpark(first.ManagedThreadId).IsSuccess);
        Assert.True(runtime.Join(main.ManagedThreadId, first.ManagedThreadId).IsSuccess);
        Assert.Equal(HybridCpuManagedThreadLifecycleStateV1.WaitSleepJoin,
            runtime.Threads().Single(item => item.ManagedThreadId == main.ManagedThreadId).State);
        Assert.True(runtime.Exit(first.ManagedThreadId, 17).IsSuccess);
        Assert.Equal(HybridCpuManagedThreadLifecycleStateV1.Runnable,
            runtime.Threads().Single(item => item.ManagedThreadId == main.ManagedThreadId).State);

        HybridCpuManagedThreadResultV1 faulted = runtime.TerminateByUnhandledException(
            second.ManagedThreadId, "System.InvalidOperationException");
        Assert.True(faulted.IsSuccess, faulted.Reason);
        Assert.Equal(HybridCpuManagedThreadLifecycleStateV1.TerminatedByUnhandledException, faulted.Thread!.State);
        Assert.Equal("System.InvalidOperationException", faulted.Thread.UnhandledExceptionIdentity);
        Assert.DoesNotContain(runtime.Threads(), item => item.ManagedThreadId != second.ManagedThreadId &&
            item.State == HybridCpuManagedThreadLifecycleStateV1.TerminatedByUnhandledException);
    }

    [Fact]
    public void CooperativeSchedule_IsRoundRobinAndDeterministicAcrossIndependentRuntimes()
    {
        ulong[] Run()
        {
            (_, HybridCpuManagedThreadingRuntimeV1 runtime) = Runtime();
            CreateAndStart(runtime, "one", WorkerRequest(1));
            CreateAndStart(runtime, "two", WorkerRequest(2, 0x5100_0000, 0x6200_0000, 0x6300_0000));
            return Enumerable.Range(0, 6).Select(_ => runtime.ScheduleNext().Thread!.ManagedThreadId).ToArray();
        }

        ulong[] first = Run();
        ulong[] second = Run();
        Assert.Equal([2UL, 3UL, 1UL, 2UL, 3UL, 1UL], first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void GcRendezvous_StopsRunningRunnableAndParkedThenRestoresExactStates()
    {
        (DeterministicRuntimeKernelV1 kernel, HybridCpuManagedThreadingRuntimeV1 runtime) = Runtime();
        HybridCpuManagedThreadSnapshotV1 main = runtime.Threads().Single();
        HybridCpuManagedThreadSnapshotV1 worker = CreateAndStart(runtime, "worker", WorkerRequest(1));
        Assert.True(runtime.Park(worker.ManagedThreadId, "parked-before-gc").IsSuccess);

        HybridCpuKernelRendezvousResultV1 enter = kernel.GcRendezvousEnter(main.ExecutionContextId);
        Assert.True(enter.IsSuccess, enter.Reason);
        Assert.All(enter.Snapshot!.Contexts.Where(static item => item.State != HybridCpuExecutionContextStateV1.Exited),
            static item => Assert.Equal(HybridCpuExecutionContextStateV1.GcRendezvous, item.State));
        HybridCpuKernelRendezvousResultV1 stale = kernel.GcRendezvousLeave(main.ExecutionContextId,
            enter.Snapshot.Epoch + 1);
        Assert.Equal(HybridCpuKernelStatusV1.InvalidRequest, stale.Status);
        HybridCpuKernelRendezvousResultV1 leave = kernel.GcRendezvousLeave(main.ExecutionContextId,
            enter.Snapshot.Epoch);
        Assert.True(leave.IsSuccess, leave.Reason);
        Assert.Equal(HybridCpuExecutionContextStateV1.Running,
            leave.Snapshot!.Contexts.Single(item => item.Descriptor.ContextId == main.ExecutionContextId).State);
        Assert.Equal(HybridCpuExecutionContextStateV1.Parked,
            leave.Snapshot.Contexts.Single(item => item.Descriptor.ContextId == worker.ExecutionContextId).State);
        Assert.Equal(64, enter.Snapshot.SnapshotDigest.Length);
        Assert.Equal(64, leave.Snapshot.SnapshotDigest.Length);
    }

    [Fact]
    public void AllThreadTlsRootOracle_DrivesRealNonMovingCollectionInsideRendezvous()
    {
        (DeterministicRuntimeKernelV1 kernel, HybridCpuManagedThreadingRuntimeV1 runtime) = Runtime();
        HybridCpuManagedThreadSnapshotV1 main = runtime.Threads().Single();
        HybridCpuManagedThreadSnapshotV1 worker = CreateAndStart(runtime, "worker", WorkerRequest(1));
        HybridCpuManagedTypeSystemV1 types = Types();
        HybridCpuManagedHeapAllocatorV1 heap = new HybridCpuManagedHeapAllocatorV1(kernel, types,
            HybridCpuManagedHeapOptionsV1.Create(0x7000_0000, 4096, 4096, -3));
        Assert.True(heap.Initialize().IsSuccess);
        HybridCpuManagedTypeDescriptorV1 node = types.Descriptors.Single();
        ulong handle = types.TypeHandle(node.TypeId)!.Value;
        ulong dead = heap.Allocate(handle).ObjectReference;
        ulong live = heap.Allocate(handle).ObjectReference;
        Assert.True(runtime.WriteTls(worker.ManagedThreadId, "root", live).IsSuccess);
        Assert.Equal([live], runtime.EnumerateThreadRoots().Select(static item => item.ObjectReference));
        HybridCpuManagedAbiFamilyV1 abi = HybridCpuManagedAbiFamilyV1.Default;
        HybridCpuManagedNonMovingGcV1 collector = new HybridCpuManagedNonMovingGcV1(types, heap, abi.ContractDigest,
            abi.TargetContractDigest, abi.NativeAbiDigest, HybridCpuManagedAbiFamilyV1.RuntimePackRevision);

        HybridCpuManagedMultiThreadGcResultV1 result = runtime.CollectAllThreads(main.ManagedThreadId,
            collector, [], []);

        Assert.True(result.IsSuccess, result.Reason);
        Assert.Equal([live], result.Collection!.ReachableObjects);
        Assert.Equal([dead], result.Collection.ReclaimedObjects);
        Assert.NotEqual(result.EnterSnapshotDigest, result.LeaveSnapshotDigest);
        Assert.Contains(kernel.Contexts(), item => item.Descriptor.ContextId == worker.ExecutionContextId &&
            item.State == HybridCpuExecutionContextStateV1.Runnable);
    }

    [Fact]
    public void InvalidContextShapesAndBudgets_FailClosedWithoutPartialMappings()
    {
        DeterministicRuntimeKernelV1 kernel = BootKernel();
        int before = kernel.CurrentContext()!.VmMappings.Count;
        HybridCpuKernelContextResultV1 missingGuard = kernel.CreateContext(WorkerRequest(1) with { GuardSize = 0 });
        HybridCpuKernelContextResultV1 overlapping = kernel.CreateContext(WorkerRequest(1) with
        {
            GuardBase = 0x0020_0000,
            StackBase = 0x0020_1000
        });
        Assert.Equal(HybridCpuKernelStatusV1.InvalidRequest, missingGuard.Status);
        Assert.Equal(HybridCpuKernelStatusV1.AddressConflict, overlapping.Status);
        Assert.Equal(before, kernel.CurrentContext()!.VmMappings.Count);
        Assert.Single(kernel.Contexts());

        Assert.True(kernel.CreateContext(WorkerRequest(1)).IsSuccess);
        Assert.True(kernel.CreateContext(WorkerRequest(2, 0x5100_0000, 0x6200_0000, 0x6300_0000)).IsSuccess);
        Assert.True(kernel.CreateContext(WorkerRequest(3, 0x5200_0000, 0x6400_0000, 0x6500_0000)).IsSuccess);
        HybridCpuKernelContextResultV1 exhausted = kernel.CreateContext(
            WorkerRequest(-1, 0x5300_0000, 0x6600_0000, 0x6700_0000));
        Assert.Equal(HybridCpuKernelStatusV1.BudgetExhausted, exhausted.Status);
        Assert.Equal(HybridCpuPlatformContractV1.MaximumExecutionContexts, kernel.Contexts().Count);
    }

    [Fact]
    public void RealImageBootstrapKernelManagedThreadAndIseX4_UseOneExecutionPath()
    {
        const string entry = "phase11_managed_entry";
        const string helperName = "__hybridcpu_managed_alloc";
        const string constructor = "phase11_ctor";
        HybridCpuStaticLinkArtifactV1 link = CompilerRefPlan7Phase03ManagedHeapAllocatorTests.LinkAllocationImage(
            entry, helperName, constructor, 1, 7);
        HybridCpuRuntimeHelperV1 helper = HybridCpuManagedAbiFamilyV1.Default.ResolveRuntimeHelper(helperName)!;
        HybridCpuImageRuntimeBootstrapDescriptorV1 descriptor = HybridCpuImageRuntimeBootstrapContractV1.Create(
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest, entry, entry,
            [new(helperName, helper.Signature, true)], []);
        HybridCpuRestrictedImageBuilderV1 builder = new HybridCpuRestrictedImageBuilderV1();
        HybridCpuRestrictedImageV1 image = builder.Inspect(builder.Build(new(link, entry,
            RuntimeBootstrap: descriptor)).PackageBytes);
        Assert.Equal(HybridCpuStartupStatusV1.Success, image.Status);
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        HybridCpuKernelBootResultV1 boot = kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest,
            image.OptionsDigest, image.ImageBase,
            CompilerRefPlan7Phase03ManagedHeapAllocatorTests.AlignUp((ulong)image.ImageBytes.Length, 4096),
            image.EntryAddress, HybridCpuRestrictedStartupOptionsV1.Production.StackBase,
            HybridCpuRestrictedStartupOptionsV1.Production.StackSize, 0));
        Assert.True(boot.IsSuccess, boot.Reason);
        Assert.True(new HybridCpuManagedBootstrapRuntimeV1(HybridCpuManagedAbiFamilyV1.Default.ContractDigest)
            .Bootstrap(image.RuntimeBootstrap!, kernel,
                new Dictionary<string, HybridCpuRuntimeHelperEntryV1> { [helperName] = _ => true }).IsSuccess);
        HybridCpuManagedThreadingRuntimeV1 runtime = new HybridCpuManagedThreadingRuntimeV1(kernel, TlsLayout,
            HybridCpuManagedThreadingOptionsV1.Qualification);
        HybridCpuManagedThreadSnapshotV1 main = runtime.AttachInitialThread("main").Thread!;
        HybridCpuManagedThreadSnapshotV1 worker = CreateAndStart(runtime, "worker", WorkerRequest(1));

        Processor.MultiBankMemoryArea memory = new Processor.MultiBankMemoryArea(4, 0x10000UL);
        Processor.CPU_Core core = new Processor.CPU_Core(0, CpuCorePlatformContext.CreateFixed(memory, ProcessorMode.Compiler));
        core.InitializePipeline();
        core.PrepareExecutionStart(image.EntryAddress, activeVtId: 0);
        core.WriteCommittedArch(0, HybridCpuNativeAbiContractV2.ThreadPointerRegister,
            kernel.Contexts().Single(item => item.Descriptor.ContextId == main.ExecutionContextId).Descriptor.ContextCarrierAddress);
        core.WriteCommittedArch(1, HybridCpuNativeAbiContractV2.ThreadPointerRegister,
            kernel.Contexts().Single(item => item.Descriptor.ContextId == worker.ExecutionContextId).Descriptor.ContextCarrierAddress);
        core.ActiveVirtualThreadId = 1;
        core.FlushPipeline(YAKSys_Hybrid_CPU.Core.AssistInvalidationReason.Trap);

        Assert.Equal(main.TlsBase, core.ReadArch(0, HybridCpuNativeAbiContractV2.ThreadPointerRegister));
        Assert.NotEqual(worker.ManagedThreadId,
            core.ReadArch(1, HybridCpuNativeAbiContractV2.ThreadPointerRegister));
        Assert.Equal(worker.TlsBase,
            kernel.Contexts().Single(item => item.Descriptor.ContextId == worker.ExecutionContextId).TlsBase);
        Assert.Equal(image.EntryAddress, boot.Context!.EntryAddress);
    }

    [Fact]
    public void TlsLayoutAndContextScheduleDigests_AreOrderIndependentAndRepeatable()
    {
        HybridCpuManagedTlsLayoutV1 reverse = HybridCpuManagedTlsLayoutV1.Create(
        [
            new("root", true),
            new("counter", false)
        ]);
        Assert.Equal(TlsLayout.LayoutDigest, reverse.LayoutDigest);
        Assert.Equal(TlsLayout.Slots, reverse.Slots);

        string Snapshot()
        {
            (DeterministicRuntimeKernelV1 kernel, HybridCpuManagedThreadingRuntimeV1 runtime) = Runtime();
            CreateAndStart(runtime, "worker", WorkerRequest(1));
            HybridCpuManagedThreadSnapshotV1 main = runtime.Threads().Single(item => item.Name == "main");
            HybridCpuKernelRendezvousResultV1 result = kernel.GcRendezvousEnter(main.ExecutionContextId);
            return result.Snapshot!.SnapshotDigest;
        }
        Assert.Equal(Snapshot(), Snapshot());
    }

    private static (DeterministicRuntimeKernelV1 Kernel, HybridCpuManagedThreadingRuntimeV1 Runtime) Runtime()
    {
        DeterministicRuntimeKernelV1 kernel = BootKernel();
        HybridCpuManagedThreadingRuntimeV1 runtime = new HybridCpuManagedThreadingRuntimeV1(kernel, TlsLayout,
            HybridCpuManagedThreadingOptionsV1.Qualification);
        Assert.True(runtime.AttachInitialThread("main").IsSuccess);
        return (kernel, runtime);
    }

    private static DeterministicRuntimeKernelV1 BootKernel()
    {
        DeterministicRuntimeKernelV1 kernel = new DeterministicRuntimeKernelV1();
        Assert.True(kernel.Boot(new(HybridCpuPlatformContractV1.ContractDigest, new string('a', 64),
            0x0010_0000, 4096, 0x0010_0000, 0x0020_0000, 4096, 0)).IsSuccess);
        return kernel;
    }

    private static HybridCpuExecutionContextCreateRequestV1 WorkerRequest(int carrier,
        ulong guardBase = 0x5000_0000, ulong contextCarrier = 0x6000_0000,
        ulong tlsBase = 0x6100_0000) => new(0x0010_0000, guardBase + 4096, 4096,
        guardBase, 4096, contextCarrier, tlsBase, carrier);

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
