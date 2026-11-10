using MinimalAsmApp.Examples.Abstractions;

namespace MinimalAsmApp.Examples.StackNotSupported;

public sealed class StackNotSupportedExample : ICpuExample
{
    public string Name => "stack-not-supported";

    public string Description => "Documents stack/call support missing from the simple ISA example surface.";

    public string Category => "06_Stack_NotSupported";

    public CpuExampleResult Run()
    {
        return CpuExampleResult.Ok(
            "No PUSH/POP/CALL/RET instruction family is exposed as a simple canonical example surface in this project.",
            notes:
            [
                "A stack demo would need an agreed stack pointer register convention, memory push/pop helpers, and a safe call/return target helper.",
                "JAL/JALR can be a foundation for call/return later, but that should be a separate control-flow phase."
            ]);
    }
}
