using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU.Core.Decoder;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core.Pipeline.MicroOps;

namespace MinimalAsmApp.Examples.InstructionClosure;

using static CpuInstructions;

public sealed class FenceOrderingClosureExample : ICpuExample
{
    public string Name => "fence-ordering-closure";

    public string Description => "Builds canonical zero-payload FENCE and FENCE.I carriers and verifies the runtime decoder accepts them.";

    public string Category => "13_InstructionClosure";

    public CpuExampleResult Run()
    {
        VLIW_Instruction fence = Fence();
        VLIW_Instruction fenceI = FenceI();

        CpuInstructionDescriber.ExpectValid(in fence);
        CpuInstructionDescriber.ExpectValid(in fenceI);

        VliwDecoderV4 decoder = new VliwDecoderV4();
        InstructionIR fenceIr = decoder.Decode(in fence, 0);
        InstructionIR fenceIIr = decoder.Decode(in fenceI, 1);

        return CpuExampleResult.Ok(
            "Built canonical FENCE and FENCE.I carriers and verified the runtime decoder accepts the zero-payload form.",
            notes:
            [
                "FENCE:",
                .. CpuInstructionDescriber.Describe(in fence),
                $"decoded rd = {fenceIr.Rd}, rs1 = {fenceIr.Rs1}, rs2 = {fenceIr.Rs2}, acquire = {fenceIr.AcquireOrdering}, release = {fenceIr.ReleaseOrdering}",
                "FENCE.I:",
                .. CpuInstructionDescriber.Describe(in fenceI),
                $"decoded rd = {fenceIIr.Rd}, rs1 = {fenceIIr.Rs1}, rs2 = {fenceIIr.Rs2}, acquire = {fenceIIr.AcquireOrdering}, release = {fenceIIr.ReleaseOrdering}"
            ]);
    }
}
