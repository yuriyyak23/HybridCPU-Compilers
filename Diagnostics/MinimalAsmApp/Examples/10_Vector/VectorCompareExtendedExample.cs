using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;

namespace MinimalAsmApp.Examples.Vector;

using static CpuInstructions;
using Instruction = YAKSys_Hybrid_CPU.Processor.CPU_Core.InstructionsEnum;

public sealed class VectorCompareExtendedExample : ICpuExample
{
    public string Name => "vector-compare-extended";

    public string Description => "Runs VCMPEQ, VCMPNE, VCMPLT, VCMPLE, VCMPGE instructions.";

    public string Category => "10_Vector";

    public CpuExampleResult Run()
    {
        ulong src1 = 0x1000;
        ulong src2 = 0x2000;
        ulong dest = 0x3000;
        ulong elements = 4;

        VLIW_Instruction[] program = WithFences(
            Vector1D(Instruction.VCMPEQ, src1, src2, elements),
            Vector1D(Instruction.VCMPNE, src1, src2, elements),
            Vector1D(Instruction.VCMPLT, src1, src2, elements),
            Vector1D(Instruction.VCMPLE, src1, src2, elements),
            Vector1D(Instruction.VCMPGE, src1, src2, elements)
        );

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions());

        return CpuExampleResult.Ok(
            "Expected vector compares executed correctly producing mask/register results.",
            execution.Registers);
    }
}
