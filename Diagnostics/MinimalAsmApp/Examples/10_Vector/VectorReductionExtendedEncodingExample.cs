using HybridCPU_ISE.Arch;
using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;

namespace MinimalAsmApp.Examples.Vector;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class VectorReductionExtendedEncodingExample : ICpuExample
{
    private const ulong SourceBase = 0x5700;
    private const ulong DotSourceBase = 0x5800;

    public string Name => "vector-reduction-extended-encoding";

    public string Description => "Encodes extended vector reductions and dot-product reduction carriers.";

    public string Category => "10_Vector";

    public CpuExampleResult Run()
    {
        Instruction[] reductions =
        [
            Instruction.VREDMAX,
            Instruction.VREDMIN,
            Instruction.VREDMAXU,
            Instruction.VREDMINU,
            Instruction.VREDAND,
            Instruction.VREDOR,
            Instruction.VREDXOR
        ];

        Instruction[] dots =
        [
            Instruction.VDOT,
            Instruction.VDOTU,
            Instruction.VDOTF,
            Instruction.VDOT_FP8,
            Instruction.VDOT_WIDE
        ];

        List<string> notes = [];
        foreach (Instruction opcode in reductions)
        {
            VLIW_Instruction instruction = VectorReduction(
                opcode,
                SourceBase + ((uint)opcode * 0x10UL),
                destinationRegister: 12,
                elementCount: 4);

            CpuInstructionDescriber.ExpectValid(in instruction);
            notes.Add($"{opcode}:");
            notes.AddRange(CpuInstructionDescriber.Describe(in instruction));
        }

        foreach (Instruction opcode in dots)
        {
            DataTypeEnum dataType = opcode is Instruction.VDOTF or Instruction.VDOT_FP8 or Instruction.VDOT_WIDE
                ? DataTypeEnum.FLOAT32
                : DataTypeEnum.UINT64;

            ulong source = DotSourceBase + ((uint)opcode * 0x10UL);
            VLIW_Instruction instruction = InstructionEncoder.EncodeDotProduct(
                (uint)opcode,
                dataType,
                destPtr: source,
                src1Ptr: source,
                src2Ptr: DotSourceBase + 0x800,
                streamLength: 4,
                stride: sizeof(ulong));

            CpuInstructionDescriber.ExpectValid(in instruction);
            notes.Add($"{opcode}:");
            notes.AddRange(CpuInstructionDescriber.Describe(in instruction));
        }

        return CpuExampleResult.Ok(
            "Encoded extended reduction and dot-product families with explicit reduction carriers.",
            notes: notes);
    }
}
