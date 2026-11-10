using System;
using System.Collections.Generic;
using HybridCPU_ISE.Core;
using HybridCPU_ISE.Tests.TestHelpers;
using Xunit;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU_ISE.Tests
{
    public partial class AssistRuntimeTests
    {
        [Fact]
        public void AssistDonorSourceDescriptor_WhenCanonicalFourTuplesAreEnumerated_MatchesFrozenLegalityMatrix()
        {
            var mismatches = new List<string>();

            foreach (AssistKind assistKind in Enum.GetValues<AssistKind>())
            {
                foreach (AssistExecutionMode executionMode in Enum.GetValues<AssistExecutionMode>())
                {
                    foreach (AssistCarrierKind carrierKind in Enum.GetValues<AssistCarrierKind>())
                    {
                        foreach (AssistDonorSourceKind donorSourceKind in Enum.GetValues<AssistDonorSourceKind>())
                        {
                            AssistDonorSourceDescriptor descriptor =
                                CreateCanonicalDonorSourceDescriptor(donorSourceKind);
                            bool actual =
                                descriptor.IsLegalFor(assistKind, executionMode, carrierKind);
                            bool expected = IsExpectedLegalTuple(
                                assistKind,
                                executionMode,
                                carrierKind,
                                donorSourceKind);
                            if (actual != expected)
                            {
                                mismatches.Add(
                                    $"{assistKind}/{executionMode}/{carrierKind}/{donorSourceKind}: expected {expected}, got {actual}");
                            }
                        }
                    }
                }
            }

            Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
        }

        [Theory]
        [InlineData(AssistDonorSourceKind.InterCoreVtDonorVector)]
        [InlineData(AssistDonorSourceKind.InterCoreVtDonorVectorWriteback)]
        public void AssistConstructor_WhenInterCoreVdsaDonorKindIsBoundToSameVt_FailsClosed(
            AssistDonorSourceKind donorSourceKind)
        {
            AssistDonorSourceDescriptor donorSource = new(
                donorSourceKind,
                donorVirtualThreadId: 1,
                targetVirtualThreadId: 1,
                ownerContextId: 3,
                domainTag: 1,
                sourceCoreId: 4,
                sourcePodId: 0x120,
                sourceAssistEpochId: 9);

            Assert.False(donorSource.IsLegalFor(
                AssistKind.Vdsa,
                AssistExecutionMode.StreamRegisterPrefetch,
                AssistCarrierKind.Lane6Dma));

            var ownerBinding = new AssistOwnerBinding(
                carrierVirtualThreadId: 0,
                donorVirtualThreadId: 1,
                targetVirtualThreadId: 1,
                ownerContextId: 3,
                domainTag: 1,
                replayEpochId: 11,
                assistEpochId: 7,
                localityHint: LocalityHint.None,
                donorSource: donorSource,
                carrierCoreId: 0,
                targetCoreId: 0,
                podId: 0x120);

            Assert.Throws<ArgumentException>(() => new AssistMicroOp(
                AssistKind.Vdsa,
                AssistExecutionMode.StreamRegisterPrefetch,
                AssistCarrierKind.Lane6Dma,
                baseAddress: 0x3A00,
                prefetchLength: 64,
                elementSize: 4,
                elementCount: 8,
                ownerBinding));
        }

        [Fact]
        public void AssistVisibilityEnvelope_WhenObservedAcrossRuntimeSurfaces_RemainsRetireInvisibleButCacheTraceAndTelemetryVisible()
        {
            YAKSys_Hybrid_CPU.Processor.MainMemoryArea originalMainMemory =
                YAKSys_Hybrid_CPU.Processor.MainMemory;

            try
            {
                YAKSys_Hybrid_CPU.Processor.MainMemory =
                    new YAKSys_Hybrid_CPU.Processor.MultiBankMemoryArea(4, 0x4000000UL);

                LoadMicroOp seed = TestHelpers.MicroOpTestHelper.CreateLoad(
                    virtualThreadId: 1,
                    destReg: 5,
                    address: 0xD000,
                    domainTag: 1);

                Assert.True(AssistMicroOp.TryCreateFromSeed(
                    seed,
                    carrierVirtualThreadId: 0,
                    replayEpochId: 0,
                    assistEpochId: 0,
                    out AssistMicroOp retireInvisibleAssist));

                Assert.True(retireInvisibleAssist.IsAssist);
                Assert.False(retireInvisibleAssist.IsRetireVisible);
                Assert.True(retireInvisibleAssist.IsReplayDiscardable);
                Assert.True(retireInvisibleAssist.SuppressesArchitecturalFaults);

                var core = new YAKSys_Hybrid_CPU.Processor.CPU_Core(0);
                core.PrepareExecutionStart(0x1000);

                Assert.True(core.ExecuteAssistMicroOp(retireInvisibleAssist));
                Assert.Equal(0UL, core.GetPipelineControl().InstructionsRetired);
                Assert.Equal(0UL, core.GetPipelineControl().ScalarLanesRetired);
                Assert.Equal(0UL, core.GetPipelineControl().NonScalarLanesRetired);
                Assert.Equal(1, CountAssistResidentLines(core.L1_Data, AssistCarrierKind.LsuHosted));

                Assert.True(AssistMicroOp.TryCreateFromSeed(
                    TestHelpers.MicroOpTestHelper.CreateLoad(
                        virtualThreadId: 1,
                        destReg: 7,
                        address: 0xD040,
                        domainTag: 1),
                    carrierVirtualThreadId: 0,
                    replayEpochId: 22,
                    assistEpochId: 35,
                    out AssistMicroOp telemeteredAssist));

                var scheduler = new MicroOpScheduler
                {
                    TypedSlotEnabled = true
                };
                scheduler.NominateAssistCandidate(1, telemeteredAssist);

                MicroOp[] bundle = new MicroOp[8];
                bundle[0] = TestHelpers.MicroOpTestHelper.CreateScalarALU(0, destReg: 1, src1Reg: 2, src2Reg: 3);
                MicroOp[] packed = scheduler.PackBundleIntraCoreSmt(
                    bundle,
                    ownerVirtualThreadId: 0,
                    localCoreId: 0);

                Assert.Contains(packed, microOp => ReferenceEquals(microOp, telemeteredAssist));

                SchedulerPhaseMetrics metrics = scheduler.GetPhaseMetrics();
                Assert.Equal(1, metrics.AssistInjections);
                Assert.Equal(1, metrics.AssistDonorPrefetchInjects);
                Assert.NotEqual(0UL, metrics.LastAssistOwnershipSignature);

                HybridCPU_ISE.Core.TraceSink trace = CreateAssistReplayTrace(
                    assistOwnershipSignature: 0x0102030405060708UL,
                    assistDonorPrefetchInjects: 1,
                    assistSameVtInjects: 1,
                    assistDonorVtInjects: 0);
                HybridCPU_ISE.Core.FullStateTraceEvent evt = Assert.Single(trace.GetThreadTrace(0));

                Assert.Equal(1, evt.AssistDonorPrefetchInjects);
                Assert.Equal(0x0102030405060708UL, evt.AssistOwnershipSignature);
            }
            finally
            {
                YAKSys_Hybrid_CPU.Processor.MainMemory = originalMainMemory;
            }
        }

        private static AssistDonorSourceDescriptor CreateCanonicalDonorSourceDescriptor(
            AssistDonorSourceKind donorSourceKind)
        {
            return donorSourceKind switch
            {
                AssistDonorSourceKind.SameThreadSeed => CreateDonorSource(
                    donorSourceKind,
                    donorVirtualThreadId: 1,
                    targetVirtualThreadId: 1,
                    hasExplicitCoreSource: false),
                AssistDonorSourceKind.IntraCoreVtDonorVector => CreateDonorSource(
                    donorSourceKind,
                    donorVirtualThreadId: 2,
                    targetVirtualThreadId: 1,
                    hasExplicitCoreSource: false),
                AssistDonorSourceKind.InterCoreSameVtSeed or
                AssistDonorSourceKind.InterCoreSameVtVector or
                AssistDonorSourceKind.InterCoreSameVtVectorWriteback or
                AssistDonorSourceKind.InterCoreSameVtHotLoadSeed or
                AssistDonorSourceKind.InterCoreSameVtHotStoreSeed or
                AssistDonorSourceKind.InterCoreSameVtColdStoreSeed or
                AssistDonorSourceKind.InterCoreSameVtDefaultStoreSeed => CreateDonorSource(
                    donorSourceKind,
                    donorVirtualThreadId: 1,
                    targetVirtualThreadId: 1,
                    hasExplicitCoreSource: true),
                AssistDonorSourceKind.InterCoreVtDonorVector or
                AssistDonorSourceKind.InterCoreVtDonorSeed or
                AssistDonorSourceKind.InterCoreVtDonorVectorWriteback or
                AssistDonorSourceKind.InterCoreVtDonorHotLoadSeed or
                AssistDonorSourceKind.InterCoreVtDonorHotStoreSeed or
                AssistDonorSourceKind.InterCoreVtDonorColdStoreSeed or
                AssistDonorSourceKind.InterCoreVtDonorDefaultStoreSeed => CreateDonorSource(
                    donorSourceKind,
                    donorVirtualThreadId: 2,
                    targetVirtualThreadId: 1,
                    hasExplicitCoreSource: true),
                _ => throw new ArgumentOutOfRangeException(nameof(donorSourceKind), donorSourceKind, "Unknown assist donor-source kind.")
            };
        }

        private static AssistDonorSourceDescriptor CreateDonorSource(
            AssistDonorSourceKind donorSourceKind,
            int donorVirtualThreadId,
            int targetVirtualThreadId,
            bool hasExplicitCoreSource)
        {
            return new AssistDonorSourceDescriptor(
                donorSourceKind,
                donorVirtualThreadId,
                targetVirtualThreadId,
                ownerContextId: 7,
                domainTag: 0x2,
                sourceCoreId: hasExplicitCoreSource ? 4 : -1,
                sourcePodId: hasExplicitCoreSource ? (ushort)0x120 : (ushort)0,
                sourceAssistEpochId: hasExplicitCoreSource ? 11UL : 0UL);
        }

        private static bool IsExpectedLegalTuple(
            AssistKind assistKind,
            AssistExecutionMode executionMode,
            AssistCarrierKind carrierKind,
            AssistDonorSourceKind donorSourceKind)
        {
            return (assistKind, executionMode, carrierKind) switch
            {
                (AssistKind.DonorPrefetch, AssistExecutionMode.CachePrefetch, AssistCarrierKind.LsuHosted) =>
                    donorSourceKind == AssistDonorSourceKind.SameThreadSeed,
                (AssistKind.DonorPrefetch, AssistExecutionMode.StreamRegisterPrefetch, AssistCarrierKind.Lane6Dma) =>
                    donorSourceKind is AssistDonorSourceKind.InterCoreSameVtSeed
                        or AssistDonorSourceKind.InterCoreVtDonorSeed
                        or AssistDonorSourceKind.InterCoreSameVtHotLoadSeed
                        or AssistDonorSourceKind.InterCoreVtDonorHotLoadSeed
                        or AssistDonorSourceKind.InterCoreSameVtHotStoreSeed
                        or AssistDonorSourceKind.InterCoreVtDonorHotStoreSeed
                        or AssistDonorSourceKind.InterCoreSameVtDefaultStoreSeed
                        or AssistDonorSourceKind.InterCoreVtDonorDefaultStoreSeed,
                (AssistKind.Ldsa, AssistExecutionMode.CachePrefetch, AssistCarrierKind.LsuHosted) =>
                    donorSourceKind == AssistDonorSourceKind.SameThreadSeed,
                (AssistKind.Ldsa, AssistExecutionMode.StreamRegisterPrefetch, AssistCarrierKind.Lane6Dma) =>
                    donorSourceKind is AssistDonorSourceKind.InterCoreSameVtSeed
                        or AssistDonorSourceKind.InterCoreVtDonorSeed
                        or AssistDonorSourceKind.InterCoreSameVtColdStoreSeed
                        or AssistDonorSourceKind.InterCoreVtDonorColdStoreSeed,
                (AssistKind.Vdsa, AssistExecutionMode.StreamRegisterPrefetch, AssistCarrierKind.Lane6Dma) =>
                    donorSourceKind is AssistDonorSourceKind.SameThreadSeed
                        or AssistDonorSourceKind.IntraCoreVtDonorVector
                        or AssistDonorSourceKind.InterCoreSameVtVector
                        or AssistDonorSourceKind.InterCoreVtDonorVector
                        or AssistDonorSourceKind.InterCoreSameVtVectorWriteback
                        or AssistDonorSourceKind.InterCoreVtDonorVectorWriteback,
                _ => false
            };
        }
    }
}
