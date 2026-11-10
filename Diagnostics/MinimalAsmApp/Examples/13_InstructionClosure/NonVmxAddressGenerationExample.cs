using HybridCPU.Compiler.Core.API.Facade;
using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.InstructionClosure;

using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class NonVmxAddressGenerationExample : ICpuExample
{
    public string Name => "non-vmx-address-generation";

    public string Description => "Emits SHxADD, ADD.UW, SHxADD.UW, and SLLI.UW through open address-generation helpers.";

    public string Category => "13_InstructionClosure";

    public CpuExampleResult Run()
    {
        return NonVmxCompilerExampleSupport.RunAppFacadeExample(
            "Compiler facade emitted canonical scalar address-generation carriers.",
            [
                new(
                    "SH1ADD",
                    "IAppAsmFacade.ShiftLeftOneAndAdd",
                    Instruction.SH1ADD,
                    facade => facade.ShiftLeftOneAndAdd(new AsmRegister(10), new AsmRegister(1), new AsmRegister(2)),
                    ExpectedRd: 10,
                    ExpectedRs1: 1,
                    ExpectedRs2: 2,
                    ExpectedDataType: DataTypeEnum.UINT64),
                new(
                    "SH2ADD",
                    "IAppAsmFacade.ShiftLeftTwoAndAdd",
                    Instruction.SH2ADD,
                    facade => facade.ShiftLeftTwoAndAdd(new AsmRegister(11), new AsmRegister(1), new AsmRegister(2)),
                    ExpectedRd: 11,
                    ExpectedRs1: 1,
                    ExpectedRs2: 2,
                    ExpectedDataType: DataTypeEnum.UINT64),
                new(
                    "SH3ADD",
                    "IAppAsmFacade.ShiftLeftThreeAndAdd",
                    Instruction.SH3ADD,
                    facade => facade.ShiftLeftThreeAndAdd(new AsmRegister(12), new AsmRegister(1), new AsmRegister(2)),
                    ExpectedRd: 12,
                    ExpectedRs1: 1,
                    ExpectedRs2: 2,
                    ExpectedDataType: DataTypeEnum.UINT64),
                new(
                    "ADD.UW",
                    "IAppAsmFacade.AddUnsignedWord",
                    Instruction.ADD_UW,
                    facade => facade.AddUnsignedWord(new AsmRegister(13), new AsmRegister(1), new AsmRegister(2)),
                    ExpectedRd: 13,
                    ExpectedRs1: 1,
                    ExpectedRs2: 2,
                    ExpectedDataType: DataTypeEnum.UINT64),
                new(
                    "SH1ADD.UW",
                    "IAppAsmFacade.ShiftLeftOneAndAddUnsignedWord",
                    Instruction.SH1ADD_UW,
                    facade => facade.ShiftLeftOneAndAddUnsignedWord(new AsmRegister(14), new AsmRegister(1), new AsmRegister(2)),
                    ExpectedRd: 14,
                    ExpectedRs1: 1,
                    ExpectedRs2: 2,
                    ExpectedDataType: DataTypeEnum.UINT64),
                new(
                    "SH2ADD.UW",
                    "IAppAsmFacade.ShiftLeftTwoAndAddUnsignedWord",
                    Instruction.SH2ADD_UW,
                    facade => facade.ShiftLeftTwoAndAddUnsignedWord(new AsmRegister(15), new AsmRegister(1), new AsmRegister(2)),
                    ExpectedRd: 15,
                    ExpectedRs1: 1,
                    ExpectedRs2: 2,
                    ExpectedDataType: DataTypeEnum.UINT64),
                new(
                    "SH3ADD.UW",
                    "IAppAsmFacade.ShiftLeftThreeAndAddUnsignedWord",
                    Instruction.SH3ADD_UW,
                    facade => facade.ShiftLeftThreeAndAddUnsignedWord(new AsmRegister(16), new AsmRegister(1), new AsmRegister(2)),
                    ExpectedRd: 16,
                    ExpectedRs1: 1,
                    ExpectedRs2: 2,
                    ExpectedDataType: DataTypeEnum.UINT64),
                new(
                    "SLLI.UW",
                    "IAppAsmFacade.ShiftLeftUnsignedWordByImmediate",
                    Instruction.SLLI_UW,
                    facade => facade.ShiftLeftUnsignedWordByImmediate(new AsmRegister(17), new AsmRegister(1), 4),
                    ExpectedRd: 17,
                    ExpectedRs1: 1,
                    ExpectedRs2: 0,
                    ExpectedImmediate: 4,
                    ExpectedDataType: DataTypeEnum.UINT64)
            ]);
    }
}
