using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;

namespace MinimalAsmApp.Examples.Vector;

using static CpuInstructions;
using Instruction = YAKSys_Hybrid_CPU.Processor.CPU_Core.InstructionsEnum;

public sealed class VectorShiftExample : ICpuExample
{
    public string Name => "vector-shift";

    public string Description => "Runs VSLL, VSRL, VSRA instructions.";

    public string Category => "10_Vector";

    public CpuExampleResult Run()
    {
        ulong src1 = 0x1000;
        ulong src2 = 0x2000;
        ulong dest = 0x3000;
        ulong elements = 4;

        VLIW_Instruction[] program = WithFences(
            Vector1D(Instruction.VSLL, dest, src2, elements),
            Vector1D(Instruction.VSRL, dest, src2, elements),
            Vector1D(Instruction.VSRA, dest, src2, elements)
        );

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions());

        return CpuExampleResult.Ok(
            "Expected correct vector shifts over ranges.",
            execution.Registers);
    }
}
