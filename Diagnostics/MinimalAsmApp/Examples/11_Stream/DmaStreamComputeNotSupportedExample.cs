using MinimalAsmApp.Examples.Abstractions;

namespace MinimalAsmApp.Examples.Stream;

public sealed class DmaStreamComputeNotSupportedExample : ICpuExample
{
    public string Name => "dma-stream-compute-not-supported";

    public string Description => "Documents why lane6 DmaStreamCompute is not emitted as a MinimalAsmApp native VLIW example yet.";

    public string Category => "11_Stream";

    public CpuExampleResult Run()
    {
        return CpuExampleResult.Ok(
            "DmaStreamCompute requires a typed descriptor sideband and owner/domain guard decision; MinimalAsmApp keeps it documented until a small safe descriptor builder is exposed.");
    }
}
