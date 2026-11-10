using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;

namespace MinimalAsmApp.Examples.Vector;

using static CpuInstructions;
using Instruction = YAKSys_Hybrid_CPU.Processor.CPU_Core.InstructionsEnum;

public sealed class VectorMaskLogicExample : ICpuExample
{
    public string Name => "vector-mask-logic";

    public string Description => "Runs VMAND, VMOR, VMXOR, VMNOT, VPOPC, VMSBF instructions.";

    public string Category => "10_Vector";

    public CpuExampleResult Run()
    {
        ulong src1 = 0x1000;
        ulong src2 = 0x2000;
        ulong dest = 0x3000;
        ulong elements = 4;

        VLIW_Instruction[] program = WithFences(
            Vector1D(Instruction.VMAND, src1, src2, elements),
            Vector1D(Instruction.VMOR, src1, src2, elements),
            Vector1D(Instruction.VMXOR, src1, src2, elements),
            Vector1D(Instruction.VMNOT, src1, src2, elements),
            Vector1D(Instruction.VPOPC, src1, src2, elements),
            Vector1D(Instruction.VMSBF, src1, src2, elements)
        );

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions());

        return CpuExampleResult.Ok(
            "Expected correct vector mask combinations and population counts.",
            execution.Registers);
    }
}
