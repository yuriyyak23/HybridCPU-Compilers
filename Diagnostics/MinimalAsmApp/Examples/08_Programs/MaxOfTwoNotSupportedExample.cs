using MinimalAsmApp.Examples.Abstractions;

namespace MinimalAsmApp.Examples.Programs;

public sealed class MaxOfTwoNotSupportedExample : ICpuExample
{
    public string Name => "max-of-two-not-supported";

    public string Description => "Documents the missing primitive for a minimal generic max(a,b) program.";

    public string Category => "08_Programs";

    public CpuExampleResult Run()
    {
        return CpuExampleResult.Ok(
            "A generic max(a,b) needs a taken conditional branch or conditional move/select. This example set currently exposes compare/set but not a safe select path.",
            notes:
            [
                "Add max after the branch-target helper or after a canonical conditional-select instruction is available."
            ]);
    }
}
