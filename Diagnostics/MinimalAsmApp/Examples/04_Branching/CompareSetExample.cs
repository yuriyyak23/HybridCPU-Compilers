using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Branching;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class CompareSetExample : ICpuExample
{
    public string Name => "compare-set";

    public string Description => "Uses SLT/SLTI as the simple compare result surface for branch conditions.";

    public string Category => "04_Branching";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            AddImmediate(1, 0, 3),
            AddImmediate(2, 0, 9),
            Binary(Instruction.SLT, 3, 1, 2),
            Binary(Instruction.SLT, 4, 2, 1),
            AddImmediate(5, 1, 0),
            new VLIW_Instruction
            {
                OpCode = (uint)Instruction.SLTI,
                DataTypeValue = DataTypeEnum.INT64,
                Immediate = 5,
                DestSrc1Pointer = VLIW_Instruction.PackArchRegs(6, 1, VLIW_Instruction.NoArchReg)
            });

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions { RegisterDump = [1, 2, 3, 4, 6] });

        execution.ExpectRegister(3, 1);
        execution.ExpectRegister(4, 0);
        execution.ExpectRegister(6, 1);

        return CpuExampleResult.Ok(
            "Expected x3=(3<9)=1, x4=(9<3)=0, x6=(3<5)=1.",
            execution.Registers);
    }
}
