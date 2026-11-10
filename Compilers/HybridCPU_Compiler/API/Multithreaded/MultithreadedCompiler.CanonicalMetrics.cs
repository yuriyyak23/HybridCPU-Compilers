using System;
using HybridCPU.Compiler.Core.IR;

namespace HybridCPU.Compiler.Core.Multithreaded
{
    public partial class HybridCpuMultithreadedCompiler
    {
        private static int GetCanonicalBundleCount(HybridCpuCompiledProgram?[] canonicalPrograms)
        {
            ArgumentNullException.ThrowIfNull(canonicalPrograms);

            int canonicalBundleCount = 0;
            foreach (HybridCpuCompiledProgram? canonicalProgram in canonicalPrograms)
            {
                if (canonicalProgram is not null)
                {
                    canonicalBundleCount += canonicalProgram.BundleCount;
                }
            }

            return canonicalBundleCount;
        }

        private static double GetCanonicalBundleUtilization(HybridCpuCompiledProgram?[] canonicalPrograms, int totalInstructions)
        {
            ArgumentNullException.ThrowIfNull(canonicalPrograms);
            if (totalInstructions <= 0)
            {
                return 0d;
            }

            int canonicalBundleCount = GetCanonicalBundleCount(canonicalPrograms);
            return canonicalBundleCount == 0
                ? 0d
                : (double)totalInstructions / (canonicalBundleCount * HybridCpuSlotModel.SlotCount);
        }
    }
}
