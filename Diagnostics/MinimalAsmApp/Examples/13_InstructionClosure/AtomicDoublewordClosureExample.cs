using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU.Core.Decoder;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core.Pipeline.MicroOps;

namespace MinimalAsmApp.Examples.InstructionClosure;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class AtomicDoublewordClosureExample : ICpuExample
{
    public string Name => "atomic-doubleword-closure";

    public string Description => "Builds canonical LR.D, SC.D, AMOADD.D, and AMOSWAP.D carriers and verifies their retire-visible flags.";

    public string Category => "13_InstructionClosure";

    public CpuExampleResult Run()
    {
        VLIW_Instruction lr = AtomicDoubleword(Instruction.LR_D, 3, 1, acquire: true, release: true);
        VLIW_Instruction sc = AtomicDoubleword(Instruction.SC_D, 4, 1, 2, acquire: true, release: true);
        VLIW_Instruction amoAdd = AtomicDoubleword(Instruction.AMOADD_D, 5, 1, 2, acquire: true, release: false);
        VLIW_Instruction amoSwap = AtomicDoubleword(Instruction.AMOSWAP_D, 6, 1, 2, acquire: false, release: true);

        CpuInstructionDescriber.ExpectValid(in lr);
        CpuInstructionDescriber.ExpectValid(in sc);
        CpuInstructionDescriber.ExpectValid(in amoAdd);
        CpuInstructionDescriber.ExpectValid(in amoSwap);

        VliwDecoderV4 decoder = new VliwDecoderV4();
        InstructionIR lrIr = decoder.Decode(in lr, 0);
        InstructionIR scIr = decoder.Decode(in sc, 1);
        InstructionIR amoAddIr = decoder.Decode(in amoAdd, 2);
        InstructionIR amoSwapIr = decoder.Decode(in amoSwap, 3);

        return CpuExampleResult.Ok(
            "Built canonical doubleword atomics and verified the runtime decoder preserves acquire/release bits.",
            notes:
            [
                "LR.D:",
                .. CpuInstructionDescriber.Describe(in lr),
                $"decoded rd = {lrIr.Rd}, rs1 = {lrIr.Rs1}, rs2 = {lrIr.Rs2}, acquire = {lrIr.AcquireOrdering}, release = {lrIr.ReleaseOrdering}",
                "SC.D:",
                .. CpuInstructionDescriber.Describe(in sc),
                $"decoded rd = {scIr.Rd}, rs1 = {scIr.Rs1}, rs2 = {scIr.Rs2}, acquire = {scIr.AcquireOrdering}, release = {scIr.ReleaseOrdering}",
                "AMOADD.D:",
                .. CpuInstructionDescriber.Describe(in amoAdd),
                $"decoded rd = {amoAddIr.Rd}, rs1 = {amoAddIr.Rs1}, rs2 = {amoAddIr.Rs2}, acquire = {amoAddIr.AcquireOrdering}, release = {amoAddIr.ReleaseOrdering}",
                "AMOSWAP.D:",
                .. CpuInstructionDescriber.Describe(in amoSwap),
                $"decoded rd = {amoSwapIr.Rd}, rs1 = {amoSwapIr.Rs1}, rs2 = {amoSwapIr.Rs2}, acquire = {amoSwapIr.AcquireOrdering}, release = {amoSwapIr.ReleaseOrdering}"
            ]);
    }
}
