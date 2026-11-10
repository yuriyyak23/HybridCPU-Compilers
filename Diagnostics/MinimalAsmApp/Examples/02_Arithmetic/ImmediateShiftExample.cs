using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;

namespace MinimalAsmApp.Examples.Arithmetic;

using static CpuInstructions;
using Instruction = YAKSys_Hybrid_CPU.Processor.CPU_Core.InstructionsEnum;

public sealed class ImmediateShiftExample : ICpuExample
{
    public string Name => "immediate-shift";

    public string Description => "Runs SLLI, SRLI, SRAI instructions and checks results.";

    public string Category => "02_Arithmetic";

    public CpuExampleResult Run()
    {
        unchecked
        {
            VLIW_Instruction[] program = WithFences(
                AddImmediate(1, 0, 0b0000000001010100),
                AddImmediate(2, 0, (short)0b1111111110101100),
                BinaryImmediate(Instruction.SLLI, 3, 1, 2),
                BinaryImmediate(Instruction.SRLI, 4, 1, 2),
                BinaryImmediate(Instruction.SRAI, 5, 2, 2)
            );

            CpuProgramExecution execution = CpuProgramExecutor.Run(
                program,
                new CpuProgramRunOptions { RegisterDump = [1, 2, 3, 4, 5] });

            execution.ExpectRegister(3, 0b0000000101010000); // SLLI
            execution.ExpectRegister(4, 0b0000000000010101); // SRLI

            // Expected SRAI output on sign-extended register. 
            // In C# int shift for SRAI we take negative and sign extend.
            // 0b1111111110101100 as sign-extended 64-bit right shifted by 2
            execution.ExpectRegister(5, unchecked((ulong)((long)(short)0b1111111110101100 >> 2)));

            return CpuExampleResult.Ok(
                "Expected correct shifts.",
                execution.Registers);
        }
    }
}
