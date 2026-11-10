using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.DebugTrace;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class RegisterDumpExample : ICpuExample
{
    public string Name => "register-dump";

    public string Description => "Shows a before/after style register dump for a short ALU program.";

    public string Category => "09_DebugTrace";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            AddImmediate(1, 0, 11),
            AddImmediate(2, 0, 4),
            Binary(Instruction.SUB, 3, 1, 2));

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions { RegisterDump = [1, 2, 3] });

        execution.ExpectRegister(3, 7);

        return CpuExampleResult.Ok(
            "Before execution x1/x2/x3 are zero in a fresh CPU instance. Expected after execution: x1=11, x2=4, x3=7.",
            execution.Registers);
    }
}
