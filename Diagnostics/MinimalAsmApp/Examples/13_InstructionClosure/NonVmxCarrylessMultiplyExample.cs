using HybridCPU.Compiler.Core.API.Facade;
using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.InstructionClosure;

using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class NonVmxCarrylessMultiplyExample : ICpuExample
{
    public string Name => "non-vmx-carryless-multiply";

    public string Description => "Emits CLMUL, CLMULH, and CLMULR through the open compiler carry-less multiply helpers.";

    public string Category => "13_InstructionClosure";

    public CpuExampleResult Run()
    {
        return NonVmxCompilerExampleSupport.RunAppFacadeExample(
            "Compiler facade emitted canonical carry-less multiply carriers.",
            [
                new(
                    "CLMUL",
                    "IAppAsmFacade.BinaryPolynomialProductLow",
                    Instruction.CLMUL,
                    facade => facade.BinaryPolynomialProductLow(new AsmRegister(18), new AsmRegister(1), new AsmRegister(2)),
                    ExpectedRd: 18,
                    ExpectedRs1: 1,
                    ExpectedRs2: 2,
                    ExpectedDataType: DataTypeEnum.UINT64),
                new(
                    "CLMULH",
                    "IAppAsmFacade.BinaryPolynomialProductHigh",
                    Instruction.CLMULH,
                    facade => facade.BinaryPolynomialProductHigh(new AsmRegister(19), new AsmRegister(1), new AsmRegister(2)),
                    ExpectedRd: 19,
                    ExpectedRs1: 1,
                    ExpectedRs2: 2,
                    ExpectedDataType: DataTypeEnum.UINT64),
                new(
                    "CLMULR",
                    "IAppAsmFacade.BinaryPolynomialProductReverse",
                    Instruction.CLMULR,
                    facade => facade.BinaryPolynomialProductReverse(new AsmRegister(20), new AsmRegister(1), new AsmRegister(2)),
                    ExpectedRd: 20,
                    ExpectedRs1: 1,
                    ExpectedRs2: 2,
                    ExpectedDataType: DataTypeEnum.UINT64)
            ]);
    }
}
