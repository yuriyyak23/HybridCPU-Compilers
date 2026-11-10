using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.Programs;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class ArraySumExample : ICpuExample
{
    private const ulong A0 = 0x3000;
    private const ulong A1 = 0x3008;
    private const ulong A2 = 0x3010;
    private const ulong A3 = 0x3018;

    public string Name => "array-sum";

    public string Description => "Loads four memory cells and sums them in a register.";

    public string Category => "08_Programs";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            LoadDoubleword(1, A0),
            LoadDoubleword(2, A1),
            LoadDoubleword(3, A2),
            LoadDoubleword(4, A3),
            Binary(Instruction.ADD, 10, 1, 2),
            Binary(Instruction.ADD, 10, 10, 3),
            Binary(Instruction.ADD, 10, 10, 4));

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions
            {
                InitialMemory = new Dictionary<ulong, ulong>
                {
                    [A0] = 4,
                    [A1] = 6,
                    [A2] = 8,
                    [A3] = 10
                },
                RegisterDump = [1, 2, 3, 4, 10],
                MemoryDump = [A0, A1, A2, A3]
            });

        execution.ExpectRegister(10, 28);

        return CpuExampleResult.Ok(
            "Expected x10=4+6+8+10=28.",
            execution.Registers,
            execution.Memory);
    }
}
