using System;
using System.Collections.Generic;
using HybridCPU.Compiler.Core.IR.Telemetry;

namespace HybridCPU.Compiler.Core.IR;

public sealed partial class HybridCpuLocalListScheduler
{
    internal HybridCpuProfileDecisionTraceCollectorV1? ProfileDecisionTrace { get; set; }
    /// <summary>
    /// Enables Phase 02 counter-bounded comparison of complete ready subsets.
    /// Default is <c>false</c>, which executes the exact pre-Phase-02 greedy path.
    /// </summary>
    public bool UseJointCycleComposition { get; set; }

    /// <summary>
    /// Deterministic search limits. Wall-clock observations cannot select a result or fallback.
    /// </summary>
    public HybridCpuCycleSearchOptionsV1 JointCycleSearchOptions { get; set; } =
        HybridCpuCycleSearchOptionsV1.Default;

    /// <summary>
    /// When <c>true</c>, the scheduler uses a loaded telemetry profile to penalize
    /// high-pressure slot classes, spreading their operations across more cycles.
    /// Default: <c>false</c> (profile data ignored even if loaded).
    /// </summary>
    public bool UseProfileGuidedScheduling { get; set; }

    /// <summary>
    /// When <c>true</c>, ready-node ordering incorporates a telemetry-backed
    /// post-FSP quality score instead of relying solely on static fullness proxies.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UsePostFspScoring { get; set; }

    /// <summary>
    /// When <c>true</c>, cycle formation may stop early to preserve advisory slack
    /// when the loaded profile indicates likely post-FSP payoff.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UseConditionalSlackReservation { get; set; }

    /// <summary>
    /// When <c>true</c>, class-pressure penalties are applied only as bounded tie-breaks
    /// after latency-driven ordering has already tied.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UseClassPressureSmoothingTieBreaks { get; set; }

    /// <summary>
    /// When <c>true</c>, conservative penalties discourage repeated pinned-lane choke windows
    /// when telemetry shows control/pinned contention.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UsePinnedLaneChokeAvoidance { get; set; }

    /// <summary>
    /// When <c>true</c>, ready-node ordering consumes typed-effect dependency edges as bounded tie-breaks.
    /// Default: <c>false</c> so the legacy coarse ordering path remains intact until Wave 3 validation completes.
    /// </summary>
    public bool UseTypedEffectEdgeOrdering { get; set; }

    /// <summary>
    /// When <c>true</c>, ready-node ordering prefers candidates with structural placement
    /// flexibility (ClassFlexible binding, non-pinned-lane) as a bounded tie-break.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UseFlexibilityAwareOrdering { get; set; }

    /// <summary>
    /// When <c>true</c>, the scheduler applies conservative shaping for reduction-oriented
    /// steady-state programs without changing reduction semantics.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UseReductionAwareShaping { get; set; }

    /// <summary>
    /// When <c>true</c>, block scheduling may consume loop-phase telemetry for the current block start PC.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UseLoopPhaseTelemetry { get; set; }

    /// <summary>
    /// When <c>true</c>, ready-node ordering prefers replay-friendly, phase-stable loop shapes.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UsePhaseStableLoopShaping { get; set; }

    /// <summary>
    /// When <c>true</c>, hot-loop telemetry adds bounded selection bias for steady-state candidates.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UseHotLoopTelemetryGuidance { get; set; }

    /// <summary>
    /// When <c>true</c>, cycle formation applies local normalization penalties to reduce loop steady-state spikes.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UseLoopBodyNormalization { get; set; }

    /// <summary>
    /// When <c>true</c>, the shared Wave 2 memory-spacing path also consumes bounded Wave 5 bank-pressure hints
    /// so clustered LSU placements are reduced only when legal alternatives already exist.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UseLoadClusteringAvoidance { get; set; }

    /// <summary>
    /// When <c>true</c>, worker-path ready-node ordering consumes per-VT advisory backend pressure
    /// as a bounded tie-break. Coordinator-special paths remain excluded.
    /// Default: <c>false</c>.
    /// </summary>
    public bool UseVtAwareBackendPressureTieBreaks { get; set; }

