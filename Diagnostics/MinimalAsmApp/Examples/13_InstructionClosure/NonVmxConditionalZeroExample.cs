using HybridCPU.Compiler.Core.API.Facade;
using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.InstructionClosure;

using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class NonVmxConditionalZeroExample : ICpuExample
{
    public string Name => "non-vmx-conditional-zero";

    public string Description => "Emits CZERO.EQZ and CZERO.NEZ through the open compiler conditional-zero helpers.";

    public string Category => "13_InstructionClosure";

    public CpuExampleResult Run()
    {
        return NonVmxCompilerExampleSupport.RunAppFacadeExample(
            "Compiler facade emitted canonical CZERO.EQZ and CZERO.NEZ carriers without opening CSEL.",
            [
                new(
                    "CZERO.EQZ",
                    "IAppAsmFacade.ZeroIfConditionEqualZero",
                    Instruction.CZERO_EQZ,
                    facade => facade.ZeroIfConditionEqualZero(
                        new AsmRegister(5),
                        new AsmRegister(1),
                        new AsmRegister(2)),
                    ExpectedRd: 5,
                    ExpectedRs1: 1,
                    ExpectedRs2: 2,
                    ExpectedDataType: DataTypeEnum.UINT64),
                new(
                    "CZERO.NEZ",
                    "IAppAsmFacade.ZeroIfConditionNotEqualZero",
                    Instruction.CZERO_NEZ,
                    facade => facade.ZeroIfConditionNotEqualZero(
                        new AsmRegister(6),
                        new AsmRegister(1),
                        new AsmRegister(2)),
                    ExpectedRd: 6,
                    ExpectedRs1: 1,
                    ExpectedRs2: 2,
                    ExpectedDataType: DataTypeEnum.UINT64)
            ]);
    }
}
