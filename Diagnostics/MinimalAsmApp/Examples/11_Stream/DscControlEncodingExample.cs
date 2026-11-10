using HybridCPU_ISE.Arch;
using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Stream;

using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class DscControlEncodingExample : ICpuExample
{
    public string Name => "dsc-control-encoding";

    public string Description => "Encodes DSC_STATUS and DSC_QUERY_CAPS Lane6 control carriers.";

    public string Category => "11_Stream";

    public CpuExampleResult Run()
    {
        VLIW_Instruction status = new()
        {
            OpCode = (uint)Instruction.DSC_STATUS,
            DataTypeValue = DataTypeEnum.UINT64,
            DestSrc1Pointer = VLIW_Instruction.PackArchRegs(3, 1, VLIW_Instruction.NoArchReg)
        };

        VLIW_Instruction queryCaps = new()
        {
            OpCode = (uint)Instruction.DSC_QUERY_CAPS,
            DataTypeValue = DataTypeEnum.UINT64,
            DestSrc1Pointer = VLIW_Instruction.PackArchRegs(4, VLIW_Instruction.NoArchReg, VLIW_Instruction.NoArchReg)
        };

        CpuInstructionDescriber.ExpectValid(in status);
        CpuInstructionDescriber.ExpectValid(in queryCaps);

        return CpuExampleResult.Ok(
            "Encoded Lane6 DSC status/capability carriers; queue ownership and guarded commit remain runtime-owned.",
            notes:
            [
                "DSC_STATUS:",
                .. CpuInstructionDescriber.Describe(in status),
                "DSC_QUERY_CAPS:",
                .. CpuInstructionDescriber.Describe(in queryCaps)
            ]);
    }
}
