using HybridCPU.Compiler.Core.API.Facade;
using MinimalAsmApp.Examples.Abstractions;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.InstructionClosure;

using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class NonVmxRdcycleExample : ICpuExample
{
    public string Name => "non-vmx-rdcycle";

    public string Description => "Emits RDCYCLE through the open platform compiler system-counter helper.";

    public string Category => "13_InstructionClosure";

    public CpuExampleResult Run()
    {
        return NonVmxCompilerExampleSupport.RunPlatformFacadeExample(
            "Platform facade emitted a canonical RDCYCLE carrier; RDTIME/RDINSTRET/PAUSE remain closed.",
            [
                new(
                    "RDCYCLE",
                    "IPlatformAsmFacade.ReadSystemCycleCounter",
                    Instruction.RDCYCLE,
                    facade => facade.ReadSystemCycleCounter(new AsmRegister(21)),
                    ExpectedRd: 21,
                    ExpectedRs1: 0,
                    ExpectedRs2: 0,
                    ExpectedDataType: DataTypeEnum.UINT64)
            ]);
    }
}
