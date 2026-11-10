using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.HelloCpu;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class HelloCpuExample : ICpuExample
{
    private const ulong ResultAddress = 0x2000;

    public string Name => "hello-cpu";

    public string Description => "Minimal ALU plus memory round-trip: 5 + 7 -> x3 -> memory -> x4.";

    public string Category => "00_HelloCpu";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            AddImmediate(1, 0, 5),
            AddImmediate(2, 0, 7),
            Binary(Instruction.ADD, 3, 1, 2),
            StoreDoubleword(3, ResultAddress),
            LoadDoubleword(4, ResultAddress));

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions
            {
                RegisterDump = [1, 2, 3, 4],
                MemoryDump = [ResultAddress],
                // The ten-bundle form needs the longer drain for the final LSU writeback.
                DrainCycles = 16
            });

        execution.ExpectRegister(3, 12);
        execution.ExpectRegister(4, 12);
        execution.ExpectMemory(ResultAddress, 12);

        return CpuExampleResult.Ok(
            $"Expected x3=12, x4=12, mem[0x{ResultAddress:X}]=12. " +
            $"Bundles={execution.BundleCount}, retired={execution.InstructionsRetired}, cycles={execution.Cycles}.",
            execution.Registers,
            execution.Memory);
    }
}
