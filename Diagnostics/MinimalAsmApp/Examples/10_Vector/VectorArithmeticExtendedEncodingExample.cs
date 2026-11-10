using HybridCPU_ISE.Arch;
using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Vector;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class VectorArithmeticExtendedEncodingExample : ICpuExample
{
    private const ulong DestinationBase = 0x5100;
    private const ulong SourceBase = 0x5200;

    public string Name => "vector-arithmetic-extended-encoding";

    public string Description => "Encodes VSUB, VDIV, logical, unary and signed min/max vector carriers.";

    public string Category => "10_Vector";

    public CpuExampleResult Run()
    {
        Instruction[] opcodes =
        [
            Instruction.VSUB,
            Instruction.VDIV,
            Instruction.VXOR,
            Instruction.VOR,
            Instruction.VAND,
            Instruction.VNOT,
            Instruction.VSQRT,
            Instruction.VMOD,
            Instruction.VMIN,
            Instruction.VMAX
        ];

        List<string> notes = [];
        foreach (Instruction opcode in opcodes)
        {
            DataTypeEnum dataType = opcode is Instruction.VSQRT
                ? DataTypeEnum.FLOAT64
                : DataTypeEnum.UINT64;

            VLIW_Instruction instruction = Vector1D(
                opcode,
                DestinationBase + ((uint)opcode * 0x10UL),
                SourceBase,
                elementCount: 4);
            instruction.DataTypeValue = dataType;

            CpuInstructionDescriber.ExpectValid(in instruction);
            notes.Add($"{opcode}:");
            notes.AddRange(CpuInstructionDescriber.Describe(in instruction));
        }

        return CpuExampleResult.Ok(
            "Encoded extended vector arithmetic/logical carriers over a four-element stream.",
            notes: notes);
    }
}
