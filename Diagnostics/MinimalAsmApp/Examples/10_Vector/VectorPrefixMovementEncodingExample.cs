using HybridCPU_ISE.Arch;
using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;

namespace MinimalAsmApp.Examples.Vector;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class VectorPrefixMovementEncodingExample : ICpuExample
{
    private const ulong SourceBase = 0x5A00;
    private const ulong DestinationBase = 0x5B00;

    public string Name => "vector-prefix-movement-encoding";

    public string Description => "Encodes VZEXT, VSCAN.SUM, VCOMPRESS and VEXPAND carriers.";

    public string Category => "10_Vector";

    public CpuExampleResult Run()
    {
        VLIW_Instruction zext = Vector1D(Instruction.VZEXT, DestinationBase, SourceBase, elementCount: 4);
        zext.DataTypeValue = DataTypeEnum.UINT32;

        VLIW_Instruction scanSum = Vector1D(Instruction.VSCAN_SUM, DestinationBase + 0x100, SourceBase, elementCount: 4);

        VLIW_Instruction compress = InstructionEncoder.EncodePredicativeMovement(
            (uint)Instruction.VCOMPRESS,
            DataTypeEnum.UINT64,
            SourceBase,
            DestinationBase + 0x200,
            streamLength: 4,
            predicateMask: 1);

        VLIW_Instruction expand = InstructionEncoder.EncodePredicativeMovement(
            (uint)Instruction.VEXPAND,
            DataTypeEnum.UINT64,
            SourceBase,
            DestinationBase + 0x300,
            streamLength: 4,
            predicateMask: 1);

        foreach (VLIW_Instruction instruction in new[] { zext, scanSum, compress, expand })
        {
            CpuInstructionDescriber.ExpectValid(in instruction);
        }

        return CpuExampleResult.Ok(
            "Encoded vector prefix/convert and predicative movement carriers.",
            notes:
            [
                "VZEXT:",
                .. CpuInstructionDescriber.Describe(in zext),
                "VSCAN_SUM:",
                .. CpuInstructionDescriber.Describe(in scanSum),
                "VCOMPRESS:",
                .. CpuInstructionDescriber.Describe(in compress),
                "VEXPAND:",
                .. CpuInstructionDescriber.Describe(in expand)
            ]);
    }
}