    /// <summary>
    /// Marks the current scheduler instance as compiling the reduction coordinator path.
    /// This only changes advisory heuristics and never changes legality or emitted contracts.
    /// Default: <c>false</c>.
    /// </summary>
    public bool TreatAsReductionCoordinator { get; set; }

    /// <summary>
    /// Marks the current scheduler instance as compiling the coordinator-special path.
    /// This is advisory-only and keeps VT0 shaping distinct from worker shaping.
    /// Default: <c>false</c>.
    /// </summary>
    public bool TreatAsCoordinatorPath { get; set; }

    /// <summary>
    /// Telemetry profile reader supplying class-pressure data.
    /// Set before calling <see cref="ScheduleProgram(IrProgram)"/> to enable profile guidance.
    /// </summary>
    public TelemetryProfileReader? ProfileReader { get; set; }

    /// <summary>
    /// Threshold above which a class-pressure ratio triggers a scheduling penalty.
    /// Default: 0.15 (15 % reject rate).
    /// </summary>
    private const double ClassPressureThreshold = 0.15;

    /// <summary>
    /// Threshold above which a decoder fallback ratio triggers a scheduling penalty.
    /// Default: 0.20 (20 % fallback rate). Phase 06 §6.5.
    /// </summary>
    private const double DecoderFallbackThreshold = 0.20;
    private const double PinnedLaneHazardThreshold = 0.02;
    private const double ControlFlowHazardThreshold = 0.02;
    private const int HotLoopIterationThreshold = 4;
    private const double LoopOverallVarianceThreshold = 0.25;
    private const double LoopClassVarianceThreshold = 0.20;
    private const double LoopTemplateReuseThreshold = 0.60;
    private const double BankClusteringSignalThreshold = 0.10;
    private const double BankPressureSignalThreshold = 0.20;
    private const double SevereBankPressureSignalThreshold = 0.35;
    private const double VtAwareBackendPressureThreshold = 0.20;
    private const double SevereVtAwareBackendPressureThreshold = 0.40;

    private int GetBankAwareHintPenalty(IrInstruction instruction, IReadOnlyList<IrInstruction> scheduledCycleInstructions)
    {
        if (!HasUsableProfile ||
            !UseLoadClusteringAvoidance ||
            _currentBlockContext.BankPressureSignal <= BankPressureSignalThreshold ||
            !IsMemoryHeavyInstruction(instruction))
        {
            return 0;
        }

        int scheduledMemoryCount = CountMemoryHeavyInstructions(scheduledCycleInstructions);
        if (scheduledMemoryCount == 0)
        {
            return scheduledCycleInstructions.Count == 0 ? 1 : 0;
        }

        return _currentBlockContext.BankPressureSignal >= SevereBankPressureSignalThreshold && scheduledMemoryCount > 1
            ? 2
            : 1;
    }

    /// <summary>
    /// Returns a non-negative penalty for scheduling an instruction of the given
    /// <see cref="IrSlotClass"/> in the current cycle when the profile shows that class
    /// is under high runtime pressure (reject rate above <see cref="ClassPressureThreshold"/>).
    /// <para>
    /// A positive penalty causes <see cref="CompareCycleCandidatePriorities"/> to defer
    /// the instruction to a later cycle, effectively spreading operations of the same
    /// class across more cycles and reducing runtime contention.
    /// </para>
    /// </summary>
    /// <param name="slotClass">The slot class to query.</param>
    /// <returns>Penalty value (0 = no penalty).</returns>
    public int GetClassPressurePenalty(IrSlotClass slotClass)
    {
        if (!HasUsableProfile)
            return 0;

        double pressure = ProfileReader!.GetClassPressure(slotClass);
        if (pressure <= ClassPressureThreshold)
            return 0;

        // Scale penalty: light (1) for moderate pressure, heavier (2) for severe
        return pressure > 0.30 ? 2 : 1;
    }

    /// <summary>
    /// Phase 06: Returns a non-negative penalty based on decoder fallback ratio from
    /// decoder-modernization telemetry. When the runtime frequently falls back from
    /// cluster-prepared mode to legacy mode, the scheduler should spread scalar operations
    /// across more cycles to reduce contention. Conservative penalties only.
    /// </summary>
    /// <returns>Penalty value (0 = no penalty).</returns>
    public int GetDecoderFallbackPenalty()
    {
        if (!HasUsableDecoderFeedback)
            return 0;

        double fallbackRatio = ProfileReader!.GetDecoderFallbackRatio();
        if (fallbackRatio <= DecoderFallbackThreshold)
            return 0;

        // Conservative penalty: light (1) for moderate fallback, heavier (2) for severe
        return fallbackRatio > 0.40 ? 2 : 1;
    }

