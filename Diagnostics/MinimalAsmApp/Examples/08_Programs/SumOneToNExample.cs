using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Programs;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class SumOneToNExample : ICpuExample
{
    public string Name => "sum-1-to-n";

    public string Description => "Small straight-line sum grouped as 10+5, representing 1+2+3+4+5.";

    public string Category => "08_Programs";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            AddImmediate(1, 0, 10),
            AddImmediate(2, 0, 5),
            Binary(Instruction.ADD, 10, 1, 2));

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions { RegisterDump = [10] });

        execution.ExpectRegister(10, 15);

        return CpuExampleResult.Ok(
            "Expected x10=15 for 1+2+3+4+5, grouped as 10+5.",
            execution.Registers);
    }
}
