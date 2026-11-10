using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Programs;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class FibonacciExample : ICpuExample
{
    public string Name => "fibonacci";

    public string Description => "Computes the final step of a small Fibonacci sequence as 3+5.";

    public string Category => "08_Programs";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            AddImmediate(5, 0, 3),
            AddImmediate(6, 0, 5),
            Binary(Instruction.ADD, 7, 5, 6));

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions { RegisterDump = [5, 6, 7] });

        execution.ExpectRegister(7, 8);

        return CpuExampleResult.Ok(
            "Expected final Fibonacci step 3+5=8 with x7=8.",
            execution.Registers);
    }
}
