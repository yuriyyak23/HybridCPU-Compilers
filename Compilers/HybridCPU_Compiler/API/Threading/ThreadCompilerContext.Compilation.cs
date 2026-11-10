using HybridCPU.Compiler.Core.IR;

namespace HybridCPU.Compiler.Core.Threading
{
    public partial class HybridCpuThreadCompilerContext
    {
        /// <summary>
        /// Compiles the current VT-local instruction buffer through scheduling, bundling, lowering, and serialization.
        /// </summary>
        public HybridCpuCompiledProgram CompileProgram()
        {
            return GetOrCompileCanonicalProgram();
        }

        /// <summary>
        /// Compiles the current VT-local instruction buffer and emits the fetch-ready image to main memory.
        /// </summary>
        public HybridCpuCompiledProgram CompileProgram(ulong baseAddress)
        {
            return EmitCanonicalProgram(baseAddress);
        }

    }
}
