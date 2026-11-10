using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;

namespace MinimalAsmApp.Examples.Registers;

using static CpuInstructions;

public sealed class LoadImmediateExample : ICpuExample
{
    public string Name => "load-immediate";

    public string Description => "Writes a small immediate value into an architectural register with ADDI.";

    public string Category => "01_Registers";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(AddImmediate(1, 0, 42));

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions { RegisterDump = [1] });

        execution.ExpectRegister(1, 42);

        return CpuExampleResult.Ok(
            "Expected x1=42. x0 is the zero source register.",
            execution.Registers);
    }
}
