using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU.Core.Decoder;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core.Pipeline.MicroOps;

namespace MinimalAsmApp.Examples.InstructionClosure;

using static CpuInstructions;

public sealed class ScalarExtensionClosureExample : ICpuExample
{
    public string Name => "scalar-extension-closure";

    public string Description => "Builds canonical SEXT.W and ZEXT.W carriers and verifies the runtime decoder accepts them.";

    public string Category => "13_InstructionClosure";

    public CpuExampleResult Run()
    {
        VLIW_Instruction sext = SignExtendWord(2, 1);
        VLIW_Instruction zext = ZeroExtendWord(3, 1);

        CpuInstructionDescriber.ExpectValid(in sext);
        CpuInstructionDescriber.ExpectValid(in zext);

        VliwDecoderV4 decoder = new VliwDecoderV4();
        InstructionIR sextIr = decoder.Decode(in sext, 0);
        InstructionIR zextIr = decoder.Decode(in zext, 1);

        return CpuExampleResult.Ok(
            "Built canonical SEXT.W and ZEXT.W carriers and verified the runtime decoder accepts them.",
            notes:
            [
                "SEXT.W:",
                .. CpuInstructionDescriber.Describe(in sext),
                $"decoded rd = {sextIr.Rd}, rs1 = {sextIr.Rs1}, rs2 = {sextIr.Rs2}",
                "ZEXT.W:",
                .. CpuInstructionDescriber.Describe(in zext),
                $"decoded rd = {zextIr.Rd}, rs1 = {zextIr.Rs1}, rs2 = {zextIr.Rs2}"
            ]);
    }
}
