using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using HybridCPU_ISE.Tests.TestHelpers;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Decoder;
using YAKSys_Hybrid_CPU.Core.Execution.DmaStreamCompute;
using YAKSys_Hybrid_CPU.Core.Pipeline.MicroOps;
using HybridCPU_ISE.Arch;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests;

public sealed class HardPinnedLane6AdmissionTraceTests
{
    private sealed record Candidate(string Name, int Vt, MicroOp Op);

    [Fact]
    public void EmitsProductionFspStageAStageBAndPostCommitTrace()
    {
        object[] observations =
        [
            Run("dma-correct-class", Dma("dma-correct", 1)),
            Run("matrix-tile-correct-class", MatrixTile("matrix-correct", 1)),
            Run("alu-class-hardpinned-to-lane6", AluHardPinnedLane6("alu-lane6", 1)),
            Run("dma-retagged-as-matrix-tile", Retag(Dma("dma-wrong-class", 1), SlotClass.MatrixTileStreamClass)),
            Run("matrix-tile-retagged-as-dma", Retag(MatrixTile("matrix-wrong-class", 1), SlotClass.DmaStreamClass)),
            RunPair("post-commit-mixed-class-lane6-conflict", Dma("dma-first", 1), MatrixTile("matrix-second", 2))
        ];
        string trace = Environment.GetEnvironmentVariable("CW_HARDPINNED_LANE6_TRACE")
            ?? throw new InvalidOperationException("CW_HARDPINNED_LANE6_TRACE is required.");
        File.WriteAllText(trace, JsonSerializer.Serialize(observations));
        Assert.Equal(6, observations.Length);
    }

    private static Candidate Dma(string name, int nominationVt)
    {
        DmaStreamComputeDescriptor descriptor = DmaStreamComputeTestDescriptorFactory.CreateDescriptor();
        DmaStreamComputeMicroOp op = new(descriptor);
        SetNominationVt(op, nominationVt);
        return new Candidate(name, nominationVt, op);
    }

    private static Candidate MatrixTile(string name, int nominationVt)
    {
        VLIW_Instruction[] sourceSlots = new VLIW_Instruction[8];
        sourceSlots[0] = InstructionEncoder.EncodeVector1D(
            (uint)InstructionsEnum.MTILE_LOAD,
            DataTypeEnum.INT32,
            destSrc1Ptr: 0x1000,
            src2Ptr: 0x2000,
            streamLength: 4,
            stride: 16);
        InstructionSlotMetadata[] metadata = Enumerable.Repeat(InstructionSlotMetadata.Default, 8).ToArray();
        DecodedInstructionBundle decoded = new VliwDecoderV4().DecodeInstructionBundle(
            sourceSlots,
            new VliwBundleAnnotations(metadata),
            bundleAddress: 0x1000,
            bundleSerial: 11);
        MicroOp?[] carriers = DecodedBundleTransportProjector.BuildCanonicalCarrierBundleForTesting(sourceSlots, decoded);
        MatrixTileMicroOp op = Assert.IsAssignableFrom<MatrixTileMicroOp>(carriers[0]);
        SetNominationVt(op, nominationVt);
        return new Candidate(name, nominationVt, op);
    }

    private static Candidate AluHardPinnedLane6(string name, int nominationVt)
    {
        ScalarALUMicroOp op = new()
        {
            OpCode = (uint)InstructionsEnum.ADD,
            OwnerThreadId = 1,
            DestRegID = 3,
            Src1RegID = 1,
            Src2RegID = 2,
            WritesRegister = true
        };
        op.Placement = op.Placement with
        {
            RequiredSlotClass = SlotClass.AluClass,
            PinningKind = SlotPinningKind.HardPinned,
            PinnedLaneId = 6
        };
        op.InitializeMetadata();
        SetNominationVt(op, nominationVt);
        return new Candidate(name, nominationVt, op);
    }

