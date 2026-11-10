using System;
using System.IO;
using HybridCPU_ISE.Core;
using HybridCPU_ISE.Tests.TestHelpers;
using Xunit;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Registers;
using YAKSys_Hybrid_CPU.Memory;

namespace HybridCPU_ISE.Tests
{
    public partial class AssistRuntimeTests
    {
        [Fact]
        public void ResolveStableRetireOrder_ExcludesAssistLanesFromRetireTruth()
        {
            LoadMicroOp seed = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 1,
                destReg: 7,
                address: 0x6000,
                domainTag: 1);
            Assert.True(AssistMicroOp.TryCreateFromSeed(
                seed,
                carrierVirtualThreadId: 0,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp assist));

            Processor.CPU_Core.WriteBackStage writeBackStage = new();
            writeBackStage.Clear();
            writeBackStage.Valid = true;
            writeBackStage.UsesExplicitPacketLanes = true;
            writeBackStage.RetainsReferenceSequentialPath = false;

            Processor.CPU_Core.ScalarWriteBackLaneState lane0 = new();
            lane0.Clear(0);
            lane0.IsOccupied = true;
            lane0.MicroOp = MicroOpTestHelper.CreateScalarALU(0, destReg: 1, src1Reg: 2, src2Reg: 3);
            writeBackStage.SetLane(0, lane0);

            Processor.CPU_Core.ScalarWriteBackLaneState lane4 = new();
            lane4.Clear(4);
            lane4.IsOccupied = true;
            lane4.MicroOp = assist;
            writeBackStage.SetLane(4, lane4);

            Span<byte> retireOrder = stackalloc byte[7];
            int retireLaneCount = Processor.CPU_Core.ResolveStableRetireOrder(writeBackStage, retireOrder);

            Assert.Equal(new byte[] { 0 }, retireOrder[..retireLaneCount].ToArray());
            Assert.False(Processor.CPU_Core.CanRetireLanePrecisely(writeBackStage, 4));
        }

        [Fact]
        public void NotifySerializingCommit_InvalidatesAssistNominationState()
        {
            var scheduler = new MicroOpScheduler();
            LoadMicroOp seed = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 1,
                destReg: 7,
                address: 0x7000,
                domainTag: 1);
            Assert.True(AssistMicroOp.TryCreateFromSeed(
                seed,
                carrierVirtualThreadId: 0,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp assist));

            scheduler.NominateAssistCandidate(1, assist);
            scheduler.NotifySerializingCommit();

            Assert.Equal(1, scheduler.AssistInvalidations);
            Assert.Equal(AssistInvalidationReason.SerializingBoundary, scheduler.LastAssistInvalidationReason);

            MicroOp[] bundle = new MicroOp[8];
            bundle[0] = MicroOpTestHelper.CreateScalarALU(0, destReg: 1, src1Reg: 2, src2Reg: 3);
            MicroOp[] packed = scheduler.PackBundleIntraCoreSmt(bundle, ownerVirtualThreadId: 0, localCoreId: 0);

            Assert.DoesNotContain(packed, microOp => ReferenceEquals(microOp, assist));
            Assert.Equal(0, scheduler.AssistInjectionsCount);
        }

        [Fact]
        public void ApplyRetiredSystemEventForTesting_Fence_InvalidatesAssistRuntimeWithFenceReason()
        {
            var core = new Processor.CPU_Core(0);
            core.PrepareExecutionStart(0x1000);
            core.TestInitializeFSPScheduler();

            LoadMicroOp seed = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 1,
                destReg: 4,
                address: 0x7100,
                domainTag: 1);
            Assert.True(AssistMicroOp.TryCreateFromSeed(
                seed,
                carrierVirtualThreadId: 0,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp assist));

            MicroOpScheduler scheduler = core.TestGetFSPScheduler()!;
            scheduler.NominateAssistCandidate(1, assist);
            long assistInvalidationsBefore = scheduler.AssistInvalidations;

            core.ApplyRetiredSystemEventForTesting(
                new YAKSys_Hybrid_CPU.Core.Pipeline.FenceEvent
                {
                    VtId = 0,
                    BundleSerial = 1,
                    IsInstructionFence = false
                },
                virtualThreadId: 0,
                retiredPc: 0x1000);

            Assert.Equal(assistInvalidationsBefore + 1, scheduler.AssistInvalidations);
            Assert.Equal(AssistInvalidationReason.Fence, scheduler.LastAssistInvalidationReason);
        }

