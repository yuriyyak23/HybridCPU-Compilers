using HybridCPU_ISE.Arch;
using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;

namespace MinimalAsmApp.Examples.Vector;

using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class VectorFmaEncodingExample : ICpuExample
{
    private const ulong AccumulatorBase = 0x5400;
    private const ulong TriOpDescriptorBase = 0x5500;

    public string Name => "vector-fma-encoding";

    public string Description => "Encodes VFMADD, VFMSUB, VFNMADD and VFNMSUB through the TriOp descriptor ABI.";

    public string Category => "10_Vector";

    public CpuExampleResult Run()
    {
        Instruction[] opcodes =
        [
            Instruction.VFMADD,
            Instruction.VFMSUB,
            Instruction.VFNMADD,
            Instruction.VFNMSUB
        ];

        List<string> notes = [];
        foreach (Instruction opcode in opcodes)
        {
            VLIW_Instruction instruction = InstructionEncoder.EncodeFMA(
                (uint)opcode,
                DataTypeEnum.FLOAT64,
                AccumulatorBase + ((uint)opcode * 0x10UL),
                TriOpDescriptorBase + ((uint)opcode * 0x20UL),
                streamLength: 4,
                destStride: sizeof(double));

            CpuInstructionDescriber.ExpectValid(in instruction);
            notes.Add($"{opcode}:");
            notes.AddRange(CpuInstructionDescriber.Describe(in instruction));
        }

        return CpuExampleResult.Ok(
            "Encoded the vector FMA family with descriptor sideband addresses; execution remains descriptor/runtime governed.",
            notes: notes);
    }
}
