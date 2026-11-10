using HybridCPU.Compiler.Core.API.Facade;
using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.InstructionClosure;

using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class NonVmxScalarExtensionExample : ICpuExample
{
    public string Name => "non-vmx-scalar-extension";

    public string Description => "Emits SEXT.B, SEXT.H, ZEXT.H, SEXT.W, and ZEXT.W through the open compiler scalar extension helpers.";

    public string Category => "13_InstructionClosure";

    public CpuExampleResult Run()
    {
        return NonVmxCompilerExampleSupport.RunAppFacadeExample(
            "Compiler facade emitted canonical byte/half/word scalar extension carriers.",
            [
                new(
                    "SEXT.B",
                    "IAppAsmFacade.SignExtendByte",
                    Instruction.SEXT_B,
                    facade => facade.SignExtendByte(new AsmRegister(20), new AsmRegister(1)),
                    ExpectedRd: 20,
                    ExpectedRs1: 1,
                    ExpectedRs2: 0,
                    ExpectedDataType: DataTypeEnum.UINT64),
                new(
                    "SEXT.H",
                    "IAppAsmFacade.SignExtendHalf",
                    Instruction.SEXT_H,
                    facade => facade.SignExtendHalf(new AsmRegister(21), new AsmRegister(1)),
                    ExpectedRd: 21,
                    ExpectedRs1: 1,
                    ExpectedRs2: 0,
                    ExpectedDataType: DataTypeEnum.UINT64),
                new(
                    "ZEXT.H",
                    "IAppAsmFacade.ZeroExtendHalf",
                    Instruction.ZEXT_H,
                    facade => facade.ZeroExtendHalf(new AsmRegister(22), new AsmRegister(1)),
                    ExpectedRd: 22,
                    ExpectedRs1: 1,
                    ExpectedRs2: 0,
                    ExpectedDataType: DataTypeEnum.UINT64),
                new(
                    "SEXT.W",
                    "IAppAsmFacade.SignExtendWord",
                    Instruction.SEXT_W,
                    facade => facade.SignExtendWord(new AsmRegister(23), new AsmRegister(1)),
                    ExpectedRd: 23,
                    ExpectedRs1: 1,
                    ExpectedRs2: 0,
                    ExpectedDataType: DataTypeEnum.INT32),
                new(
                    "ZEXT.W",
                    "IAppAsmFacade.ZeroExtendWord",
                    Instruction.ZEXT_W,
                    facade => facade.ZeroExtendWord(new AsmRegister(24), new AsmRegister(1)),
                    ExpectedRd: 24,
                    ExpectedRs1: 1,
                    ExpectedRs2: 0,
                    ExpectedDataType: DataTypeEnum.INT32)
            ]);
    }
}
