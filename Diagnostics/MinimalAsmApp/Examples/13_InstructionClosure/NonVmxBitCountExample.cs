using HybridCPU.Compiler.Core.API.Facade;
using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.InstructionClosure;

using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class NonVmxBitCountExample : ICpuExample
{
    public string Name => "non-vmx-bit-count";

    public string Description => "Emits CLZ, CTZ, and CPOP through the open compiler bit-count helpers.";

    public string Category => "13_InstructionClosure";

    public CpuExampleResult Run()
    {
        return NonVmxCompilerExampleSupport.RunAppFacadeExample(
            "Compiler facade emitted canonical CLZ, CTZ, and CPOP carriers.",
            [
                new(
                    "CLZ",
                    "IAppAsmFacade.CountLeadingZeros",
                    Instruction.CLZ,
                    facade => facade.CountLeadingZeros(new AsmRegister(2), new AsmRegister(1)),
                    ExpectedRd: 2,
                    ExpectedRs1: 1,
                    ExpectedRs2: 0,
                    ExpectedDataType: DataTypeEnum.UINT64),
                new(
                    "CTZ",
                    "IAppAsmFacade.CountTrailingZeros",
                    Instruction.CTZ,
                    facade => facade.CountTrailingZeros(new AsmRegister(3), new AsmRegister(1)),
                    ExpectedRd: 3,
                    ExpectedRs1: 1,
                    ExpectedRs2: 0,
                    ExpectedDataType: DataTypeEnum.UINT64),
                new(
                    "CPOP",
                    "IAppAsmFacade.CountSetBits",
                    Instruction.CPOP,
                    facade => facade.CountSetBits(new AsmRegister(4), new AsmRegister(1)),
                    ExpectedRd: 4,
                    ExpectedRs1: 1,
                    ExpectedRs2: 0,
                    ExpectedDataType: DataTypeEnum.UINT64)
            ]);
    }
}
