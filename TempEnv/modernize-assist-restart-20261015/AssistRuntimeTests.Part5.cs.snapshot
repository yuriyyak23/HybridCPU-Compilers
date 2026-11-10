using System;
using System.IO;
using HybridCPU_ISE.Arch;
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
        public void BundleResourceCertificate_WhenInterCoreLane6ColdStoreLdsaOwnerTupleDiffers_ProducesDistinctStructuralIdentity()
        {
            StoreMicroOp store = MicroOpTestHelper.CreateStore(
                virtualThreadId: 2,
                srcReg: 5,
                address: 0xA500,
                domainTag: 1);
            store.OwnerContextId = 3;
            store.MemoryLocalityHint = LocalityHint.Cold;
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

            Assert.Equal(AssistKind.Ldsa, sameVtAssist.Kind);
            Assert.Equal(AssistKind.Ldsa, donorVtAssist.Kind);
            Assert.Equal(AssistCarrierKind.Lane6Dma, sameVtAssist.CarrierKind);
            Assert.Equal(AssistCarrierKind.Lane6Dma, donorVtAssist.CarrierKind);
            Assert.Equal(AssistDonorSourceKind.InterCoreSameVtColdStoreSeed, sameVtAssist.DonorSource.Kind);
            Assert.Equal(AssistDonorSourceKind.InterCoreVtDonorColdStoreSeed, donorVtAssist.DonorSource.Kind);
            Assert.NotEqual(
                AssistOwnershipFingerprint.Compute(sameVtAssist),
                AssistOwnershipFingerprint.Compute(donorVtAssist));
            Assert.NotEqual(
                sameVtCertificate.AssistStructuralKey,
                donorVtCertificate.AssistStructuralKey);
            Assert.NotEqual(sameVtCertificate.StructuralIdentity, donorVtCertificate.StructuralIdentity);
        }

        [Fact]
        public void BundleResourceCertificate_WhenInterCoreLane6LdsaOwnerTupleDiffers_ProducesDistinctStructuralIdentity()
        {
            LoadMicroOp load = MicroOpTestHelper.CreateLoad(
                virtualThreadId: 2,
                destReg: 5,
                address: 0xA600,
                domainTag: 1);
            load.OwnerContextId = 3;
            load.MemoryLocalityHint = LocalityHint.Cold;
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

            Assert.Equal(AssistKind.Ldsa, sameVtAssist.Kind);
            Assert.Equal(AssistKind.Ldsa, donorVtAssist.Kind);
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
        public void ReplayTrace_WhenAssistOwnershipSignatureDiverges_FailsReplayContract()
        {
            TraceSink baseline = CreateAssistReplayTrace(assistOwnershipSignature: 0x0102030405060708UL);
            TraceSink candidate = CreateAssistReplayTrace(assistOwnershipSignature: 0x0102030405060709UL);

            ReplayDeterminismReport report = ReplayEngine.CompareRepeatedRuns(baseline, candidate);

            Assert.False(report.IsDeterministic);
            Assert.Equal("AssistOwnershipSignature", report.MismatchField);
            Assert.Contains("0x0102030405060708", report.ExpectedValue);
            Assert.Contains("0x0102030405060709", report.ActualValue);
        }

        [Fact]
        public void RecordPhaseAwareState_WhenAssistInterCoreLocalitySplitIsPresent_EmitsSplitCounters()
        {
            TraceSink trace = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCorePodLocalInjections: 1,
                assistInterCoreCrossPodInjections: 2,
                assistInterCorePodLocalRejects: 3,
                assistInterCoreCrossPodRejects: 4,
                assistInterCorePodLocalDomainRejects: 5,
                assistInterCoreCrossPodDomainRejects: 6,
                assistInterCoreSameVtVectorInjects: 7,
                assistInterCoreDonorVtVectorInjects: 8,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);

            FullStateTraceEvent evt = Assert.Single(trace.GetThreadTrace(0));
            Assert.Equal(1, evt.AssistInterCorePodLocalInjections);
            Assert.Equal(2, evt.AssistInterCoreCrossPodInjections);
            Assert.Equal(3, evt.AssistInterCorePodLocalRejects);
            Assert.Equal(4, evt.AssistInterCoreCrossPodRejects);
            Assert.Equal(5, evt.AssistInterCorePodLocalDomainRejects);
            Assert.Equal(6, evt.AssistInterCoreCrossPodDomainRejects);
            Assert.Equal(7, evt.AssistInterCoreSameVtVectorInjects);
            Assert.Equal(8, evt.AssistInterCoreDonorVtVectorInjects);
        }

        [Fact]
        public void RecordPhaseAwareState_WhenInterCoreWritebackVectorCountersArePresent_EmitsDedicatedCounters()
        {
            TraceSink trace = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreSameVtVectorWritebackInjects: 9,
                assistInterCoreDonorVtVectorWritebackInjects: 10,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);

            FullStateTraceEvent evt = Assert.Single(trace.GetThreadTrace(0));
            Assert.Equal(9, evt.AssistInterCoreSameVtVectorWritebackInjects);
            Assert.Equal(10, evt.AssistInterCoreDonorVtVectorWritebackInjects);
        }

        [Fact]
        public void RecordPhaseAwareState_WhenInterCoreLane6LdsaCounterIsPresent_EmitsDedicatedCounter()
        {
            TraceSink trace = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreLane6LdsaInjects: 3,
                assistLdsaInjects: 1,
                assistVdsaInjects: 0,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);

            FullStateTraceEvent evt = Assert.Single(trace.GetThreadTrace(0));
            Assert.Equal(3, evt.AssistInterCoreLane6LdsaInjects);
            Assert.Equal(1, evt.AssistLdsaInjects);
            Assert.Equal(0, evt.AssistVdsaInjects);
        }

        [Fact]
        public void RecordPhaseAwareState_WhenInterCoreLane6ColdStoreLdsaCounterIsPresent_EmitsDedicatedCounter()
        {
            TraceSink trace = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreLane6ColdStoreLdsaInjects: 5,
                assistLdsaInjects: 1,
                assistVdsaInjects: 0,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);

            FullStateTraceEvent evt = Assert.Single(trace.GetThreadTrace(0));
            Assert.Equal(5, evt.AssistInterCoreLane6ColdStoreLdsaInjects);
            Assert.Equal(1, evt.AssistLdsaInjects);
            Assert.Equal(0, evt.AssistVdsaInjects);
        }

        [Fact]
        public void RecordPhaseAwareState_WhenInterCoreLane6DonorPrefetchCounterIsPresent_EmitsDedicatedCounter()
        {
            TraceSink trace = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreLane6DonorPrefetchInjects: 4,
                assistDonorPrefetchInjects: 1,
                assistVdsaInjects: 0,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);

            FullStateTraceEvent evt = Assert.Single(trace.GetThreadTrace(0));
            Assert.Equal(4, evt.AssistInterCoreLane6DonorPrefetchInjects);
            Assert.Equal(1, evt.AssistDonorPrefetchInjects);
            Assert.Equal(0, evt.AssistVdsaInjects);
        }

        [Fact]
        public void RecordPhaseAwareState_WhenInterCoreScalarTupleCountersArePresent_EmitsTupleSpecificCountersOnly()
        {
            TraceSink trace = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreLane6DefaultStoreDonorPrefetchInjects: 2,
                assistInterCoreLane6HotLoadDonorPrefetchInjects: 3,
                assistInterCoreLane6HotStoreDonorPrefetchInjects: 4,
                assistDonorPrefetchInjects: 9,
                assistVdsaInjects: 0,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);

            FullStateTraceEvent evt = Assert.Single(trace.GetThreadTrace(0));
            Assert.Equal(2, evt.AssistInterCoreLane6DefaultStoreDonorPrefetchInjects);
            Assert.Equal(3, evt.AssistInterCoreLane6HotLoadDonorPrefetchInjects);
            Assert.Equal(4, evt.AssistInterCoreLane6HotStoreDonorPrefetchInjects);
            Assert.Equal(9, evt.AssistDonorPrefetchInjects);
            Assert.Equal(0, evt.AssistVdsaInjects);
        }

        [Fact]
        public void RecordPhaseAwareState_WhenInterCoreLane6DefaultStoreDonorPrefetchCounterIsPresent_EmitsDedicatedCounter()
        {
            TraceSink trace = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreLane6DefaultStoreDonorPrefetchInjects: 2,
                assistDonorPrefetchInjects: 2,
                assistVdsaInjects: 0,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);

            FullStateTraceEvent evt = Assert.Single(trace.GetThreadTrace(0));
            Assert.Equal(2, evt.AssistInterCoreLane6DefaultStoreDonorPrefetchInjects);
            Assert.Equal(2, evt.AssistDonorPrefetchInjects);
            Assert.Equal(0, evt.AssistVdsaInjects);
        }

        [Fact]
        public void RecordPhaseAwareState_WhenInterCoreLane6HotLoadDonorPrefetchCounterIsPresent_EmitsDedicatedCounter()
        {
            TraceSink trace = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreLane6HotLoadDonorPrefetchInjects: 6,
                assistDonorPrefetchInjects: 1,
                assistVdsaInjects: 0,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);

            FullStateTraceEvent evt = Assert.Single(trace.GetThreadTrace(0));
            Assert.Equal(6, evt.AssistInterCoreLane6HotLoadDonorPrefetchInjects);
            Assert.Equal(1, evt.AssistDonorPrefetchInjects);
            Assert.Equal(0, evt.AssistVdsaInjects);
        }

        [Fact]
        public void RecordPhaseAwareState_WhenInterCoreLane6HotStoreDonorPrefetchCounterIsPresent_EmitsDedicatedCounter()
        {
            TraceSink trace = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreLane6HotStoreDonorPrefetchInjects: 7,
                assistDonorPrefetchInjects: 1,
                assistVdsaInjects: 0,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);

            FullStateTraceEvent evt = Assert.Single(trace.GetThreadTrace(0));
            Assert.Equal(7, evt.AssistInterCoreLane6HotStoreDonorPrefetchInjects);
            Assert.Equal(1, evt.AssistDonorPrefetchInjects);
            Assert.Equal(0, evt.AssistVdsaInjects);
        }

        [Fact]
        public void ReplayTrace_WhenAssistInterCoreCrossPodInjectionDiverges_FailsReplayContract()
        {
            TraceSink baseline = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreCrossPodInjections: 1);
            TraceSink candidate = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreCrossPodInjections: 2);

            ReplayDeterminismReport report = ReplayEngine.CompareRepeatedRuns(baseline, candidate);

            Assert.False(report.IsDeterministic);
            Assert.Equal("AssistInterCoreCrossPodInjections", report.MismatchField);
            Assert.Equal("1", report.ExpectedValue);
            Assert.Equal("2", report.ActualValue);
        }

        [Fact]
        public void ReplayTrace_WhenAssistInterCoreSameVtVectorInjectionDiverges_FailsReplayContract()
        {
            TraceSink baseline = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreSameVtVectorInjects: 1,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);
            TraceSink candidate = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreSameVtVectorInjects: 2,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);

            ReplayDeterminismReport report = ReplayEngine.CompareRepeatedRuns(baseline, candidate);

            Assert.False(report.IsDeterministic);
            Assert.Equal("AssistInterCoreSameVtVectorInjects", report.MismatchField);
            Assert.Equal("1", report.ExpectedValue);
            Assert.Equal("2", report.ActualValue);
        }

        [Fact]
        public void ReplayTrace_WhenAssistInterCoreSameVtVectorWritebackInjectionDiverges_FailsReplayContract()
        {
            TraceSink baseline = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreSameVtVectorWritebackInjects: 1,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);
            TraceSink candidate = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreSameVtVectorWritebackInjects: 2,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);

            ReplayDeterminismReport report = ReplayEngine.CompareRepeatedRuns(baseline, candidate);

            Assert.False(report.IsDeterministic);
            Assert.Equal("AssistInterCoreSameVtVectorWritebackInjects", report.MismatchField);
            Assert.Equal("1", report.ExpectedValue);
            Assert.Equal("2", report.ActualValue);
        }

        [Fact]
        public void ReplayTrace_WhenAssistInterCoreLane6LdsaInjectionDiverges_FailsReplayContract()
        {
            TraceSink baseline = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreLane6LdsaInjects: 1,
                assistLdsaInjects: 1,
                assistVdsaInjects: 0,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);
            TraceSink candidate = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreLane6LdsaInjects: 2,
                assistLdsaInjects: 1,
                assistVdsaInjects: 0,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);

            ReplayDeterminismReport report = ReplayEngine.CompareRepeatedRuns(baseline, candidate);

            Assert.False(report.IsDeterministic);
            Assert.Equal("AssistInterCoreLane6LdsaInjects", report.MismatchField);
            Assert.Equal("1", report.ExpectedValue);
            Assert.Equal("2", report.ActualValue);
        }

        [Fact]
        public void ReplayTrace_WhenAssistInterCoreLane6ColdStoreLdsaInjectionDiverges_FailsReplayContract()
        {
            TraceSink baseline = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreLane6ColdStoreLdsaInjects: 1,
                assistLdsaInjects: 1,
                assistVdsaInjects: 0,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);
            TraceSink candidate = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreLane6ColdStoreLdsaInjects: 2,
                assistLdsaInjects: 1,
                assistVdsaInjects: 0,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);

            ReplayDeterminismReport report = ReplayEngine.CompareRepeatedRuns(baseline, candidate);

            Assert.False(report.IsDeterministic);
            Assert.Equal("AssistInterCoreLane6ColdStoreLdsaInjects", report.MismatchField);
            Assert.Equal("1", report.ExpectedValue);
            Assert.Equal("2", report.ActualValue);
        }

        [Fact]
        public void ReplayTrace_WhenAssistInterCoreLane6DefaultStoreDonorPrefetchInjectionDiverges_FailsReplayContract()
        {
            TraceSink baseline = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreLane6DefaultStoreDonorPrefetchInjects: 1,
                assistDonorPrefetchInjects: 1,
                assistVdsaInjects: 0,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);
            TraceSink candidate = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreLane6DefaultStoreDonorPrefetchInjects: 2,
                assistDonorPrefetchInjects: 1,
                assistVdsaInjects: 0,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);

            ReplayDeterminismReport report = ReplayEngine.CompareRepeatedRuns(baseline, candidate);

            Assert.False(report.IsDeterministic);
            Assert.Equal("AssistInterCoreLane6DefaultStoreDonorPrefetchInjects", report.MismatchField);
            Assert.Equal("1", report.ExpectedValue);
            Assert.Equal("2", report.ActualValue);
        }

        [Fact]
        public void ReplayTrace_WhenAssistInterCoreLane6HotLoadDonorPrefetchInjectionDiverges_FailsReplayContract()
        {
            TraceSink baseline = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreLane6HotLoadDonorPrefetchInjects: 1,
                assistDonorPrefetchInjects: 1,
                assistVdsaInjects: 0,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);
            TraceSink candidate = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreLane6HotLoadDonorPrefetchInjects: 2,
                assistDonorPrefetchInjects: 1,
                assistVdsaInjects: 0,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);

            ReplayDeterminismReport report = ReplayEngine.CompareRepeatedRuns(baseline, candidate);

            Assert.False(report.IsDeterministic);
            Assert.Equal("AssistInterCoreLane6HotLoadDonorPrefetchInjects", report.MismatchField);
            Assert.Equal("1", report.ExpectedValue);
            Assert.Equal("2", report.ActualValue);
        }

        [Fact]
        public void ReplayTrace_WhenAssistInterCoreLane6HotStoreDonorPrefetchInjectionDiverges_FailsReplayContract()
        {
            TraceSink baseline = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreLane6HotStoreDonorPrefetchInjects: 1,
                assistDonorPrefetchInjects: 1,
                assistVdsaInjects: 0,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);
            TraceSink candidate = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreLane6HotStoreDonorPrefetchInjects: 2,
                assistDonorPrefetchInjects: 1,
                assistVdsaInjects: 0,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);

            ReplayDeterminismReport report = ReplayEngine.CompareRepeatedRuns(baseline, candidate);

            Assert.False(report.IsDeterministic);
            Assert.Equal("AssistInterCoreLane6HotStoreDonorPrefetchInjects", report.MismatchField);
            Assert.Equal("1", report.ExpectedValue);
            Assert.Equal("2", report.ActualValue);
        }

        [Fact]
        public void ReplayTrace_WhenAssistInterCoreLane6DonorPrefetchInjectionDiverges_FailsReplayContract()
        {
            TraceSink baseline = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreLane6DonorPrefetchInjects: 1,
                assistDonorPrefetchInjects: 1,
                assistVdsaInjects: 0,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);
            TraceSink candidate = CreateAssistReplayTrace(
                assistOwnershipSignature: 0x0102030405060708UL,
                assistInterCoreLane6DonorPrefetchInjects: 2,
                assistDonorPrefetchInjects: 1,
                assistVdsaInjects: 0,
                assistSameVtInjects: 1,
                assistDonorVtInjects: 0);

            ReplayDeterminismReport report = ReplayEngine.CompareRepeatedRuns(baseline, candidate);

            Assert.False(report.IsDeterministic);
            Assert.Equal("AssistInterCoreLane6DonorPrefetchInjects", report.MismatchField);
            Assert.Equal("1", report.ExpectedValue);
            Assert.Equal("2", report.ActualValue);
        }

        [Fact]
        public void ReplayBinaryTrace_WhenAssistOwnershipSignatureDiverges_PreservesMismatchAcrossRoundTrip()
        {
            string baselinePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-assist-replay-baseline.bin");
            string candidatePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-assist-replay-candidate.bin");

            try
            {
                TraceSink baseline = CreateAssistReplayTrace(assistOwnershipSignature: 0x1122334455667788UL);
                TraceSink candidate = CreateAssistReplayTrace(assistOwnershipSignature: 0x1122334455667799UL);
                baseline.ExportBinaryTrace(baselinePath);
                candidate.ExportBinaryTrace(candidatePath);

                var baselineReplay = new ReplayEngine(baselinePath);
                var candidateReplay = new ReplayEngine(candidatePath);
                ReplayDeterminismReport report = baselineReplay.CompareReplayPhaseBehavior(candidateReplay);

                Assert.False(report.IsDeterministic);
                Assert.Equal("AssistOwnershipSignature", report.MismatchField);
                Assert.Contains("0x1122334455667788", report.ExpectedValue);
                Assert.Contains("0x1122334455667799", report.ActualValue);
            }
            finally
            {
                File.Delete(baselinePath);
                File.Delete(candidatePath);
            }
        }

        [Fact]
        public void ReplayBinaryTrace_WhenAssistInterCoreDonorVtVectorInjectionDiverges_PreservesMismatchAcrossRoundTrip()
        {
            string baselinePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-assist-vdsa-donor-baseline.bin");
            string candidatePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-assist-vdsa-donor-candidate.bin");

            try
            {
                TraceSink baseline = CreateAssistReplayTrace(
                    assistOwnershipSignature: 0x1122334455667788UL,
                    assistInterCoreDonorVtVectorInjects: 1);
                TraceSink candidate = CreateAssistReplayTrace(
                    assistOwnershipSignature: 0x1122334455667788UL,
                    assistInterCoreDonorVtVectorInjects: 2);
                baseline.ExportBinaryTrace(baselinePath);
                candidate.ExportBinaryTrace(candidatePath);

                var baselineReplay = new ReplayEngine(baselinePath);
                var candidateReplay = new ReplayEngine(candidatePath);
                ReplayDeterminismReport report = baselineReplay.CompareReplayPhaseBehavior(candidateReplay);

                Assert.False(report.IsDeterministic);
                Assert.Equal("AssistInterCoreDonorVtVectorInjects", report.MismatchField);
                Assert.Equal("1", report.ExpectedValue);
                Assert.Equal("2", report.ActualValue);
            }
            finally
            {
                File.Delete(baselinePath);
                File.Delete(candidatePath);
            }
        }

        [Fact]
        public void ReplayBinaryTrace_WhenAssistInterCoreDonorVtVectorWritebackInjectionDiverges_PreservesMismatchAcrossRoundTrip()
        {
            string baselinePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-assist-vdsa-writeback-donor-baseline.bin");
            string candidatePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-assist-vdsa-writeback-donor-candidate.bin");

            try
            {
                TraceSink baseline = CreateAssistReplayTrace(
                    assistOwnershipSignature: 0x1122334455667788UL,
                    assistInterCoreDonorVtVectorWritebackInjects: 1);
                TraceSink candidate = CreateAssistReplayTrace(
                    assistOwnershipSignature: 0x1122334455667788UL,
                    assistInterCoreDonorVtVectorWritebackInjects: 2);
                baseline.ExportBinaryTrace(baselinePath);
                candidate.ExportBinaryTrace(candidatePath);

                var baselineReplay = new ReplayEngine(baselinePath);
                var candidateReplay = new ReplayEngine(candidatePath);
                ReplayDeterminismReport report = baselineReplay.CompareReplayPhaseBehavior(candidateReplay);

                Assert.False(report.IsDeterministic);
                Assert.Equal("AssistInterCoreDonorVtVectorWritebackInjects", report.MismatchField);
                Assert.Equal("1", report.ExpectedValue);
                Assert.Equal("2", report.ActualValue);
            }
            finally
            {
                File.Delete(baselinePath);
                File.Delete(candidatePath);
            }
        }

        [Fact]
        public void ReplayBinaryTrace_WhenAssistInterCorePodLocalInjectionDiverges_PreservesMismatchAcrossRoundTrip()
        {
            string baselinePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-assist-podlocal-baseline.bin");
            string candidatePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-assist-podlocal-candidate.bin");

            try
            {
                TraceSink baseline = CreateAssistReplayTrace(
                    assistOwnershipSignature: 0x1122334455667788UL,
                    assistInterCorePodLocalInjections: 1);
                TraceSink candidate = CreateAssistReplayTrace(
                    assistOwnershipSignature: 0x1122334455667788UL,
                    assistInterCorePodLocalInjections: 2);
                baseline.ExportBinaryTrace(baselinePath);
                candidate.ExportBinaryTrace(candidatePath);

                var baselineReplay = new ReplayEngine(baselinePath);
                var candidateReplay = new ReplayEngine(candidatePath);
                ReplayDeterminismReport report = baselineReplay.CompareReplayPhaseBehavior(candidateReplay);

                Assert.False(report.IsDeterministic);
                Assert.Equal("AssistInterCorePodLocalInjections", report.MismatchField);
                Assert.Equal("1", report.ExpectedValue);
                Assert.Equal("2", report.ActualValue);
            }
            finally
            {
                File.Delete(baselinePath);
                File.Delete(candidatePath);
            }
        }

        [Fact]
        public void ReplayBinaryTrace_WhenAssistInterCoreLane6LdsaInjectionDiverges_PreservesMismatchAcrossRoundTrip()
        {
            string baselinePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-assist-ldsa-lane6-baseline.bin");
            string candidatePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-assist-ldsa-lane6-candidate.bin");

            try
            {
                TraceSink baseline = CreateAssistReplayTrace(
                    assistOwnershipSignature: 0x1122334455667788UL,
                    assistInterCoreLane6LdsaInjects: 1,
                    assistLdsaInjects: 1,
                    assistVdsaInjects: 0,
                    assistSameVtInjects: 1,
                    assistDonorVtInjects: 0);
                TraceSink candidate = CreateAssistReplayTrace(
                    assistOwnershipSignature: 0x1122334455667788UL,
                    assistInterCoreLane6LdsaInjects: 2,
                    assistLdsaInjects: 1,
                    assistVdsaInjects: 0,
                    assistSameVtInjects: 1,
                    assistDonorVtInjects: 0);
                baseline.ExportBinaryTrace(baselinePath);
                candidate.ExportBinaryTrace(candidatePath);

                var baselineReplay = new ReplayEngine(baselinePath);
                var candidateReplay = new ReplayEngine(candidatePath);
                ReplayDeterminismReport report = baselineReplay.CompareReplayPhaseBehavior(candidateReplay);

                Assert.False(report.IsDeterministic);
                Assert.Equal("AssistInterCoreLane6LdsaInjects", report.MismatchField);
                Assert.Equal("1", report.ExpectedValue);
                Assert.Equal("2", report.ActualValue);
            }
            finally
            {
                File.Delete(baselinePath);
                File.Delete(candidatePath);
            }
        }

        [Fact]
        public void ReplayBinaryTrace_WhenAssistInterCoreLane6ColdStoreLdsaInjectionDiverges_PreservesMismatchAcrossRoundTrip()
        {
            string baselinePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-assist-ldsa-lane6-coldstore-baseline.bin");
            string candidatePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-assist-ldsa-lane6-coldstore-candidate.bin");

            try
            {
                TraceSink baseline = CreateAssistReplayTrace(
                    assistOwnershipSignature: 0x1122334455667788UL,
                    assistInterCoreLane6ColdStoreLdsaInjects: 1,
                    assistLdsaInjects: 1,
                    assistVdsaInjects: 0,
                    assistSameVtInjects: 1,
                    assistDonorVtInjects: 0);
                TraceSink candidate = CreateAssistReplayTrace(
                    assistOwnershipSignature: 0x1122334455667788UL,
                    assistInterCoreLane6ColdStoreLdsaInjects: 2,
                    assistLdsaInjects: 1,
                    assistVdsaInjects: 0,
                    assistSameVtInjects: 1,
                    assistDonorVtInjects: 0);
                baseline.ExportBinaryTrace(baselinePath);
                candidate.ExportBinaryTrace(candidatePath);

                var baselineReplay = new ReplayEngine(baselinePath);
                var candidateReplay = new ReplayEngine(candidatePath);
                ReplayDeterminismReport report = baselineReplay.CompareReplayPhaseBehavior(candidateReplay);

                Assert.False(report.IsDeterministic);
                Assert.Equal("AssistInterCoreLane6ColdStoreLdsaInjects", report.MismatchField);
                Assert.Equal("1", report.ExpectedValue);
                Assert.Equal("2", report.ActualValue);
            }
            finally
            {
                File.Delete(baselinePath);
                File.Delete(candidatePath);
            }
        }

        [Fact]
        public void ReplayBinaryTrace_WhenAssistInterCoreLane6DefaultStoreDonorPrefetchInjectionDiverges_PreservesMismatchAcrossRoundTrip()
        {
            string baselinePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-assist-donorprefetch-lane6-defaultstore-baseline.bin");
            string candidatePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-assist-donorprefetch-lane6-defaultstore-candidate.bin");

            try
            {
                TraceSink baseline = CreateAssistReplayTrace(
                    assistOwnershipSignature: 0x1122334455667788UL,
                    assistInterCoreLane6DefaultStoreDonorPrefetchInjects: 1,
                    assistDonorPrefetchInjects: 1,
                    assistVdsaInjects: 0,
                    assistSameVtInjects: 1,
                    assistDonorVtInjects: 0);
                TraceSink candidate = CreateAssistReplayTrace(
                    assistOwnershipSignature: 0x1122334455667788UL,
                    assistInterCoreLane6DefaultStoreDonorPrefetchInjects: 2,
                    assistDonorPrefetchInjects: 1,
                    assistVdsaInjects: 0,
                    assistSameVtInjects: 1,
                    assistDonorVtInjects: 0);
                baseline.ExportBinaryTrace(baselinePath);
                candidate.ExportBinaryTrace(candidatePath);

                var baselineReplay = new ReplayEngine(baselinePath);
                var candidateReplay = new ReplayEngine(candidatePath);
                ReplayDeterminismReport report = baselineReplay.CompareReplayPhaseBehavior(candidateReplay);

                Assert.False(report.IsDeterministic);
                Assert.Equal("AssistInterCoreLane6DefaultStoreDonorPrefetchInjects", report.MismatchField);
                Assert.Equal("1", report.ExpectedValue);
                Assert.Equal("2", report.ActualValue);
            }
            finally
            {
                File.Delete(baselinePath);
                File.Delete(candidatePath);
            }
        }

        [Fact]
        public void ReplayBinaryTrace_WhenAssistInterCoreLane6HotLoadDonorPrefetchInjectionDiverges_PreservesMismatchAcrossRoundTrip()
        {
            string baselinePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-assist-donorprefetch-lane6-hotload-baseline.bin");
            string candidatePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-assist-donorprefetch-lane6-hotload-candidate.bin");

            try
            {
                TraceSink baseline = CreateAssistReplayTrace(
                    assistOwnershipSignature: 0x1122334455667788UL,
                    assistInterCoreLane6HotLoadDonorPrefetchInjects: 1,
                    assistDonorPrefetchInjects: 1,
                    assistVdsaInjects: 0,
                    assistSameVtInjects: 1,
                    assistDonorVtInjects: 0);
                TraceSink candidate = CreateAssistReplayTrace(
                    assistOwnershipSignature: 0x1122334455667788UL,
                    assistInterCoreLane6HotLoadDonorPrefetchInjects: 2,
                    assistDonorPrefetchInjects: 1,
                    assistVdsaInjects: 0,
                    assistSameVtInjects: 1,
                    assistDonorVtInjects: 0);
                baseline.ExportBinaryTrace(baselinePath);
                candidate.ExportBinaryTrace(candidatePath);

                var baselineReplay = new ReplayEngine(baselinePath);
                var candidateReplay = new ReplayEngine(candidatePath);
                ReplayDeterminismReport report = baselineReplay.CompareReplayPhaseBehavior(candidateReplay);

                Assert.False(report.IsDeterministic);
                Assert.Equal("AssistInterCoreLane6HotLoadDonorPrefetchInjects", report.MismatchField);
                Assert.Equal("1", report.ExpectedValue);
                Assert.Equal("2", report.ActualValue);
            }
            finally
            {
                File.Delete(baselinePath);
                File.Delete(candidatePath);
            }
        }

        [Fact]
        public void ReplayBinaryTrace_WhenAssistInterCoreLane6HotStoreDonorPrefetchInjectionDiverges_PreservesMismatchAcrossRoundTrip()
        {
            string baselinePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-assist-donorprefetch-lane6-hotstore-baseline.bin");
            string candidatePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-assist-donorprefetch-lane6-hotstore-candidate.bin");

            try
            {
                TraceSink baseline = CreateAssistReplayTrace(
                    assistOwnershipSignature: 0x1122334455667788UL,
                    assistInterCoreLane6HotStoreDonorPrefetchInjects: 1,
                    assistDonorPrefetchInjects: 1,
                    assistVdsaInjects: 0,
                    assistSameVtInjects: 1,
                    assistDonorVtInjects: 0);
                TraceSink candidate = CreateAssistReplayTrace(
                    assistOwnershipSignature: 0x1122334455667788UL,
                    assistInterCoreLane6HotStoreDonorPrefetchInjects: 2,
                    assistDonorPrefetchInjects: 1,
                    assistVdsaInjects: 0,
                    assistSameVtInjects: 1,
                    assistDonorVtInjects: 0);
                baseline.ExportBinaryTrace(baselinePath);
                candidate.ExportBinaryTrace(candidatePath);

                var baselineReplay = new ReplayEngine(baselinePath);
                var candidateReplay = new ReplayEngine(candidatePath);
                ReplayDeterminismReport report = baselineReplay.CompareReplayPhaseBehavior(candidateReplay);

                Assert.False(report.IsDeterministic);
                Assert.Equal("AssistInterCoreLane6HotStoreDonorPrefetchInjects", report.MismatchField);
                Assert.Equal("1", report.ExpectedValue);
                Assert.Equal("2", report.ActualValue);
            }
            finally
            {
                File.Delete(baselinePath);
                File.Delete(candidatePath);
            }
        }

        [Fact]
        public void ReplayBinaryTrace_WhenRetiredPreV20HotStoreLayoutIsLoaded_RejectsTraceVersion()
        {
            string tracePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-assist-donorprefetch-lane6-hotstore-retired-v19.bin");

            try
            {
                TraceSink trace = CreateAssistReplayTrace(
                    assistOwnershipSignature: 0x1122334455667788UL,
                    assistInterCoreLane6HotStoreDonorPrefetchInjects: 1,
                    assistDonorPrefetchInjects: 1,
                    assistVdsaInjects: 0,
                    assistSameVtInjects: 1,
                    assistDonorVtInjects: 0);
                trace.ExportBinaryTrace(tracePath);
                RewriteTraceBinaryVersion(tracePath, 19);

                InvalidDataException exception = Assert.Throws<InvalidDataException>(() => new ReplayEngine(tracePath));
                Assert.Equal("Unsupported trace version: 19", exception.Message);
            }
            finally
            {
                File.Delete(tracePath);
            }
        }

        [Fact]
        public void ReplayBinaryTrace_WhenRetiredPreV21DefaultStoreLayoutIsLoaded_RejectsTraceVersion()
        {
            string tracePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-assist-donorprefetch-defaultstore-retired-v20.bin");

            try
            {
                TraceSink trace = CreateAssistReplayTrace(
                    assistOwnershipSignature: 0x1122334455667788UL,
                    assistInterCoreLane6DefaultStoreDonorPrefetchInjects: 1,
                    assistDonorPrefetchInjects: 1,
                    assistVdsaInjects: 0,
                    assistSameVtInjects: 1,
                    assistDonorVtInjects: 0);
                trace.ExportBinaryTrace(tracePath);
                RewriteTraceBinaryVersion(tracePath, 20);

                InvalidDataException exception = Assert.Throws<InvalidDataException>(() => new ReplayEngine(tracePath));
                Assert.Equal("Unsupported trace version: 20", exception.Message);
            }
            finally
            {
                File.Delete(tracePath);
            }
        }

        [Fact]
        public void ReplayBinaryTrace_WhenAssistInterCoreLane6DonorPrefetchInjectionDiverges_PreservesMismatchAcrossRoundTrip()
        {
            string baselinePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-assist-donorprefetch-lane6-baseline.bin");
            string candidatePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-assist-donorprefetch-lane6-candidate.bin");

            try
            {
                TraceSink baseline = CreateAssistReplayTrace(
                    assistOwnershipSignature: 0x1122334455667788UL,
                    assistInterCoreLane6DonorPrefetchInjects: 1,
                    assistDonorPrefetchInjects: 1,
                    assistVdsaInjects: 0,
                    assistSameVtInjects: 1,
                    assistDonorVtInjects: 0);
                TraceSink candidate = CreateAssistReplayTrace(
                    assistOwnershipSignature: 0x1122334455667788UL,
                    assistInterCoreLane6DonorPrefetchInjects: 2,
                    assistDonorPrefetchInjects: 1,
                    assistVdsaInjects: 0,
                    assistSameVtInjects: 1,
                    assistDonorVtInjects: 0);
                baseline.ExportBinaryTrace(baselinePath);
                candidate.ExportBinaryTrace(candidatePath);

                var baselineReplay = new ReplayEngine(baselinePath);
                var candidateReplay = new ReplayEngine(candidatePath);
                ReplayDeterminismReport report = baselineReplay.CompareReplayPhaseBehavior(candidateReplay);

                Assert.False(report.IsDeterministic);
                Assert.Equal("AssistInterCoreLane6DonorPrefetchInjects", report.MismatchField);
                Assert.Equal("1", report.ExpectedValue);
                Assert.Equal("2", report.ActualValue);
            }
            finally
            {
                File.Delete(baselinePath);
                File.Delete(candidatePath);
            }
        }

        [Fact]
        public void TryAllocateAssistRegister_WhenForegroundOwnsAllStreamRegisters_RejectsWithoutCreatingAssistOwnership()
        {
            var srf = new StreamRegisterFile(numRegisters: 2);
            byte[] scratch = new byte[16];

            for (int index = 0; index < 2; index++)
            {
                int regIndex = srf.AllocateRegister(
                    sourceAddr: 0x2000UL + ((ulong)index * 0x100UL),
                    elementSize: 4,
                    elementCount: 4);
                Assert.NotEqual(-1, regIndex);
                Assert.True(srf.WriteRegister(regIndex, scratch, (uint)scratch.Length));
            }

            bool allocated = srf.TryAllocateAssistRegister(
                sourceAddr: 0x2800,
                elementSize: 4,
                elementCount: 8,
                policy: AssistStreamRegisterPartitionPolicy.Default,
                registerIndex: out int registerIndex,
                rejectKind: out AssistStreamRegisterRejectKind rejectKind);

            Assert.False(allocated);
            Assert.Equal(-1, registerIndex);
            Assert.Equal(AssistStreamRegisterRejectKind.NoAssistVictim, rejectKind);
            Assert.Equal(0, srf.CountAssistOwnedRegisters());
        }

        private static VectorBinaryOpMicroOp CreateVectorSeed(
            int virtualThreadId = 2,
            int ownerContextId = 9,
            ulong baseAddress = 0x3000,
            ulong src2Address = 0x3800,
            uint streamLength = 64,
            ulong domainTag = 0)
        {
            var vector = new VectorBinaryOpMicroOp
            {
                OwnerThreadId = virtualThreadId,
                VirtualThreadId = virtualThreadId,
                OwnerContextId = ownerContextId,
                Instruction = new VLIW_Instruction
                {
                    DestSrc1Pointer = baseAddress,
                    Src2Pointer = src2Address,
                    StreamLength = streamLength,
                    Stride = 4,
                    DataTypeValue = DataTypeEnum.UINT32
                }
            };
            vector.InitializeMetadata();
            vector.Placement = vector.Placement with { DomainTag = domainTag };
            return vector;
        }

        private static StoreSegmentMicroOp CreateVectorStoreSeed(
            int virtualThreadId = 2,
            int ownerContextId = 9,
            ulong baseAddress = 0x3C00,
            uint streamLength = 64,
            ulong domainTag = 0)
        {
            var vectorStore = new StoreSegmentMicroOp
            {
                OwnerThreadId = virtualThreadId,
                VirtualThreadId = virtualThreadId,
                OwnerContextId = ownerContextId,
                Instruction = new VLIW_Instruction
                {
                    DestSrc1Pointer = baseAddress,
                    StreamLength = streamLength,
                    Stride = 4,
                    DataTypeValue = DataTypeEnum.UINT32
                }
            };
            vectorStore.InitializeMetadata();
            vectorStore.Placement = vectorStore.Placement with { DomainTag = domainTag };
            return vectorStore;
        }

        private static TraceSink CreateAssistReplayTrace(
            ulong assistOwnershipSignature,
            AssistInvalidationReason invalidationReason = AssistInvalidationReason.InterCoreOwnerDrift,
            long assistInterCorePodLocalInjections = 0,
            long assistInterCoreCrossPodInjections = 0,
            long assistInterCorePodLocalRejects = 0,
            long assistInterCoreCrossPodRejects = 0,
            long assistInterCorePodLocalDomainRejects = 0,
            long assistInterCoreCrossPodDomainRejects = 0,
            long assistInterCoreSameVtVectorInjects = 0,
            long assistInterCoreDonorVtVectorInjects = 0,
            long assistInterCoreSameVtVectorWritebackInjects = 0,
            long assistInterCoreDonorVtVectorWritebackInjects = 0,
            long assistInterCoreLane6DefaultStoreDonorPrefetchInjects = 0,
            long assistInterCoreLane6HotLoadDonorPrefetchInjects = 0,
            long assistInterCoreLane6HotStoreDonorPrefetchInjects = 0,
            long assistInterCoreLane6DonorPrefetchInjects = 0,
            long assistInterCoreLane6ColdStoreLdsaInjects = 0,
            long assistInterCoreLane6LdsaInjects = 0,
            long assistDonorPrefetchInjects = 0,
            long assistLdsaInjects = 0,
            long assistVdsaInjects = 1,
            long assistSameVtInjects = 0,
            long assistDonorVtInjects = 1)
        {
            var trace = new TraceSink(TraceFormat.JSON, "assist-runtime-replay.json");
            trace.SetEnabled(true);
            trace.SetLevel(TraceLevel.Full);

            var phase = new ReplayPhaseContext(
                isActive: true,
                epochId: 77,
                cachedPc: 0xB000,
                epochLength: 4,
                completedReplays: 1,
                validSlotCount: 3,
                stableDonorMask: 0x07,
                lastInvalidationReason: ReplayPhaseInvalidationReason.None);

            var metrics = new SchedulerPhaseMetrics
            {
                ReplayAwareCycles = 1,
                PhaseCertificateReadyHits = 2,
                AssistNominations = 1,
                AssistInjections = 1,
                AssistInterCoreNominations = 1,
                AssistInterCoreInjections = 1,
                AssistInterCorePodLocalInjections = assistInterCorePodLocalInjections,
                AssistInterCoreCrossPodInjections = assistInterCoreCrossPodInjections,
                AssistInterCorePodLocalRejects = assistInterCorePodLocalRejects,
                AssistInterCoreCrossPodRejects = assistInterCoreCrossPodRejects,
                AssistInterCorePodLocalDomainRejects = assistInterCorePodLocalDomainRejects,
                AssistInterCoreCrossPodDomainRejects = assistInterCoreCrossPodDomainRejects,
                AssistInterCoreSameVtVectorInjects = assistInterCoreSameVtVectorInjects,
                AssistInterCoreDonorVtVectorInjects = assistInterCoreDonorVtVectorInjects,
                AssistInterCoreSameVtVectorWritebackInjects = assistInterCoreSameVtVectorWritebackInjects,
                AssistInterCoreDonorVtVectorWritebackInjects = assistInterCoreDonorVtVectorWritebackInjects,
                AssistInterCoreLane6DefaultStoreDonorPrefetchInjects = assistInterCoreLane6DefaultStoreDonorPrefetchInjects,
                AssistInterCoreLane6HotLoadDonorPrefetchInjects = assistInterCoreLane6HotLoadDonorPrefetchInjects,
                AssistInterCoreLane6HotStoreDonorPrefetchInjects = assistInterCoreLane6HotStoreDonorPrefetchInjects,
                AssistInterCoreLane6DonorPrefetchInjects = assistInterCoreLane6DonorPrefetchInjects,
                AssistInterCoreLane6ColdStoreLdsaInjects = assistInterCoreLane6ColdStoreLdsaInjects,
                AssistInterCoreLane6LdsaInjects = assistInterCoreLane6LdsaInjects,
                AssistDonorPrefetchInjects = assistDonorPrefetchInjects,
                AssistLdsaInjects = assistLdsaInjects,
                AssistVdsaInjects = assistVdsaInjects,
                AssistSameVtInjects = assistSameVtInjects,
                AssistDonorVtInjects = assistDonorVtInjects,
                AssistInvalidations = invalidationReason == AssistInvalidationReason.None ? 0 : 1,
                LastAssistInvalidationReason = invalidationReason,
                LastAssistOwnershipSignature = assistOwnershipSignature
            };

            trace.RecordPhaseAwareState(
                new FullStateTraceEvent
                {
                    ThreadId = 0,
                    CycleNumber = 900,
                    BundleId = 4,
                    OpIndex = 0,
                    Opcode = 0x40,
                    PipelineStage = "CYCLE",
                    CurrentFSPPolicy = "ReplayAwarePhase1.DenseTimeline"
                },
                phase,
                metrics,
                phaseCertificateTemplateReusable: true);

            return trace;
        }

        private static void RewriteTraceBinaryVersion(string path, ushort version)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.Position = sizeof(uint);
            using var writer = new BinaryWriter(stream);
            writer.Write(version);
        }

        private static int CountAssistResidentLines(
            Processor.CPU_Core.Cache_Data_Object[]? lines,
            AssistCarrierKind? carrierKind = null)
        {
            if (lines == null)
            {
                return 0;
            }

            int count = 0;
            for (int index = 0; index < lines.Length; index++)
            {
                if (!lines[index].AssistResident ||
                    lines[index].DataCache_MemoryAddress == 0)
                {
                    continue;
                }

                if (carrierKind.HasValue &&
                    lines[index].AssistCarrierKind != carrierKind.Value)
                {
                    continue;
                }

                count++;
            }

            return count;
        }
    }
}
