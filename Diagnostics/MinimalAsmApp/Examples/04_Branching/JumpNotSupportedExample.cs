using MinimalAsmApp.Examples.Abstractions;

namespace MinimalAsmApp.Examples.Branching;

public sealed class JumpNotSupportedExample : ICpuExample
{
    public string Name => "jump-not-supported";

    public string Description => "Documents the current limitation for safe standalone jump demos.";

    public string Category => "04_Branching";

    public CpuExampleResult Run()
    {
        return CpuExampleResult.Ok(
            "JAL/JALR exist in the ISA surface, but this example set does not emit a taken jump yet.",
            notes:
            [
                "A practical jump demo needs a tiny label/relocation helper so examples can target emitted bundle addresses safely.",
                "No instruction-set changes are required for that follow-up."
            ]);
    }
}
