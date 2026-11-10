using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Vector;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class VectorMulExample : ICpuExample
{
    private const ulong LeftBase = 0x4200;
    private const ulong RightBase = 0x4300;

    public string Name => "vector-mul-encoding";

    public string Description => "Encodes a VMUL 1D UInt64 vector instruction and validates its stream fields.";

    public string Category => "10_Vector";

    public CpuExampleResult Run()
    {
        VLIW_Instruction instruction = Vector1D(
            Instruction.VMUL,
            LeftBase,
            RightBase,
            elementCount: 3);

        CpuInstructionDescriber.ExpectValid(in instruction);

        return CpuExampleResult.Ok(
            "Encoded VMUL as a 1D in-place vector operation: dst/src1=0x4200, src2=0x4300, length=3, stride=8.",
            notes: CpuInstructionDescriber.Describe(in instruction));
    }
}
