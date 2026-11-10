using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;

namespace MinimalAsmApp.Examples.ControlFlow;

using static CpuInstructions;
using Instruction = YAKSys_Hybrid_CPU.Processor.CPU_Core.InstructionsEnum;

public sealed class BranchExample : ICpuExample
{
    public string Name => "branch-all";

    public string Description => "Decodes BEQ, BLT, BGE, BLTU, BGEU branching instructions.";

    public string Category => "04_Branching";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            Branch(Instruction.BEQ, 1, 2, 4),
            Branch(Instruction.BLT, 1, 2, 4),
            Branch(Instruction.BGE, 1, 2, 4),
            Branch(Instruction.BLTU, 1, 2, 4),
            Branch(Instruction.BGEU, 1, 2, 4)
        );

        return CpuExampleResult.Ok(
            "Expected successful branching decode.",
            new Dictionary<string, ulong>());
    }
}
