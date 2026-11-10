using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Branching;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class ConditionalBranchNotTakenExample : ICpuExample
{
    public string Name => "branch-not-taken";

    public string Description => "Runs a BNE that is not taken, then verifies fall-through execution.";

    public string Category => "04_Branching";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            AddImmediate(1, 0, 5),
            AddImmediate(2, 0, 5),
            Branch(Instruction.BNE, 1, 2, relativeOffset: 0),
            AddImmediate(3, 0, 1));

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions { RegisterDump = [1, 2, 3] });

        execution.ExpectRegister(3, 1);

        return CpuExampleResult.Ok(
            "Expected BNE not taken because x1==x2; fall-through writes x3=1.",
            execution.Registers,
            notes:
            [
                "Taken branch and symbolic target examples are intentionally not emitted here until MinimalAsmApp has a small branch-target helper over the compiler/runtime boundary."
            ]);
    }
}
