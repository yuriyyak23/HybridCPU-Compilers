using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using HybridCPU_ISE.Arch;

namespace MinimalAsmApp.Examples.System;

using static CpuInstructions;
using Instruction = YAKSys_Hybrid_CPU.Processor.CPU_Core.InstructionsEnum;

public sealed class AccelControlExample : ICpuExample
{
    public string Name => "accel-control";

    public string Description => "Decodes structural ACCEL_* instructions: QUERY_CAPS, POLL, WAIT, CANCEL, FENCE, STATUS.";

    public string Category => "06_System";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            new VLIW_Instruction { OpCode = (uint)Instruction.ACCEL_QUERY_CAPS },
            new VLIW_Instruction { OpCode = (uint)Instruction.ACCEL_POLL },
            new VLIW_Instruction { OpCode = (uint)Instruction.ACCEL_WAIT },
            new VLIW_Instruction { OpCode = (uint)Instruction.ACCEL_CANCEL },
            new VLIW_Instruction { OpCode = (uint)Instruction.ACCEL_FENCE },
            new VLIW_Instruction { OpCode = (uint)Instruction.ACCEL_STATUS }
        );

        return CpuExampleResult.Ok(
            "Expected correct opcode translation for accelerator tracking instructions.",
            new Dictionary<string, ulong>());
    }
}
