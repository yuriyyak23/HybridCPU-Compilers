using HybridCPU_ISE.Arch;
using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;

namespace MinimalAsmApp.Examples.Vector;

using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class VectorCompareMaskExample : ICpuExample
{
    private const ulong LeftBase = 0x4800;
    private const ulong RightBase = 0x4900;

    public string Name => "vector-compare-mask-encoding";

    public string Description => "Encodes VCMPGT over UInt64 vectors and writes the comparison result to predicate register p1.";

    public string Category => "10_Vector";

    public CpuExampleResult Run()
    {
        VLIW_Instruction instruction = InstructionEncoder.EncodeVectorComparison(
            (uint)Instruction.VCMPGT,
            DataTypeEnum.UINT64,
            LeftBase,
            RightBase,
            streamLength: 4,
            destPredicateReg: 1,
            stride: sizeof(ulong));

        CpuInstructionDescriber.ExpectValid(in instruction);

        return CpuExampleResult.Ok(
            "Encoded VCMPGT: src1=0x4800, src2=0x4900, length=4, predicate destination=p1.",
            notes: CpuInstructionDescriber.Describe(in instruction));
    }
}
