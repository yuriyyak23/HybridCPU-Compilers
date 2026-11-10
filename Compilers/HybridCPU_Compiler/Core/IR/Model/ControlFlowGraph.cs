using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Control-flow graph for a single virtual-thread IR program.
    /// </summary>
    public sealed record ControlFlowGraph(
        IReadOnlyList<IrBasicBlock> Blocks,
        IReadOnlyList<IrControlFlowEdge> Edges);
}
