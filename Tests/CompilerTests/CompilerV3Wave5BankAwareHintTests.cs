using System.Collections.Generic;
using System.Linq;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU.Compiler.Core.IR.Telemetry;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Diagnostics;
using static YAKSys_Hybrid_CPU.Processor;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests.CompilerTests;

/// <summary>
/// Focused tests for Compiler V3 Wave 5 selective bank-aware hints.
/// Validates advisory bank interpretation, same-bank clustering reduction,
/// bank-policy non-leakage, and telemetry-absent fallback.
/// </summary>
public class CompilerV3Wave5BankAwareHintTests
{
    [Fact]
    public void WhenBankTelemetryPresentThenAdvisorySignalIsNonNegative()
    {
        TelemetryProfileReader reader = CreateBankAwareReader();

        double bankPressureSignal = reader.GetAdvisoryBankPressureSignal();
        double clusteringSignal = reader.GetAdvisoryMemoryClusteringSignal();

        Assert.True(bankPressureSignal >= 0.0);
        Assert.True(clusteringSignal >= 0.0);
    }

    [Fact]
    public void WhenBankTelemetryAbsentThenAllBankSignalsFallBackToZero()
    {
        TelemetryProfileReader empty = TelemetryProfileReader.CreateEmpty();

        Assert.Equal(0.0, empty.GetAdvisoryBankPressureSignal());
        Assert.Equal(0.0, empty.GetAdvisoryMemoryClusteringSignal());
        Assert.Equal(0.0, empty.GetBankConflictStallRate());
        Assert.Equal(0.0, empty.GetMemoryClusteringEventRate());
        Assert.Equal(0.0, empty.GetPeakBankPendingRejectPressure());
    }

    [Fact]
    public void WhenSameBankClusteringHighThenSchedulerSpreadsMemoryOps()
    {
        IrProgram program = BuildProgram(
            CreateInstruction(InstructionsEnum.LD, destSrc1: 0x40, src2: 0x1000),
            CreateInstruction(InstructionsEnum.LD, destSrc1: 0x50, src2: 0x2000),
            CreateStealableInstruction(InstructionsEnum.ADDI, immediate: 1, destSrc1: 0x60),
            CreateStealableInstruction(InstructionsEnum.ADDI, immediate: 2, destSrc1: 0x70));

        TelemetryProfileReader reader = CreateHighBankClusteringReader();

        HybridCpuLocalListScheduler withHints = new HybridCpuLocalListScheduler
        {
            UseProfileGuidedScheduling = true,
            ProfileReader = reader,
            UseLoadClusteringAvoidance = true
        };

        HybridCpuLocalListScheduler withoutHints = new HybridCpuLocalListScheduler
        {
            UseProfileGuidedScheduling = true,
            ProfileReader = reader,
            UseLoadClusteringAvoidance = false
        };

        IrBasicBlockSchedule scheduleWith = Assert.Single(withHints.ScheduleProgram(program).BlockSchedules);
        IrBasicBlockSchedule scheduleWithout = Assert.Single(withoutHints.ScheduleProgram(program).BlockSchedules);

        Assert.Equal(4, scheduleWith.ScheduledInstructions.Count);
        Assert.Equal(4, scheduleWithout.ScheduledInstructions.Count);

        Assert.True(scheduleWith.CycleGroups.Count >= scheduleWithout.CycleGroups.Count,
            "Bank-aware clustering avoidance should spread memory ops across at least as many cycles.");
    }

    [Fact]
    public void WhenBankAwareHintsDisabledThenScheduleMatchesBaseline()
    {
        IrProgram program = BuildProgram(
            CreateInstruction(InstructionsEnum.LD, destSrc1: 0x40, src2: 0x1000),
            CreateStealableInstruction(InstructionsEnum.ADDI, immediate: 1, destSrc1: 0x60));

        IrBasicBlockSchedule baseline = Assert.Single(new HybridCpuLocalListScheduler().ScheduleProgram(program).BlockSchedules);

        HybridCpuLocalListScheduler withReader = new HybridCpuLocalListScheduler
        {
            UseProfileGuidedScheduling = true,
            ProfileReader = CreateHighBankClusteringReader(),
            UseLoadClusteringAvoidance = false
        };

        IrBasicBlockSchedule featureOff = Assert.Single(withReader.ScheduleProgram(program).BlockSchedules);

        Assert.Equal(
            baseline.ScheduledInstructions.Select(i => i.InstructionIndex).ToArray(),
            featureOff.ScheduledInstructions.Select(i => i.InstructionIndex).ToArray());
    }

