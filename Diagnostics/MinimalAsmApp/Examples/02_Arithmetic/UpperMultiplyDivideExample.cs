using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;

namespace MinimalAsmApp.Examples.Arithmetic;

using static CpuInstructions;
using Instruction = YAKSys_Hybrid_CPU.Processor.CPU_Core.InstructionsEnum;

public sealed class UpperMultiplyDivideExample : ICpuExample
{
    public string Name => "upper-multiply-div";

    public string Description => "Runs MULH, MULHU, MULHSU, DIVU, REMU instructions.";

    public string Category => "02_Arithmetic";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            AddImmediate(1, 0, unchecked((short)0xFFFF)),
            AddImmediate(2, 0, 0x000F),
            Binary(Instruction.MULH, 3, 1, 2),
            Binary(Instruction.MULHU, 4, 1, 2),
            Binary(Instruction.MULHSU, 5, 1, 2),
            Binary(Instruction.DIVU, 6, 1, 2),
            Binary(Instruction.REMU, 7, 1, 2)
        );

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions { RegisterDump = [3, 4, 5, 6, 7] });

        return CpuExampleResult.Ok(
            "Expected successful execution of high-multiply and unsigned div/rem.",
            execution.Registers);
    }
}
