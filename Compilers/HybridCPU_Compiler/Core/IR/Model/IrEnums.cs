using System;

namespace HybridCPU.Compiler.Core.IR
{
    internal enum SystemEventKind : byte
    {
        None,
        Fence,
        FenceI,
        Ecall,
        Ebreak,
        Mret,
        Sret,
        Wfi,
        Wfe,
        Sev,
        Yield,
        PodBarrier,
        VtBarrier
    }

    /// <summary>
    /// Operand kinds used by the normalized compiler IR.
    /// </summary>
    public enum IrOperandKind
    {
        None = 0,
        Pointer = 1,
        Immediate = 2,
        PredicateMask = 3,
        StreamLength = 4,
        Stride = 5,
        RowStride = 6,
        Tile = 7,
        MemoryAddress = 8,
        ArchitecturalRegister = 9,
        VirtualValue = 10,
        Constant = 11
    }

    /// <summary>
    /// Coarse resource classes used by early hazard-aware analysis.
    /// </summary>
    public enum IrResourceClass
    {
        Unknown = 0,
        ScalarAlu = 1,
        VectorAlu = 2,
        LoadStore = 3,
        ControlFlow = 4,
        System = 5,
        DmaStream = 6
    }

    /// <summary>
    /// Latency buckets used by the initial IR bootstrap.
    /// </summary>
    public enum IrLatencyClass
    {
        Unknown = 0,
        SingleCycle = 1,
        Vector = 2,
        LoadUse = 3,
        ControlFlow = 4,
        Serialized = 5
    }

    /// <summary>
    /// Compiler-owned structural slot class. Numeric values are part of the native
    /// compiler contract; runtime adapters translate explicitly at the boundary.
    /// </summary>
    public enum IrSlotClass : byte
    {
        AluClass = 0,
        LsuClass = 1,
        DmaStreamClass = 2,
        BranchControl = 3,
        SystemSingleton = 4,
        MatrixTileStreamClass = 5,
        Unclassified = 7
    }

    /// <summary>
    /// Control-flow behavior classification for IR instructions.
    /// </summary>
    public enum IrControlFlowKind
    {
        None = 0,
        ConditionalBranch = 1,
        UnconditionalBranch = 2,
        Return = 3,
        Stop = 4,
        Call = 5
    }

    /// <summary>
    /// Edge kinds currently materialized in the control-flow graph.
    /// </summary>
    public enum IrControlFlowEdgeKind
    {
        Fallthrough = 0,
        Branch = 1,
        Return = 2,
        Stop = 3
    }
}
