using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;

namespace MinimalAsmApp.Examples.Arithmetic;

using static CpuInstructions;
using Instruction = YAKSys_Hybrid_CPU.Processor.CPU_Core.InstructionsEnum;

public sealed class ImmediateLogicExample : ICpuExample
{
    public string Name => "immediate-logic";

    public string Description => "Runs ANDI, ORI, XORI instructions and checks results.";

    public string Category => "02_Arithmetic";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            AddImmediate(1, 0, 0b1010),
            BinaryImmediate(Instruction.ANDI, 2, 1, 0b1100),
            BinaryImmediate(Instruction.ORI, 3, 1, 0b0100),
            BinaryImmediate(Instruction.XORI, 4, 1, 0b1111)
        );

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions { RegisterDump = [1, 2, 3, 4] });

        execution.ExpectRegister(2, 0b1000); // 1010 & 1100 = 1000
        execution.ExpectRegister(3, 0b1110); // 1010 | 0100 = 1110
        execution.ExpectRegister(4, 0b0101); // 1010 ^ 1111 = 0101

        return CpuExampleResult.Ok(
            "Expected correct logical application.",
            execution.Registers);
    }
}
