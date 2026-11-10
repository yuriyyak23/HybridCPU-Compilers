using HybridCPU_ISE.Arch;
using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Vector;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class MatrixTileEncodingExample : ICpuExample
{
    private const ulong TileDescriptorBase = 0x7000;
    private const ulong MemoryDescriptorBase = 0x7100;

    public string Name => "matrix-tile-encoding";

    public string Description => "Encodes MTILE_LOAD, MTILE_STORE, MTILE_MACC and MTRANSPOSE carriers without executing tile state.";

    public string Category => "10_Vector";

    public CpuExampleResult Run()
    {
        VLIW_Instruction load = MatrixCarrier(Instruction.MTILE_LOAD, TileDescriptorBase, MemoryDescriptorBase);
        VLIW_Instruction store = MatrixCarrier(Instruction.MTILE_STORE, TileDescriptorBase + 0x100, MemoryDescriptorBase + 0x100);
        VLIW_Instruction macc = MatrixCarrier(Instruction.MTILE_MACC, TileDescriptorBase + 0x200, MemoryDescriptorBase + 0x200);
        VLIW_Instruction transpose = MatrixCarrier(Instruction.MTRANSPOSE, TileDescriptorBase + 0x300, MemoryDescriptorBase + 0x300);

        foreach (VLIW_Instruction instruction in new[] { load, store, macc, transpose })
        {
            CpuInstructionDescriber.ExpectValid(in instruction);
        }

        return CpuExampleResult.Ok(
            "Encoded MatrixTile runtime-ISA carriers as descriptor evidence; execute capture and retire publication remain runtime-owned.",
            notes:
            [
                "MTILE_LOAD:",
                .. CpuInstructionDescriber.Describe(in load),
                "MTILE_STORE:",
                .. CpuInstructionDescriber.Describe(in store),
                "MTILE_MACC:",
                .. CpuInstructionDescriber.Describe(in macc),
                "MTRANSPOSE:",
                .. CpuInstructionDescriber.Describe(in transpose)
            ]);
    }

    private static VLIW_Instruction MatrixCarrier(
        Instruction opcode,
        ulong tileDescriptor,
        ulong memoryDescriptor)
    {
        VLIW_Instruction instruction = Vector1D(opcode, tileDescriptor, memoryDescriptor, elementCount: 16);
        instruction.DataTypeValue = DataTypeEnum.FLOAT32;
        instruction.Is2D = true;
        instruction.RowStride = sizeof(float) * 4;
        return instruction;
    }
}
