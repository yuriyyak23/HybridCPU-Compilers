namespace HybridCPU.Compiler.Core.IR;

/// <summary>
/// Compiler-side classification of how an instruction binds to physical lanes.
/// </summary>
/// <remarks>
/// <see cref="IrSlotBindingKind"/> is strictly about lane topology and placement flexibility.
/// It is orthogonal to the advisory runtime hint
/// <see cref="IrInstructionAnnotation.StealabilityHint"/>:
/// <c>ClassFlexible</c> does NOT imply stealable; <c>HardPinned</c> does NOT imply non-stealable.
/// </remarks>
public enum IrSlotBindingKind : byte
{
    /// <summary>
    /// Instruction can be placed in any lane of its <see cref="YAKSys_Hybrid_CPU.Core.IrSlotClass"/>.
    /// Compiler does not depend on specific lane for correctness.
    /// </summary>
    ClassFlexible = 0,

    /// <summary>
    /// Instruction must be placed in a specific physical lane.
    /// Typically: branch/system (lane 7), DMA (lane 6).
    /// </summary>
    HardPinned = 1,

    /// <summary>
    /// Instruction is the sole user of its <see cref="YAKSys_Hybrid_CPU.Core.IrSlotClass"/>
    /// (e.g., DmaStreamClass has capacity=1). Semantically ClassFlexible
    /// but topology constrains to exactly one lane.
    /// </summary>
    /// <remarks>
    /// Runtime handoff: maps to <see cref="YAKSys_Hybrid_CPU.Core.SlotPinningKind.ClassFlexible"/>
    /// via <see cref="IrSlotClassMapping.ToRuntimePinningKind"/>. This is correct because the
    /// singleton constraint is enforced by <see cref="YAKSys_Hybrid_CPU.Core.SlotClassLaneMap"/>
    /// topology (single-lane class mask), not by pinning metadata.
    /// </remarks>
    SingletonClass = 2
}
