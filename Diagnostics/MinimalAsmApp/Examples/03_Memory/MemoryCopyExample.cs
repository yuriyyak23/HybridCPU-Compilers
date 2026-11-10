using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;

namespace MinimalAsmApp.Examples.Memory;

using static CpuInstructions;

public sealed class MemoryCopyExample : ICpuExample
{
    private const ulong SourceAddress = 0x2200;
    private const ulong DestinationAddress = 0x2210;

    public string Name => "memory-copy";

    public string Description => "Copies one memory cell to another through x2.";

    public string Category => "03_Memory";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            AddImmediate(1, 0, 77),
            StoreDoubleword(1, SourceAddress),
            LoadDoubleword(2, SourceAddress),
            StoreDoubleword(2, DestinationAddress),
            LoadDoubleword(3, DestinationAddress));

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions
            {
                RegisterDump = [1, 2, 3],
                MemoryDump = [SourceAddress, DestinationAddress]
            });

        execution.ExpectRegister(3, 77);
        execution.ExpectMemory(SourceAddress, 77);
        execution.ExpectMemory(DestinationAddress, 77);

        return CpuExampleResult.Ok(
            $"Expected mem[0x{SourceAddress:X}]=77 and mem[0x{DestinationAddress:X}]=77.",
            execution.Registers,
            execution.Memory);
    }
}
