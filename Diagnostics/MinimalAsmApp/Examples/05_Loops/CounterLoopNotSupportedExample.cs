using MinimalAsmApp.Examples.Abstractions;

namespace MinimalAsmApp.Examples.Loops;

public sealed class CounterLoopNotSupportedExample : ICpuExample
{
    public string Name => "counter-loop-not-supported";

    public string Description => "Documents why executable loop examples are not emitted yet.";

    public string Category => "05_Loops";

    public CpuExampleResult Run()
    {
        return CpuExampleResult.Ok(
            "Counter loops require a taken backward branch. The current MinimalAsmApp helper keeps examples straight-line until branch target relocation is wrapped cleanly.",
            notes:
            [
                "Use the straight-line program examples for arithmetic validation now.",
                "Add a separate control-flow helper phase before adding executable loop back-edges."
            ]);
    }
}