    [Fact]
    public void WhenSchedulerRunsTwiceWithSameBankProfileThenResultIsDeterministic()
    {
        IrProgram program = BuildProgram(
            CreateInstruction(InstructionsEnum.LD, destSrc1: 0x40, src2: 0x1000),
            CreateInstruction(InstructionsEnum.LD, destSrc1: 0x50, src2: 0x2000),
            CreateStealableInstruction(InstructionsEnum.ADDI, immediate: 1, destSrc1: 0x60),
            CreateStealableInstruction(InstructionsEnum.ADDI, immediate: 2, destSrc1: 0x70));

        HybridCpuLocalListScheduler scheduler = new HybridCpuLocalListScheduler
        {
            UseProfileGuidedScheduling = true,
            ProfileReader = CreateHighBankClusteringReader(),
            UseLoadClusteringAvoidance = true
        };

        IrBasicBlockSchedule first = Assert.Single(scheduler.ScheduleProgram(program).BlockSchedules);
        IrBasicBlockSchedule second = Assert.Single(scheduler.ScheduleProgram(program).BlockSchedules);

        Assert.Equal(
            first.ScheduledInstructions.Select(i => i.InstructionIndex).ToArray(),
            second.ScheduledInstructions.Select(i => i.InstructionIndex).ToArray());
    }

    [Fact]
    public void WhenBankPolicyEncodingAttemptedThenCompilerArtifactsStayBankAgnostic()
    {
        _ = new Processor(ProcessorMode.Compiler);
        IrProgram program = BuildProgram(
            CreateInstruction(InstructionsEnum.LD, destSrc1: 0x40, src2: 0x1000),
            CreateStealableInstruction(InstructionsEnum.ADDI, immediate: 1, destSrc1: 0x60));

        HybridCpuLocalListScheduler scheduler = new HybridCpuLocalListScheduler
        {
            UseProfileGuidedScheduling = true,
            ProfileReader = CreateHighBankClusteringReader(),
            UseLoadClusteringAvoidance = true
        };

        IrProgramSchedule schedule = scheduler.ScheduleProgram(program);

        HybridCpuBundleFormer bundleFormer = new HybridCpuBundleFormer(useClassFirstBinding: true);
        IrProgramBundlingResult bundling = bundleFormer.BundleProgram(schedule);

        foreach (IrBasicBlockBundlingResult blockResult in bundling.BlockResults)
        {
            foreach (IrMaterializedBundle bundle in blockResult.Bundles)
            {
                foreach (IrMaterializedBundleSlot slot in bundle.Slots)
                {
                    if (slot.Instruction is not null)
                    {
                        Assert.True(
                            slot.Instruction.Annotation.BindingKind is IrSlotBindingKind.ClassFlexible or IrSlotBindingKind.HardPinned or IrSlotBindingKind.SingletonClass,
                            "Compiler should not encode bank ownership or arbitration policy into instruction binding metadata.");
                    }
                }
            }
        }
    }