    private static Candidate Retag(Candidate source, SlotClass requiredClass)
    {
        source.Op.Placement = source.Op.Placement with { RequiredSlotClass = requiredClass };
        source.Op.RefreshAdmissionMetadata();
        return source;
    }

    private static void SetNominationVt(MicroOp op, int nominationVt)
    {
        op.VirtualThreadId = nominationVt;
        op.RefreshAdmissionMetadata();
    }

    private static object Run(string scenario, Candidate candidate) => RunMany(scenario, candidate);

    private static object RunPair(string scenario, Candidate first, Candidate second) => RunMany(scenario, first, second);

    private static object RunMany(string scenario, params Candidate[] candidates)
    {
        MicroOpScheduler scheduler = new() { TypedSlotEnabled = true, PipelinedFspEnabled = true };
        scheduler.ClearSmtNominationPorts();
        byte readyMask = 0;
        foreach (Candidate candidate in candidates)
        {
            scheduler.NominateSmtCandidate(candidate.Vt, candidate.Op);
            readyMask |= (byte)(1 << candidate.Vt);
        }

        MicroOp?[] initial = new MicroOp?[BundleMetadata.BundleSlotCount];
        MicroOp[] primed = scheduler.PackBundleIntraCoreSmt(initial, ownerVirtualThreadId: 0, localCoreId: 0, eligibleVirtualThreadMask: readyMask);
        MicroOp[] committed = scheduler.PackBundleIntraCoreSmt(primed, ownerVirtualThreadId: 0, localCoreId: 0, eligibleVirtualThreadMask: readyMask);
        TypedSlotRejectClassification reject = scheduler.TestGetLastRejectClassification();
        var candidateResults = candidates.Select(candidate => new
        {
            candidate.Name,
            candidate.Vt,
            runtimeType = candidate.Op.GetType().FullName,
            requestedClass = candidate.Op.Placement.RequiredSlotClass.ToString(),
            pinning = candidate.Op.Placement.PinningKind.ToString(),
            pinnedLane = candidate.Op.Placement.PinnedLaneId,
            isMemoryOp = candidate.Op.IsMemoryOp,
            classAdmissionMetadata = candidate.Op.AdmissionMetadata.Placement.RequiredSlotClass.ToString(),
            injected = candidate.Op.IsFspInjected,
            selectedLane = Array.FindIndex(committed, op => ReferenceEquals(op, candidate.Op)),
            sourceAttemptCreated = candidate.Op.PostStageBIssuedAttempt is not null
        }).ToArray();
        foreach (var candidate in candidateResults)
            if (candidate.injected && candidate.selectedLane != candidate.pinnedLane)
                throw new InvalidOperationException("A HardPinned candidate committed to a different physical lane.");

        return new
        {
            scenario,
            path = "PackBundleIntraCoreSmt: first call primes SCHED1, second call executes production SCHED2 Stage A/Stage B/commit",
            candidates = candidateResults,
            scheduler.SmtInjectionsCount,
            scheduler.SmtRejectionsCount,
            scheduler.DmaStreamClassInjects,
            scheduler.MatrixTileStreamClassInjects,
            lastLegalityReject = scheduler.LastSmtLegalityRejectKind.ToString(),
            legalityAuthority = scheduler.LastSmtLegalityAuthoritySource.ToString(),
            legalityRejectsByAluClass = scheduler.SmtLegalityRejectByAluClass,
            lastReject = new
            {
                reason = reject.AdmissionReject.ToString(),
                candidateClass = reject.CandidateClass.ToString(),
                reject.IsPinnedConflict,
                reject.IsClassCapacityIssue,
                reject.IsStaticOvercommit,
                reject.IsDynamicExhaustion,
                reject.IsDynamicStateIssue
            },
            committedBundleOccupancy = committed.Select((op, lane) => (op, lane)).Where(x => x.op is not null).Aggregate(0, (mask, x) => mask | (1 << x.lane))
        };
    }
}
