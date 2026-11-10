using System;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// One physical slot in a materialized bundle.
    /// </summary>
    public sealed record IrMaterializedBundleSlot(
        int SlotIndex,
        IrInstruction? Instruction,
        int? OrderInCycle,
        [property: Obsolete("Compiler-side InstructionLegalSlots are structurally allowed slot facts only; use InstructionStructurallyAllowedSlots.", false)]
        IrIssueSlotMask InstructionLegalSlots,
        string? EmptyReason = null,
        IrSlotBindingKind? BindingKind = null,
        IrSlotClass? AssignedClass = null)
    {
        /// <summary>
        /// Gets a value indicating whether the slot was left empty and materialized as an internal NOP.
        /// </summary>
        public bool IsNop => Instruction is null;

        /// <summary>
        /// Gets the physical slot mask represented by this slot index.
        /// </summary>
        public IrIssueSlotMask PhysicalSlotMask => (IrIssueSlotMask)(1 << SlotIndex);

        /// <summary>
        /// Gets the structural slot facts for the assigned instruction.
        /// </summary>
#pragma warning disable CS0618
        public IrIssueSlotMask InstructionStructurallyAllowedSlots => InstructionLegalSlots;
#pragma warning restore CS0618

        /// <summary>
        /// Gets a value indicating whether the assigned instruction is structurally placed in an allowed slot.
        /// </summary>
        public bool IsStructuralPlacement => Instruction is null || (InstructionStructurallyAllowedSlots & PhysicalSlotMask) != 0;

        /// <summary>
        /// Compatibility alias for structural placement.
        /// </summary>
        [Obsolete("Compiler-side IsLegalPlacement is structural placement only; use IsStructuralPlacement.", false)]
        public bool IsLegalPlacement => Instruction is null || (InstructionLegalSlots & PhysicalSlotMask) != 0;
    }
}
