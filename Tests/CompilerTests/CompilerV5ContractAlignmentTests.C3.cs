using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Core.IR.Telemetry;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;
using static YAKSys_Hybrid_CPU.Processor;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests.CompilerTests;

public partial class CompilerV5ContractAlignmentTests
{
    #region C3 — Structural Predicates in Scheduling Heuristics

    [Fact]
    public void WhenFlexibilityAwareOrderingEnabledThenScheduleUsesBindingKindNotStealability()
    {
        // C3 contract: scheduling heuristics use BindingKind / IsPinnedLaneChokeInstruction,
        // not CanBeStolenCandidate. Two programs identical except CanBeStolen bit
        // must produce the same schedule under flexibility-aware ordering.
        IrProgram programWithSteal = BuildProgram(
            CreateStealableInstruction(InstructionsEnum.ADDI, destSrc1: Pack(4, 5, 0), src2: 1),
            CreateScalarAluInstruction(destReg: 6, srcReg: 7),
            CreateLoadInstruction(destinationRegister: 8, address: 0x1000));

        IrProgram programWithoutSteal = BuildProgram(
            CreateScalarAluInstruction(destReg: 4, srcReg: 5),
            CreateScalarAluInstruction(destReg: 6, srcReg: 7),
            CreateLoadInstruction(destinationRegister: 8, address: 0x1000));

        HybridCpuLocalListScheduler scheduler1 = new HybridCpuLocalListScheduler { UseFlexibilityAwareOrdering = true };
        HybridCpuLocalListScheduler scheduler2 = new HybridCpuLocalListScheduler { UseFlexibilityAwareOrdering = true };

        IrBasicBlockSchedule schedule1 = Assert.Single(scheduler1.ScheduleProgram(programWithSteal).BlockSchedules);
        IrBasicBlockSchedule schedule2 = Assert.Single(scheduler2.ScheduleProgram(programWithoutSteal).BlockSchedules);

        Assert.Equal(
            schedule1.ScheduledInstructions.Select(i => i.Instruction.Annotation.ResourceClass).ToArray(),
            schedule2.ScheduledInstructions.Select(i => i.Instruction.Annotation.ResourceClass).ToArray());
    }

    [Fact]
    public void WhenFlexibilityAwareOrderingDisabledThenNoFlexibilityInfluenceOnSchedule()
    {
        // Baseline: with UseFlexibilityAwareOrdering=false, the scheduler does not
        // consult flexibility metadata at all.
        IrProgram program = BuildProgram(
            CreateScalarAluInstruction(destReg: 4, srcReg: 5),
            CreateScalarAluInstruction(destReg: 6, srcReg: 7),
            CreateLoadInstruction(destinationRegister: 8, address: 0x1000));

        HybridCpuLocalListScheduler baselineScheduler = new HybridCpuLocalListScheduler();
        HybridCpuLocalListScheduler flexDisabledScheduler = new HybridCpuLocalListScheduler { UseFlexibilityAwareOrdering = false };

        IrBasicBlockSchedule baseline = Assert.Single(baselineScheduler.ScheduleProgram(program).BlockSchedules);
        IrBasicBlockSchedule flexDisabled = Assert.Single(flexDisabledScheduler.ScheduleProgram(program).BlockSchedules);

        Assert.Equal(
            baseline.ScheduledInstructions.Select(i => i.InstructionIndex).ToArray(),
            flexDisabled.ScheduledInstructions.Select(i => i.InstructionIndex).ToArray());
    }

    [Fact]
    public void WhenMixedFlexibleAndPinnedInstructionsThenFlexibilityAwareOrderingIsDeterministic()
    {
        // C3 contract: running the same program twice with flexibility-aware ordering
        // produces identical results.
        IrProgram program = BuildProgram(
            CreateScalarAluInstruction(destReg: 4, srcReg: 5),
            CreateLoadInstruction(destinationRegister: 8, address: 0x1000),
            CreateBranchInstruction(0x0));

        HybridCpuLocalListScheduler scheduler = new HybridCpuLocalListScheduler { UseFlexibilityAwareOrdering = true };

        IrBasicBlockSchedule first = Assert.Single(scheduler.ScheduleProgram(program).BlockSchedules);
        IrBasicBlockSchedule second = Assert.Single(scheduler.ScheduleProgram(program).BlockSchedules);

        Assert.Equal(
            first.ScheduledInstructions.Select(i => i.InstructionIndex).ToArray(),
            second.ScheduledInstructions.Select(i => i.InstructionIndex).ToArray());
    }

