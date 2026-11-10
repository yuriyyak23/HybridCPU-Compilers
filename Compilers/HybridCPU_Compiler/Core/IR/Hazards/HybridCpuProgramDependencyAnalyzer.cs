using System;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Builds the full Stage 4 dependence query surface for one IR program.
    /// </summary>
    public sealed class HybridCpuProgramDependencyAnalyzer
    {
        private readonly HybridCpuBasicBlockDependencyAnalyzer _basicBlockAnalyzer = new();
        private readonly HybridCpuInterBlockDependencyAnalyzer _interBlockAnalyzer = new();

        /// <summary>
        /// Builds intra-block and inter-block dependence graphs for a program.
        /// </summary>
        public IrProgramDependencyGraph AnalyzeProgram(IrProgram program)
        {
            ArgumentNullException.ThrowIfNull(program);

            return new IrProgramDependencyGraph(
                BlockGraphs: _basicBlockAnalyzer.AnalyzeProgram(program),
                InterBlockGraph: _interBlockAnalyzer.AnalyzeProgram(program));
        }
    }
}
