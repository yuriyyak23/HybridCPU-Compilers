using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR.Resources;

public enum IrResourceFactPrecisionV1 : byte
{
    Unknown = 0,
    Exact = 1,
    Conservative = 2
}

public enum IrResourceFootprintProvenanceV1 : byte
{
    Unknown = 0,
    StaticCanonical = 1,
    ConservativeFallback = 2
}

[System.Flags]
public enum IrMemoryEffectV1 : byte
{
    None = 0,
    Read = 1,
    Write = 2
}

public sealed record IrStructuralResourceDemandV1(
    IrStructuralResource Resource,
    int Units,
    IrResourceFactPrecisionV1 Precision);

/// <summary>
/// Versioned compiler-owned static demand derived from canonical IR facts.
/// It is structural evidence only and never represents live runtime legality.
/// </summary>
public sealed record IrResourceFootprint(
    string Schema,
    int SchemaVersion,
    int InstructionIndex,
    IrIssueSlotMask StructurallyAllowedSlots,
    IrSlotClass RequiredSlotClass,
    IrSlotBindingKind BindingKind,
    IReadOnlyList<IrStructuralResourceDemandV1> StructuralDemands,
    IrSerializationKind Serialization,
    IrControlFlowKind ControlFlowKind,
    IrMemoryEffectV1 MemoryEffect,
    IrResourceFactPrecisionV1 SlotPrecision,
    IrResourceFactPrecisionV1 ClassPrecision,
    IrResourceFactPrecisionV1 MemoryPrecision,
    IrResourceFootprintProvenanceV1 Provenance,
    bool RequiresExactPlacement,
    bool RequiresLegacyFallback,
    string? FallbackReason,
    string Fingerprint)
{
    public const string SchemaName = "IrResourceFootprintV1";
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Optional Phase 03 topology evidence. It does not participate in Phase 01 feasibility
    /// or the legacy footprint fingerprint, so default behavior remains bit-for-bit stable.
    /// </summary>
    public IrTopologyResourceFootprintV1? Topology { get; init; }
}

public enum HybridCpuResourceReservationDecisionV1 : byte
{
    Allowed = 0,
    Rejected = 1,
    RequiresLegacyFallback = 2
}

public enum HybridCpuResourceReasonCodeV1 : byte
{
    None = 0,
    UnknownFootprint = 1,
    UnsupportedTopology = 2,
    DuplicateInstruction = 3,
    IssueWidthExceeded = 4,
    ClassCapacityExceeded = 5,
    AliasedLaneConflict = 6,
    StructuralCapacityExceeded = 7,
    ExclusiveCycleRequired = 8,
    NoStructuralPlacement = 9,
    StateModelMismatch = 10,
    ShadowEvaluationFailure = 11
}

public sealed record HybridCpuResourceReservationResultV1(
    HybridCpuResourceReservationDecisionV1 Decision,
    HybridCpuResourceReasonCodeV1 ReasonCode,
    string ResourceKey,
    int Requested,
    int Used,
    int Capacity,
    string StateDigest)
{
    public bool IsAllowed => Decision == HybridCpuResourceReservationDecisionV1.Allowed;
}

/// <summary>
/// Stable profitability tuple. It is deliberately separate from hard feasibility.
/// </summary>
public readonly record struct HybridCpuResourceIncrementalCostV1(
    int OccupiedSlots,
    int MaximumClassPressurePermille,
    int StructuralPressure,
    int HardPinPressure,
    int InstructionIndex) : System.IComparable<HybridCpuResourceIncrementalCostV1>
{
    public int CompareTo(HybridCpuResourceIncrementalCostV1 other)
    {
        int result = OccupiedSlots.CompareTo(other.OccupiedSlots);
        if (result != 0) return result;
        result = MaximumClassPressurePermille.CompareTo(other.MaximumClassPressurePermille);
        if (result != 0) return result;
        result = StructuralPressure.CompareTo(other.StructuralPressure);
        if (result != 0) return result;
        result = HardPinPressure.CompareTo(other.HardPinPressure);
        return result != 0 ? result : InstructionIndex.CompareTo(other.InstructionIndex);
    }
}
