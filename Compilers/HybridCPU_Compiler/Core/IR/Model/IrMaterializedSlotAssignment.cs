using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Physical slot assignment chosen for one legal candidate group.
    /// </summary>
    public sealed record IrMaterializedSlotAssignment(
        IrSlotAssignmentAnalysis Analysis,
        IReadOnlyList<int> InstructionSlots,
        IrBundlePlacementQuality Quality,
        IrBundlePlacementSearchSummary SearchSummary,
        IrBundleTransitionQuality TransitionQuality)
    {
        /// <summary>
        /// Gets a value indicating whether the candidate group has a valid physical slot assignment.
        /// </summary>
        public bool HasStructuralPlacement => Analysis.HasStructuralPlacement;

        [Obsolete(
            "Compiler-side HasLegalAssignment is structural placement evidence only; use HasStructuralPlacement.",
            false)]
        public bool HasLegalAssignment => HasStructuralPlacement;
    }
}
