using System;
using System.IO;
using HybridCPU_ISE.Core;
using HybridCPU_ISE.Tests.TestHelpers;
using Xunit;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Memory;
using YAKSys_Hybrid_CPU.Core.Registers;
using YAKSys_Hybrid_CPU.Memory;

namespace HybridCPU_ISE.Tests
{
    public partial class AssistRuntimeTests
    {
        [Fact]
        public void AssistFactory_FromLoadSeed_ProducesNonRetiringDonorPrefetch()
        {
            LoadMicroOp load = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 1,
                destReg: 5,
                address: 0x2000,
                domainTag: 3);

            bool created = AssistMicroOp.TryCreateFromSeed(
                load,
                carrierVirtualThreadId: 0,
                replayEpochId: 11,
                assistEpochId: 7,
                out AssistMicroOp assist);

            Assert.True(created);
            Assert.NotNull(assist);
            Assert.Equal(AssistKind.DonorPrefetch, assist.Kind);
            Assert.Equal(AssistExecutionMode.CachePrefetch, assist.ExecutionMode);
            Assert.Equal(AssistCarrierKind.LsuHosted, assist.CarrierKind);
            Assert.True(assist.IsAssist);
            Assert.False(assist.IsRetireVisible);
            Assert.True(assist.IsReplayDiscardable);
            Assert.Equal(SlotClass.LsuClass, assist.Placement.RequiredSlotClass);
            Assert.Equal(11UL, assist.ReplayEpochId);
            Assert.Equal(7UL, assist.AssistEpochId);
            Assert.Equal(1, assist.TargetVirtualThreadId);
            Assert.Equal(0, assist.CarrierVirtualThreadId);
            Assert.Single(assist.ReadMemoryRanges);
            Assert.Equal((ulong)0x2000, assist.ReadMemoryRanges[0].Address);
        }

        [Fact]
        public void AssistFactory_FromLoadSeed_UsesRuntimeMemoryGeometryForBankId()
        {
            MemorySubsystem? savedMemory = Processor.Memory;
            try
            {
                Processor proc = default;
                Processor.Memory = new MemorySubsystem(ref proc)
                {
                    NumBanks = 16,
                    BankWidthBytes = 128
                };

                LoadMicroOp load = MicroOpTestHelper.CreateLoad(
                    virtualThreadId: 1,
                    destReg: 5,
                    address: (128UL * 5UL) + 17UL,
                    domainTag: 3);

                bool created = AssistMicroOp.TryCreateFromSeed(
                    load,
                    carrierVirtualThreadId: 0,
                    replayEpochId: 11,
                    assistEpochId: 7,
                    out AssistMicroOp assist);

                Assert.True(created);
                Assert.Equal(5, load.MemoryBankId);
                Assert.Equal(load.MemoryBankId, assist.MemoryBankId);
            }
            finally
            {
                Processor.Memory = savedMemory;
            }
        }

        [Fact]
        public void AssistFactory_WhenRuntimeMemoryGeometryIsUnavailable_UsesExplicitUninitializedBankContour()
        {
            ProcessorMemoryScope.WithProcessorMemory(memory: null, () =>
            {
                LoadMicroOp load = MicroOpTestHelper.CreateLoad(
                    virtualThreadId: 1,
                    destReg: 5,
                    address: 0x2000,
                    domainTag: 3);

                bool created = AssistMicroOp.TryCreateFromSeed(
                    load,
                    carrierVirtualThreadId: 0,
                    replayEpochId: 11,
                    assistEpochId: 7,
                    out AssistMicroOp assist);

                Assert.True(created);
                Assert.Equal(MemoryBankRouting.UninitializedSchedulerVisibleBankId, load.MemoryBankId);
                Assert.Equal(load.MemoryBankId, assist.MemoryBankId);
                Assert.False(assist.HasResolvedMemoryBankId);
                Assert.Equal(0UL, assist.ResourceMask.High);
                Assert.Equal(0UL, assist.SafetyMask.High);
            });
        }

