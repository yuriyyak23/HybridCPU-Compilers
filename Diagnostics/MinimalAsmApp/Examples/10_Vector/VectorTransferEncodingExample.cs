using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Vector;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class VectorTransferEncodingExample : ICpuExample
{
    private const ulong VectorBase = 0x4C00;
    private const ulong MemoryBase = 0x4D00;

    public string Name => "vector-transfer-encoding";

    public string Description => "Encodes VLOAD and VSTORE transfer carriers for a four-element UInt64 stream.";

    public string Category => "10_Vector";

    public CpuExampleResult Run()
    {
        VLIW_Instruction load = Vector1D(
            Instruction.VLOAD,
            VectorBase,
            MemoryBase,
            elementCount: 4);

        VLIW_Instruction store = Vector1D(
            Instruction.VSTORE,
            VectorBase,
            MemoryBase,
            elementCount: 4);

        CpuInstructionDescriber.ExpectValid(in load);
        CpuInstructionDescriber.ExpectValid(in store);

        return CpuExampleResult.Ok(
            "Encoded VLOAD and VSTORE transfer carriers. Mainline execution currently rejects raw transfer fallback paths.",
            notes:
            [
                "VLOAD:",
                .. CpuInstructionDescriber.Describe(in load),
                "VSTORE:",
                .. CpuInstructionDescriber.Describe(in store)
            ]);
    }
}