    /// <summary>
    /// Returns the class-pressure-aware priority adjustment for a scheduling candidate.
    /// Positive values mean "defer this candidate".
    /// Phase 06: Incorporates decoder-fallback penalty for scalar-only instructions.
    /// </summary>
    internal int GetProfilePenaltyForInstruction(IrInstruction instruction)
    {
        if (!HasUsableProfile)
            return 0;

        IrSlotClass slotClass = instruction.Annotation.RequiredSlotClass;
        int classPenalty = GetClassPressurePenalty(slotClass);

        // Phase 06: Add decoder-fallback penalty for scalar-class instructions
        int decoderPenalty = 0;
        if (slotClass == IrSlotClass.AluClass)
        {
            decoderPenalty = GetDecoderFallbackPenalty();
        }

        return classPenalty + decoderPenalty;
    }

    private bool HasUsableProfile => UseProfileGuidedScheduling && ProfileReader is not null && ProfileReader.HasProfile;
    private bool HasUsableDecoderFeedback => UseProfileGuidedScheduling && ProfileReader?.DecoderCounters is not null;
    private bool HasUsableLoopPhaseTelemetry => HasUsableProfile && UseLoopPhaseTelemetry;

    private int GetCycleClassPressurePenalty(IrInstruction instruction, IReadOnlyList<IrInstruction> scheduledCycleInstructions)
    {
        if (!HasUsableProfile)
        {
            return 0;
        }

        if (!UseClassPressureSmoothingTieBreaks)
        {
            return GetProfilePenaltyForInstruction(instruction);
        }

        return GetClassPressureSmoothingPenalty(instruction, scheduledCycleInstructions);
    }

    private int GetClassPressureSmoothingPenalty(IrInstruction instruction, IReadOnlyList<IrInstruction> scheduledCycleInstructions)
    {
        if (!UseClassPressureSmoothingTieBreaks || scheduledCycleInstructions.Count == 0)
        {
            return 0;
        }

        IrSlotClass slotClass = instruction.Annotation.RequiredSlotClass;
        int sameClassCount = 0;
        for (int index = 0; index < scheduledCycleInstructions.Count; index++)
        {
            if (scheduledCycleInstructions[index].Annotation.RequiredSlotClass == slotClass)
            {
                sameClassCount++;
            }
        }

        if (sameClassCount == 0)
        {
            return 0;
        }

        int smoothingPenalty = GetProfilePenaltyForInstruction(instruction) + sameClassCount - 1;
        return Math.Min(smoothingPenalty, 2);
    }

    private int GetFlexibilityTieBreakScore(IrInstruction instruction)
    {
        if (!UseFlexibilityAwareOrdering)
        {
            return 0;
        }

        int score = instruction.Annotation.BindingKind == IrSlotBindingKind.ClassFlexible ? 1 : 0;

        if (score > 0 && !IsPinnedLaneChokeInstruction(instruction))
        {
            score++;
        }

        if (_currentProgramContext.ReductionAwareShapingEnabled && !_currentProgramContext.TreatAsReductionCoordinator && score > 0)
        {
            score++;
        }

        return score;
    }

    private int GetVtAwareBackendPressureScore(IrInstruction instruction)
    {
        if (!HasUsableProfile ||
            !UseVtAwareBackendPressureTieBreaks ||
            _currentProgramContext.TreatAsCoordinatorPath ||
            _currentProgramContext.BackendResourceShapingPressure <= VtAwareBackendPressureThreshold)
        {
            return 0;
        }

        int score = 0;
        if (instruction.Annotation.BindingKind == IrSlotBindingKind.ClassFlexible)
        {
            score++;
        }

        if (!IsPinnedLaneChokeInstruction(instruction) && instruction.Annotation.ResourceClass != IrResourceClass.System)
        {
            score++;
        }

        if (score == 0)
        {
            return 0;
        }

        return _currentProgramContext.BackendResourceShapingPressure >= SevereVtAwareBackendPressureThreshold && score >= 2
            ? 2
            : 1;
    }

