using HybridCPU.Compiler.Core.API.Facade;
using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.InstructionClosure;

using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class NonVmxScalarMinMaxExample : ICpuExample
{
    public string Name => "non-vmx-scalar-min-max";

    public string Description => "Emits MIN, MAX, MINU, and MAXU through the open compiler scalar min/max helpers.";

    public string Category => "13_InstructionClosure";

    public CpuExampleResult Run()
    {
        return NonVmxCompilerExampleSupport.RunAppFacadeExample(
            "Compiler facade emitted canonical signed and unsigned scalar min/max carriers.",
            [
                new(
                    "MIN",
                    "IAppAsmFacade.ScalarMinSigned",
                    Instruction.MIN,
                    facade => facade.ScalarMinSigned(new AsmRegister(14), new AsmRegister(1), new AsmRegister(2)),
                    ExpectedRd: 14,
                    ExpectedRs1: 1,
                    ExpectedRs2: 2,
                    ExpectedDataType: DataTypeEnum.UINT64),
                new(
                    "MAX",
                    "IAppAsmFacade.ScalarMaxSigned",
                    Instruction.MAX,
                    facade => facade.ScalarMaxSigned(new AsmRegister(15), new AsmRegister(1), new AsmRegister(2)),
                    ExpectedRd: 15,
                    ExpectedRs1: 1,
                    ExpectedRs2: 2,
                    ExpectedDataType: DataTypeEnum.UINT64),
                new(
                    "MINU",
                    "IAppAsmFacade.ScalarMinUnsigned",
                    Instruction.MINU,
                    facade => facade.ScalarMinUnsigned(new AsmRegister(16), new AsmRegister(1), new AsmRegister(2)),
                    ExpectedRd: 16,
                    ExpectedRs1: 1,
                    ExpectedRs2: 2,
                    ExpectedDataType: DataTypeEnum.UINT64),
                new(
                    "MAXU",
                    "IAppAsmFacade.ScalarMaxUnsigned",
                    Instruction.MAXU,
                    facade => facade.ScalarMaxUnsigned(new AsmRegister(17), new AsmRegister(1), new AsmRegister(2)),
                    ExpectedRd: 17,
                    ExpectedRs1: 1,
                    ExpectedRs2: 2,
                    ExpectedDataType: DataTypeEnum.UINT64)
            ]);
    }
}
