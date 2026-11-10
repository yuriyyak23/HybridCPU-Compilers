using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Arithmetic;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class CanonicalRemainderExample : ICpuExample
{
    public string Name => "canonical-rem-encoding";

    public string Description => "Builds the canonical ISA v4 REM carrier.";

    public string Category => "02_Arithmetic";

    public CpuExampleResult Run()
    {
        VLIW_Instruction instruction = Binary(Instruction.REM, 5, 1, 2);

        CpuInstructionDescriber.ExpectValid(in instruction);

        return CpuExampleResult.Ok(
            "Encoded canonical REM opcode 224 with rd=x5, rs1=x1, rs2=x2.",
            notes: CpuInstructionDescriber.Describe(in instruction));
    }
}
