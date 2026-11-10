using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;

namespace MinimalAsmApp.Examples.Memory;

using static CpuInstructions;

public sealed class MemoryLoadStoreExample : ICpuExample
{
    private const ulong Address = 0x2100;

    public string Name => "memory-load-store";

    public string Description => "Stores x1 to memory and loads the same doubleword back into x2.";

    public string Category => "03_Memory";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            AddImmediate(1, 0, 123),
            StoreDoubleword(1, Address),
            LoadDoubleword(2, Address));

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions
            {
                RegisterDump = [1, 2],
                MemoryDump = [Address]
            });

        execution.ExpectRegister(2, 123);
        execution.ExpectMemory(Address, 123);

        return CpuExampleResult.Ok(
            $"Expected x2=123 and mem[0x{Address:X}]=123.",
            execution.Registers,
            execution.Memory);
    }
}
