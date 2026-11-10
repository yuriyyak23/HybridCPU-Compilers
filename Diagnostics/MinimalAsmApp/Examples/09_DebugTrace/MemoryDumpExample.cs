using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;

namespace MinimalAsmApp.Examples.DebugTrace;

using static CpuInstructions;

public sealed class MemoryDumpExample : ICpuExample
{
    private const ulong Address = 0x3300;

    public string Name => "memory-dump";

    public string Description => "Shows memory before/after through the result memory dump.";

    public string Category => "09_DebugTrace";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            AddImmediate(1, 0, 88),
            StoreDoubleword(1, Address),
            LoadDoubleword(2, Address));

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions
            {
                RegisterDump = [1, 2],
                MemoryDump = [Address]
            });

        execution.ExpectRegister(2, 88);
        execution.ExpectMemory(Address, 88);

        return CpuExampleResult.Ok(
            $"Expected memory dump to contain mem[0x{Address:X}]=88.",
            execution.Registers,
            execution.Memory);
    }
}
