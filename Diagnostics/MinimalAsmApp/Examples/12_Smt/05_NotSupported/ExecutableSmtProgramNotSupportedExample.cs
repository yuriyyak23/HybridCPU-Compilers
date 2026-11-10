using MinimalAsmApp.Examples.Abstractions;

namespace MinimalAsmApp.Examples.Smt;

public sealed class ExecutableSmtProgramNotSupportedExample : ICpuExample
{
    public string Name => "smt-executable-program-not-supported";

    public string Description => "Documents why MinimalAsmApp does not yet emit one runnable multi-VT program.";

    public string Category => "12_Smt/05_NotSupported";

    public CpuExampleResult Run()
    {
        return CpuExampleResult.Ok(
            "The example runner currently compiles and executes a single VT0 NativeVLIW program. Multi-VT execution needs a small runner phase that publishes per-slot VT metadata through final bundle annotations and verifies per-VT register state.");
    }
}