    private static TelemetryProfileReader CreateBankAwareReader()
    {
        TypedSlotTelemetryProfile profile = new(
            ProgramHash: "wave5-bank-profile",
            TotalInjectionsPerClass: new Dictionary<SlotClass, long>
            {
                [SlotClass.AluClass] = 64,
                [SlotClass.LsuClass] = 48
            },
            TotalRejectsPerClass: new Dictionary<SlotClass, long>
            {
                [SlotClass.AluClass] = 4,
                [SlotClass.LsuClass] = 4
            },
            RejectsByReason: new Dictionary<TypedSlotRejectReason, long>
            {
                [TypedSlotRejectReason.DynamicClassExhaustion] = 4
            },
            AverageNopDensity: 0.08,
            AverageBundleUtilization: 0.90,
            TotalBundlesExecuted: 100,
            TotalNopsExecuted: 18,
            ReplayTemplateHits: 92,
            ReplayTemplateMisses: 18,
            ReplayHitRate: 0.84,
            FairnessStarvationEvents: 0,
            PerVtInjectionCounts: new Dictionary<int, long>
            {
                [0] = 28,
                [1] = 26,
                [2] = 24,
                [3] = 22
            },
            WorkerMetrics: null)
        {
            BankPendingRejectsPerBank = new Dictionary<int, long>
            {
                [0] = 14,
                [1] = 4,
                [2] = 2
            },
            MemoryClusteringEventCount = 8,
            BankConflictStallCycles = 6
        };

        return TelemetryProfileReader.Create(profile);
    }

    private static TelemetryProfileReader CreateHighBankClusteringReader()
    {
        TypedSlotTelemetryProfile profile = new(
            ProgramHash: "wave5-high-bank-profile",
            TotalInjectionsPerClass: new Dictionary<SlotClass, long>
            {
                [SlotClass.AluClass] = 64,
                [SlotClass.LsuClass] = 48
            },
            TotalRejectsPerClass: new Dictionary<SlotClass, long>
            {
                [SlotClass.AluClass] = 4,
                [SlotClass.LsuClass] = 4
            },
            RejectsByReason: new Dictionary<TypedSlotRejectReason, long>
            {
                [TypedSlotRejectReason.DynamicClassExhaustion] = 4
            },
            AverageNopDensity: 0.08,
            AverageBundleUtilization: 0.90,
            TotalBundlesExecuted: 100,
            TotalNopsExecuted: 18,
            ReplayTemplateHits: 92,
            ReplayTemplateMisses: 18,
            ReplayHitRate: 0.84,
            FairnessStarvationEvents: 0,
            PerVtInjectionCounts: new Dictionary<int, long>
            {
                [0] = 28,
                [1] = 26,
                [2] = 24,
                [3] = 22
            },
            WorkerMetrics: null)
        {
            BankPendingRejectsPerBank = new Dictionary<int, long>
            {
                [0] = 30,
                [1] = 8,
                [2] = 2
            },
            MemoryClusteringEventCount = 35,
            BankConflictStallCycles = 28
        };

        return TelemetryProfileReader.Create(profile);
    }

    private static IrProgram BuildProgram(params VLIW_Instruction[] instructions)
    {
        _ = new Processor(ProcessorMode.Compiler);
        HybridCpuIrBuilder builder = new HybridCpuIrBuilder();
        return builder.BuildProgram(0, instructions, bundleAnnotations: LegacyInstructionAnnotationBuilder.Build(instructions));
    }

    private static VLIW_Instruction CreateInstruction(
        InstructionsEnum opcode,
        ulong destSrc1 = 0,
        ulong src2 = 0,
        ushort immediate = 0)
    {
        return new VLIW_Instruction
        {
            OpCode = (uint)opcode,
            DataTypeValue = DataTypeEnum.INT32,
            PredicateMask = 0xFF,
            Immediate = immediate,
            DestSrc1Pointer = destSrc1,
            Src2Pointer = src2,
            StreamLength = InstructionClassifier.GetClass(opcode) == InstructionClass.Memory ? 0u : 1u,
            Stride = InstructionClassifier.GetClass(opcode) == InstructionClass.Memory ? (ushort)0 : (ushort)1,
            VirtualThreadId = 0
        };
    }

    private static VLIW_Instruction CreateStealableInstruction(
        InstructionsEnum opcode,
        ulong destSrc1 = 0,
        ulong src2 = 0,
        ushort immediate = 0)
    {
        VLIW_Instruction instruction = CreateInstruction(opcode, destSrc1, src2, immediate);
        instruction.Word3 |= 1UL << 50;
        return instruction;
    }
}