        [Fact]
        public void AssistFactory_FromVectorSeed_ProducesVdsaClassification()
        {
            VectorBinaryOpMicroOp vector = CreateVectorSeed();

            bool created = AssistMicroOp.TryCreateFromSeed(
                vector,
                carrierVirtualThreadId: 0,
                replayEpochId: 5,
                assistEpochId: 2,
                out AssistMicroOp assist);

            Assert.True(created);
            Assert.NotNull(assist);
            Assert.Equal(AssistKind.Vdsa, assist.Kind);
            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, assist.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, assist.CarrierKind);
            Assert.Equal(SlotClass.DmaStreamClass, assist.CarrierSlotClass);
            Assert.Equal(MicroOpClass.Dma, assist.Class);
            Assert.Equal((byte)4, assist.ElementSize);
            Assert.Equal((uint)64, assist.ElementCount);
            Assert.Equal(AssistDonorSourceKind.SameThreadSeed, assist.DonorSource.Kind);
            Assert.False(assist.DonorSource.IsCrossVirtualThread);
        }

        [Fact]
        public void AssistFactory_FromNearContiguousVectorSeed_UsesCoalescedIngressWindow()
        {
            VectorBinaryOpMicroOp vector = CreateVectorSeed(
                baseAddress: 0x3400,
                src2Address: 0x3428,
                streamLength: 4);

            bool created = AssistMicroOp.TryCreateFromSeed(
                vector,
                carrierVirtualThreadId: 0,
                replayEpochId: 5,
                assistEpochId: 2,
                out AssistMicroOp assist);

            Assert.True(created);
            Assert.True(vector.AdmissionMetadata.AssistCoalescingDescriptor.IsValid);
            Assert.Equal(0x3400UL, assist.BaseAddress);
            Assert.Equal(0x38UL, assist.PrefetchLength);
            Assert.Equal((uint)4, assist.ElementCount);
        }

        [Fact]
        public void CoreAssistCandidate_FromNearContiguousVectorSeed_ReachesProductionCandidateGate()
        {
            var core = new Processor.CPU_Core(0);
            core.PrepareExecutionStart(0x1000);

            VectorBinaryOpMicroOp vector = CreateVectorSeed(
                baseAddress: 0x3500,
                src2Address: 0x3528,
                streamLength: 4,
                domainTag: 1);

            bool created = core.TestTryCreateAssistCandidate(
                vector,
                carrierVirtualThreadId: 0,
                out AssistMicroOp assist);

            Assert.True(created);
            Assert.Equal(AssistKind.Vdsa, assist.Kind);
            Assert.Equal(0x3500UL, assist.BaseAddress);
            Assert.Equal(0x38UL, assist.PrefetchLength);
            Assert.Equal(1UL, assist.Placement.DomainTag);
        }

        [Fact]
        public void AssistFactory_FromVectorSeed_WithExplicitTargetVt_ProducesCrossVtDonorBinding()
        {
            VectorBinaryOpMicroOp vector = CreateVectorSeed(virtualThreadId: 2);

            bool created = AssistMicroOp.TryCreateFromSeed(
                vector,
                carrierVirtualThreadId: 0,
                targetVirtualThreadId: 1,
                replayEpochId: 5,
                assistEpochId: 2,
                assistMicroOp: out AssistMicroOp assist);

            Assert.True(created);
            Assert.NotNull(assist);
            Assert.Equal(2, assist.DonorVirtualThreadId);
            Assert.Equal(1, assist.TargetVirtualThreadId);
            Assert.Equal(AssistDonorSourceKind.IntraCoreVtDonorVector, assist.DonorSource.Kind);
            Assert.True(assist.DonorSource.IsCrossVirtualThread);
        }

        [Fact]
        public void AssistFactory_FromVectorSeed_WithInterCoreTransport_ProducesExplicitCoreOwnership()
        {
            VectorBinaryOpMicroOp vector = CreateVectorSeed(
                virtualThreadId: 2,
                ownerContextId: 3,
                baseAddress: 0x3200,
                src2Address: 0x3A00,
                domainTag: 1);

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                vector,
                donorCoreId: 1,
                donorPodId: 0x120,
                donorAssistEpochId: 17,
                out AssistInterCoreTransport transport));

            bool created = AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: 0x120,
                carrierCoreId: 0,
                carrierVirtualThreadId: 0,
                targetCoreId: 0,
                targetVirtualThreadId: 1,
                targetOwnerContextId: 11,
                targetDomainTag: 1,
                replayEpochId: 5,
                assistEpochId: 2,
                out AssistMicroOp assist);

            Assert.True(created);
            Assert.NotNull(assist);
            Assert.Equal(AssistKind.Vdsa, assist.Kind);
            Assert.Equal(AssistDonorSourceKind.InterCoreVtDonorVector, assist.DonorSource.Kind);
            Assert.Equal(1, assist.DonorSource.SourceCoreId);
            Assert.Equal((ushort)0x120, assist.DonorSource.SourcePodId);
            Assert.Equal(17UL, transport.DonorAssistEpochId);
            Assert.Equal(17UL, assist.DonorSource.SourceAssistEpochId);
            Assert.Equal(0, assist.CarrierCoreId);
            Assert.Equal(0, assist.TargetCoreId);
            Assert.Equal((ushort)0x120, assist.PodId);
            Assert.Equal(2, assist.DonorVirtualThreadId);
            Assert.Equal(1, assist.TargetVirtualThreadId);
            Assert.Equal(11, assist.OwnerContextId);
            Assert.Equal(1UL, assist.Placement.DomainTag);
        }

        [Fact]
        public void AssistFactory_FromVectorSeed_WithInterCoreTransportAndSameTargetVt_ProducesExplicitSameVtCoreSourceOnDmaCarrier()
        {
            VectorBinaryOpMicroOp vector = CreateVectorSeed(
                virtualThreadId: 2,
                ownerContextId: 3,
                baseAddress: 0x3400,
                src2Address: 0x3C00,
                domainTag: 1);

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                vector,
                donorCoreId: 1,
                donorPodId: 0x120,
                donorAssistEpochId: 18,
                out AssistInterCoreTransport transport));

            bool created = AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: 0x120,
                carrierCoreId: 0,
                carrierVirtualThreadId: 0,
                targetCoreId: 0,
                targetVirtualThreadId: 2,
                targetOwnerContextId: 11,
                targetDomainTag: 1,
                replayEpochId: 5,
                assistEpochId: 2,
                out AssistMicroOp assist);

            Assert.True(created);
            Assert.NotNull(assist);
            Assert.Equal(AssistKind.Vdsa, assist.Kind);
            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtVector, assist.DonorSource.Kind);
            Assert.Equal(1, assist.DonorSource.SourceCoreId);
            Assert.Equal((ushort)0x120, assist.DonorSource.SourcePodId);
            Assert.Equal(18UL, transport.DonorAssistEpochId);
            Assert.Equal(18UL, assist.DonorSource.SourceAssistEpochId);
            Assert.Equal(2, assist.DonorVirtualThreadId);
            Assert.Equal(2, assist.TargetVirtualThreadId);
            Assert.False(assist.DonorSource.IsCrossVirtualThread);
        }

        [Fact]
        public void AssistFactory_FromVectorStoreSeed_WithoutInterCoreTransport_RemainsOutsideDirectCurrentContour()
        {
            StoreSegmentMicroOp vectorStore = CreateVectorStoreSeed(
                virtualThreadId: 2,
                ownerContextId: 3,
                baseAddress: 0x3500,
                domainTag: 1);

            bool created = AssistMicroOp.TryCreateFromSeed(
                vectorStore,
                carrierVirtualThreadId: 0,
                replayEpochId: 5,
                assistEpochId: 2,
                out _);

            Assert.False(created);
        }

        [Fact]
        public void AssistFactory_FromVectorStoreSeed_WithInterCoreTransport_ProducesWritebackSameVtVdsaClassification()
        {
            StoreSegmentMicroOp vectorStore = CreateVectorStoreSeed(
                virtualThreadId: 2,
                ownerContextId: 3,
                baseAddress: 0x3600,
                domainTag: 1);

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                vectorStore,
                donorCoreId: 1,
                donorPodId: 0x120,
                donorAssistEpochId: 19,
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
                replayEpochId: 5,
                assistEpochId: 2,
                out AssistMicroOp assist));

            Assert.Equal(AssistKind.Vdsa, assist.Kind);
            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, assist.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, assist.CarrierKind);
            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtVectorWriteback, assist.DonorSource.Kind);
            Assert.Equal((ulong)0x3600, assist.BaseAddress);
            Assert.Equal(1, assist.DonorSource.SourceCoreId);
            Assert.Equal((ushort)0x120, assist.DonorSource.SourcePodId);
            Assert.Equal(19UL, assist.DonorSource.SourceAssistEpochId);
            Assert.False(assist.DonorSource.IsCrossVirtualThread);
        }

        [Fact]
        public void AssistFactory_FromVectorStoreSeed_WithInterCoreTransportAndCrossVtTarget_ProducesWritebackDonorVtVdsaClassification()
        {
            StoreSegmentMicroOp vectorStore = CreateVectorStoreSeed(
                virtualThreadId: 2,
                ownerContextId: 3,
                baseAddress: 0x3700,
                domainTag: 1);

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                vectorStore,
                donorCoreId: 1,
                donorPodId: 0x120,
                donorAssistEpochId: 20,
                out AssistInterCoreTransport transport));

            Assert.True(AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: 0x120,
                carrierCoreId: 0,
                carrierVirtualThreadId: 0,
                targetCoreId: 0,
                targetVirtualThreadId: 1,
                targetOwnerContextId: 11,
                targetDomainTag: 1,
                replayEpochId: 5,
                assistEpochId: 2,
                out AssistMicroOp assist));

            Assert.Equal(AssistKind.Vdsa, assist.Kind);
            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, assist.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, assist.CarrierKind);
            Assert.Equal(AssistDonorSourceKind.InterCoreVtDonorVectorWriteback, assist.DonorSource.Kind);
            Assert.Equal(2, assist.DonorVirtualThreadId);
            Assert.Equal(1, assist.TargetVirtualThreadId);
            Assert.True(assist.DonorSource.IsCrossVirtualThread);
        }

        [Fact]
        public void AssistFactory_FromLoadSeed_WithInterCoreTransport_ProducesExplicitCoreSourceOnLane6Carrier()
        {
            LoadMicroOp load = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 2,
                destReg: 5,
                address: 0x3600,
                domainTag: 1);
            load.OwnerContextId = 3;
            load.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                load,
                donorCoreId: 1,
                donorPodId: 0x120,
                donorAssistEpochId: 19,
                out AssistInterCoreTransport transport));

            bool created = AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: 0x120,
                carrierCoreId: 0,
                carrierVirtualThreadId: 0,
                targetCoreId: 0,
                targetVirtualThreadId: 2,
                targetOwnerContextId: 11,
                targetDomainTag: 1,
                replayEpochId: 5,
                assistEpochId: 2,
                out AssistMicroOp assist);

            Assert.True(created);
            Assert.NotNull(assist);
            Assert.Equal(AssistKind.DonorPrefetch, assist.Kind);
            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, assist.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, assist.CarrierKind);
            Assert.Equal(SlotClass.DmaStreamClass, assist.CarrierSlotClass);
            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtSeed, assist.DonorSource.Kind);
            Assert.Equal(1, assist.DonorSource.SourceCoreId);
            Assert.Equal((ushort)0x120, assist.DonorSource.SourcePodId);
            Assert.Equal(19UL, transport.DonorAssistEpochId);
            Assert.Equal(19UL, assist.DonorSource.SourceAssistEpochId);
            Assert.Equal(0, assist.CarrierCoreId);
            Assert.Equal(0, assist.TargetCoreId);
            Assert.Equal((ushort)0x120, assist.PodId);
            Assert.Equal(2, assist.DonorVirtualThreadId);
            Assert.Equal(2, assist.TargetVirtualThreadId);
            Assert.Equal(11, assist.OwnerContextId);
            Assert.Equal(1UL, assist.Placement.DomainTag);
        }

        [Fact]
        public void AssistFactory_FromHotLoadSeed_WithInterCoreTransport_ProducesExplicitCoreSourceOnLane6Carrier()
        {
            LoadMicroOp load = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 2,
                destReg: 5,
                address: 0x3680,
                domainTag: 1);
            load.OwnerContextId = 3;
            load.MemoryLocalityHint = LocalityHint.Hot;
            load.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                load,
                donorCoreId: 1,
                donorPodId: 0x120,
                donorAssistEpochId: 21,
                out AssistInterCoreTransport transport));

            bool created = AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: 0x120,
                carrierCoreId: 0,
                carrierVirtualThreadId: 0,
                targetCoreId: 0,
                targetVirtualThreadId: 2,
                targetOwnerContextId: 11,
                targetDomainTag: 1,
                replayEpochId: 5,
                assistEpochId: 2,
                out AssistMicroOp assist);

            Assert.True(created);
            Assert.NotNull(assist);
            Assert.Equal(AssistKind.DonorPrefetch, assist.Kind);
            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, assist.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, assist.CarrierKind);
            Assert.Equal(SlotClass.DmaStreamClass, assist.CarrierSlotClass);
            Assert.Equal(LocalityHint.Hot, assist.LocalityHint);
            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtHotLoadSeed, assist.DonorSource.Kind);
            Assert.Equal(1, assist.DonorSource.SourceCoreId);
            Assert.Equal((ushort)0x120, assist.DonorSource.SourcePodId);
            Assert.Equal(21UL, transport.DonorAssistEpochId);
            Assert.Equal(21UL, assist.DonorSource.SourceAssistEpochId);
            Assert.Equal(2, assist.DonorVirtualThreadId);
            Assert.Equal(2, assist.TargetVirtualThreadId);
            Assert.False(assist.DonorSource.IsCrossVirtualThread);
        }

        [Fact]
        public void AssistFactory_FromColdLoadSeed_WithInterCoreTransport_ProducesLdsaClassification()
        {
            LoadMicroOp load = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 1,
                destReg: 7,
                address: 0x3A00,
                domainTag: 1);
            load.OwnerContextId = 5;
            load.MemoryLocalityHint = LocalityHint.Cold;
            load.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                load,
                donorCoreId: 2,
                donorPodId: 0x220,
                out AssistInterCoreTransport transport));

            Assert.True(AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: 0x220,
                carrierCoreId: 0,
                carrierVirtualThreadId: 0,
                targetCoreId: 0,
                targetVirtualThreadId: 1,
                targetOwnerContextId: 9,
                targetDomainTag: 1,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp assist));

            Assert.Equal(AssistKind.Ldsa, assist.Kind);
            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, assist.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, assist.CarrierKind);
            Assert.Equal(SlotClass.DmaStreamClass, assist.CarrierSlotClass);
            Assert.Equal(LocalityHint.Cold, assist.LocalityHint);
            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtSeed, assist.DonorSource.Kind);
            Assert.Equal(2, assist.DonorSource.SourceCoreId);
            Assert.Equal((ushort)0x220, assist.DonorSource.SourcePodId);
        }

        [Fact]
        public void AssistFactory_FromLoadSeed_WithInterCoreTransportAndCrossVtTarget_ProducesCrossVtExplicitCoreSourceOnLane6Carrier()
        {
            LoadMicroOp load = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 2,
                destReg: 5,
                address: 0x3C00,
                domainTag: 1);
            load.OwnerContextId = 3;
            load.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                load,
                donorCoreId: 1,
                donorPodId: 0x120,
                out AssistInterCoreTransport transport));

            bool created = AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: 0x120,
                carrierCoreId: 0,
                carrierVirtualThreadId: 1,
                targetCoreId: 0,
                targetVirtualThreadId: 1,
                targetOwnerContextId: 11,
                targetDomainTag: 1,
                replayEpochId: 5,
                assistEpochId: 2,
                out AssistMicroOp assist);

            Assert.True(created);
            Assert.NotNull(assist);
            Assert.Equal(AssistKind.DonorPrefetch, assist.Kind);
            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, assist.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, assist.CarrierKind);
            Assert.Equal(SlotClass.DmaStreamClass, assist.CarrierSlotClass);
            Assert.Equal(AssistDonorSourceKind.InterCoreVtDonorSeed, assist.DonorSource.Kind);
            Assert.Equal(1, assist.DonorSource.SourceCoreId);
            Assert.Equal((ushort)0x120, assist.DonorSource.SourcePodId);
            Assert.Equal(0, assist.CarrierCoreId);
            Assert.Equal(0, assist.TargetCoreId);
            Assert.Equal((ushort)0x120, assist.PodId);
            Assert.Equal(2, assist.DonorVirtualThreadId);
            Assert.Equal(1, assist.TargetVirtualThreadId);
            Assert.True(assist.DonorSource.IsCrossVirtualThread);
            Assert.Equal(11, assist.OwnerContextId);
            Assert.Equal(1UL, assist.Placement.DomainTag);
        }

        [Fact]
        public void AssistFactory_FromHotLoadSeed_WithInterCoreTransportAndCrossVtTarget_ProducesCrossVtExplicitCoreSourceOnLane6Carrier()
        {
            LoadMicroOp load = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 2,
                destReg: 5,
                address: 0x3D00,
                domainTag: 1);
            load.OwnerContextId = 3;
            load.MemoryLocalityHint = LocalityHint.Hot;
            load.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                load,
                donorCoreId: 1,
                donorPodId: 0x120,
                donorAssistEpochId: 22,
                out AssistInterCoreTransport transport));

            bool created = AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: 0x120,
                carrierCoreId: 0,
                carrierVirtualThreadId: 1,
                targetCoreId: 0,
                targetVirtualThreadId: 1,
                targetOwnerContextId: 11,
                targetDomainTag: 1,
                replayEpochId: 5,
                assistEpochId: 2,
                out AssistMicroOp assist);

            Assert.True(created);
            Assert.NotNull(assist);
            Assert.Equal(AssistKind.DonorPrefetch, assist.Kind);
            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, assist.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, assist.CarrierKind);
            Assert.Equal(SlotClass.DmaStreamClass, assist.CarrierSlotClass);
            Assert.Equal(LocalityHint.Hot, assist.LocalityHint);
            Assert.Equal(AssistDonorSourceKind.InterCoreVtDonorHotLoadSeed, assist.DonorSource.Kind);
            Assert.Equal(2, assist.DonorVirtualThreadId);
            Assert.Equal(1, assist.TargetVirtualThreadId);
            Assert.True(assist.DonorSource.IsCrossVirtualThread);
            Assert.Equal(1, assist.DonorSource.SourceCoreId);
            Assert.Equal((ushort)0x120, assist.DonorSource.SourcePodId);
            Assert.Equal(22UL, assist.DonorSource.SourceAssistEpochId);
        }

        [Fact]
        public void AssistFactory_FromColdLoadSeed_WithInterCoreTransportAndCrossVtTarget_ProducesCrossVtLdsaClassification()
        {
            LoadMicroOp load = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 2,
                destReg: 7,
                address: 0x3E00,
                domainTag: 1);
            load.OwnerContextId = 5;
            load.MemoryLocalityHint = LocalityHint.Cold;
            load.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                load,
                donorCoreId: 2,
                donorPodId: 0x220,
                out AssistInterCoreTransport transport));

            Assert.True(AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: 0x220,
                carrierCoreId: 0,
                carrierVirtualThreadId: 1,
                targetCoreId: 0,
                targetVirtualThreadId: 1,
                targetOwnerContextId: 9,
                targetDomainTag: 1,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp assist));

            Assert.Equal(AssistKind.Ldsa, assist.Kind);
            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, assist.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, assist.CarrierKind);
            Assert.Equal(SlotClass.DmaStreamClass, assist.CarrierSlotClass);
            Assert.Equal(LocalityHint.Cold, assist.LocalityHint);
            Assert.Equal(AssistDonorSourceKind.InterCoreVtDonorSeed, assist.DonorSource.Kind);
            Assert.Equal(2, assist.DonorVirtualThreadId);
            Assert.Equal(1, assist.TargetVirtualThreadId);
            Assert.True(assist.DonorSource.IsCrossVirtualThread);
            Assert.Equal(2, assist.DonorSource.SourceCoreId);
            Assert.Equal((ushort)0x220, assist.DonorSource.SourcePodId);
        }

        [Fact]
        public void AssistFactory_FromLoadSeed_WithCrossPodInterCoreTransport_ProducesDistinctDonorAndTargetPodOwnership()
        {
            LoadMicroOp load = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 2,
                destReg: 5,
                address: 0x4000,
                domainTag: 1);
            load.OwnerContextId = 3;
            load.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                load,
                donorCoreId: 1,
                donorPodId: 0x120,
                out AssistInterCoreTransport transport));

            bool created = AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: 0x320,
                carrierCoreId: 0,
                carrierVirtualThreadId: 1,
                targetCoreId: 0,
                targetVirtualThreadId: 1,
                targetOwnerContextId: 11,
                targetDomainTag: 1,
                replayEpochId: 5,
                assistEpochId: 2,
                out AssistMicroOp assist);

            Assert.True(created);
            Assert.NotNull(assist);
            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, assist.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, assist.CarrierKind);
            Assert.Equal(SlotClass.DmaStreamClass, assist.CarrierSlotClass);
            Assert.Equal((ushort)0x320, assist.PodId);
            Assert.Equal((ushort)0x120, assist.DonorSource.SourcePodId);
            Assert.Equal(AssistDonorSourceKind.InterCoreVtDonorSeed, assist.DonorSource.Kind);
            Assert.Equal(2, assist.DonorVirtualThreadId);
            Assert.Equal(1, assist.TargetVirtualThreadId);
            Assert.True(assist.DonorSource.IsCrossVirtualThread);
        }

        [Fact]
        public void AssistFactory_FromStoreSeed_RejectsIntraCoreConstruction()
        {
            StoreMicroOp store = MicroOpTestHelper.CreateStore(
                virtualThreadId: 2,
                srcReg: 5,
                address: 0x4200,
                domainTag: 1);
            store.OwnerContextId = 3;
            store.InitializeMetadata();

            bool created = AssistMicroOp.TryCreateFromSeed(
                store,
                carrierVirtualThreadId: 0,
                replayEpochId: 5,
                assistEpochId: 2,
                out AssistMicroOp assist);

            Assert.False(created);
            Assert.Null(assist);
        }

        [Fact]
        public void AssistFactory_FromStoreSeed_WithInterCoreTransport_ProducesExplicitCoreSourceOnLane6Carrier()
        {
            StoreMicroOp store = MicroOpTestHelper.CreateStore(
                virtualThreadId: 2,
                srcReg: 5,
                address: 0x4400,
                domainTag: 1);
            store.OwnerContextId = 3;
            store.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                store,
                donorCoreId: 1,
                donorPodId: 0x120,
                donorAssistEpochId: 21,
                out AssistInterCoreTransport transport));

            bool created = AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: 0x120,
                carrierCoreId: 0,
                carrierVirtualThreadId: 0,
                targetCoreId: 0,
                targetVirtualThreadId: 2,
                targetOwnerContextId: 11,
                targetDomainTag: 1,
                replayEpochId: 5,
                assistEpochId: 2,
                out AssistMicroOp assist);

            Assert.True(created);
            Assert.NotNull(assist);
            Assert.Equal(AssistKind.DonorPrefetch, assist.Kind);
            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, assist.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, assist.CarrierKind);
            Assert.Equal(SlotClass.DmaStreamClass, assist.CarrierSlotClass);
            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtDefaultStoreSeed, assist.DonorSource.Kind);
            Assert.Equal(1, assist.DonorSource.SourceCoreId);
            Assert.Equal((ushort)0x120, assist.DonorSource.SourcePodId);
            Assert.Equal(21UL, transport.DonorAssistEpochId);
            Assert.Equal(21UL, assist.DonorSource.SourceAssistEpochId);
            Assert.Equal(0, assist.CarrierCoreId);
            Assert.Equal(0, assist.TargetCoreId);
            Assert.Equal((ushort)0x120, assist.PodId);
            Assert.Equal(2, assist.DonorVirtualThreadId);
            Assert.Equal(2, assist.TargetVirtualThreadId);
            Assert.Equal(11, assist.OwnerContextId);
            Assert.Equal(1UL, assist.Placement.DomainTag);
        }

        [Fact]
        public void AssistFactory_FromHotStoreSeed_WithInterCoreTransport_ProducesExplicitCoreSourceOnLane6Carrier()
        {
            StoreMicroOp store = MicroOpTestHelper.CreateStore(
                virtualThreadId: 2,
                srcReg: 5,
                address: 0x4500,
                domainTag: 1);
            store.OwnerContextId = 3;
            store.MemoryLocalityHint = LocalityHint.Hot;
            store.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                store,
                donorCoreId: 1,
                donorPodId: 0x120,
                donorAssistEpochId: 24,
                out AssistInterCoreTransport transport));

            bool created = AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: 0x120,
                carrierCoreId: 0,
                carrierVirtualThreadId: 0,
                targetCoreId: 0,
                targetVirtualThreadId: 2,
                targetOwnerContextId: 11,
                targetDomainTag: 1,
                replayEpochId: 5,
                assistEpochId: 2,
                out AssistMicroOp assist);

            Assert.True(created);
            Assert.NotNull(assist);
            Assert.Equal(AssistKind.DonorPrefetch, assist.Kind);
            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, assist.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, assist.CarrierKind);
            Assert.Equal(SlotClass.DmaStreamClass, assist.CarrierSlotClass);
            Assert.Equal(LocalityHint.Hot, assist.LocalityHint);
            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtHotStoreSeed, assist.DonorSource.Kind);
            Assert.Equal(1, assist.DonorSource.SourceCoreId);
            Assert.Equal((ushort)0x120, assist.DonorSource.SourcePodId);
            Assert.Equal(24UL, transport.DonorAssistEpochId);
            Assert.Equal(24UL, assist.DonorSource.SourceAssistEpochId);
            Assert.Equal(2, assist.DonorVirtualThreadId);
            Assert.Equal(2, assist.TargetVirtualThreadId);
            Assert.False(assist.DonorSource.IsCrossVirtualThread);
        }

        [Fact]
        public void AssistFactory_FromStoreSeed_WithInterCoreTransportAndCrossVtTarget_ProducesCrossVtExplicitCoreSourceOnLane6Carrier()
        {
            StoreMicroOp store = MicroOpTestHelper.CreateStore(
                virtualThreadId: 2,
                srcReg: 5,
                address: 0x4600,
                domainTag: 1);
            store.OwnerContextId = 3;
            store.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                store,
                donorCoreId: 1,
                donorPodId: 0x120,
                out AssistInterCoreTransport transport));

            bool created = AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: 0x120,
                carrierCoreId: 0,
                carrierVirtualThreadId: 1,
                targetCoreId: 0,
                targetVirtualThreadId: 1,
                targetOwnerContextId: 11,
                targetDomainTag: 1,
                replayEpochId: 5,
                assistEpochId: 2,
                out AssistMicroOp assist);

            Assert.True(created);
            Assert.NotNull(assist);
            Assert.Equal(AssistKind.DonorPrefetch, assist.Kind);
            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, assist.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, assist.CarrierKind);
            Assert.Equal(SlotClass.DmaStreamClass, assist.CarrierSlotClass);
            Assert.Equal(AssistDonorSourceKind.InterCoreVtDonorDefaultStoreSeed, assist.DonorSource.Kind);
            Assert.Equal(1, assist.DonorSource.SourceCoreId);
            Assert.Equal((ushort)0x120, assist.DonorSource.SourcePodId);
            Assert.Equal(2, assist.DonorVirtualThreadId);
            Assert.Equal(1, assist.TargetVirtualThreadId);
            Assert.True(assist.DonorSource.IsCrossVirtualThread);
        }

        [Fact]
        public void AssistFactory_FromHotStoreSeed_WithInterCoreTransportAndCrossVtTarget_ProducesCrossVtExplicitCoreSourceOnLane6Carrier()
        {
            StoreMicroOp store = MicroOpTestHelper.CreateStore(
                virtualThreadId: 2,
                srcReg: 5,
                address: 0x4700,
                domainTag: 1);
            store.OwnerContextId = 3;
            store.MemoryLocalityHint = LocalityHint.Hot;
            store.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                store,
                donorCoreId: 1,
                donorPodId: 0x120,
                donorAssistEpochId: 25,
                out AssistInterCoreTransport transport));

            bool created = AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: 0x120,
                carrierCoreId: 0,
                carrierVirtualThreadId: 1,
                targetCoreId: 0,
                targetVirtualThreadId: 1,
                targetOwnerContextId: 11,
                targetDomainTag: 1,
                replayEpochId: 5,
                assistEpochId: 2,
                out AssistMicroOp assist);

            Assert.True(created);
            Assert.NotNull(assist);
            Assert.Equal(AssistKind.DonorPrefetch, assist.Kind);
            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, assist.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, assist.CarrierKind);
            Assert.Equal(SlotClass.DmaStreamClass, assist.CarrierSlotClass);
            Assert.Equal(LocalityHint.Hot, assist.LocalityHint);
            Assert.Equal(AssistDonorSourceKind.InterCoreVtDonorHotStoreSeed, assist.DonorSource.Kind);
            Assert.Equal(2, assist.DonorVirtualThreadId);
            Assert.Equal(1, assist.TargetVirtualThreadId);
            Assert.True(assist.DonorSource.IsCrossVirtualThread);
            Assert.Equal(1, assist.DonorSource.SourceCoreId);
            Assert.Equal((ushort)0x120, assist.DonorSource.SourcePodId);
            Assert.Equal(25UL, assist.DonorSource.SourceAssistEpochId);
        }

        [Fact]
        public void AssistFactory_FromColdStoreSeed_WithInterCoreTransport_ProducesLane6LdsaClassification()
        {
            StoreMicroOp store = MicroOpTestHelper.CreateStore(
                virtualThreadId: 2,
                srcReg: 7,
                address: 0x4800,
                domainTag: 1);
            store.OwnerContextId = 5;
            store.MemoryLocalityHint = LocalityHint.Cold;
            store.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                store,
                donorCoreId: 2,
                donorPodId: 0x220,
                out AssistInterCoreTransport transport));

            Assert.True(AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: 0x220,
                carrierCoreId: 0,
                carrierVirtualThreadId: 0,
                targetCoreId: 0,
                targetVirtualThreadId: 2,
                targetOwnerContextId: 9,
                targetDomainTag: 1,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp assist));

            Assert.Equal(AssistKind.Ldsa, assist.Kind);
            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, assist.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, assist.CarrierKind);
            Assert.Equal(SlotClass.DmaStreamClass, assist.CarrierSlotClass);
            Assert.Equal(LocalityHint.Cold, assist.LocalityHint);
            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtColdStoreSeed, assist.DonorSource.Kind);
            Assert.Equal(2, assist.DonorSource.SourceCoreId);
            Assert.Equal((ushort)0x220, assist.DonorSource.SourcePodId);
        }

        [Fact]
        public void AssistFactory_FromColdStoreSeed_WithInterCoreTransportAndCrossVtTarget_ProducesCrossVtLane6LdsaClassification()
        {
            StoreMicroOp store = MicroOpTestHelper.CreateStore(
                virtualThreadId: 2,
                srcReg: 7,
                address: 0x4A00,
                domainTag: 1);
            store.OwnerContextId = 5;
            store.MemoryLocalityHint = LocalityHint.Cold;
            store.InitializeMetadata();

            Assert.True(AssistMicroOp.TryCreateInterCoreTransportFromSeed(
                store,
                donorCoreId: 2,
                donorPodId: 0x220,
                out AssistInterCoreTransport transport));

            Assert.True(AssistMicroOp.TryCreateFromInterCoreTransport(
                transport,
                podId: 0x220,
                carrierCoreId: 0,
                carrierVirtualThreadId: 1,
                targetCoreId: 0,
                targetVirtualThreadId: 1,
                targetOwnerContextId: 9,
                targetDomainTag: 1,
                replayEpochId: 0,
                assistEpochId: 0,
                out AssistMicroOp assist));

            Assert.Equal(AssistKind.Ldsa, assist.Kind);
            Assert.Equal(AssistExecutionMode.StreamRegisterPrefetch, assist.ExecutionMode);
            Assert.Equal(AssistCarrierKind.Lane6Dma, assist.CarrierKind);
            Assert.Equal(SlotClass.DmaStreamClass, assist.CarrierSlotClass);
            Assert.Equal(LocalityHint.Cold, assist.LocalityHint);
            Assert.Equal(AssistDonorSourceKind.InterCoreVtDonorColdStoreSeed, assist.DonorSource.Kind);
            Assert.Equal(2, assist.DonorVirtualThreadId);
            Assert.Equal(1, assist.TargetVirtualThreadId);
            Assert.True(assist.DonorSource.IsCrossVirtualThread);
            Assert.Equal(2, assist.DonorSource.SourceCoreId);
            Assert.Equal((ushort)0x220, assist.DonorSource.SourcePodId);
        }

        [Fact]
        public void AssistFactory_FromLoadSeed_WithExplicitTargetVt_RejectsCrossVtDonorExpansion()
        {
            LoadMicroOp load = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 2,
                destReg: 5,
                address: 0x2200,
                domainTag: 3);

            bool created = AssistMicroOp.TryCreateFromSeed(
                load,
                carrierVirtualThreadId: 0,
                targetVirtualThreadId: 1,
                replayEpochId: 11,
                assistEpochId: 7,
                assistMicroOp: out AssistMicroOp assist);

            Assert.False(created);
            Assert.Null(assist);
        }

        [Fact]
        public void AssistConstructor_WithLane6CarrierOutsideVdsaContour_ThrowsArgumentException()
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
                AssistExecutionMode.CachePrefetch,
                AssistCarrierKind.Lane6Dma,
                baseAddress: 0x2200,
                prefetchLength: 32,
                elementSize: 4,
                elementCount: 1,
                ownerBinding));
        }

        [Fact]
        public void AssistConstructor_WithSeedDonorSourceOnLane6Vdsa_ThrowsArgumentException()
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
                AssistKind.Vdsa,
                AssistExecutionMode.StreamRegisterPrefetch,
                AssistCarrierKind.Lane6Dma,
                baseAddress: 0x3200,
                prefetchLength: 128,
                elementSize: 4,
                elementCount: 32,
                ownerBinding));
        }

        [Fact]
        public void AssistConstructor_WithSameThreadLdsaOnLane6Carrier_ThrowsArgumentException()
        {
            var ownerBinding = new AssistOwnerBinding(
                carrierVirtualThreadId: 0,
                donorVirtualThreadId: 0,
                targetVirtualThreadId: 0,
                ownerContextId: 3,
                domainTag: 1,
                replayEpochId: 11,
                assistEpochId: 7,
                localityHint: LocalityHint.Cold,
                donorSource: new AssistDonorSourceDescriptor(
                    AssistDonorSourceKind.SameThreadSeed,
                    donorVirtualThreadId: 0,
                    targetVirtualThreadId: 0,
                    ownerContextId: 3,
                    domainTag: 1));

            Assert.Throws<ArgumentException>(() => new AssistMicroOp(
                AssistKind.Ldsa,
                AssistExecutionMode.StreamRegisterPrefetch,
                AssistCarrierKind.Lane6Dma,
                baseAddress: 0x2800,
                prefetchLength: 64,
                elementSize: 8,
                elementCount: 8,
                ownerBinding));
        }

    }
}
