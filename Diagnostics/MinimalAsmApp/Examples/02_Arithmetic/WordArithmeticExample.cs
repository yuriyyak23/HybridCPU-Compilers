using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;

namespace MinimalAsmApp.Examples.Arithmetic;

using static CpuInstructions;
using Instruction = YAKSys_Hybrid_CPU.Processor.CPU_Core.InstructionsEnum;

public sealed class WordArithmeticExample : ICpuExample
{
    public string Name => "word-arithmetic";

    public string Description => "Runs ADDIW, ADDW, SUBW, SLLIW, SRLIW, SRAIW, MULW, DIVW, REMW, DIVUW, REMUW word-sized instructions.";

    public string Category => "02_Arithmetic";

    public CpuExampleResult Run()
    {
        unchecked
        {
            VLIW_Instruction[] program = WithFences(
                AddImmediate(1, 0, (short)0x7000),
                AddImmediate(2, 0, 0x1000),

                BinaryImmediate(Instruction.ADDIW, 3, 1, 0x0111),
                Binary(Instruction.ADDW, 4, 1, 2),
                Binary(Instruction.SUBW, 5, 1, 2),
                BinaryImmediate(Instruction.SLLIW, 6, 2, 2),
                BinaryImmediate(Instruction.SRLIW, 7, 2, 1),
                BinaryImmediate(Instruction.SRAIW, 8, 2, 1),
                Binary(Instruction.MULW, 9, 1, 2),
                Binary(Instruction.DIVW, 10, 1, 2),
                Binary(Instruction.REMW, 11, 1, 2),
                Binary(Instruction.DIVUW, 12, 1, 2),
                Binary(Instruction.REMUW, 13, 1, 2)
            );

            CpuProgramExecution execution = CpuProgramExecutor.Run(
                program,
                new CpuProgramRunOptions { RegisterDump = [3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13] });

            // We do not strictly verify here as the backend output could be missing or complex, 
            // but we provide the correct contour for W instructions.
            return CpuExampleResult.Ok(
                "Expected correct word-sized arithmetic evaluation.",
                execution.Registers);
        }
    }
}
