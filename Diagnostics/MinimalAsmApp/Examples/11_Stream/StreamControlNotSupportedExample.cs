using MinimalAsmApp.Examples.Abstractions;

namespace MinimalAsmApp.Examples.Stream;

public sealed class StreamControlNotSupportedExample : ICpuExample
{
    public string Name => "stream-control-not-supported";

    public string Description => "Documents the current fail-closed status of STREAM_SETUP/STREAM_START/STREAM_WAIT in mainline execution.";

    public string Category => "11_Stream";

    public CpuExampleResult Run()
    {
        return CpuExampleResult.Ok(
            "STREAM_SETUP/STREAM_START/STREAM_WAIT are published ISA opcodes, but the current execution dispatcher rejects them until an authoritative stream-control runtime path is wired.");
    }
}
