using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Arithmetic;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class BitwiseExample : ICpuExample
{
    public string Name => "bitwise";

    public string Description => "Runs AND, OR, and XOR on two registers and checks the results.";

    public string Category => "02_Arithmetic";

    public CpuExampleResult Run()
    {
        // 5 = 0101
        // 3 = 0011
        // AND: 0001 = 1
        // OR:  0111 = 7
        // XOR: 0110 = 6
        VLIW_Instruction[] program = WithFences(
            AddImmediate(1, 0, 5),
            AddImmediate(2, 0, 3),
            Binary(Instruction.AND, 3, 1, 2),
            Binary(Instruction.OR, 4, 1, 2),
            Binary(Instruction.XOR, 5, 1, 2));

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions { RegisterDump = [1, 2, 3, 4, 5] });

        execution.ExpectRegister(3, 1);
        execution.ExpectRegister(4, 7);
        execution.ExpectRegister(5, 6);

        return CpuExampleResult.Ok(
            "Expected x3=1 (AND), x4=7 (OR), x5=6 (XOR).",
            execution.Registers);
    }
}
