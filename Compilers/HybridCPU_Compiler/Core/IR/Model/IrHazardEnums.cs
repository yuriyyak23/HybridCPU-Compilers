using System;

namespace HybridCPU.Compiler.Core.IR
{
    public enum IrHazardEffectKind : byte
    {
        RegisterData = 0,
        MemoryBank = 1,
        ControlFlow = 2,
        SystemBarrier = 3,
        PinnedLane = 4
    }

    public enum IrTypedSlotRejectReason : byte
    {
        None = 0,
        StaticClassOvercommit = 1,
        DynamicClassExhaustion = 2,
        ResourceConflict = 3,
        DomainReject = 4,
        ScoreboardReject = 5,
        BankPendingReject = 6,
        HardwareBudgetReject = 7,
        SpeculationBudgetReject = 8,
        AssistQuotaReject = 9,
        AssistBackpressureReject = 10,
        PinnedLaneConflict = 11,
        LateBindingConflict = 12,
        InvalidPinnedLane = 13,
        FairnessDeferred = 20
    }

    /// <summary>
    /// Compiler-visible issue-slot mask used by the early legality model.
    /// </summary>
    [Flags]
    public enum IrIssueSlotMask : ushort
    {
        None = 0,
        Slot0 = 1 << 0,
        Slot1 = 1 << 1,
        Slot2 = 1 << 2,
        Slot3 = 1 << 3,
        Slot4 = 1 << 4,
        Slot5 = 1 << 5,
        Slot6 = 1 << 6,
        Slot7 = 1 << 7,
        Scalar = Slot0 | Slot1 | Slot2 | Slot3,
        Vector = Slot4 | Slot5,
        Lsu = Slot4 | Slot5,
        // Retained legacy alias. Scalar load/store production uses Lsu.
        Memory = Slot6,
        DmaStream = Slot6,
        Control = Slot7,
        System = Slot7,
        All = Scalar | Vector | Memory | Slot7
    }

    /// <summary>
    /// High-level hazard buckets surfaced by the compiler legality layer.
    /// </summary>
    public enum IrHazardCategory
    {
        None = 0,
        Data = 1,
        Latency = 2,
        Structural = 3,
        Slot = 4,
        Control = 5
    }

    /// <summary>
    /// Data-dependence kinds recognized by the current hazard foundation.
    /// </summary>
    public enum IrDataHazardKind
    {
        None = 0,
        ReadAfterWrite = 1,
        WriteAfterRead = 2,
        WriteAfterWrite = 3,
        MemoryDependency = 4
    }

    /// <summary>
    /// Reason codes used by the legality checker when rejecting same-cycle candidates.
    /// </summary>
    public enum IrHazardReason
    {
        None = 0,
        ReadAfterWrite = 1,
        WriteAfterRead = 2,
        WriteAfterWrite = 3,
        MemoryDependency = 4,
        LatencyConstraint = 5,
        StructuralResourceConflict = 6,
        ExclusiveCycleRequired = 7,
        SlotCapacityExceeded = 8,
        NoLegalSlotAssignment = 9,
        ControlDependency = 10,

        /// <summary>Class-capacity exceeded for one or more slot classes.</summary>
        ClassCapacityExceeded = 11,

        /// <summary>Aliased-lane conflict (e.g., BranchControl + SystemSingleton both need lane 7).</summary>
        AliasedLaneConflict = 12,

        /// <summary>
        /// Instruction group is legal only under sequential 1-slot issue and would be rejected
        /// by the cluster-prepared decode path. Phase 06 cluster-aware legality tightening.
        /// </summary>
        ClusterPreparedSequentialOnly = 13
    }

    /// <summary>
    /// Directional dependence kinds used by Stage 4 pair analysis and future scheduler-facing APIs.
    /// </summary>
    public enum IrInstructionDependencyKind
    {
        None = 0,
        RegisterRaw = 1,
        RegisterWar = 2,
        RegisterWaw = 3,
        Memory = 4,
        Control = 5,
        Serialization = 6
    }

    /// <summary>
    /// Precision bucket for memory dependences in the current Stage 4 foundation.
    /// </summary>
    public enum IrMemoryDependencyPrecision
    {
        None = 0,
        Must = 1,
        May = 2
    }

    /// <summary>
    /// Shared structural resources that may conflict even when slot placement is otherwise possible.
    /// </summary>
    [Flags]
    public enum IrStructuralResource
    {
        None = 0,
        ReductionUnit = 1 << 0,
        VectorPermuteCrossbar = 1 << 1,
        AddressGenerationUnit = 1 << 2,
        LoadDataPort = 1 << 3,
        StoreDataPort = 1 << 4,
        BranchResolver = 1 << 5,
        ControlSequencer = 1 << 6,
        SystemSequencer = 1 << 7,
        CsrPort = 1 << 8,
        VmStatePort = 1 << 9,
        BarrierSequencer = 1 << 10
    }

    /// <summary>
    /// Serialization rules surfaced to the compiler before scheduling is introduced.
    /// </summary>
    [Flags]
    public enum IrSerializationKind
    {
        None = 0,
        ControlFlowBoundary = 1 << 0,
        BarrierBoundary = 1 << 1,
        SystemBoundary = 1 << 2,
        ExclusiveCycle = 1 << 3
    }
}
