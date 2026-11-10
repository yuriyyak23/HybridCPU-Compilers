using HybridCPU.Compiler.Core.API.Facade;
using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.InstructionClosure;

using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class NonVmxRotateExample : ICpuExample
{
    public string Name => "non-vmx-rotate";

    public string Description => "Emits ROL, ROR, ROLI, and RORI through the open compiler rotate helpers.";

    public string Category => "13_InstructionClosure";

    public CpuExampleResult Run()
    {
        return NonVmxCompilerExampleSupport.RunAppFacadeExample(
            "Compiler facade emitted canonical register and immediate rotate carriers.",
            [
                new(
                    "ROL",
                    "IAppAsmFacade.RotateLeftRegister",
                    Instruction.ROL,
                    facade => facade.RotateLeftRegister(
                        new AsmRegister(7),
                        new AsmRegister(1),
                        new AsmRegister(2)),
                    ExpectedRd: 7,
                    ExpectedRs1: 1,
                    ExpectedRs2: 2,
                    ExpectedDataType: DataTypeEnum.UINT64),
                new(
                    "ROR",
                    "IAppAsmFacade.RotateRightRegister",
                    Instruction.ROR,
                    facade => facade.RotateRightRegister(
                        new AsmRegister(8),
                        new AsmRegister(1),
                        new AsmRegister(2)),
                    ExpectedRd: 8,
                    ExpectedRs1: 1,
                    ExpectedRs2: 2,
                    ExpectedDataType: DataTypeEnum.UINT64),
                new(
                    "ROLI",
                    "IAppAsmFacade.RotateLeftByImmediate",
                    Instruction.ROLI,
                    facade => facade.RotateLeftByImmediate(new AsmRegister(9), new AsmRegister(1), 5),
                    ExpectedRd: 9,
                    ExpectedRs1: 1,
                    ExpectedRs2: 0,
                    ExpectedImmediate: 5,
                    ExpectedDataType: DataTypeEnum.UINT64),
                new(
                    "RORI",
                    "IAppAsmFacade.RotateRightByImmediate",
                    Instruction.RORI,
                    facade => facade.RotateRightByImmediate(new AsmRegister(10), new AsmRegister(1), 9),
                    ExpectedRd: 10,
                    ExpectedRs1: 1,
                    ExpectedRs2: 0,
                    ExpectedImmediate: 9,
                    ExpectedDataType: DataTypeEnum.UINT64)
            ]);
    }
}
