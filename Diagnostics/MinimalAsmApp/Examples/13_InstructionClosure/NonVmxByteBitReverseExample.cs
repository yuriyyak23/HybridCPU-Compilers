using HybridCPU.Compiler.Core.API.Facade;
using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.InstructionClosure;

using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class NonVmxByteBitReverseExample : ICpuExample
{
    public string Name => "non-vmx-byte-bit-reverse";

    public string Description => "Emits REV8 and BREV8 through the open compiler byte/bit reverse helpers.";

    public string Category => "13_InstructionClosure";

    public CpuExampleResult Run()
    {
        return NonVmxCompilerExampleSupport.RunAppFacadeExample(
            "Compiler facade emitted canonical REV8 and BREV8 carriers.",
            [
                new(
                    "REV8",
                    "IAppAsmFacade.ReverseByteOrder",
                    Instruction.REV8,
                    facade => facade.ReverseByteOrder(new AsmRegister(18), new AsmRegister(1)),
                    ExpectedRd: 18,
                    ExpectedRs1: 1,
                    ExpectedRs2: 0,
                    ExpectedDataType: DataTypeEnum.UINT64),
                new(
                    "BREV8",
                    "IAppAsmFacade.ReverseBitsInEachByte",
                    Instruction.BREV8,
                    facade => facade.ReverseBitsInEachByte(new AsmRegister(19), new AsmRegister(1)),
                    ExpectedRd: 19,
                    ExpectedRs1: 1,
                    ExpectedRs2: 0,
                    ExpectedDataType: DataTypeEnum.UINT64)
            ]);
    }
}
