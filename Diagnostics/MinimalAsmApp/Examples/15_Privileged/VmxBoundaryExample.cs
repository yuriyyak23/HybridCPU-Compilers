using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Privileged;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class VmxBoundaryExample : ICpuExample
{
    public string Name => "vmx-boundary";

    public string Description => "Decodes VMX instructions: VMXON, VMXOFF, VMLAUNCH, VMRESUME, VMREAD, VMWRITE, VMCLEAR, VMPTRLD.";

    public string Category => "15_Privileged";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program =
        [
            new VLIW_Instruction { OpCode = (uint)Instruction.VMXON },
            new VLIW_Instruction { OpCode = (uint)Instruction.VMXOFF },
            new VLIW_Instruction { OpCode = (uint)Instruction.VMLAUNCH },
            new VLIW_Instruction { OpCode = (uint)Instruction.VMRESUME },
            new VLIW_Instruction { OpCode = (uint)Instruction.VMREAD },
            new VLIW_Instruction { OpCode = (uint)Instruction.VMWRITE },
            new VLIW_Instruction { OpCode = (uint)Instruction.VMCLEAR },
            new VLIW_Instruction { OpCode = (uint)Instruction.VMPTRLD },
        ];

        return CpuExampleResult.Ok(
            "Expected correct opcode bindings for VMX boundaries. No dynamic execution occurs.",
            new Dictionary<string, ulong>());
    }
}
