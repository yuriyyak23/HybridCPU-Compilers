using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Runtime;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using static YAKSys_Hybrid_CPU.Processor;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU_ISE.Tests.CompilerTests;

public class CompilerV4LsuTruthTests
{
    [Fact]
    public void WhenIndependentLoadAndStoreShareCandidateThenLegalityCheckerKeepsBundleLegal()
    {
        HybridCpuInstructionLegalityChecker checker = new();
        IrProgram program = BuildProgram(
            CreateLoadInstruction(destinationRegister: 4, address: 0x1000),
            CreateStoreInstruction(sourceRegister: 8, address: 0x1180));

        IrCandidateBundleAnalysis analysis = checker.AnalyzeCandidateBundle(program.Instructions);

        Assert.DoesNotContain(analysis.Legality.Hazards, hazard => hazard.Reason == IrHazardReason.ExclusiveCycleRequired);
        Assert.DoesNotContain(analysis.Legality.Hazards, hazard => hazard.Reason == IrHazardReason.StructuralResourceConflict);
        Assert.Empty(analysis.StructuralAnalysis.ConflictingUsages);
    }

    [Fact]
    public void WhenTwoIndependentLoadsShareCandidateThenCompilerHonorsTwoLaneLsuCapacity()
    {
        HybridCpuInstructionLegalityChecker checker = new();
        IrProgram program = BuildProgram(
            CreateLoadInstruction(destinationRegister: 4, address: 0x1000),
            CreateLoadInstruction(destinationRegister: 5, address: 0x1080));

        IrCandidateBundleAnalysis analysis = checker.AnalyzeCandidateBundle(program.Instructions);

        Assert.NotNull(analysis.ClassCapacityResult);
        Assert.Equal(2, analysis.ClassCapacityResult!.LsuCapacity);
        Assert.Empty(analysis.StructuralAnalysis.ConflictingUsages);
        Assert.DoesNotContain(analysis.Legality.Hazards, hazard => hazard.Reason == IrHazardReason.ExclusiveCycleRequired);
        Assert.DoesNotContain(analysis.Legality.Hazards, hazard => hazard.Reason == IrHazardReason.StructuralResourceConflict);
    }

    private static IrProgram BuildProgram(params VLIW_Instruction[] instructions)
    {
        _ = new Processor(ProcessorMode.Compiler);
        HybridCpuIrBuilder builder = new();
        return builder.BuildProgram(0, instructions, bundleAnnotations: LegacyInstructionAnnotationBuilder.Build(instructions));
    }

    private static VLIW_Instruction CreateLoadInstruction(byte destinationRegister, ulong address)
    {
        return new VLIW_Instruction
        {
            OpCode = (uint)InstructionsEnum.LD,
            DataTypeValue = DataTypeEnum.INT32,
            PredicateMask = 0xFF,
            DestSrc1Pointer = VLIW_Instruction.PackArchRegs(
                destinationRegister,
                1,
                VLIW_Instruction.NoArchReg),
            Src2Pointer = address,
            StreamLength = 0,
            Stride = 0,
            VirtualThreadId = 0
        };
    }

    private static VLIW_Instruction CreateStoreInstruction(byte sourceRegister, ulong address)
    {
        return new VLIW_Instruction
        {
            OpCode = (uint)InstructionsEnum.SD,
            DataTypeValue = DataTypeEnum.INT32,
            PredicateMask = 0xFF,
            DestSrc1Pointer = VLIW_Instruction.PackArchRegs(
                VLIW_Instruction.NoArchReg,
                1,
                sourceRegister),
            Src2Pointer = address,
            StreamLength = 0,
            Stride = 0,
            VirtualThreadId = 0
        };
    }
}