    private int GetLoopPhaseSelectionScore(IrInstruction instruction, IReadOnlyList<IrInstruction> scheduledCycleInstructions)
    {
        if (!_currentBlockContext.HasLoopProfile)
        {
            return 0;
        }

        int score = 0;
        double classVariance = ProfileReader!.GetLoopClassVariance(_currentBlockContext.LoopPcAddress, instruction.Annotation.RequiredSlotClass);

        if (UsePhaseStableLoopShaping)
        {
            if (_currentBlockContext.TemplateReuseRate >= LoopTemplateReuseThreshold && instruction.Annotation.BindingKind != IrSlotBindingKind.HardPinned)
            {
                score++;
            }

            if (classVariance >= LoopClassVarianceThreshold && instruction.Annotation.BindingKind == IrSlotBindingKind.ClassFlexible)
            {
                score++;
            }

            if (scheduledCycleInstructions.Count > 0 && !ContainsSlotClass(scheduledCycleInstructions, instruction.Annotation.RequiredSlotClass))
            {
                score++;
            }
        }

        if (UseHotLoopTelemetryGuidance && _currentBlockContext.IsHotLoop)
        {
            if (!IsMemoryHeavyInstruction(instruction))
            {
                score++;
            }

            if (_currentBlockContext.IterationsSampled >= HotLoopIterationThreshold * 2 && instruction.Annotation.BindingKind != IrSlotBindingKind.HardPinned)
            {
                score++;
            }
        }

        return Math.Min(score, 3);
    }

    private int GetLoopNormalizationPenalty(IrInstruction instruction, IReadOnlyList<IrInstruction> scheduledCycleInstructions)
    {
        if (!_currentBlockContext.HasLoopProfile ||
            !UseLoopBodyNormalization ||
            scheduledCycleInstructions.Count == 0)
        {
            return 0;
        }

        int penalty = 0;
        IrSlotClass slotClass = instruction.Annotation.RequiredSlotClass;
        if (ProfileReader!.GetLoopClassVariance(_currentBlockContext.LoopPcAddress, slotClass) >= LoopClassVarianceThreshold &&
            CountSlotClass(scheduledCycleInstructions, slotClass) > 0)
        {
            penalty++;
        }

        if (_currentBlockContext.OverallClassVariance >= LoopOverallVarianceThreshold &&
            IsPinnedLaneChokeInstruction(instruction) &&
            ContainsPinnedLaneChokeInstruction(scheduledCycleInstructions))
        {
            penalty++;
        }

        return Math.Min(penalty, 2);
    }

    private int GetLoadClusteringPenalty(IrInstruction instruction, IReadOnlyList<IrInstruction> scheduledCycleInstructions)
    {
        if (!HasUsableProfile ||
            !UseLoadClusteringAvoidance ||
            scheduledCycleInstructions.Count == 0 ||
            !IsMemoryHeavyInstruction(instruction) ||
            CountMemoryHeavyInstructions(scheduledCycleInstructions) == 0)
        {
            return 0;
        }

        bool hotClusteringSignal = _currentBlockContext.BankClusteringSignal > BankClusteringSignalThreshold;
        bool hotBankPressureSignal = _currentBlockContext.BankPressureSignal > BankPressureSignalThreshold;
        if (!hotClusteringSignal && !hotBankPressureSignal)
        {
            return 0;
        }

        int penalty = hotClusteringSignal && CountMemoryHeavyInstructions(scheduledCycleInstructions) > 1 ? 2 : 1;
        if (hotClusteringSignal && hotBankPressureSignal)
        {
            penalty = Math.Min(penalty + 1, 2);
        }

        return penalty;
    }

