using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;

namespace MinimalAsmApp.Examples.Registers;

using static CpuInstructions;

public sealed class RegisterMoveExample : ICpuExample
{
    public string Name => "register-move";

    public string Description => "Copies x1 to x2 through the canonical ADDI rd, rs1, 0 form.";

    public string Category => "01_Registers";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            AddImmediate(1, 0, 37),
            Move(2, 1));

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions { RegisterDump = [1, 2] });

        execution.ExpectRegister(1, 37);
        execution.ExpectRegister(2, 37);

        return CpuExampleResult.Ok(
            "Expected x1=37 and x2=37.",
            execution.Registers);
    }
}
