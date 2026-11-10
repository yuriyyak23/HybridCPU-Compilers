using MinimalAsmApp.Examples.Abstractions;
using MinimalAsmApp.Examples.Support;
using YAKSys_Hybrid_CPU;

namespace MinimalAsmApp.Examples.DebugTrace;

using static CpuInstructions;
using Instruction = Processor.CPU_Core.InstructionsEnum;

public sealed class StepByStepTraceExample : ICpuExample
{
    public string Name => "step-trace";

    public string Description => "Captures a compact per-cycle pipeline trace while a tiny program retires.";

    public string Category => "09_DebugTrace";

    public CpuExampleResult Run()
    {
        VLIW_Instruction[] program = WithFences(
            AddImmediate(1, 0, 2),
            AddImmediate(2, 0, 3),
            Binary(Instruction.ADD, 3, 1, 2));

        CpuProgramExecution execution = CpuProgramExecutor.Run(
            program,
            new CpuProgramRunOptions
            {
                RegisterDump = [1, 2, 3],
                CaptureTrace = true,
                MaxTraceLines = 24,
                TraceRegisters = [1, 2, 3]
            });

        execution.ExpectRegister(3, 5);

        return CpuExampleResult.Ok(
            "Expected final x3=5. Trace rows show cycle, retired count, active PC and selected registers.",
            execution.Registers,
            trace: execution.Trace);
    }
}
