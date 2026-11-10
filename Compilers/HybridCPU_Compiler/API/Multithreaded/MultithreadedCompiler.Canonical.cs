using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Threading;

namespace HybridCPU.Compiler.Core.Multithreaded
{
    public partial class HybridCpuMultithreadedCompiler
    {
        /// <summary>
        /// Compiles each populated VT-local instruction stream through the canonical Stage 5/6 path without constructing the legacy interleaved binary output.
        /// </summary>
        public HybridCpuCompiledProgram?[] CompileCanonicalThreadPrograms()
        {
            return CompileCanonicalThreadPrograms(_threadContexts);
        }

        internal HybridCpuCompiledProgram?[] CompileCanonicalThreadPrograms(HybridCpuThreadCompilerContext[] threadContexts)
        {
            HybridCpuCompiledProgram?[] canonicalPrograms = new HybridCpuCompiledProgram?[4];

            for (byte vt = 0; vt < 4; vt++)
            {
                if (threadContexts[vt].InstructionCount == 0)
                {
                    continue;
                }

                canonicalPrograms[vt] = threadContexts[vt].CompileProgram();
            }

            return canonicalPrograms;
        }
    }
}