    private bool ShouldStopForLoadClusteringWindow(IReadOnlyList<IrInstruction> scheduledCycleInstructions, IReadOnlyList<IrSchedulingNode> remainingReadyNodes)
    {
        if (!HasUsableProfile ||
            !UseLoadClusteringAvoidance ||
            CountMemoryHeavyInstructions(scheduledCycleInstructions) == 0)
        {
            return false;
        }

        bool hasRemainingMemoryCandidate = false;
        bool hasRemainingNonMemoryCandidate = false;
        for (int index = 0; index < remainingReadyNodes.Count; index++)
        {
            if (IsMemoryHeavyInstruction(remainingReadyNodes[index].Instruction))
            {
                hasRemainingMemoryCandidate = true;
            }
            else
            {
                hasRemainingNonMemoryCandidate = true;
            }
        }

        if (!hasRemainingMemoryCandidate)
        {
            return false;
        }

        if (_currentBlockContext.BankPressureSignal >= SevereBankPressureSignalThreshold &&
            CountMemoryHeavyInstructions(scheduledCycleInstructions) < scheduledCycleInstructions.Count &&
            !hasRemainingNonMemoryCandidate)
        {
            return true;
        }

        return _currentBlockContext.BankClusteringSignal > BankClusteringSignalThreshold &&
               scheduledCycleInstructions.Count >= 2;
    }

    private int GetPinnedLaneChokePenalty(IrInstruction instruction, IReadOnlyList<IrInstruction> scheduledCycleInstructions)
    {
        if (!HasUsableProfile || !UsePinnedLaneChokeAvoidance || scheduledCycleInstructions.Count != 0)
        {
            return 0;
        }

        if (!_previousScheduledCycleWasPinnedLaneChoke || !IsPinnedLaneChokeInstruction(instruction))
        {
            return 0;
        }

        int penalty = 0;
        if (ProfileReader!.GetHazardEffectRate(IrHazardEffectKind.PinnedLane) > PinnedLaneHazardThreshold)
        {
            penalty++;
        }

        if (ProfileReader.GetHazardEffectRate(IrHazardEffectKind.ControlFlow) > ControlFlowHazardThreshold ||
            ProfileReader.GetHazardEffectRate(IrHazardEffectKind.SystemBarrier) > ControlFlowHazardThreshold)
        {
            penalty++;
        }

        return penalty;
    }

    private static bool IsPinnedLaneChokeInstruction(IrInstruction instruction)
    {
        return instruction.Annotation.BindingKind == IrSlotBindingKind.HardPinned ||
               instruction.Annotation.ControlFlowKind != IrControlFlowKind.None ||
               instruction.Annotation.ResourceClass == IrResourceClass.System ||
               instruction.Annotation.IsBarrierLike;
    }

    private static bool ContainsSlotClass(IReadOnlyList<IrInstruction> scheduledCycleInstructions, IrSlotClass slotClass)
    {
        for (int index = 0; index < scheduledCycleInstructions.Count; index++)
        {
            if (scheduledCycleInstructions[index].Annotation.RequiredSlotClass == slotClass)
            {
                return true;
            }
        }

        return false;
    }

    private static int CountSlotClass(IReadOnlyList<IrInstruction> scheduledCycleInstructions, IrSlotClass slotClass)
    {
        int count = 0;
        for (int index = 0; index < scheduledCycleInstructions.Count; index++)
        {
            if (scheduledCycleInstructions[index].Annotation.RequiredSlotClass == slotClass)
            {
                count++;
            }
        }

        return count;
    }

    private static bool ContainsPinnedLaneChokeInstruction(IReadOnlyList<IrInstruction> scheduledCycleInstructions)
    {
        for (int index = 0; index < scheduledCycleInstructions.Count; index++)
        {
            if (IsPinnedLaneChokeInstruction(scheduledCycleInstructions[index]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMemoryHeavyInstruction(IrInstruction instruction)
    {
        return instruction.Annotation.RequiredSlotClass == IrSlotClass.LsuClass ||
               instruction.Annotation.MemoryReadRegion is not null ||
               instruction.Annotation.MemoryWriteRegion is not null;
    }

    private static int CountMemoryHeavyInstructions(IReadOnlyList<IrInstruction> scheduledCycleInstructions)
    {
        int count = 0;
        for (int index = 0; index < scheduledCycleInstructions.Count; index++)
        {
            if (IsMemoryHeavyInstruction(scheduledCycleInstructions[index]))
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsPinnedLaneChokeCycle(IReadOnlyList<IrSchedulingNode> scheduledThisCycle)
    {
        return scheduledThisCycle.Count == 1 && IsPinnedLaneChokeInstruction(scheduledThisCycle[0].Instruction);
    }
}
