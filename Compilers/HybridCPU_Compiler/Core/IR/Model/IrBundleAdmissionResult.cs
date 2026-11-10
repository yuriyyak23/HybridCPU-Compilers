using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Describes the structural admissibility classification assigned by the compiler.
    /// </summary>
    /// <remarks>
    /// Classification priority: typed-slot structural validity is checked first (primary),
    /// then safety-mask diagnostics (secondary structural signal).
    /// Stealability analysis remains advisory and does not affect admissibility classification.
    /// `StructurallyAdmissible` therefore means compiler preflight passed; it is not a
    /// guarantee that runtime will avoid later dynamic rejects.
    /// </remarks>
    public enum AdmissibilityClassification
    {
        /// <summary>The bundle is structurally admissible and should be accepted by the runtime fast path.</summary>
        StructurallyAdmissible = 0,

        /// <summary>Structural diagnostic: the bundle has a safety-mask conflict (secondary to typed-slot checks).</summary>
        SafetyMaskConflict = 1,

        /// <summary>The bundle exceeds per-class lane capacity in typed-slot facts (primary structural check).</summary>
        TypedSlotClassCapacityExceeded = 4,

        /// <summary>The bundle has aliased-lane conflict (BranchControl + SystemSingleton) (primary structural check).</summary>
        TypedSlotAliasedLaneConflict = 5,

        /// <summary>The typed-slot facts failed internal consistency validation (primary structural check).</summary>
        TypedSlotFactsInvalid = 6
    }

    /// <summary>
    /// Stage 7 admission result for one materialized bundle.
    /// </summary>
    /// <remarks>
    /// Primary structural handoff: <see cref="TypedSlotFacts"/> and <see cref="TypedSlotFactsValid"/>.
    /// Safety-mask diagnostics are structural metadata. Typed-slot facts remain the
    /// authoritative compiler-preflight handoff, while the current runtime mainline
    /// still advertises <see cref="TypedSlotFactStaging.CurrentMode"/> ==
    /// <see cref="TypedSlotFactMode.ValidationOnly"/> rather than a mandatory-facts mode.
    /// <see cref="SafetyMaskResult"/> survives as a compatibility alias; new code should use <see cref="SafetyMaskDiagnostic"/>.
    /// </remarks>
#pragma warning disable CS0618
    public sealed record IrBundleAdmissionResult(
        int BundleCycle,
        AdmissibilityClassification Classification,
        [property: Obsolete("Compatibility alias (REF-C3). Use SafetyMaskDiagnostic.")]
        [property: EditorBrowsable(EditorBrowsableState.Never)]
        SafetyMaskCompatibilityResult SafetyMaskResult,
        IReadOnlyList<StealabilityVerdict> StealVerdicts,
        IrTypedSlotBundleFacts TypedSlotFacts = default,
        bool TypedSlotFactsValid = true)
    {
        /// <summary>
        /// Gets a value indicating whether the bundle is structurally admissible.
        /// Requires both a structurally admissible classification and valid typed-slot facts.
        /// </summary>
        public bool IsStructurallyAdmissible =>
            Classification == AdmissibilityClassification.StructurallyAdmissible
            && TypedSlotFactsValid;

        /// <summary>
        /// Gets the canonical safety-mask diagnostic result for this bundle.
        /// </summary>
        public SafetyMaskDiagnosticResult SafetyMaskDiagnostic => SafetyMaskResult;

        /// <summary>
        /// Gets the number of stealable instructions in this bundle.
        /// </summary>
        public int StealableInstructionCount => StealVerdicts.Count(v => v.IsStealable);

        /// <summary>
        /// Gets a value indicating whether any instruction's compiler-derived steal verdict
        /// differs from the original metadata value.
        /// </summary>
        public bool HasStealMismatch => StealVerdicts.Any(v => v.IsStealable != v.OriginalMetadataValue);
    }
#pragma warning restore CS0618
}
