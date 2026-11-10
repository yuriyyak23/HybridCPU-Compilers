using HybridCPU_ISE.Arch;
using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;

namespace MinimalAsmApp.Examples.Vector;

using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class Vector2DAddressingExample : ICpuExample
{
    private const ulong LeftBase = 0x4A00;
    private const ulong RightBase = 0x4B00;

    public string Name => "vector-2d-addressing-encoding";

    public string Description => "Encodes a 2D VADD shape with row length and row stride metadata.";

    public string Category => "10_Vector";

    public CpuExampleResult Run()
    {
        VLIW_Instruction instruction = InstructionEncoder.EncodeVector2D(
            (uint)Instruction.VADD,
            DataTypeEnum.UINT64,
            LeftBase,
            RightBase,
            streamLength: 6,
            colStride: sizeof(ulong),
            rowStride: 32,
            rowLength: 3);

        CpuInstructionDescriber.ExpectValid(in instruction);

        return CpuExampleResult.Ok(
            "Encoded 2D VADD: 2 rows x 3 elements, col stride=8, row stride=32.",
            notes: CpuInstructionDescriber.Describe(in instruction));
    }
}