#if false // Retired successful VM-transition assist invalidation through VMCS state; manager removed in task 190.
        [Fact]
        public void ApplyRetiredVmxEffectForTesting_OnInactiveGuestVt_InvalidatesAssistRuntimeWithVmTransition()
        {
            var core = new Processor.CPU_Core(0);
            core.PrepareExecutionStart(0x1000, activeVtId: 0);
            core.TestInitializeFSPScheduler();
            core.WriteCommittedPc(2, 0x2200);
            core.WriteCommittedArch(2, 2, 0x3300);
            core.WriteVirtualThreadPipelineState(2, PipelineState.GuestExecution);
            core.Csr.Write(CsrAddresses.VmxEnable, 1, PrivilegeLevel.Machine);
            core.Vmcs.LoadPointer(0x1002);
            core.Vmcs.WriteFieldValue(VmcsField.HostPc, unchecked((long)0x6600UL));
            core.Vmcs.WriteFieldValue(VmcsField.HostSp, unchecked((long)0x7700UL));

            LoadMicroOp seed = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 2,
                destReg: 8,
                address: 0x7200,
                domainTag: 1);
            Assert.True(AssistMicroOp.TryCreateFromSeed(
                seed,
                carrierVirtualThreadId: 0,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp assist));

            MicroOpScheduler scheduler = core.TestGetFSPScheduler()!;
            scheduler.NominateAssistCandidate(2, assist);
            long assistInvalidationsBefore = scheduler.AssistInvalidations;

            VmxRetireOutcome outcome = core.ApplyRetiredVmxEffectForTesting(
                VmxRetireEffect.Control(
                    VmxOperationKind.VmxOff,
                    exitGuestContextOnRetire: true),
                virtualThreadId: 2);

            Assert.True(outcome.FlushesPipeline);
            Assert.Equal(assistInvalidationsBefore + 1, scheduler.AssistInvalidations);
            Assert.Equal(AssistInvalidationReason.VmTransition, scheduler.LastAssistInvalidationReason);
        }

