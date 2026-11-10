using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Privileged;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class TrapsAndReturnsExample : ICpuExample
{
    public string Name => "traps-and-returns";

    public string Description => "Decodes privileged ECALL, EBREAK, MRET, SRET, WFI instructions without activating externally owned boundaries.";

    public string Category => "15_Privileged";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program =
        [
            new VLIW_Instruction { OpCode = (uint)Instruction.ECALL },
            new VLIW_Instruction { OpCode = (uint)Instruction.EBREAK },
            new VLIW_Instruction { OpCode = (uint)Instruction.MRET },
            new VLIW_Instruction { OpCode = (uint)Instruction.SRET },
            new VLIW_Instruction { OpCode = (uint)Instruction.WFI },
        ];

        return CpuExampleResult.Ok(
            "Expected isolated legality/descriptor checking for trap instructions. No execution boundary is crossed.",
            new Dictionary<string, ulong>());
    }
}
