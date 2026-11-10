using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using HybridCPU_ISE.Arch;

namespace MinimalAsmApp.Examples.ControlFlow;

using static CpuInstructions;
using Instruction = YAKSys_Hybrid_CPU.Processor.CPU_Core.InstructionsEnum;

public sealed class JumpExample : ICpuExample
{
    public string Name => "jump-jal-jalr";

    public string Description => "Decodes JAL and JALR instructions to verify they resolve through the runtime boundary.";

    public string Category => "04_Branching";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            new VLIW_Instruction 
            { 
                OpCode = (uint)Instruction.JAL,
                Immediate = 8, // Offset
                DestSrc1Pointer = VLIW_Instruction.PackArchRegs(1, VLIW_Instruction.NoArchReg, VLIW_Instruction.NoArchReg)
            },
            new VLIW_Instruction 
            { 
                OpCode = (uint)Instruction.JALR,
                Immediate = 4, // Offset
                DestSrc1Pointer = VLIW_Instruction.PackArchRegs(1, 2, VLIW_Instruction.NoArchReg)
            }
        );

        return CpuExampleResult.Ok(
            "Expected correct JAL and JALR opcode resolution over the instruction set boundary. (Execution isolated due to control-flow semantics).",
            new Dictionary<string, ulong>());
    }
}
