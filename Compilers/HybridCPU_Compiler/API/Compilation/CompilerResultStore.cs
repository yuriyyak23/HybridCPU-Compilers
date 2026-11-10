using System.Collections.Concurrent;

namespace HybridCPU.Compiler.Core.IR
{
    public static class CompilerResultStore
    {
        private static readonly ConcurrentDictionary<int, HybridCpuCompiledProgram> _results = new();

        public static void StoreResult(int vtId, HybridCpuCompiledProgram program)
        {
            ArgumentNullException.ThrowIfNull(program);
            program.ValidateRuntimeContractCompatibility($"{nameof(CompilerResultStore)}.{nameof(StoreResult)}");
            _results[vtId] = program;
        }

        public static bool TryGetResult(int vtId, out HybridCpuCompiledProgram program)
        {
            return _results.TryGetValue(vtId, out program!);
        }
    }
}
