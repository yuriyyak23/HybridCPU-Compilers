// Scope-specific doubles: validate bridge decisions, not ISE execution or compiler integration.
namespace HybridCPU.Compiler.Core.IR
{
    public sealed class HybridCpuCompiledProgram { }
    public static class CompilerResultStore
    {
        public static bool TryGetResult(int id, out HybridCpuCompiledProgram program) { program = new(); return true; }
    }
}
namespace HybridCPU_ISE.Legacy
{
    public static class LegacyProcessorMachineStateSource { public static object SharedSyncRoot = new(); }
    public static class LegacyObservationServiceFactory { public static object CreateLegacyGlobalCompat(object value) => value; }
}
namespace CpuInterfaceBridge
{
    public sealed class IseCoreStateService(object ignored) : ICoreStateService
    {
        public CoreStateSnapshot GetCoreState(int id) => new() { LiveInstructionPointer = YAKSys_Hybrid_CPU.Processor.Pc };
        public byte[] ReadMemory(ulong address, int length) => new byte[length];
        public int GetTotalCores() => 1;
    }
}
namespace CpuInterfaceBridge.Legacy
{
    public static class LegacyHostCoreStateBridge { public static void PublishCoreStatInfoChanged(int id) { } }
}
namespace YAKSys_Hybrid_CPU
{
    public static class Processor
    {
        public static ulong Pc;
        public static Action? OnCycle;
        public static CPU_Core[] CPU_Cores = [new()];
        public sealed class CPU_Core
        {
            public PipelineControl GetPipelineControl() => new();
            public void PrepareExecutionStart(ulong pc) => Pc = pc;
            public void ExecutePipelineCycle() { Pc++; OnCycle?.Invoke(); }
            public sealed class PipelineControl { public bool Enabled => false; }
        }
    }
}