    [Fact]
    public void WhenFlexibilityAwareOrderingEnabledThenClassFlexibleInstructionGetsHigherScoreThanPinnedBranch()
    {
        IrProgram program = BuildProgram(
            CreateScalarAluInstruction(destReg: 4, srcReg: 5),
            CreateBranchInstruction(0x0));

        IrInstruction aluInstruction = program.Instructions
            .Single(instruction => instruction.Annotation.BindingKind == IrSlotBindingKind.ClassFlexible);
        IrInstruction branchInstruction = program.Instructions
            .Single(instruction => instruction.Annotation.BindingKind == IrSlotBindingKind.HardPinned);

        int aluScore = GetFlexibilityTieBreakScore(aluInstruction, useFlexibilityAwareOrdering: true);
        int branchScore = GetFlexibilityTieBreakScore(branchInstruction, useFlexibilityAwareOrdering: true);

        Assert.True(aluScore > branchScore,
            "UseFlexibilityAwareOrdering must prefer structural flexibility (ClassFlexible, non-pinned-lane) over pinned branch placement.");
    }

    [Fact]
    public void WhenFlexibilityAwareOrderingDisabledThenStructuralFlexibilityScoreIsZero()
    {
        IrInstruction instruction = Assert.Single(BuildProgram(
            CreateScalarAluInstruction(destReg: 4, srcReg: 5)).Instructions);

        int score = GetFlexibilityTieBreakScore(instruction, useFlexibilityAwareOrdering: false);

        Assert.Equal(0, score);
    }

    [Fact]
    public void WhenInstructionsScheduledThenAnnotationsCarryBindingKindNotStealability()
    {
        // After scheduling, every instruction annotation must have a valid BindingKind.
        // CanBeStolenCandidate is advisory and must not affect structural annotation.
        IrProgram program = BuildProgram(
            CreateScalarAluInstruction(destReg: 4, srcReg: 5),
            CreateLoadInstruction(destinationRegister: 8, address: 0x1000));

        HybridCpuLocalListScheduler scheduler = new HybridCpuLocalListScheduler();
        IrBasicBlockSchedule schedule = Assert.Single(scheduler.ScheduleProgram(program).BlockSchedules);

        foreach (IrScheduledInstruction scheduled in schedule.ScheduledInstructions)
        {
            Assert.True(
                scheduled.Instruction.Annotation.BindingKind is IrSlotBindingKind.ClassFlexible
                    or IrSlotBindingKind.HardPinned
                    or IrSlotBindingKind.SingletonClass,
                $"Instruction {scheduled.InstructionIndex} must have a valid structural BindingKind.");
        }
    }

    [Fact]
    public void WhenBranchInstructionPresentThenAnnotationHasHardPinnedBindingKind()
    {
        // C3 contract: branch instructions are pinned-lane-choke (ControlFlowKind != None)
        // and must receive HardPinned binding kind — structural predicate, not stealability.
        IrProgram program = BuildProgram(
            CreateScalarAluInstruction(destReg: 4, srcReg: 5),
            CreateBranchInstruction(0x0));

        HybridCpuLocalListScheduler scheduler = new HybridCpuLocalListScheduler();
        IrBasicBlockSchedule schedule = Assert.Single(scheduler.ScheduleProgram(program).BlockSchedules);

        IrScheduledInstruction branchInstruction = schedule.ScheduledInstructions
            .Single(s => s.Instruction.Annotation.ControlFlowKind != IrControlFlowKind.None);

        Assert.Equal(IrSlotBindingKind.HardPinned, branchInstruction.Instruction.Annotation.BindingKind);
    }

    [Fact]
    public void WhenFlexibleAluAndPinnedBranchScheduledThenAluIsClassFlexible()
    {
        // C3 contract: ALU instructions get ClassFlexible binding kind — they have lane flexibility
        // and are NOT constrained by stealability metadata.
        IrProgram program = BuildProgram(
            CreateScalarAluInstruction(destReg: 4, srcReg: 5),
            CreateBranchInstruction(0x0));

        HybridCpuLocalListScheduler scheduler = new HybridCpuLocalListScheduler();
        IrBasicBlockSchedule schedule = Assert.Single(scheduler.ScheduleProgram(program).BlockSchedules);

        IrScheduledInstruction aluInstruction = schedule.ScheduledInstructions
            .Single(s => s.Instruction.Annotation.ControlFlowKind == IrControlFlowKind.None);

        Assert.Equal(IrSlotBindingKind.ClassFlexible, aluInstruction.Instruction.Annotation.BindingKind);
    }

    private static int GetFlexibilityTieBreakScore(IrInstruction instruction, bool useFlexibilityAwareOrdering)
    {
        HybridCpuLocalListScheduler scheduler = new HybridCpuLocalListScheduler { UseFlexibilityAwareOrdering = useFlexibilityAwareOrdering };
        MethodInfo method = typeof(HybridCpuLocalListScheduler).GetMethod(
            "GetFlexibilityTieBreakScore",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("HybridCpuLocalListScheduler.GetFlexibilityTieBreakScore was not found.");

        object? result = method.Invoke(scheduler, [instruction]);
        return Assert.IsType<int>(result);
    }

    #endregion
}
