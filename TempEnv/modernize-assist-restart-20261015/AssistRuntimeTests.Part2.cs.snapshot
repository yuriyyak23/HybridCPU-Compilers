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
        public void AssistConstructor_WithSameThreadDonorPrefetchOnLane6Carrier_ThrowsArgumentException()
        {
            var ownerBinding = new AssistOwnerBinding(
                carrierVirtualThreadId: 0,
                donorVirtualThreadId: 0,
                targetVirtualThreadId: 0,
                ownerContextId: 3,
                domainTag: 1,
                replayEpochId: 11,
                assistEpochId: 7,
                localityHint: LocalityHint.None,
                donorSource: new AssistDonorSourceDescriptor(
                    AssistDonorSourceKind.SameThreadSeed,
                    donorVirtualThreadId: 0,
                    targetVirtualThreadId: 0,
                    ownerContextId: 3,
                    domainTag: 1));

            Assert.Throws<ArgumentException>(() => new AssistMicroOp(
                AssistKind.DonorPrefetch,
                AssistExecutionMode.StreamRegisterPrefetch,
                AssistCarrierKind.Lane6Dma,
                baseAddress: 0x2C00,
                prefetchLength: 64,
                elementSize: 8,
                elementCount: 8,
                ownerBinding));
        }

        [Fact]
        public void AssistConstructor_WithGenericInterCoreSeedOnLsuDonorPrefetch_ThrowsArgumentException()
        {
            var ownerBinding = new AssistOwnerBinding(
                carrierVirtualThreadId: 0,
                donorVirtualThreadId: 2,
                targetVirtualThreadId: 2,
                ownerContextId: 3,
                domainTag: 1,
                replayEpochId: 11,
                assistEpochId: 7,
                localityHint: LocalityHint.None,
                donorSource: new AssistDonorSourceDescriptor(
                    AssistDonorSourceKind.InterCoreSameVtSeed,
                    donorVirtualThreadId: 2,
                    targetVirtualThreadId: 2,
                    ownerContextId: 3,
                    domainTag: 1,
                    sourceCoreId: 1,
                    sourcePodId: 0x120,
                    sourceAssistEpochId: 19));

            Assert.Throws<ArgumentException>(() => new AssistMicroOp(
                AssistKind.DonorPrefetch,
                AssistExecutionMode.CachePrefetch,
                AssistCarrierKind.LsuHosted,
                baseAddress: 0x2E00,
                prefetchLength: 64,
                elementSize: 8,
                elementCount: 1,
                ownerBinding));
        }

        [Fact]
        public void AssistConstructor_WithInterCoreSeedOnLsuLdsa_ThrowsArgumentException()
        {
            var ownerBinding = new AssistOwnerBinding(
                carrierVirtualThreadId: 0,
                donorVirtualThreadId: 2,
                targetVirtualThreadId: 2,
                ownerContextId: 3,
                domainTag: 1,
                replayEpochId: 11,
                assistEpochId: 7,
                localityHint: LocalityHint.Cold,
                donorSource: new AssistDonorSourceDescriptor(
                    AssistDonorSourceKind.InterCoreSameVtSeed,
                    donorVirtualThreadId: 2,
                    targetVirtualThreadId: 2,
                    ownerContextId: 3,
                    domainTag: 1,
                    sourceCoreId: 1,
                    sourcePodId: 0x120,
                    sourceAssistEpochId: 19));

            Assert.Throws<ArgumentException>(() => new AssistMicroOp(
                AssistKind.Ldsa,
                AssistExecutionMode.CachePrefetch,
                AssistCarrierKind.LsuHosted,
                baseAddress: 0x3000,
                prefetchLength: 64,
                elementSize: 8,
                elementCount: 1,
                ownerBinding));
        }

        [Fact]
        public void AssistInterCoreTransportConstructor_WithNonAssistRuntimeSeed_ThrowsArgumentException()
        {
            MicroOp seed = MicroOpTestHelper.CreateScalarALU(
                virtualThreadId: 0,
                destReg: 1,
                src1Reg: 2,
                src2Reg: 3);

            Assert.Throws<ArgumentException>(() => new AssistInterCoreTransport(
                seed,
                donorCoreId: 0,
                donorPodId: 0x120,
                donorAssistEpochId: 7));
        }

        [Fact]
        public void AssistInterCoreTransportConstructor_WithAssistSeedOutsideCurrentClassSet_ThrowsArgumentException()
        {
            VectorBinaryOpMicroOp vector = CreateVectorSeed(virtualThreadId: 2);
            Assert.True(AssistMicroOp.TryCreateFromSeed(
                vector,
                carrierVirtualThreadId: 1,
                replayEpochId: 5,
                assistEpochId: 2,
                out AssistMicroOp assist));

            Assert.Throws<ArgumentException>(() => new AssistInterCoreTransport(
                assist,
                donorCoreId: 0,
                donorPodId: 0x120,
                donorAssistEpochId: 7));
        }

        [Fact]
        public void PackBundle_WithInterCoreVdsaAssistNomination_InjectsAssistIntoLane6ViaDedicatedAssistTransport()
        {
            Array.Fill(Processor.Pods, null!);

            var scheduler = new MicroOpScheduler
            {
                TypedSlotEnabled = true
            };
            var pod = new PodController(0, 0, scheduler);
            Processor.Pods[0] = pod;

            pod.PublishAssistOwnerSnapshot(
                localCoreId: 1,
                virtualThreadId: 2,
                ownerContextId: 3,
                domainTag: 1,
                assistEpochId: 0);

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                CreateVectorSeed(
                    virtualThreadId: 2,
                    ownerContextId: 3,
                    baseAddress: 0xB200,
                    src2Address: 0xBA00,
                    domainTag: 1),
                donorCoreId: 1,
                donorPodId: 0,
                out AssistInterCoreTransport transport));

            scheduler.NominateInterCoreAssistCandidate(1, transport);

            MicroOp[] bundle = new MicroOp[8];
            bundle[0] = MicroOpTestHelper.CreateScalarALU(2, destReg: 1, src1Reg: 2, src2Reg: 3);

            MicroOp[] packed = scheduler.PackBundle(
                bundle,
                currentThreadId: 2,
                stealEnabled: true,
                stealMask: 0xFF,
                localCoreId: 0,
                bundleOwnerContextId: 9,
                bundleOwnerDomainTag: 1,
                assistRuntimeEpoch: 5);

            AssistMicroOp injectedAssist = Assert.IsType<AssistMicroOp>(packed[6]);
            Assert.Equal(AssistKind.Vdsa, injectedAssist.Kind);
            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtVector, injectedAssist.DonorSource.Kind);
            Assert.Equal(1, injectedAssist.DonorSource.SourceCoreId);
            Assert.Equal(0, injectedAssist.CarrierCoreId);
            Assert.Equal(0, injectedAssist.TargetCoreId);
            Assert.Equal(5UL, injectedAssist.AssistEpochId);
            Assert.Equal(0, scheduler.SuccessfulInjectionsCount);
            Assert.Equal(1, scheduler.AssistInterCoreNominations);
            Assert.Equal(1, scheduler.AssistInterCoreInjections);
            Assert.Equal(0, scheduler.AssistInterCoreRejects);
            Assert.Equal(1, scheduler.AssistInjectionsCount);
            Assert.Equal(1, scheduler.AssistVdsaInjects);
            Assert.Equal(1, scheduler.AssistSameVtInjects);
            Assert.Equal(0, scheduler.AssistDonorVtInjects);
            Assert.Equal(1, scheduler.AssistInterCoreSameVtVectorInjects);
            Assert.Equal(0, scheduler.AssistInterCoreDonorVtVectorInjects);
            Assert.Equal(1, scheduler.DmaStreamClassInjects);

            SchedulerPhaseMetrics metrics = scheduler.GetPhaseMetrics();
            Assert.Equal(1, metrics.AssistInterCoreNominations);
            Assert.Equal(1, metrics.AssistInterCoreInjections);
            Assert.Equal(0, metrics.AssistInterCoreRejects);
            Assert.Equal(1, metrics.AssistInterCoreSameVtVectorInjects);
            Assert.Equal(0, metrics.AssistInterCoreDonorVtVectorInjects);
        }

        [Fact]
        public void PackBundle_WithInterCoreCrossVtVdsaAssistNomination_InjectsAssistIntoLane6ViaDedicatedAssistTransport()
        {
            Array.Fill(Processor.Pods, null!);

            var scheduler = new MicroOpScheduler
            {
                TypedSlotEnabled = true
            };
            var pod = new PodController(0, 0, scheduler);
            Processor.Pods[0] = pod;

            pod.PublishAssistOwnerSnapshot(
                localCoreId: 1,
                virtualThreadId: 2,
                ownerContextId: 3,
                domainTag: 1,
                assistEpochId: 0);

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                CreateVectorSeed(
                    virtualThreadId: 2,
                    ownerContextId: 3,
                    baseAddress: 0xB600,
                    src2Address: 0xBE00,
                    domainTag: 1),
                donorCoreId: 1,
                donorPodId: 0,
                out AssistInterCoreTransport transport));

            scheduler.NominateInterCoreAssistCandidate(1, transport);

            MicroOp[] bundle = new MicroOp[8];
            bundle[0] = MicroOpTestHelper.CreateScalarALU(1, destReg: 1, src1Reg: 2, src2Reg: 3);

            MicroOp[] packed = scheduler.PackBundle(
                bundle,
                currentThreadId: 1,
                stealEnabled: true,
                stealMask: 0xFF,
                localCoreId: 0,
                bundleOwnerContextId: 9,
                bundleOwnerDomainTag: 1,
                assistRuntimeEpoch: 5);

            AssistMicroOp injectedAssist = Assert.IsType<AssistMicroOp>(packed[6]);
            Assert.Equal(AssistKind.Vdsa, injectedAssist.Kind);
            Assert.Equal(AssistDonorSourceKind.InterCoreVtDonorVector, injectedAssist.DonorSource.Kind);
            Assert.Equal(2, injectedAssist.DonorVirtualThreadId);
            Assert.Equal(1, injectedAssist.TargetVirtualThreadId);
            Assert.True(injectedAssist.DonorSource.IsCrossVirtualThread);
            Assert.Equal(1, injectedAssist.DonorSource.SourceCoreId);
            Assert.Equal(0, injectedAssist.CarrierCoreId);
            Assert.Equal(0, injectedAssist.TargetCoreId);
            Assert.Equal(5UL, injectedAssist.AssistEpochId);
            Assert.Equal(1, scheduler.AssistInterCoreNominations);
            Assert.Equal(1, scheduler.AssistInterCoreInjections);
            Assert.Equal(0, scheduler.AssistInterCoreRejects);
            Assert.Equal(1, scheduler.AssistInjectionsCount);
            Assert.Equal(1, scheduler.AssistVdsaInjects);
            Assert.Equal(0, scheduler.AssistSameVtInjects);
            Assert.Equal(1, scheduler.AssistDonorVtInjects);
            Assert.Equal(0, scheduler.AssistInterCoreSameVtVectorInjects);
            Assert.Equal(1, scheduler.AssistInterCoreDonorVtVectorInjects);
            Assert.Equal(1, scheduler.DmaStreamClassInjects);

            SchedulerPhaseMetrics metrics = scheduler.GetPhaseMetrics();
            Assert.Equal(1, metrics.AssistInterCoreNominations);
            Assert.Equal(1, metrics.AssistInterCoreInjections);
            Assert.Equal(0, metrics.AssistInterCoreRejects);
            Assert.Equal(0, metrics.AssistInterCoreSameVtVectorInjects);
            Assert.Equal(1, metrics.AssistInterCoreDonorVtVectorInjects);
        }

        [Fact]
        public void PackBundle_WithInterCoreWritebackSameVtVdsaAssistNomination_InjectsAssistIntoLane6ViaDedicatedAssistTransport()
        {
            Array.Fill(Processor.Pods, null!);

            var scheduler = new MicroOpScheduler
            {
                TypedSlotEnabled = true
            };
            var pod = new PodController(0, 0, scheduler);
            Processor.Pods[0] = pod;

            pod.PublishAssistOwnerSnapshot(
                localCoreId: 1,
                virtualThreadId: 2,
                ownerContextId: 3,
                domainTag: 1,
                assistEpochId: 0);

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                CreateVectorStoreSeed(
                    virtualThreadId: 2,
                    ownerContextId: 3,
                    baseAddress: 0xB900,
                    domainTag: 1),
                donorCoreId: 1,
                donorPodId: 0,
                out AssistInterCoreTransport transport));

            scheduler.NominateInterCoreAssistCandidate(1, transport);

            MicroOp[] bundle = new MicroOp[8];
            bundle[0] = MicroOpTestHelper.CreateScalarALU(2, destReg: 1, src1Reg: 2, src2Reg: 3);

            MicroOp[] packed = scheduler.PackBundle(
                bundle,
                currentThreadId: 2,
                stealEnabled: true,
                stealMask: 0xFF,
                localCoreId: 0,
                bundleOwnerContextId: 9,
                bundleOwnerDomainTag: 1,
                assistRuntimeEpoch: 5);

            AssistMicroOp injectedAssist = Assert.IsType<AssistMicroOp>(packed[6]);
            Assert.Equal(AssistKind.Vdsa, injectedAssist.Kind);
            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtVectorWriteback, injectedAssist.DonorSource.Kind);
            Assert.Equal(1, scheduler.AssistInterCoreNominations);
            Assert.Equal(1, scheduler.AssistInterCoreInjections);
            Assert.Equal(1, scheduler.AssistVdsaInjects);
            Assert.Equal(1, scheduler.AssistSameVtInjects);
            Assert.Equal(0, scheduler.AssistDonorVtInjects);
            Assert.Equal(0, scheduler.AssistInterCoreSameVtVectorInjects);
            Assert.Equal(0, scheduler.AssistInterCoreDonorVtVectorInjects);
            Assert.Equal(1, scheduler.AssistInterCoreSameVtVectorWritebackInjects);
            Assert.Equal(0, scheduler.AssistInterCoreDonorVtVectorWritebackInjects);

            SchedulerPhaseMetrics metrics = scheduler.GetPhaseMetrics();
            Assert.Equal(0, metrics.AssistInterCoreSameVtVectorInjects);
            Assert.Equal(0, metrics.AssistInterCoreDonorVtVectorInjects);
            Assert.Equal(1, metrics.AssistInterCoreSameVtVectorWritebackInjects);
            Assert.Equal(0, metrics.AssistInterCoreDonorVtVectorWritebackInjects);
        }

        [Fact]
        public void PackBundle_WithInterCoreWritebackCrossVtVdsaAssistNomination_InjectsAssistIntoLane6ViaDedicatedAssistTransport()
        {
            Array.Fill(Processor.Pods, null!);

            var scheduler = new MicroOpScheduler
            {
                TypedSlotEnabled = true
            };
            var pod = new PodController(0, 0, scheduler);
            Processor.Pods[0] = pod;

            pod.PublishAssistOwnerSnapshot(
                localCoreId: 1,
                virtualThreadId: 2,
                ownerContextId: 3,
                domainTag: 1,
                assistEpochId: 0);

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                CreateVectorStoreSeed(
                    virtualThreadId: 2,
                    ownerContextId: 3,
                    baseAddress: 0xBB00,
                    domainTag: 1),
                donorCoreId: 1,
                donorPodId: 0,
                out AssistInterCoreTransport transport));

            scheduler.NominateInterCoreAssistCandidate(1, transport);

            MicroOp[] bundle = new MicroOp[8];
            bundle[0] = MicroOpTestHelper.CreateScalarALU(1, destReg: 1, src1Reg: 2, src2Reg: 3);

            MicroOp[] packed = scheduler.PackBundle(
                bundle,
                currentThreadId: 1,
                stealEnabled: true,
                stealMask: 0xFF,
                localCoreId: 0,
                bundleOwnerContextId: 9,
                bundleOwnerDomainTag: 1,
                assistRuntimeEpoch: 5);

            AssistMicroOp injectedAssist = Assert.IsType<AssistMicroOp>(packed[6]);
            Assert.Equal(AssistKind.Vdsa, injectedAssist.Kind);
            Assert.Equal(AssistDonorSourceKind.InterCoreVtDonorVectorWriteback, injectedAssist.DonorSource.Kind);
            Assert.Equal(1, scheduler.AssistInterCoreNominations);
            Assert.Equal(1, scheduler.AssistInterCoreInjections);
            Assert.Equal(1, scheduler.AssistVdsaInjects);
            Assert.Equal(0, scheduler.AssistSameVtInjects);
            Assert.Equal(1, scheduler.AssistDonorVtInjects);
            Assert.Equal(0, scheduler.AssistInterCoreSameVtVectorInjects);
            Assert.Equal(0, scheduler.AssistInterCoreDonorVtVectorInjects);
            Assert.Equal(0, scheduler.AssistInterCoreSameVtVectorWritebackInjects);
            Assert.Equal(1, scheduler.AssistInterCoreDonorVtVectorWritebackInjects);

            SchedulerPhaseMetrics metrics = scheduler.GetPhaseMetrics();
            Assert.Equal(0, metrics.AssistInterCoreSameVtVectorInjects);
            Assert.Equal(0, metrics.AssistInterCoreDonorVtVectorInjects);
            Assert.Equal(0, metrics.AssistInterCoreSameVtVectorWritebackInjects);
            Assert.Equal(1, metrics.AssistInterCoreDonorVtVectorWritebackInjects);
        }

        [Fact]
        public void PackBundle_WithInterCoreDonorPrefetchAssistNomination_InjectsAssistIntoLane6ViaDedicatedAssistTransport()
        {
            Array.Fill(Processor.Pods, null!);

            var scheduler = new MicroOpScheduler
            {
                TypedSlotEnabled = true
            };
            var pod = new PodController(0, 0, scheduler);
            Processor.Pods[0] = pod;

            LoadMicroOp load = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 2,
                destReg: 5,
                address: 0xC200,
                domainTag: 1);
            load.OwnerContextId = 3;
            load.InitializeMetadata();

            pod.PublishAssistOwnerSnapshot(
                localCoreId: 1,
                virtualThreadId: 2,
                ownerContextId: 3,
                domainTag: 1,
                assistEpochId: 0);

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                load,
                donorCoreId: 1,
                donorPodId: 0,
                out AssistInterCoreTransport transport));

            scheduler.NominateInterCoreAssistCandidate(1, transport);

            MicroOp[] bundle = new MicroOp[8];
            bundle[0] = MicroOpTestHelper.CreateScalarALU(2, destReg: 1, src1Reg: 2, src2Reg: 3);

            MicroOp[] packed = scheduler.PackBundle(
                bundle,
                currentThreadId: 2,
                stealEnabled: true,
                stealMask: 0xFF,
                localCoreId: 0,
                bundleOwnerContextId: 9,
                bundleOwnerDomainTag: 1,
                assistRuntimeEpoch: 5);

            AssistMicroOp injected = Assert.IsType<AssistMicroOp>(packed[6]);
            Assert.Equal(AssistKind.DonorPrefetch, injected.Kind);
            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, injected.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, injected.CarrierKind);
            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtSeed, injected.DonorSource.Kind);
            Assert.Equal(1, injected.DonorSource.SourceCoreId);
            Assert.Equal(0, injected.CarrierCoreId);
            Assert.Equal(0, injected.TargetCoreId);
            Assert.Equal(5UL, injected.AssistEpochId);
            Assert.Equal(0, scheduler.SuccessfulInjectionsCount);
            Assert.Equal(1, scheduler.AssistInterCoreNominations);
            Assert.Equal(1, scheduler.AssistInterCoreInjections);
            Assert.Equal(0, scheduler.AssistInterCoreRejects);
            Assert.Equal(1, scheduler.AssistInjectionsCount);
            Assert.Equal(1, scheduler.AssistDonorPrefetchInjects);
            Assert.Equal(1, scheduler.AssistInterCoreLane6DonorPrefetchInjects);
            Assert.Equal(1, scheduler.AssistSameVtInjects);
            Assert.Equal(0, scheduler.AssistDonorVtInjects);
            Assert.Equal(1, scheduler.DmaStreamClassInjects);

            SchedulerPhaseMetrics metrics = scheduler.GetPhaseMetrics();
            Assert.Equal(1, metrics.AssistInterCoreNominations);
            Assert.Equal(1, metrics.AssistInterCoreInjections);
            Assert.Equal(0, metrics.AssistInterCoreRejects);
            Assert.Equal(1, metrics.AssistDonorPrefetchInjects);
            Assert.Equal(1, metrics.AssistInterCoreLane6DonorPrefetchInjects);
            Assert.Equal(1, metrics.AssistSameVtInjects);
            Assert.Equal(0, metrics.AssistDonorVtInjects);
            Assert.Equal(1, metrics.DmaStreamClassInjects);
        }

        [Fact]
        public void PackBundle_WithInterCoreCrossVtDonorPrefetchAssistNomination_InjectsAssistIntoLane6ViaDedicatedAssistTransport()
        {
            Array.Fill(Processor.Pods, null!);

            var scheduler = new MicroOpScheduler
            {
                TypedSlotEnabled = true
            };
            var pod = new PodController(0, 0, scheduler);
            Processor.Pods[0] = pod;

            LoadMicroOp load = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 2,
                destReg: 5,
                address: 0xC600,
                domainTag: 1);
            load.OwnerContextId = 3;
            load.InitializeMetadata();

            pod.PublishAssistOwnerSnapshot(
                localCoreId: 1,
                virtualThreadId: 2,
                ownerContextId: 3,
                domainTag: 1,
                assistEpochId: 0);

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                load,
                donorCoreId: 1,
                donorPodId: 0,
                out AssistInterCoreTransport transport));

            scheduler.NominateInterCoreAssistCandidate(1, transport);

            MicroOp[] bundle = new MicroOp[8];
            bundle[0] = MicroOpTestHelper.CreateScalarALU(1, destReg: 1, src1Reg: 2, src2Reg: 3);

            MicroOp[] packed = scheduler.PackBundle(
                bundle,
                currentThreadId: 1,
                stealEnabled: true,
                stealMask: 0xFF,
                localCoreId: 0,
                bundleOwnerContextId: 9,
                bundleOwnerDomainTag: 1,
                assistRuntimeEpoch: 5);

            AssistMicroOp injected = Assert.IsType<AssistMicroOp>(packed[6]);
            Assert.Equal(AssistKind.DonorPrefetch, injected.Kind);
            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, injected.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, injected.CarrierKind);
            Assert.Equal(AssistDonorSourceKind.InterCoreVtDonorSeed, injected.DonorSource.Kind);
            Assert.Equal(2, injected.DonorVirtualThreadId);
            Assert.Equal(1, injected.TargetVirtualThreadId);
            Assert.True(injected.DonorSource.IsCrossVirtualThread);
            Assert.Equal(1, injected.DonorSource.SourceCoreId);
            Assert.Equal(0, injected.CarrierCoreId);
            Assert.Equal(0, injected.TargetCoreId);
            Assert.Equal(5UL, injected.AssistEpochId);
            Assert.Equal(0, scheduler.SuccessfulInjectionsCount);
            Assert.Equal(1, scheduler.AssistInterCoreNominations);
            Assert.Equal(1, scheduler.AssistInterCoreInjections);
            Assert.Equal(1, scheduler.AssistInterCorePodLocalInjections);
            Assert.Equal(0, scheduler.AssistInterCoreCrossPodInjections);
            Assert.Equal(0, scheduler.AssistInterCoreRejects);
            Assert.Equal(0, scheduler.AssistInterCorePodLocalRejects);
            Assert.Equal(0, scheduler.AssistInterCoreCrossPodRejects);
            Assert.Equal(1, scheduler.AssistInjectionsCount);
            Assert.Equal(1, scheduler.AssistDonorPrefetchInjects);
            Assert.Equal(1, scheduler.AssistInterCoreLane6DonorPrefetchInjects);
            Assert.Equal(0, scheduler.AssistSameVtInjects);
            Assert.Equal(1, scheduler.AssistDonorVtInjects);
            Assert.Equal(1, scheduler.DmaStreamClassInjects);

            SchedulerPhaseMetrics metrics = scheduler.GetPhaseMetrics();
            Assert.Equal(1, metrics.AssistInterCoreNominations);
            Assert.Equal(1, metrics.AssistInterCoreInjections);
            Assert.Equal(1, metrics.AssistInterCorePodLocalInjections);
            Assert.Equal(0, metrics.AssistInterCoreCrossPodInjections);
            Assert.Equal(0, metrics.AssistInterCoreRejects);
            Assert.Equal(0, metrics.AssistInterCorePodLocalRejects);
            Assert.Equal(0, metrics.AssistInterCoreCrossPodRejects);
            Assert.Equal(1, metrics.AssistDonorPrefetchInjects);
            Assert.Equal(1, metrics.AssistInterCoreLane6DonorPrefetchInjects);
            Assert.Equal(0, metrics.AssistSameVtInjects);
            Assert.Equal(1, metrics.AssistDonorVtInjects);
            Assert.Equal(1, metrics.DmaStreamClassInjects);
        }

        [Fact]
        public void PackBundle_WithInterCoreLane6HotLoadDonorPrefetchAssistNomination_InjectsAssistIntoLane6ViaDedicatedAssistTransport()
        {
            Array.Fill(Processor.Pods, null!);

            var scheduler = new MicroOpScheduler
            {
                TypedSlotEnabled = true
            };
            var pod = new PodController(0, 0, scheduler);
            Processor.Pods[0] = pod;

            LoadMicroOp load = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 2,
                destReg: 5,
                address: 0xC980,
                domainTag: 1);
            load.OwnerContextId = 3;
            load.MemoryLocalityHint = LocalityHint.Hot;
            load.InitializeMetadata();

            pod.PublishAssistOwnerSnapshot(
                localCoreId: 1,
                virtualThreadId: 2,
                ownerContextId: 3,
                domainTag: 1,
                assistEpochId: 27);

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                load,
                donorCoreId: 1,
                donorPodId: 0,
                donorAssistEpochId: 27,
                out AssistInterCoreTransport transport));

            scheduler.NominateInterCoreAssistCandidate(1, transport);

            MicroOp[] bundle = new MicroOp[8];
            bundle[0] = MicroOpTestHelper.CreateScalarALU(2, destReg: 1, src1Reg: 2, src2Reg: 3);

            MicroOp[] packed = scheduler.PackBundle(
                bundle,
                currentThreadId: 2,
                stealEnabled: true,
                stealMask: 0xFF,
                localCoreId: 0,
                bundleOwnerContextId: 9,
                bundleOwnerDomainTag: 1,
                assistRuntimeEpoch: 5);

            MicroOp injectedOp = Array.Find(packed, op => op is AssistMicroOp);
            Assert.NotNull(injectedOp);
            AssistMicroOp injected = Assert.IsType<AssistMicroOp>(injectedOp);
            Assert.Equal(AssistKind.DonorPrefetch, injected.Kind);
            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, injected.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, injected.CarrierKind);
            Assert.Equal(SlotClass.DmaStreamClass, injected.CarrierSlotClass);
            Assert.Equal(LocalityHint.Hot, injected.LocalityHint);
            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtHotLoadSeed, injected.DonorSource.Kind);
            Assert.Equal(1, injected.DonorSource.SourceCoreId);
            Assert.Equal(0, injected.CarrierCoreId);
            Assert.Equal(0, injected.TargetCoreId);
            Assert.Equal(5UL, injected.AssistEpochId);
            Assert.Equal(0, scheduler.SuccessfulInjectionsCount);
            Assert.Equal(1, scheduler.AssistInterCoreNominations);
            Assert.Equal(1, scheduler.AssistInterCoreInjections);
            Assert.Equal(0, scheduler.AssistInterCoreRejects);
            Assert.Equal(1, scheduler.AssistInjectionsCount);
            Assert.Equal(1, scheduler.AssistDonorPrefetchInjects);
            Assert.Equal(1, scheduler.AssistInterCoreLane6HotLoadDonorPrefetchInjects);
            Assert.Equal(0, scheduler.AssistInterCoreLane6DonorPrefetchInjects);
            Assert.Equal(1, scheduler.AssistSameVtInjects);
            Assert.Equal(0, scheduler.AssistDonorVtInjects);
            Assert.Equal(0, scheduler.LsuClassInjects);
            Assert.Equal(1, scheduler.DmaStreamClassInjects);

            SchedulerPhaseMetrics metrics = scheduler.GetPhaseMetrics();
            Assert.Equal(1, metrics.AssistInterCoreNominations);
            Assert.Equal(1, metrics.AssistInterCoreInjections);
            Assert.Equal(0, metrics.AssistInterCoreRejects);
            Assert.Equal(1, metrics.AssistDonorPrefetchInjects);
            Assert.Equal(1, metrics.AssistInterCoreLane6HotLoadDonorPrefetchInjects);
            Assert.Equal(0, metrics.AssistInterCoreLane6DonorPrefetchInjects);
            Assert.Equal(1, metrics.AssistSameVtInjects);
            Assert.Equal(0, metrics.AssistDonorVtInjects);
            Assert.Equal(0, metrics.LsuClassInjects);
            Assert.Equal(1, metrics.DmaStreamClassInjects);
        }

        [Fact]
        public void PackBundle_WithInterCoreLane6DefaultStoreDonorPrefetchAssistNomination_InjectsAssistIntoLane6ViaDedicatedAssistTransport()
        {
            Array.Fill(Processor.Pods, null!);

            var scheduler = new MicroOpScheduler
            {
                TypedSlotEnabled = true
            };
            var pod = new PodController(0, 0, scheduler);
            Processor.Pods[0] = pod;

            StoreMicroOp store = MicroOpTestHelper.CreateStore(
                virtualThreadId: 2,
                srcReg: 5,
                address: 0xCA00,
                domainTag: 1);
            store.OwnerContextId = 3;
            store.InitializeMetadata();

            pod.PublishAssistOwnerSnapshot(
                localCoreId: 1,
                virtualThreadId: 2,
                ownerContextId: 3,
                domainTag: 1,
                assistEpochId: 0);

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                store,
                donorCoreId: 1,
                donorPodId: 0,
                out AssistInterCoreTransport transport));

            scheduler.NominateInterCoreAssistCandidate(1, transport);

            MicroOp[] bundle = new MicroOp[8];
            bundle[0] = MicroOpTestHelper.CreateScalarALU(2, destReg: 1, src1Reg: 2, src2Reg: 3);

            MicroOp[] packed = scheduler.PackBundle(
                bundle,
                currentThreadId: 2,
                stealEnabled: true,
                stealMask: 0xFF,
                localCoreId: 0,
                bundleOwnerContextId: 9,
                bundleOwnerDomainTag: 1,
                assistRuntimeEpoch: 5);

            MicroOp injectedOp = Array.Find(packed, op => op is AssistMicroOp);
            Assert.NotNull(injectedOp);
            AssistMicroOp injected = Assert.IsType<AssistMicroOp>(injectedOp);
            Assert.Equal(AssistKind.DonorPrefetch, injected.Kind);
            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, injected.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, injected.CarrierKind);
            Assert.Equal(SlotClass.DmaStreamClass, injected.CarrierSlotClass);
            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtDefaultStoreSeed, injected.DonorSource.Kind);
            Assert.Equal(1, injected.DonorSource.SourceCoreId);
            Assert.Equal(0, injected.CarrierCoreId);
            Assert.Equal(0, injected.TargetCoreId);
            Assert.Equal(5UL, injected.AssistEpochId);
            Assert.Equal(0, scheduler.SuccessfulInjectionsCount);
            Assert.Equal(1, scheduler.AssistInterCoreNominations);
            Assert.Equal(1, scheduler.AssistInterCoreInjections);
            Assert.Equal(0, scheduler.AssistInterCoreRejects);
            Assert.Equal(1, scheduler.AssistInjectionsCount);
            Assert.Equal(1, scheduler.AssistDonorPrefetchInjects);
            Assert.Equal(1, scheduler.AssistInterCoreLane6DefaultStoreDonorPrefetchInjects);
            Assert.Equal(1, scheduler.AssistSameVtInjects);
            Assert.Equal(0, scheduler.AssistDonorVtInjects);
            Assert.Equal(0, scheduler.LsuClassInjects);
            Assert.Equal(1, scheduler.DmaStreamClassInjects);

            SchedulerPhaseMetrics metrics = scheduler.GetPhaseMetrics();
            Assert.Equal(1, metrics.AssistInterCoreNominations);
            Assert.Equal(1, metrics.AssistInterCoreInjections);
            Assert.Equal(0, metrics.AssistInterCoreRejects);
            Assert.Equal(1, metrics.AssistDonorPrefetchInjects);
            Assert.Equal(1, metrics.AssistInterCoreLane6DefaultStoreDonorPrefetchInjects);
            Assert.Equal(1, metrics.AssistSameVtInjects);
            Assert.Equal(0, metrics.AssistDonorVtInjects);
            Assert.Equal(0, metrics.LsuClassInjects);
            Assert.Equal(1, metrics.DmaStreamClassInjects);
        }

        [Fact]
        public void PackBundle_WithInterCoreLane6HotStoreDonorPrefetchAssistNomination_InjectsAssistIntoLane6ViaDedicatedAssistTransport()
        {
            Array.Fill(Processor.Pods, null!);

            var scheduler = new MicroOpScheduler
            {
                TypedSlotEnabled = true
            };
            var pod = new PodController(0, 0, scheduler);
            Processor.Pods[0] = pod;

            StoreMicroOp store = MicroOpTestHelper.CreateStore(
                virtualThreadId: 2,
                srcReg: 5,
                address: 0xCA80,
                domainTag: 1);
            store.OwnerContextId = 3;
            store.MemoryLocalityHint = LocalityHint.Hot;
            store.InitializeMetadata();

            pod.PublishAssistOwnerSnapshot(
                localCoreId: 1,
                virtualThreadId: 2,
                ownerContextId: 3,
                domainTag: 1,
                assistEpochId: 26);

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                store,
                donorCoreId: 1,
                donorPodId: 0,
                donorAssistEpochId: 26,
                out AssistInterCoreTransport transport));

            scheduler.NominateInterCoreAssistCandidate(1, transport);

            MicroOp[] bundle = new MicroOp[8];
            bundle[0] = MicroOpTestHelper.CreateScalarALU(2, destReg: 1, src1Reg: 2, src2Reg: 3);

            MicroOp[] packed = scheduler.PackBundle(
                bundle,
                currentThreadId: 2,
                stealEnabled: true,
                stealMask: 0xFF,
                localCoreId: 0,
                bundleOwnerContextId: 9,
                bundleOwnerDomainTag: 1,
                assistRuntimeEpoch: 5);

            MicroOp injectedOp = Array.Find(packed, op => op is AssistMicroOp);
            Assert.NotNull(injectedOp);
            AssistMicroOp injected = Assert.IsType<AssistMicroOp>(injectedOp);
            Assert.Equal(AssistKind.DonorPrefetch, injected.Kind);
            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, injected.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, injected.CarrierKind);
            Assert.Equal(SlotClass.DmaStreamClass, injected.CarrierSlotClass);
            Assert.Equal(LocalityHint.Hot, injected.LocalityHint);
            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtHotStoreSeed, injected.DonorSource.Kind);
            Assert.Equal(1, injected.DonorSource.SourceCoreId);
            Assert.Equal(0, injected.CarrierCoreId);
            Assert.Equal(0, injected.TargetCoreId);
            Assert.Equal(5UL, injected.AssistEpochId);
            Assert.Equal(0, scheduler.SuccessfulInjectionsCount);
            Assert.Equal(1, scheduler.AssistInterCoreNominations);
            Assert.Equal(1, scheduler.AssistInterCoreInjections);
            Assert.Equal(0, scheduler.AssistInterCoreRejects);
            Assert.Equal(1, scheduler.AssistInjectionsCount);
            Assert.Equal(1, scheduler.AssistDonorPrefetchInjects);
            Assert.Equal(1, scheduler.AssistInterCoreLane6HotStoreDonorPrefetchInjects);
            Assert.Equal(0, scheduler.AssistInterCoreLane6DonorPrefetchInjects);
            Assert.Equal(1, scheduler.AssistSameVtInjects);
            Assert.Equal(0, scheduler.AssistDonorVtInjects);
            Assert.Equal(0, scheduler.LsuClassInjects);
            Assert.Equal(1, scheduler.DmaStreamClassInjects);

            SchedulerPhaseMetrics metrics = scheduler.GetPhaseMetrics();
            Assert.Equal(1, metrics.AssistInterCoreNominations);
            Assert.Equal(1, metrics.AssistInterCoreInjections);
            Assert.Equal(0, metrics.AssistInterCoreRejects);
            Assert.Equal(1, metrics.AssistDonorPrefetchInjects);
            Assert.Equal(1, metrics.AssistInterCoreLane6HotStoreDonorPrefetchInjects);
            Assert.Equal(0, metrics.AssistInterCoreLane6DonorPrefetchInjects);
            Assert.Equal(1, metrics.AssistSameVtInjects);
            Assert.Equal(0, metrics.AssistDonorVtInjects);
            Assert.Equal(0, metrics.LsuClassInjects);
            Assert.Equal(1, metrics.DmaStreamClassInjects);
        }

        [Fact]
        public void PackBundle_WithInterCoreLane6ColdStoreLdsaAssistNomination_InjectsAssistIntoLane6ViaDedicatedAssistTransport()
        {
            Array.Fill(Processor.Pods, null!);

            var scheduler = new MicroOpScheduler
            {
                TypedSlotEnabled = true
            };
            var pod = new PodController(0, 0, scheduler);
            Processor.Pods[0] = pod;

            StoreMicroOp store = MicroOpTestHelper.CreateStore(
                virtualThreadId: 2,
                srcReg: 5,
                address: 0xCB00,
                domainTag: 1);
            store.OwnerContextId = 3;
            store.MemoryLocalityHint = LocalityHint.Cold;
            store.InitializeMetadata();

            pod.PublishAssistOwnerSnapshot(
                localCoreId: 1,
                virtualThreadId: 2,
                ownerContextId: 3,
                domainTag: 1,
                assistEpochId: 0);

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                store,
                donorCoreId: 1,
                donorPodId: 0,
                out AssistInterCoreTransport transport));

            scheduler.NominateInterCoreAssistCandidate(1, transport);

            MicroOp[] bundle = new MicroOp[8];
            bundle[0] = MicroOpTestHelper.CreateScalarALU(2, destReg: 1, src1Reg: 2, src2Reg: 3);

            MicroOp[] packed = scheduler.PackBundle(
                bundle,
                currentThreadId: 2,
                stealEnabled: true,
                stealMask: 0xFF,
                localCoreId: 0,
                bundleOwnerContextId: 9,
                bundleOwnerDomainTag: 1,
                assistRuntimeEpoch: 5);

            MicroOp injectedOp = Array.Find(packed, op => op is AssistMicroOp);
            Assert.NotNull(injectedOp);
            AssistMicroOp injected = Assert.IsType<AssistMicroOp>(injectedOp);
            Assert.Equal(AssistKind.Ldsa, injected.Kind);
            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, injected.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, injected.CarrierKind);
            Assert.Equal(SlotClass.DmaStreamClass, injected.CarrierSlotClass);
            Assert.Equal(LocalityHint.Cold, injected.LocalityHint);
            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtColdStoreSeed, injected.DonorSource.Kind);
            Assert.Equal(1, injected.DonorSource.SourceCoreId);
            Assert.Equal(0, injected.CarrierCoreId);
            Assert.Equal(0, injected.TargetCoreId);
            Assert.Equal(5UL, injected.AssistEpochId);
            Assert.Equal(0, scheduler.SuccessfulInjectionsCount);
            Assert.Equal(1, scheduler.AssistInterCoreNominations);
            Assert.Equal(1, scheduler.AssistInterCoreInjections);
            Assert.Equal(0, scheduler.AssistInterCoreRejects);
            Assert.Equal(1, scheduler.AssistInjectionsCount);
            Assert.Equal(1, scheduler.AssistLdsaInjects);
            Assert.Equal(1, scheduler.AssistInterCoreLane6ColdStoreLdsaInjects);
            Assert.Equal(1, scheduler.AssistSameVtInjects);
            Assert.Equal(0, scheduler.AssistDonorVtInjects);
            Assert.Equal(0, scheduler.LsuClassInjects);
            Assert.Equal(1, scheduler.DmaStreamClassInjects);

            SchedulerPhaseMetrics metrics = scheduler.GetPhaseMetrics();
            Assert.Equal(1, metrics.AssistInterCoreNominations);
            Assert.Equal(1, metrics.AssistInterCoreInjections);
            Assert.Equal(0, metrics.AssistInterCoreRejects);
            Assert.Equal(1, metrics.AssistLdsaInjects);
            Assert.Equal(1, metrics.AssistInterCoreLane6ColdStoreLdsaInjects);
            Assert.Equal(1, metrics.AssistSameVtInjects);
            Assert.Equal(0, metrics.AssistDonorVtInjects);
            Assert.Equal(0, metrics.LsuClassInjects);
            Assert.Equal(1, metrics.DmaStreamClassInjects);
        }

        [Fact]
        public void PackBundle_WithInterCoreLdsaAssistNomination_InjectsAssistIntoLane6ViaDedicatedAssistTransport()
        {
            Array.Fill(Processor.Pods, null!);

            var scheduler = new MicroOpScheduler
            {
                TypedSlotEnabled = true
            };
            var pod = new PodController(0, 0, scheduler);
            Processor.Pods[0] = pod;

            LoadMicroOp load = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 2,
                destReg: 5,
                address: 0xC800,
                domainTag: 1);
            load.OwnerContextId = 3;
            load.MemoryLocalityHint = LocalityHint.Cold;
            load.InitializeMetadata();

            pod.PublishAssistOwnerSnapshot(
                localCoreId: 1,
                virtualThreadId: 2,
                ownerContextId: 3,
                domainTag: 1,
                assistEpochId: 0);

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                load,
                donorCoreId: 1,
                donorPodId: 0,
                out AssistInterCoreTransport transport));

            scheduler.NominateInterCoreAssistCandidate(1, transport);

            MicroOp[] bundle = new MicroOp[8];
            bundle[0] = MicroOpTestHelper.CreateScalarALU(2, destReg: 1, src1Reg: 2, src2Reg: 3);

            MicroOp[] packed = scheduler.PackBundle(
                bundle,
                currentThreadId: 2,
                stealEnabled: true,
                stealMask: 0xFF,
                localCoreId: 0,
                bundleOwnerContextId: 9,
                bundleOwnerDomainTag: 1,
                assistRuntimeEpoch: 5);

            AssistMicroOp injectedAssist = Assert.IsType<AssistMicroOp>(packed[6]);
            Assert.Equal(AssistKind.Ldsa, injectedAssist.Kind);
            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, injectedAssist.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, injectedAssist.CarrierKind);
            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtSeed, injectedAssist.DonorSource.Kind);
            Assert.Equal(1, injectedAssist.DonorSource.SourceCoreId);
            Assert.Equal(0, injectedAssist.CarrierCoreId);
            Assert.Equal(0, injectedAssist.TargetCoreId);
            Assert.Equal(5UL, injectedAssist.AssistEpochId);
            Assert.Equal(1, scheduler.AssistInterCoreNominations);
            Assert.Equal(1, scheduler.AssistInterCoreInjections);
            Assert.Equal(0, scheduler.AssistInterCoreRejects);
            Assert.Equal(1, scheduler.AssistInjectionsCount);
            Assert.Equal(1, scheduler.AssistLdsaInjects);
            Assert.Equal(1, scheduler.AssistSameVtInjects);
            Assert.Equal(0, scheduler.AssistDonorVtInjects);
            Assert.Equal(1, scheduler.AssistInterCoreLane6LdsaInjects);
            Assert.Equal(1, scheduler.DmaStreamClassInjects);

            SchedulerPhaseMetrics metrics = scheduler.GetPhaseMetrics();
            Assert.Equal(1, metrics.AssistInterCoreNominations);
            Assert.Equal(1, metrics.AssistInterCoreInjections);
            Assert.Equal(0, metrics.AssistInterCoreRejects);
            Assert.Equal(1, metrics.AssistLdsaInjects);
            Assert.Equal(1, metrics.AssistSameVtInjects);
            Assert.Equal(0, metrics.AssistDonorVtInjects);
            Assert.Equal(1, metrics.AssistInterCoreLane6LdsaInjects);
            Assert.Equal(1, metrics.DmaStreamClassInjects);
        }

    }
}
