using MinimalAsmApp.Examples.Abstractions;

namespace MinimalAsmApp.Examples.Loops;

public sealed class SumLoopNotSupportedExample : ICpuExample
{
    public string Name => "sum-loop-not-supported";

    public string Description => "Documents the missing safe loop wrapper for sum loops.";

    public string Category => "05_Loops";

    public CpuExampleResult Run()
    {
        return CpuExampleResult.Ok(
            "A real sum loop is intentionally left as TODO until taken branch targets are handled by a small example-level relocation helper.",
            notes:
            [
                "The array-sum program under 08_Programs shows the same data path as a straight-line sequence."
            ]);
    }
}
