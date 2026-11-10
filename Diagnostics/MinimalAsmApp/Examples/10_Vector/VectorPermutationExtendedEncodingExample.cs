using HybridCPU_ISE.Arch;
using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;

namespace MinimalAsmApp.Examples.Vector;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class VectorPermutationExtendedEncodingExample : ICpuExample
{
    private const ulong SourceBase = 0x5D00;
    private const ulong DestinationBase = 0x5E00;
    private const ulong IndexDescriptorBase = 0x5F00;

    public string Name => "vector-permutation-extended-encoding";

    public string Description => "Encodes permutation, gather/scatter, slide and transpose vector carriers.";

    public string Category => "10_Vector";

    public CpuExampleResult Run()
    {
        VLIW_Instruction permute = InstructionEncoder.EncodePermutation(
            (uint)Instruction.VPERMUTE,
            DataTypeEnum.UINT64,
            SourceBase,
            IndexDescriptorBase,
            DestinationBase,
            streamLength: 4);

        VLIW_Instruction vrgather = InstructionEncoder.EncodePermutation(
            (uint)Instruction.VRGATHER,
            DataTypeEnum.UINT64,
            SourceBase + 0x100,
            IndexDescriptorBase + 0x100,
            DestinationBase + 0x100,
            streamLength: 4);

        VLIW_Instruction gather = InstructionEncoder.EncodeVectorIndexed(
            (uint)Instruction.VGATHER,
            DataTypeEnum.UINT64,
            DestinationBase + 0x200,
            IndexDescriptorBase + 0x200,
            streamLength: 4);

        VLIW_Instruction scatter = InstructionEncoder.EncodeVectorIndexed(
            (uint)Instruction.VSCATTER,
            DataTypeEnum.UINT64,
            SourceBase + 0x200,
            IndexDescriptorBase + 0x300,
            streamLength: 4);

        VLIW_Instruction slideUp = InstructionEncoder.EncodeSlide(
            (uint)Instruction.VSLIDEUP,
            DataTypeEnum.UINT64,
            SourceBase,
            DestinationBase + 0x300,
            streamLength: 4,
            slideOffset: 2);

        VLIW_Instruction slideDown = InstructionEncoder.EncodeSlide(
            (uint)Instruction.VSLIDEDOWN,
            DataTypeEnum.UINT64,
            SourceBase,
            DestinationBase + 0x400,
            streamLength: 4,
            slideOffset: 2);

        VLIW_Instruction slide1Up = InstructionEncoder.EncodeSlide(
            (uint)Instruction.VSLIDE1UP,
            DataTypeEnum.UINT64,
            SourceBase,
            DestinationBase + 0x500,
            streamLength: 4,
            slideOffset: 1);

        VLIW_Instruction slide1Down = InstructionEncoder.EncodeSlide(
            (uint)Instruction.VSLIDE1DOWN,
            DataTypeEnum.UINT64,
            SourceBase,
            DestinationBase + 0x600,
            streamLength: 4,
            slideOffset: 1);

        VLIW_Instruction perm2 = Vector1D(Instruction.VPERM2, DestinationBase + 0x700, SourceBase, elementCount: 4);
        perm2.Immediate = 0b10_01;

        VLIW_Instruction transpose = Vector1D(Instruction.VTRANSPOSE, DestinationBase + 0x800, SourceBase, elementCount: 4);
        transpose.Is2D = true;
        transpose.RowStride = sizeof(ulong) * 2;

        VLIW_Instruction[] instructions =
        [
            permute,
            vrgather,
            gather,
            scatter,
            slideUp,
            slideDown,
            slide1Up,
            slide1Down,
            perm2,
            transpose
        ];

        List<string> notes = [];
        foreach (VLIW_Instruction instruction in instructions)
        {
            CpuInstructionDescriber.ExpectValid(in instruction);
            notes.Add(((Instruction)instruction.OpCode).ToString());
            notes.AddRange(CpuInstructionDescriber.Describe(in instruction));
        }

        return CpuExampleResult.Ok(
            "Encoded extended permutation, indexed movement, slide, and transpose carriers.",
            notes: notes);
    }
}
