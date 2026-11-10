using System.Collections.Generic;
using HybridCPU.Compiler.Core.IR;

using HybridCPU.Compiler.Core.Support;
using HybridCPU.Compiler.Core.Threading;

namespace HybridCPU.Compiler.Core.Multithreaded
{
    public partial class HybridCpuMultithreadedCompiler
    {
        /// <summary>
        /// Builds normalized IR programs for all non-empty virtual-thread instruction streams.
        /// </summary>
        public IReadOnlyList<IrProgram> BuildIrPrograms()
        {
            List<IrProgram> programs = new List<IrProgram>(_threadContexts.Length);
            for (int index = 0; index < _threadContexts.Length; index++)
            {
                if (_threadContexts[index].InstructionCount == 0)
                {
                    continue;
                }

                programs.Add(_threadContexts[index].BuildIrProgram());
            }

            return programs;
        }
    }
}
