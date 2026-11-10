using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;

namespace MinimalAsmApp.Examples.Arithmetic;

using static CpuInstructions;
using Instruction = YAKSys_Hybrid_CPU.Processor.CPU_Core.InstructionsEnum;

public sealed class RegisterShiftExample : ICpuExample
{
    public string Name => "register-shift";

    public string Description => "Runs SLL, SRL, SRA, SLLW, SRLW, SRAW instructions.";

    public string Category => "02_Arithmetic";

    public CpuExampleResult Run()
    {
        unchecked
        {
            VLIW_Instruction[] program = WithFences(
                AddImmediate(1, 0, (short)0b1111111110101100),
                AddImmediate(2, 0, 2),
                Binary(Instruction.SLL, 3, 1, 2),
                Binary(Instruction.SRL, 4, 1, 2),
                Binary(Instruction.SRA, 5, 1, 2),
                Binary(Instruction.SLLW, 6, 1, 2),
                Binary(Instruction.SRLW, 7, 1, 2),
                Binary(Instruction.SRAW, 8, 1, 2)
            );

            CpuProgramExecution execution = CpuProgramExecutor.Run(
                program,
                new CpuProgramRunOptions { RegisterDump = [3, 4, 5, 6, 7, 8] });

            return CpuExampleResult.Ok(
                "Expected register-based shifts evaluated properly.",
                execution.Registers);
        }
    }
}
