using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Programs;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class FactorialExample : ICpuExample
{
    public string Name => "factorial";

    public string Description => "Computes 5! with one final multiplication step, grouped as 24*5.";

    public string Category => "08_Programs";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            AddImmediate(1, 0, 24),
            AddImmediate(2, 0, 5),
            Binary(Instruction.MUL, 10, 1, 2));

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions { RegisterDump = [10] });

        execution.ExpectRegister(10, 120);

        return CpuExampleResult.Ok(
            "Expected x10=120 for 5!, grouped as 24*5.",
            execution.Registers);
    }
}
