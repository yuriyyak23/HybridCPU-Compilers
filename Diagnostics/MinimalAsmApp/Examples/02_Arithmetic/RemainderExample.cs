using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;

namespace MinimalAsmApp.Examples.Arithmetic;

using static CpuInstructions;
using Instruction = YAKSys_Hybrid_CPU.Processor.CPU_Core.InstructionsEnum;

public sealed class RemainderExample : ICpuExample
{
    public string Name => "remainder";

    public string Description => "Runs REM instruction and checks results.";

    public string Category => "02_Arithmetic";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            AddImmediate(1, 0, 10),
            AddImmediate(2, 0, 3),
            Binary(Instruction.REM, 3, 1, 2)
        );

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions { RegisterDump = [1, 2, 3] });

        execution.ExpectRegister(3, 1); // 10 % 3 = 1

        return CpuExampleResult.Ok(
            "Expected correct remainder.",
            execution.Registers);
    }
}
