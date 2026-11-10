using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Flags;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class ZeroFlagAlternativeExample : ICpuExample
{
    public string Name => "zero-flag-alternative";

    public string Description => "Shows a zero test without a global FLAGS register by using SLTIU.";

    public string Category => "07_Flags";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            Binary(Instruction.SLTU, 1, 0, 0),
            new VLIW_Instruction
            {
                OpCode = (uint)Instruction.SLTIU,
                DataTypeValue = DataTypeEnum.INT64,
                Immediate = 1,
                DestSrc1Pointer = VLIW_Instruction.PackArchRegs(2, 0, VLIW_Instruction.NoArchReg)
            });

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions { RegisterDump = [1, 2] });

        execution.ExpectRegister(1, 0);
        execution.ExpectRegister(2, 1);

        return CpuExampleResult.Ok(
            "Expected x1=(0<0)=0 and x2=(x0<1)=1. x2 acts as a zero-test result.",
            execution.Registers,
            notes:
            [
                "The current simple examples do not expose a global Zero Flag register; compare instructions write boolean results into GPRs."
            ]);
    }
}
