using MinimalAsmApp.Examples.Abstractions;

namespace MinimalAsmApp.Examples.Flags;

public sealed class CarryFlagNotSupportedExample : ICpuExample
{
    public string Name => "carry-flag-not-supported";

    public string Description => "Documents that carry/overflow flags are not part of this simple example surface.";

    public string Category => "07_Flags";

    public CpuExampleResult Run()
    {
        return CpuExampleResult.Ok(
            "The current examples use explicit compare/set instructions instead of implicit carry/overflow flags.",
            notes:
            [
                "To add carry demos, first expose the architectural flag/CSR contract or add explicit add-with-carry style instructions in a separate ISA phase."
            ]);
    }
}
