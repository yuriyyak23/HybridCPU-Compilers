namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Directed edge between two basic blocks.
    /// </summary>
    public sealed record IrControlFlowEdge(int SourceBlockId, int TargetBlockId, IrControlFlowEdgeKind Kind);
}