#endif

        [Fact]
        public void ExecuteAssistMicroOp_WhenTargetLeavesForeground_InvalidatesAssistRuntimeWithOwnerReason()
        {
            var core = new Processor.CPU_Core(0);
            core.PrepareExecutionStart(0x1000);
            core.TestInitializeFSPScheduler();
            core.WriteVirtualThreadPipelineState(1, PipelineState.WaitForEvent);

            LoadMicroOp seed = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 1,
                destReg: 9,
                address: 0x7300,
                domainTag: 1);
            Assert.True(AssistMicroOp.TryCreateFromSeed(
                seed,
                carrierVirtualThreadId: 0,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp assist));

            MicroOpScheduler scheduler = core.TestGetFSPScheduler()!;
            scheduler.NominateAssistCandidate(1, assist);
            long assistInvalidationsBefore = scheduler.AssistInvalidations;

            Assert.True(core.ExecuteAssistMicroOp(assist));
            Assert.Equal(assistInvalidationsBefore + 1, scheduler.AssistInvalidations);
            Assert.Equal(AssistInvalidationReason.OwnerInvalidation, scheduler.LastAssistInvalidationReason);
        }

        [Fact]
        public void ExecuteAssistMicroOp_WhenDonorLeavesForeground_InvalidatesAssistRuntimeWithOwnerReason()
        {
            var core = new Processor.CPU_Core(0);
            core.PrepareExecutionStart(0x1000);
            core.TestInitializeFSPScheduler();
            core.WriteVirtualThreadPipelineState(2, PipelineState.WaitForEvent);

            Assert.True(AssistMicroOp.TryCreateFromSeed(
                CreateVectorSeed(virtualThreadId: 2, ownerContextId: 3, baseAddress: 0x7400, src2Address: 0x7800),
                carrierVirtualThreadId: 0,
                targetVirtualThreadId: 1,
                replayEpochId: 0,
                assistEpochId: 0,
                assistMicroOp: out AssistMicroOp assist));

            MicroOpScheduler scheduler = core.TestGetFSPScheduler()!;
            scheduler.NominateAssistCandidate(1, assist);
            long assistInvalidationsBefore = scheduler.AssistInvalidations;

            Assert.True(core.ExecuteAssistMicroOp(assist));
            Assert.Equal(assistInvalidationsBefore + 1, scheduler.AssistInvalidations);
            Assert.Equal(AssistInvalidationReason.OwnerInvalidation, scheduler.LastAssistInvalidationReason);
        }

        [Fact]
        public void ExecuteAssistMicroOp_WhenInterCoreDonorSnapshotDrifts_InvalidatesAssistRuntimeWithInterCoreOwnerDrift()
        {
            var core = new Processor.CPU_Core(15);
            core.PrepareExecutionStart(0x1000);
            core.TestInitializeFSPScheduler();

            var pod = new PodController(0, 0, new MicroOpScheduler());
            Processor.Pods[0] = pod;
            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                CreateVectorSeed(
                    virtualThreadId: 2,
                    ownerContextId: 3,
                    baseAddress: 0x8400,
                    src2Address: 0x8800,
                    domainTag: 1),
                donorCoreId: 1,
                donorPodId: pod.PodId,
                out AssistInterCoreTransport transport));

            Assert.True(AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: pod.PodId,
                carrierCoreId: 15,
                carrierVirtualThreadId: 0,
                targetCoreId: 15,
                targetVirtualThreadId: 0,
                targetOwnerContextId: 9,
                targetDomainTag: 1,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp assist));

            pod.PublishAssistOwnerSnapshot(localCoreId: 1, virtualThreadId: 2, ownerContextId: 99, domainTag: 1);

            MicroOpScheduler scheduler = core.TestGetFSPScheduler()!;
            scheduler.NominateAssistCandidate(1, assist);
            long assistInvalidationsBefore = scheduler.AssistInvalidations;

            Assert.True(core.ExecuteAssistMicroOp(assist));
            Assert.Equal(assistInvalidationsBefore + 1, scheduler.AssistInvalidations);
            Assert.Equal(AssistInvalidationReason.InterCoreOwnerDrift, scheduler.LastAssistInvalidationReason);
        }

        [Fact]
        public void ExecuteAssistMicroOp_WhenInterCoreDonorPrefetchSnapshotDrifts_InvalidatesAssistRuntimeWithInterCoreOwnerDrift()
        {
            var core = new Processor.CPU_Core(14);
            core.PrepareExecutionStart(0x1000);
            core.TestInitializeFSPScheduler();

            var pod = new PodController(0, 0, new MicroOpScheduler());
            Processor.Pods[0] = pod;

            LoadMicroOp load = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 2,
                destReg: 5,
                address: 0x8C00,
                domainTag: 1);
            load.OwnerContextId = 3;
            load.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                load,
                donorCoreId: 1,
                donorPodId: pod.PodId,
                out AssistInterCoreTransport transport));

            Assert.True(AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: pod.PodId,
                carrierCoreId: 14,
                carrierVirtualThreadId: 2,
                targetCoreId: 14,
                targetVirtualThreadId: 2,
                targetOwnerContextId: 9,
                targetDomainTag: 1,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp assist));

            pod.PublishAssistOwnerSnapshot(
                localCoreId: 1,
                virtualThreadId: 1,
                ownerContextId: 7,
                domainTag: 1);

            MicroOpScheduler scheduler = core.TestGetFSPScheduler()!;
            long assistInvalidationsBefore = scheduler.AssistInvalidations;

            Assert.True(core.ExecuteAssistMicroOp(assist));
            Assert.Equal(assistInvalidationsBefore + 1, scheduler.AssistInvalidations);
            Assert.Equal(AssistInvalidationReason.InterCoreOwnerDrift, scheduler.LastAssistInvalidationReason);
        }

        [Fact]
        public void ExecuteAssistMicroOp_WhenInterCoreDefaultStoreDonorPrefetchSnapshotDrifts_InvalidatesAssistRuntimeWithInterCoreOwnerDrift()
        {
            var core = new Processor.CPU_Core(21);
            core.PrepareExecutionStart(0x1000);
            core.TestInitializeFSPScheduler();

            var pod = new PodController(0, 0, new MicroOpScheduler());
            Processor.Pods[0] = pod;

            StoreMicroOp store = MicroOpTestHelper.CreateStore(
                virtualThreadId: 2,
                srcReg: 5,
                address: 0x8C80,
                domainTag: 1);
            store.OwnerContextId = 3;
            store.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                store,
                donorCoreId: 1,
                donorPodId: pod.PodId,
                donorAssistEpochId: 29,
                out AssistInterCoreTransport transport));

            Assert.True(AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: pod.PodId,
                carrierCoreId: 21,
                carrierVirtualThreadId: 2,
                targetCoreId: 21,
                targetVirtualThreadId: 2,
                targetOwnerContextId: 9,
                targetDomainTag: 1,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp assist));

            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, assist.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, assist.CarrierKind);
            Assert.Equal(SlotClass.DmaStreamClass, assist.CarrierSlotClass);
            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtDefaultStoreSeed, assist.DonorSource.Kind);

            pod.PublishAssistOwnerSnapshot(
                localCoreId: 1,
                virtualThreadId: 1,
                ownerContextId: 7,
                domainTag: 1,
                assistEpochId: 30);

            MicroOpScheduler scheduler = core.TestGetFSPScheduler()!;
            long assistInvalidationsBefore = scheduler.AssistInvalidations;

            Assert.True(core.ExecuteAssistMicroOp(assist));
            Assert.Equal(assistInvalidationsBefore + 1, scheduler.AssistInvalidations);
            Assert.Equal(AssistInvalidationReason.InterCoreOwnerDrift, scheduler.LastAssistInvalidationReason);
        }

        [Fact]
        public void ExecuteAssistMicroOp_WhenInterCoreHotLoadDonorPrefetchSnapshotDrifts_InvalidatesAssistRuntimeWithInterCoreOwnerDrift()
        {
            var core = new Processor.CPU_Core(18);
            core.PrepareExecutionStart(0x1000);
            core.TestInitializeFSPScheduler();

            var pod = new PodController(0, 0, new MicroOpScheduler());
            Processor.Pods[0] = pod;

            LoadMicroOp load = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 2,
                destReg: 5,
                address: 0x8D00,
                domainTag: 1);
            load.OwnerContextId = 3;
            load.MemoryLocalityHint = LocalityHint.Hot;
            load.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                load,
                donorCoreId: 1,
                donorPodId: pod.PodId,
                donorAssistEpochId: 23,
                out AssistInterCoreTransport transport));

            Assert.True(AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: pod.PodId,
                carrierCoreId: 18,
                carrierVirtualThreadId: 2,
                targetCoreId: 18,
                targetVirtualThreadId: 2,
                targetOwnerContextId: 9,
                targetDomainTag: 1,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp assist));

            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtHotLoadSeed, assist.DonorSource.Kind);

            pod.PublishAssistOwnerSnapshot(
                localCoreId: 1,
                virtualThreadId: 1,
                ownerContextId: 7,
                domainTag: 1,
                assistEpochId: 24);

            MicroOpScheduler scheduler = core.TestGetFSPScheduler()!;
            long assistInvalidationsBefore = scheduler.AssistInvalidations;

            Assert.True(core.ExecuteAssistMicroOp(assist));
            Assert.Equal(assistInvalidationsBefore + 1, scheduler.AssistInvalidations);
            Assert.Equal(AssistInvalidationReason.InterCoreOwnerDrift, scheduler.LastAssistInvalidationReason);
        }

        [Fact]
        public void ExecuteAssistMicroOp_WhenInterCoreHotStoreDonorPrefetchSnapshotDrifts_InvalidatesAssistRuntimeWithInterCoreOwnerDrift()
        {
            var core = new Processor.CPU_Core(19);
            core.PrepareExecutionStart(0x1000);
            core.TestInitializeFSPScheduler();

            var pod = new PodController(0, 0, new MicroOpScheduler());
            Processor.Pods[0] = pod;

            StoreMicroOp store = MicroOpTestHelper.CreateStore(
                virtualThreadId: 2,
                srcReg: 5,
                address: 0x8D80,
                domainTag: 1);
            store.OwnerContextId = 3;
            store.MemoryLocalityHint = LocalityHint.Hot;
            store.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                store,
                donorCoreId: 1,
                donorPodId: pod.PodId,
                donorAssistEpochId: 27,
                out AssistInterCoreTransport transport));

            Assert.True(AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: pod.PodId,
                carrierCoreId: 19,
                carrierVirtualThreadId: 2,
                targetCoreId: 19,
                targetVirtualThreadId: 2,
                targetOwnerContextId: 9,
                targetDomainTag: 1,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp assist));

            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, assist.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, assist.CarrierKind);
            Assert.Equal(SlotClass.DmaStreamClass, assist.CarrierSlotClass);
            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtHotStoreSeed, assist.DonorSource.Kind);

            pod.PublishAssistOwnerSnapshot(
                localCoreId: 1,
                virtualThreadId: 1,
                ownerContextId: 7,
                domainTag: 1,
                assistEpochId: 28);

            MicroOpScheduler scheduler = core.TestGetFSPScheduler()!;
            long assistInvalidationsBefore = scheduler.AssistInvalidations;

            Assert.True(core.ExecuteAssistMicroOp(assist));
            Assert.Equal(assistInvalidationsBefore + 1, scheduler.AssistInvalidations);
            Assert.Equal(AssistInvalidationReason.InterCoreOwnerDrift, scheduler.LastAssistInvalidationReason);
        }

        [Fact]
        public void ExecuteAssistMicroOp_WhenInterCoreLane6ColdStoreLdsaSnapshotDrifts_InvalidatesAssistRuntimeWithInterCoreOwnerDrift()
        {
            var core = new Processor.CPU_Core(20);
            core.PrepareExecutionStart(0x1000);
            core.TestInitializeFSPScheduler();

            var pod = new PodController(0, 0, new MicroOpScheduler());
            Processor.Pods[0] = pod;

            StoreMicroOp store = MicroOpTestHelper.CreateStore(
                virtualThreadId: 2,
                srcReg: 5,
                address: 0x8DC0,
                domainTag: 1);
            store.OwnerContextId = 3;
            store.MemoryLocalityHint = LocalityHint.Cold;
            store.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                store,
                donorCoreId: 1,
                donorPodId: pod.PodId,
                donorAssistEpochId: 29,
                out AssistInterCoreTransport transport));

            Assert.True(AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: pod.PodId,
                carrierCoreId: 20,
                carrierVirtualThreadId: 2,
                targetCoreId: 20,
                targetVirtualThreadId: 2,
                targetOwnerContextId: 9,
                targetDomainTag: 1,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp assist));

            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtColdStoreSeed, assist.DonorSource.Kind);

            pod.PublishAssistOwnerSnapshot(
                localCoreId: 1,
                virtualThreadId: 1,
                ownerContextId: 7,
                domainTag: 1,
                assistEpochId: 30);

            MicroOpScheduler scheduler = core.TestGetFSPScheduler()!;
            long assistInvalidationsBefore = scheduler.AssistInvalidations;

            Assert.True(core.ExecuteAssistMicroOp(assist));
            Assert.Equal(assistInvalidationsBefore + 1, scheduler.AssistInvalidations);
            Assert.Equal(AssistInvalidationReason.InterCoreOwnerDrift, scheduler.LastAssistInvalidationReason);
        }

        [Fact]
        public void ExecuteAssistMicroOp_WhenInterCoreCrossVtDonorPrefetchSnapshotDrifts_InvalidatesAssistRuntimeWithInterCoreOwnerDrift()
        {
            var core = new Processor.CPU_Core(13);
            core.PrepareExecutionStart(0x1000, activeVtId: 1);
            core.TestInitializeFSPScheduler();

            var pod = new PodController(0, 0, new MicroOpScheduler());
            Processor.Pods[0] = pod;

            LoadMicroOp load = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 2,
                destReg: 5,
                address: 0x8E00,
                domainTag: 1);
            load.OwnerContextId = 3;
            load.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                load,
                donorCoreId: 1,
                donorPodId: pod.PodId,
                out AssistInterCoreTransport transport));

            Assert.True(AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: pod.PodId,
                carrierCoreId: 13,
                carrierVirtualThreadId: 1,
                targetCoreId: 13,
                targetVirtualThreadId: 1,
                targetOwnerContextId: 9,
                targetDomainTag: 1,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp assist));

            pod.PublishAssistOwnerSnapshot(
                localCoreId: 1,
                virtualThreadId: 2,
                ownerContextId: 99,
                domainTag: 1);

            MicroOpScheduler scheduler = core.TestGetFSPScheduler()!;
            long assistInvalidationsBefore = scheduler.AssistInvalidations;

            Assert.True(core.ExecuteAssistMicroOp(assist));
            Assert.Equal(assistInvalidationsBefore + 1, scheduler.AssistInvalidations);
            Assert.Equal(AssistInvalidationReason.InterCoreOwnerDrift, scheduler.LastAssistInvalidationReason);
        }

        [Fact]
        public void ExecuteAssistMicroOp_WhenInterCoreDonorAssistEpochDrifts_InvalidatesAssistRuntimeWithInterCoreBoundaryDrift()
        {
            var core = new Processor.CPU_Core(12);
            core.PrepareExecutionStart(0x1000);
            core.TestInitializeFSPScheduler();

            var pod = new PodController(0, 0, new MicroOpScheduler());
            Processor.Pods[0] = pod;

            LoadMicroOp load = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 2,
                destReg: 6,
                address: 0x8D00,
                domainTag: 1);
            load.OwnerContextId = 3;
            load.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                load,
                donorCoreId: 1,
                donorPodId: pod.PodId,
                donorAssistEpochId: 7,
                out AssistInterCoreTransport transport));

            Assert.True(AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: pod.PodId,
                carrierCoreId: 12,
                carrierVirtualThreadId: 2,
                targetCoreId: 12,
                targetVirtualThreadId: 2,
                targetOwnerContextId: 9,
                targetDomainTag: 1,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp assist));

            pod.PublishAssistOwnerSnapshot(
                localCoreId: 1,
                virtualThreadId: 2,
                ownerContextId: 3,
                domainTag: 1,
                assistEpochId: 8);

            MicroOpScheduler scheduler = core.TestGetFSPScheduler()!;
            long assistInvalidationsBefore = scheduler.AssistInvalidations;

            Assert.True(core.ExecuteAssistMicroOp(assist));
            Assert.Equal(assistInvalidationsBefore + 1, scheduler.AssistInvalidations);
            Assert.Equal(AssistInvalidationReason.InterCoreBoundaryDrift, scheduler.LastAssistInvalidationReason);
        }

        [Fact]
        public void ExecuteAssistMicroOp_WhenCrossPodDonorSnapshotDrifts_InvalidatesAssistRuntimeWithInterCoreOwnerDrift()
        {
            var core = new Processor.CPU_Core(16);
            core.PrepareExecutionStart(0x1000, activeVtId: 1);
            core.TestInitializeFSPScheduler();

            var donorPod = new PodController(0, 0, new MicroOpScheduler());
            var targetPod = new PodController(1, 0, new MicroOpScheduler());
            Processor.Pods[0] = donorPod;
            Processor.Pods[1] = targetPod;

            LoadMicroOp load = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 2,
                destReg: 5,
                address: 0x8F00,
                domainTag: 1);
            load.OwnerContextId = 3;
            load.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                load,
                donorCoreId: 1,
                donorPodId: donorPod.PodId,
                out AssistInterCoreTransport transport));

            Assert.True(AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: targetPod.PodId,
                carrierCoreId: 0,
                carrierVirtualThreadId: 1,
                targetCoreId: 0,
                targetVirtualThreadId: 1,
                targetOwnerContextId: 9,
                targetDomainTag: 1,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp assist));

            donorPod.PublishAssistOwnerSnapshot(
                localCoreId: 1,
                virtualThreadId: 2,
                ownerContextId: 99,
                domainTag: 1);

            MicroOpScheduler scheduler = core.TestGetFSPScheduler()!;
            long assistInvalidationsBefore = scheduler.AssistInvalidations;

            Assert.True(core.ExecuteAssistMicroOp(assist));
            Assert.Equal(assistInvalidationsBefore + 1, scheduler.AssistInvalidations);
            Assert.Equal(AssistInvalidationReason.InterCoreOwnerDrift, scheduler.LastAssistInvalidationReason);
        }

        [Fact]
        public void ExecuteAssistMicroOp_WhenCrossPodDonorAssistEpochDrifts_InvalidatesAssistRuntimeWithInterCoreBoundaryDrift()
        {
            var core = new Processor.CPU_Core(16);
            core.PrepareExecutionStart(0x1000, activeVtId: 1);
            core.TestInitializeFSPScheduler();

            var donorPod = new PodController(0, 0, new MicroOpScheduler());
            var targetPod = new PodController(1, 0, new MicroOpScheduler());
            Processor.Pods[0] = donorPod;
            Processor.Pods[1] = targetPod;

            LoadMicroOp load = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 2,
                destReg: 5,
                address: 0x9100,
                domainTag: 1);
            load.OwnerContextId = 3;
            load.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                load,
                donorCoreId: 1,
                donorPodId: donorPod.PodId,
                donorAssistEpochId: 11,
                out AssistInterCoreTransport transport));

            Assert.True(AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: targetPod.PodId,
                carrierCoreId: 0,
                carrierVirtualThreadId: 1,
                targetCoreId: 0,
                targetVirtualThreadId: 1,
                targetOwnerContextId: 9,
                targetDomainTag: 1,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp assist));

            donorPod.PublishAssistOwnerSnapshot(
                localCoreId: 1,
                virtualThreadId: 2,
                ownerContextId: 3,
                domainTag: 1,
                assistEpochId: 12);

            MicroOpScheduler scheduler = core.TestGetFSPScheduler()!;
            long assistInvalidationsBefore = scheduler.AssistInvalidations;

            Assert.True(core.ExecuteAssistMicroOp(assist));
            Assert.Equal(assistInvalidationsBefore + 1, scheduler.AssistInvalidations);
            Assert.Equal(AssistInvalidationReason.InterCoreBoundaryDrift, scheduler.LastAssistInvalidationReason);
        }

        [Fact]
        public void BundleResourceCertificate_WhenInterCoreVdsaOwnerTupleDiffers_ProducesDistinctStructuralIdentity()
        {
            VectorBinaryOpMicroOp vector = CreateVectorSeed(
                virtualThreadId: 2,
                ownerContextId: 3,
                baseAddress: 0x9200,
                src2Address: 0x9A00,
                domainTag: 1);

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                vector,
                donorCoreId: 1,
                donorPodId: 0x120,
                donorAssistEpochId: 7,
                out AssistInterCoreTransport transport));

            Assert.True(AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: 0x120,
                carrierCoreId: 0,
                carrierVirtualThreadId: 0,
                targetCoreId: 0,
                targetVirtualThreadId: 2,
                targetOwnerContextId: 11,
                targetDomainTag: 1,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp sameVtAssist));

            Assert.True(AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: 0x120,
                carrierCoreId: 0,
                carrierVirtualThreadId: 0,
                targetCoreId: 0,
                targetVirtualThreadId: 1,
                targetOwnerContextId: 11,
                targetDomainTag: 1,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp donorVtAssist));

            BundleResourceCertificate sameVtCertificate =
                BundleResourceCertificate.Create(new MicroOp[] { sameVtAssist }, ownerThreadId: 0, cycleNumber: 1);
            BundleResourceCertificate donorVtCertificate =
                BundleResourceCertificate.Create(new MicroOp[] { donorVtAssist }, ownerThreadId: 0, cycleNumber: 1);

            Assert.NotEqual(
                AssistOwnershipFingerprint.Compute(sameVtAssist),
                AssistOwnershipFingerprint.Compute(donorVtAssist));
            Assert.NotEqual(
                sameVtCertificate.AssistStructuralKey,
                donorVtCertificate.AssistStructuralKey);
            Assert.NotEqual(sameVtCertificate.StructuralIdentity, donorVtCertificate.StructuralIdentity);
        }

        [Fact]
        public void BundleResourceCertificate_WhenInterCoreWritebackVdsaOwnerTupleDiffersFromReadBackedTuple_ProducesDistinctStructuralIdentity()
        {
            VectorBinaryOpMicroOp vectorRead = CreateVectorSeed(
                virtualThreadId: 2,
                ownerContextId: 3,
                baseAddress: 0x9220,
                src2Address: 0x9A20,
                domainTag: 1);
            StoreSegmentMicroOp vectorWrite = CreateVectorStoreSeed(
                virtualThreadId: 2,
                ownerContextId: 3,
                baseAddress: 0x9240,
                domainTag: 1);

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                vectorRead,
                donorCoreId: 1,
                donorPodId: 0x120,
                donorAssistEpochId: 7,
                out AssistInterCoreTransport readTransport));
            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                vectorWrite,
                donorCoreId: 1,
                donorPodId: 0x120,
                donorAssistEpochId: 7,
                out AssistInterCoreTransport writeTransport));

            Assert.True(AssistMicroOp.TryCreateFromInterCoreTransport(
                readTransport,
                podId: 0x120,
                carrierCoreId: 0,
                carrierVirtualThreadId: 0,
                targetCoreId: 0,
                targetVirtualThreadId: 2,
                targetOwnerContextId: 11,
                targetDomainTag: 1,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp readAssist));
            Assert.True(AssistMicroOp.TryCreateFromInterCoreTransport(
                writeTransport,
                podId: 0x120,
                carrierCoreId: 0,
                carrierVirtualThreadId: 0,
                targetCoreId: 0,
                targetVirtualThreadId: 2,
                targetOwnerContextId: 11,
                targetDomainTag: 1,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp writeAssist));

            BundleResourceCertificate readCertificate =
                BundleResourceCertificate.Create(new MicroOp[] { readAssist }, ownerThreadId: 0, cycleNumber: 1);
            BundleResourceCertificate writeCertificate =
                BundleResourceCertificate.Create(new MicroOp[] { writeAssist }, ownerThreadId: 0, cycleNumber: 1);

            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtVector, readAssist.DonorSource.Kind);
            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtVectorWriteback, writeAssist.DonorSource.Kind);
            Assert.NotEqual(
                AssistOwnershipFingerprint.Compute(readAssist),
                AssistOwnershipFingerprint.Compute(writeAssist));
            Assert.NotEqual(
                readCertificate.AssistStructuralKey,
                writeCertificate.AssistStructuralKey);
            Assert.NotEqual(readCertificate.StructuralIdentity, writeCertificate.StructuralIdentity);
        }

        [Fact]
        public void BundleResourceCertificate_WhenInterCoreDonorPrefetchOwnerTupleDiffers_ProducesDistinctStructuralIdentity()
        {
            LoadMicroOp load = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 2,
                destReg: 5,
                address: 0xA200,
                domainTag: 1);
            load.OwnerContextId = 3;
            load.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                load,
                donorCoreId: 1,
                donorPodId: 0x120,
                donorAssistEpochId: 7,
                out AssistInterCoreTransport transport));

            Assert.True(AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: 0x120,
                carrierCoreId: 0,
                carrierVirtualThreadId: 0,
                targetCoreId: 0,
                targetVirtualThreadId: 2,
                targetOwnerContextId: 11,
                targetDomainTag: 1,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp sameVtAssist));

            Assert.True(AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: 0x120,
                carrierCoreId: 0,
                carrierVirtualThreadId: 0,
                targetCoreId: 0,
                targetVirtualThreadId: 1,
                targetOwnerContextId: 11,
                targetDomainTag: 1,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp donorVtAssist));

            BundleResourceCertificate sameVtCertificate =
                BundleResourceCertificate.Create(new MicroOp[] { sameVtAssist }, ownerThreadId: 0, cycleNumber: 1);
            BundleResourceCertificate donorVtCertificate =
                BundleResourceCertificate.Create(new MicroOp[] { donorVtAssist }, ownerThreadId: 0, cycleNumber: 1);

            Assert.Equal(AssistKind.DonorPrefetch, sameVtAssist.Kind);
            Assert.Equal(AssistKind.DonorPrefetch, donorVtAssist.Kind);
            Assert.Equal(AssistCarrierKind.Lane6Dma, sameVtAssist.CarrierKind);
            Assert.Equal(AssistCarrierKind.Lane6Dma, donorVtAssist.CarrierKind);
            Assert.NotEqual(
                AssistOwnershipFingerprint.Compute(sameVtAssist),
                AssistOwnershipFingerprint.Compute(donorVtAssist));
            Assert.NotEqual(
                sameVtCertificate.AssistStructuralKey,
                donorVtCertificate.AssistStructuralKey);
            Assert.NotEqual(sameVtCertificate.StructuralIdentity, donorVtCertificate.StructuralIdentity);
        }

        [Fact]
        public void BundleResourceCertificate_WhenInterCoreLane6DefaultStoreDonorPrefetchOwnerTupleDiffers_ProducesDistinctStructuralIdentity()
        {
            StoreMicroOp store = MicroOpTestHelper.CreateStore(
                virtualThreadId: 2,
                srcReg: 5,
                address: 0xA400,
                domainTag: 1);
            store.OwnerContextId = 3;
            store.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                store,
                donorCoreId: 1,
                donorPodId: 0x120,
                donorAssistEpochId: 7,
                out AssistInterCoreTransport transport));

            Assert.True(AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: 0x120,
                carrierCoreId: 0,
                carrierVirtualThreadId: 0,
                targetCoreId: 0,
                targetVirtualThreadId: 2,
                targetOwnerContextId: 11,
                targetDomainTag: 1,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp sameVtAssist));

            Assert.True(AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: 0x120,
                carrierCoreId: 0,
                carrierVirtualThreadId: 0,
                targetCoreId: 0,
                targetVirtualThreadId: 1,
                targetOwnerContextId: 11,
                targetDomainTag: 1,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp donorVtAssist));

            BundleResourceCertificate sameVtCertificate =
                BundleResourceCertificate.Create(new MicroOp[] { sameVtAssist }, ownerThreadId: 0, cycleNumber: 1);
            BundleResourceCertificate donorVtCertificate =
                BundleResourceCertificate.Create(new MicroOp[] { donorVtAssist }, ownerThreadId: 0, cycleNumber: 1);

            Assert.Equal(AssistKind.DonorPrefetch, sameVtAssist.Kind);
            Assert.Equal(AssistKind.DonorPrefetch, donorVtAssist.Kind);
            Assert.Equal(AssistCarrierKind.Lane6Dma, sameVtAssist.CarrierKind);
            Assert.Equal(AssistCarrierKind.Lane6Dma, donorVtAssist.CarrierKind);
            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtDefaultStoreSeed, sameVtAssist.DonorSource.Kind);
            Assert.Equal(AssistDonorSourceKind.InterCoreVtDonorDefaultStoreSeed, donorVtAssist.DonorSource.Kind);
            Assert.NotEqual(
                AssistOwnershipFingerprint.Compute(sameVtAssist),
                AssistOwnershipFingerprint.Compute(donorVtAssist));
            Assert.NotEqual(
                sameVtCertificate.AssistStructuralKey,
                donorVtCertificate.AssistStructuralKey);
            Assert.NotEqual(sameVtCertificate.StructuralIdentity, donorVtCertificate.StructuralIdentity);
        }

    }
}
