using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Vector;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class VectorMinMaxExample : ICpuExample
{
    private const ulong MinLeftBase = 0x4400;
    private const ulong MaxLeftBase = 0x4500;
    private const ulong RightBase = 0x4600;

    public string Name => "vector-min-max-encoding";

    public string Description => "Encodes VMINU and VMAXU 1D UInt64 vector instructions.";

    public string Category => "10_Vector";

    public CpuExampleResult Run()
    {
        VLIW_Instruction minInstruction = Vector1D(
            Instruction.VMINU,
            MinLeftBase,
            RightBase,
            elementCount: 3);

        VLIW_Instruction maxInstruction = Vector1D(
            Instruction.VMAXU,
            MaxLeftBase,
            RightBase,
            elementCount: 3);

        CpuInstructionDescriber.ExpectValid(in minInstruction);
        CpuInstructionDescriber.ExpectValid(in maxInstruction);

        return CpuExampleResult.Ok(
            "Encoded VMINU and VMAXU as separate 1D vector operations over three UInt64 elements.",
            notes:
            [
                "VMINU:",
                .. CpuInstructionDescriber.Describe(in minInstruction),
                "VMAXU:",
                .. CpuInstructionDescriber.Describe(in maxInstruction)
            ]);
    }
}
