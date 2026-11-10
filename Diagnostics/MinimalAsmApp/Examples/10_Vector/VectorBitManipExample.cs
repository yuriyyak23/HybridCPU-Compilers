using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;

namespace MinimalAsmApp.Examples.Vector;

using static CpuInstructions;
using Instruction = YAKSys_Hybrid_CPU.Processor.CPU_Core.InstructionsEnum;

public sealed class VectorBitManipExample : ICpuExample
{
    public string Name => "vector-bitmanip";

    public string Description => "Runs VREVERSE, VPOPCNT, VCLZ, VCTZ, VBREV8 instructions.";

    public string Category => "10_Vector";

    public CpuExampleResult Run()
    {
        ulong src1 = 0x1000;
        ulong src2 = 0x2000;
        ulong elements = 4;

        VLIW_Instruction[] program = WithFences(
            Vector1D(Instruction.VREVERSE, src1, src2, elements),
            Vector1D(Instruction.VPOPCNT, src1, src2, elements),
            Vector1D(Instruction.VCLZ, src1, src2, elements),
            Vector1D(Instruction.VCTZ, src1, src2, elements),
            Vector1D(Instruction.VBREV8, src1, src2, elements)
        );

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions());

        return CpuExampleResult.Ok(
            "Expected correct vector bit manipulation over ranges.",
            execution.Registers);
    }
}
