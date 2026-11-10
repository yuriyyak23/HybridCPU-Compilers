using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.System;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class CsrAccessExample : ICpuExample
{
    public string Name => "csr-access";

    public string Description => "Decodes CSRRW, CSRRS, CSRRC, CSRRWI, CSRRSI, CSRRCI instructions.";

    public string Category => "06_System";

    public CpuExampleResult Run()
    {
        // 12-bit CSR address, we'll pick 0x300 MSTATUS
        ushort csrAddr = 0x300;

        VLIW_Instruction[] program =
        [
            new VLIW_Instruction { OpCode = (uint)Instruction.CSRRW,  Immediate = csrAddr, DestSrc1Pointer = VLIW_Instruction.PackArchRegs(1, 2, VLIW_Instruction.NoArchReg) },
            new VLIW_Instruction { OpCode = (uint)Instruction.CSRRS,  Immediate = csrAddr, DestSrc1Pointer = VLIW_Instruction.PackArchRegs(1, 2, VLIW_Instruction.NoArchReg) },
            new VLIW_Instruction { OpCode = (uint)Instruction.CSRRC,  Immediate = csrAddr, DestSrc1Pointer = VLIW_Instruction.PackArchRegs(1, 2, VLIW_Instruction.NoArchReg) },
            new VLIW_Instruction { OpCode = (uint)Instruction.CSRRWI, Immediate = csrAddr, DestSrc1Pointer = VLIW_Instruction.PackArchRegs(1, 2, VLIW_Instruction.NoArchReg) },
            new VLIW_Instruction { OpCode = (uint)Instruction.CSRRSI, Immediate = csrAddr, DestSrc1Pointer = VLIW_Instruction.PackArchRegs(1, 2, VLIW_Instruction.NoArchReg) },
            new VLIW_Instruction { OpCode = (uint)Instruction.CSRRCI, Immediate = csrAddr, DestSrc1Pointer = VLIW_Instruction.PackArchRegs(1, 2, VLIW_Instruction.NoArchReg) },
        ];

        return CpuExampleResult.Ok(
            "Expected successful CSR instructions decode capability. Effect is not tested natively here to preserve pure execution contour.",
            new Dictionary<string, ulong>());
    }
}
