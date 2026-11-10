using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Arithmetic;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class AddSubExample : ICpuExample
{
    public string Name => "add-sub";

    public string Description => "Runs ADD and SUB on two registers and checks both results.";

    public string Category => "02_Arithmetic";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            AddImmediate(1, 0, 20),
            AddImmediate(2, 0, 7),
            Binary(Instruction.ADD, 3, 1, 2),
            Binary(Instruction.SUB, 4, 1, 2));

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions { RegisterDump = [1, 2, 3, 4] });

        execution.ExpectRegister(3, 27);
        execution.ExpectRegister(4, 13);

        return CpuExampleResult.Ok(
            "Expected x3=20+7=27 and x4=20-7=13.",
            execution.Registers);
    }
}
