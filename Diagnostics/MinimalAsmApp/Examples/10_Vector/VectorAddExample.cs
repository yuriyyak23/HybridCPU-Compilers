using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Vector;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class VectorAddExample : ICpuExample
{
    private const ulong LeftBase = 0x4000;
    private const ulong RightBase = 0x4100;

    public string Name => "vector-add-encoding";

    public string Description => "Encodes a VADD 1D UInt64 vector instruction and validates its stream fields.";

    public string Category => "10_Vector";

    public CpuExampleResult Run()
    {
        VLIW_Instruction instruction = Vector1D(
            Instruction.VADD,
            LeftBase,
            RightBase,
            elementCount: 4);

        CpuInstructionDescriber.ExpectValid(in instruction);

        return CpuExampleResult.Ok(
            "Encoded VADD as a 1D in-place vector operation: dst/src1=0x4000, src2=0x4100, length=4, stride=8.",
            notes: CpuInstructionDescriber.Describe(in instruction));
    }
}
