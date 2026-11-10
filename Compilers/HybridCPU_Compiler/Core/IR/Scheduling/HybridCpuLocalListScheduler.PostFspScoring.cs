using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR;

public sealed partial class HybridCpuLocalListScheduler
{
    private const double DecoderPreparedGroupThreshold = 0.15;
    private const double ReplayHitRateThreshold = 0.55;
    private const double CertificatePressureThreshold = 0.20;

    private int GetPostFspScore(IrInstruction instruction, IReadOnlyList<IrInstruction>? scheduledCycleInstructions = null)
    {
        if (!HasUsableProfile || !UsePostFspScoring)
        {
            return 0;
        }

        PostFspScoreBreakdown score = CreatePostFspScore(instruction, scheduledCycleInstructions);
        return score.TotalScore;
    }

    private PostFspScoreBreakdown CreatePostFspScore(IrInstruction instruction, IReadOnlyList<IrInstruction>? scheduledCycleInstructions)
    {
        int preparedGroupBonus = GetDecoderPreparedGroupBonus();
        int replayReuseBonus = GetReplayReuseBonus();
        int fallbackOpportunityBonus = GetFallbackOpportunityBonus(instruction);
        int certificatePenalty = GetCertificatePressurePenalty(instruction);
        int cycleDiversityBonus = GetCycleDiversityBonus(instruction, scheduledCycleInstructions);
        int reductionBias = GetReductionAwareBias(instruction);

        return new PostFspScoreBreakdown(
            PreparedGroupBonus: preparedGroupBonus,
            ReplayReuseBonus: replayReuseBonus,
            FallbackOpportunityBonus: fallbackOpportunityBonus,
            CycleDiversityBonus: cycleDiversityBonus,
            ReductionBias: reductionBias,
            CertificatePenalty: certificatePenalty);
    }

    private bool ShouldReserveSlack(IReadOnlyList<IrInstruction> scheduledCycleInstructions, IReadOnlyList<IrSchedulingNode> remainingReadyNodes)
    {
        if (!HasUsableProfile || !UseConditionalSlackReservation || !UsePostFspScoring)
        {
            return false;
        }

        if (_currentProgramContext.TreatAsReductionCoordinator ||
            scheduledCycleInstructions.Count < 2 ||
            scheduledCycleInstructions.Count >= 4)
        {
            return false;
        }

        int slackSignal = GetDecoderPreparedGroupBonus() + GetReplayReuseBonus();
        if (GetDecoderFallbackPenalty() > 0)
        {
            slackSignal++;
        }

        for (int index = 0; index < scheduledCycleInstructions.Count; index++)
        {
            if (GetCertificatePressurePenalty(scheduledCycleInstructions[index]) > 0)
            {
                slackSignal--;
            }
        }

        if (slackSignal < 3)
        {
            return false;
        }

        for (int index = 0; index < remainingReadyNodes.Count; index++)
        {
            IrInstruction readyInstruction = remainingReadyNodes[index].Instruction;
            if (readyInstruction.Annotation.BindingKind != IrSlotBindingKind.HardPinned)
            {
                return true;
            }
        }

        return false;
    }

    private int GetDecoderPreparedGroupBonus()
    {
        double preparedGroupRate = ProfileReader!.GetDecoderPreparedGroupRate();
        if (preparedGroupRate <= DecoderPreparedGroupThreshold)
        {
            return 0;
        }

        return preparedGroupRate > 0.30 ? 2 : 1;
    }

    private int GetReplayReuseBonus()
    {
        double replayHitRate = ProfileReader!.GetReplayHitRate();
        if (replayHitRate <= ReplayHitRateThreshold)
        {
            return 0;
        }

        return replayHitRate > 0.75 ? 2 : 1;
    }

    private int GetFallbackOpportunityBonus(IrInstruction instruction)
    {
        if (instruction.Annotation.RequiredSlotClass != IrSlotClass.AluClass)
        {
            return 0;
        }

        int fallbackPenalty = GetDecoderFallbackPenalty();
        if (fallbackPenalty == 0)
        {
            return 0;
        }

        return instruction.Annotation.BindingKind == IrSlotBindingKind.ClassFlexible ? fallbackPenalty + 1 : fallbackPenalty;
    }

    private int GetCertificatePressurePenalty(IrInstruction instruction)
    {
        double certificatePressure = ProfileReader!.GetCertificatePressureByClass(instruction.Annotation.RequiredSlotClass);
        if (certificatePressure <= CertificatePressureThreshold)
        {
            return 0;
        }

        return certificatePressure > 0.35 ? 2 : 1;
    }

    private int GetCycleDiversityBonus(IrInstruction instruction, IReadOnlyList<IrInstruction>? scheduledCycleInstructions)
    {
        if (scheduledCycleInstructions is null || scheduledCycleInstructions.Count == 0)
        {
            return 0;
        }

        for (int index = 0; index < scheduledCycleInstructions.Count; index++)
        {
            if (scheduledCycleInstructions[index].Annotation.RequiredSlotClass == instruction.Annotation.RequiredSlotClass)
            {
                return 0;
            }
        }

        return 1;
    }

    private int GetReductionAwareBias(IrInstruction instruction)
    {
        if (!_currentProgramContext.ReductionAwareShapingEnabled)
        {
            return 0;
        }

        if (_currentProgramContext.TreatAsReductionCoordinator)
        {
            return instruction.Annotation.ControlFlowKind == IrControlFlowKind.None &&
                   !instruction.Annotation.IsBarrierLike &&
                   instruction.Annotation.ResourceClass != IrResourceClass.System
                ? 1
                : 0;
        }

        return instruction.Annotation.BindingKind == IrSlotBindingKind.ClassFlexible
            ? 1
            : 0;
    }

    private readonly record struct PostFspScoreBreakdown(
        int PreparedGroupBonus,
        int ReplayReuseBonus,
        int FallbackOpportunityBonus,
        int CycleDiversityBonus,
        int ReductionBias,
        int CertificatePenalty)
    {
        public int TotalScore => PreparedGroupBonus + ReplayReuseBonus + FallbackOpportunityBonus + CycleDiversityBonus + ReductionBias - CertificatePenalty;
    }
}
