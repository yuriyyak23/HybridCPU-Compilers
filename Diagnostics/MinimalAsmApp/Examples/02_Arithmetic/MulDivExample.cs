using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Arithmetic;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class MulDivExample : ICpuExample
{
    public string Name => "mul-div";

    public string Description => "Runs MUL and DIV on small integer values.";

    public string Category => "02_Arithmetic";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            AddImmediate(1, 0, 6),
            AddImmediate(2, 0, 7),
            Binary(Instruction.MUL, 3, 1, 2),
            Binary(Instruction.DIV, 4, 3, 1));

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions { RegisterDump = [1, 2, 3, 4] });

        execution.ExpectRegister(3, 42);
        execution.ExpectRegister(4, 7);

        return CpuExampleResult.Ok(
            "Expected x3=6*7=42 and x4=42/6=7.",
            execution.Registers);
    }
}
