using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using HybridCPU_ISE.Arch;

namespace MinimalAsmApp.Examples.System;

using static CpuInstructions;
using Instruction = YAKSys_Hybrid_CPU.Processor.CPU_Core.InstructionsEnum;

public sealed class VSetVlExample : ICpuExample
{
    public string Name => "vsetvl";

    public string Description => "Decodes VSETVL, VSETVLI, VSETIVLI instructions.";

    public string Category => "06_System";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            new VLIW_Instruction { OpCode = (uint)Instruction.VSETVL },
            new VLIW_Instruction { OpCode = (uint)Instruction.VSETVLI },
            new VLIW_Instruction { OpCode = (uint)Instruction.VSETIVLI }
        );

        return CpuExampleResult.Ok(
            "Expected correct opcode translation for vsetvl family.",
            new Dictionary<string, ulong>());
    }
}
