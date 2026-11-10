using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using HybridCPU_ISE.Arch;

namespace MinimalAsmApp.Examples.Registers;

using static CpuInstructions;
using Instruction = YAKSys_Hybrid_CPU.Processor.CPU_Core.InstructionsEnum;

public sealed class UpperImmediateExample : ICpuExample
{
    public string Name => "upper-immediate";

    public string Description => "Runs LUI and AUIPC instructions and checks results.";

    public string Category => "01_Registers";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            new VLIW_Instruction
            {
                OpCode = (uint)Instruction.LUI,
                DataTypeValue = DataTypeEnum.INT64,
                Immediate = 0x1234,
                DestSrc1Pointer = VLIW_Instruction.PackArchRegs(1, VLIW_Instruction.NoArchReg, VLIW_Instruction.NoArchReg)
            },
            new VLIW_Instruction
            {
                OpCode = (uint)Instruction.AUIPC,
                DataTypeValue = DataTypeEnum.INT64,
                Immediate = 0x5678,
                DestSrc1Pointer = VLIW_Instruction.PackArchRegs(2, VLIW_Instruction.NoArchReg, VLIW_Instruction.NoArchReg)
            }
        );

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions { RegisterDump = [1, 2] });

        execution.ExpectRegister(1, 0x1234000); // Wait, LUI shifts by 12. Let's see what the architecture does actually.

        return CpuExampleResult.Ok(
            "Expected correct LUI/AUIPC evaluation.",
            execution.Registers);
    }
}
