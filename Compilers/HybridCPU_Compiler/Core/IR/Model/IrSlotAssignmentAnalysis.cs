using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Summarizes compiler-visible slot feasibility for a candidate cycle group.
    /// </summary>
    public sealed record IrSlotAssignmentAnalysis(
        int CandidateInstructionCount,
        [property: Obsolete(
            "Compiler-side LegalSlots are structurally allowed slots only; use CompilerStructuralPlacementReport.StructurallyAllowedSlots.",
            false)]
        IrIssueSlotMask CombinedLegalSlots,
        int DistinctLegalSlotCount,
        [property: Obsolete(
            "Compiler-side HasLegalAssignment is structural placement evidence only; use CompilerStructuralPlacementReport.HasStructuralPlacement.",
            false)]
        bool HasLegalAssignment,
        [property: Obsolete(
            "Compiler-side LegalSlots are structurally allowed slots only; use CompilerStructuralPlacementReport.InstructionStructurallyAllowedSlots.",
            false)]
        IReadOnlyList<IrIssueSlotMask> InstructionLegalSlots)
    {
        /// <summary>
        /// Structurally allowed issue-slot mask. This is not runtime legality.
        /// </summary>
#pragma warning disable CS0618
        public IrIssueSlotMask StructurallyAllowedSlots => CombinedLegalSlots;
#pragma warning restore CS0618

        /// <summary>
        /// Compiler structural placement predicate. This is not runtime legality.
        /// </summary>
#pragma warning disable CS0618
        public bool HasStructuralPlacement => HasLegalAssignment;
#pragma warning restore CS0618

        /// <summary>
        /// Per-instruction structurally allowed issue-slot masks. These are facts, not execution rights.
        /// </summary>
#pragma warning disable CS0618
        public IReadOnlyList<IrIssueSlotMask> InstructionStructurallyAllowedSlots => InstructionLegalSlots;
#pragma warning restore CS0618
    }
}
