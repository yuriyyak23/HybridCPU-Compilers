namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Represents one directional dependence carried across a control-flow graph edge.
    /// </summary>
    public sealed record IrInterBlockDependency(
        int SourceBlockId,
        int TargetBlockId,
        IrControlFlowEdgeKind EdgeKind,
        IrInstructionDependency Dependency);
}
