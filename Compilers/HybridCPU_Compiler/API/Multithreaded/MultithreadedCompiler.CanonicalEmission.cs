using System;
using HybridCPU.Compiler.Core.IR;

namespace HybridCPU.Compiler.Core.Multithreaded
{
    public partial class HybridCpuMultithreadedCompiler
    {
        /// <summary>
        /// Compiles each populated VT-local instruction stream through the canonical Stage 5/6 path and emits each fetch-ready image to the corresponding base address.
        /// </summary>
        /// <param name="emissionBaseAddresses">Per-VT emission base addresses. The array must contain one address for each VT slot.</param>
        /// <returns>Sparse per-VT canonical Stage 5/6 artifacts with emission metadata for populated VTs.</returns>
        public HybridCpuCompiledProgram?[] CompileCanonicalThreadPrograms(ulong[] emissionBaseAddresses)
        {
            ArgumentNullException.ThrowIfNull(emissionBaseAddresses);
            if (emissionBaseAddresses.Length != _threadContexts.Length)
            {
                throw new ArgumentException($"Expected {_threadContexts.Length} emission base addresses (one per VT).", nameof(emissionBaseAddresses));
            }

            HybridCpuCompiledProgram?[] canonicalPrograms = new HybridCpuCompiledProgram?[_threadContexts.Length];
            for (byte vt = 0; vt < _threadContexts.Length; vt++)
            {
                if (_threadContexts[vt].InstructionCount == 0)
                {
                    continue;
                }

                canonicalPrograms[vt] = _threadContexts[vt].CompileProgram(emissionBaseAddresses[vt]);
            }

            return canonicalPrograms;
        }
    }
}
