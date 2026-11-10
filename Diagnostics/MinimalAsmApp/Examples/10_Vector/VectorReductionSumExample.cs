using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Vector;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class VectorReductionSumExample : ICpuExample
{
    private const ulong SourceBase = 0x4700;

    public string Name => "vector-reduce-sum-encoding";

    public string Description => "Encodes VREDSUM over four UInt64 memory elements with scalar destination x10.";

    public string Category => "10_Vector";

    public CpuExampleResult Run()
    {
        VLIW_Instruction instruction = VectorReduction(
            Instruction.VREDSUM,
            SourceBase,
            destinationRegister: 10,
            elementCount: 4);

        CpuInstructionDescriber.ExpectValid(in instruction);

        return CpuExampleResult.Ok(
            "Encoded VREDSUM: source=0x4700, length=4, stride=8, scalar destination register=x10.",
            notes: CpuInstructionDescriber.Describe(in instruction));
    }
}
