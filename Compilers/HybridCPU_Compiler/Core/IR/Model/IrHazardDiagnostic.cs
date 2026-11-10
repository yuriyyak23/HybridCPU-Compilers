using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Describes one machine-checkable legality failure discovered during hazard analysis.
    /// </summary>
    public sealed record IrHazardDiagnostic(
        IrHazardCategory Category,
        IrHazardReason Reason,
        int? LeftInstructionIndex,
        int? RightInstructionIndex,
        string Message,
        IrDataHazardKind DataHazard = IrDataHazardKind.None,
        byte RequiredLatencyCycles = 0,
        IrIssueSlotMask RelevantSlots = IrIssueSlotMask.None,
        IrStructuralResource RelevantResources = IrStructuralResource.None,
        IrInstructionDependencyKind DependencyKind = IrInstructionDependencyKind.None,
        IrMemoryDependencyPrecision MemoryPrecision = IrMemoryDependencyPrecision.None,
        IrHazardEffectKind DominantEffectKind = IrHazardEffectKind.RegisterData);

    /// <summary>
    /// Represents the legality result for a candidate cycle or bundle group.
    /// </summary>
    public sealed record IrBundleLegalityResult(IReadOnlyList<IrHazardDiagnostic> Hazards)
    {
        /// <summary>
        /// Gets a value indicating whether the candidate group is structurally admissible
        /// under the compiler hazard model. This is not runtime legality.
        /// </summary>
        public bool IsStructurallyAdmissible => Hazards.Count == 0;

        /// <summary>
        /// Legacy compiler-side structural admission predicate.
        /// </summary>
        [Obsolete(
            "Compiler-side IsLegal is structural admission evidence only; use CompilerStructuralAuthorityQuarantine.FromBundleLegalityResult.",
            false)]
        public bool IsLegal => IsStructurallyAdmissible;

        /// <summary>
        /// Returns a structural admission result without compiler hazards.
        /// </summary>
        public static IrBundleLegalityResult StructurallyAdmissible { get; } = new(Array.Empty<IrHazardDiagnostic>());

        /// <summary>
        /// Returns a structural admission result without compiler hazards.
        /// </summary>
        [Obsolete(
            "Compiler-side Legal is structural admission evidence only; use CompilerStructuralAuthorityQuarantine.FromBundleLegalityResult.",
            false)]
        public static IrBundleLegalityResult Legal { get; } = StructurallyAdmissible;
    }
}
