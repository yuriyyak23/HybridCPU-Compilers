using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Provides a compiler-facing legality analysis surface for one candidate cycle group.
    /// </summary>
    public sealed record IrCandidateBundleAnalysis(
        IReadOnlyList<IrInstruction> Instructions,
        IrBundleLegalityResult Legality,
        IrSlotAssignmentAnalysis SlotAnalysis,
        IrStructuralResourceAnalysis StructuralAnalysis,
        IrClassCapacityResult? ClassCapacityResult = null)
    {
        /// <summary>
        /// Gets a value indicating whether the candidate group is structurally admissible
        /// under the compiler hazard model. This is not runtime legality.
        /// </summary>
        public bool IsStructurallyAdmissible => Legality.IsStructurallyAdmissible;

        /// <summary>
        /// Legacy compiler-side structural admission predicate.
        /// </summary>
        [Obsolete(
            "Compiler-side IsLegal is structural admission evidence only; use CompilerStructuralAuthorityQuarantine.FromCandidateBundleAnalysis or IsStructurallyAdmissible wrappers.",
            false)]
        public bool IsLegal => IsStructurallyAdmissible;
    }
}
