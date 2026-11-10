using System.Collections.Generic;
using System.Linq;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Summarizes the compiler/runtime admissibility agreement for a complete program.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The agreement model formalizes the boundary between compiler and runtime responsibilities:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Primary structural handoff</b>: typed-slot facts validity determines whether
    ///   bundles are structurally admissible.</item>
    ///   <item><b>Structural diagnostic</b>: safety-mask compatibility is a secondary structural
    ///   check; conflicts are reported but are subordinate to typed-slot validation.</item>
    ///   <item><b>Advisory metadata</b>: stealability verdicts and steal-mismatch counts are
    ///   informational only and do not affect admissibility classification.</item>
    ///   <item><b>Runtime remains authoritative for dynamic admissibility</b>: credit fairness,
    ///   bank pressure, speculation budget, phase-aware execution, and replay context.</item>
    /// </list>
    /// <para>
    /// The compiler does not override runtime rejection decisions.
    /// It provides admission annotations to accelerate the runtime fast-path
    /// and diagnostic metadata to explain mismatches.
    /// </para>
    /// </remarks>
    public sealed record IrAdmissibilityAgreement(
        IReadOnlyList<IrBundleAdmissionResult> BundleResults)
    {
        /// <summary>
        /// Gets the total number of bundles in the program.
        /// </summary>
        public int TotalBundleCount => BundleResults.Count;

        /// <summary>
        /// Gets the number of structurally admissible bundles.
        /// </summary>
        public int AdmissibleBundleCount => BundleResults.Count(r => r.IsStructurallyAdmissible);

        /// <summary>
        /// Gets the number of bundles whose compiler-side structural agreement failed.
        /// This is a diagnostic/build-output signal; runtime legality remains authoritative.
        /// </summary>
        public int StructuralAgreementFailureCount => TotalBundleCount - AdmissibleBundleCount;

        /// <summary>
        /// Gets a value indicating whether all bundles in the program are structurally admissible.
        /// </summary>
        public bool AllBundlesAdmissible => BundleResults.All(r => r.IsStructurallyAdmissible);

        /// <summary>
        /// Gets the number of bundles with safety-mask conflicts (structural diagnostic).
        /// </summary>
        public int SafetyMaskConflictCount => BundleResults.Count(r =>
            r.Classification == AdmissibilityClassification.SafetyMaskConflict);

        /// <summary>
        /// Gets the number of bundles where compiler-derived stealability differs from metadata (advisory only).
        /// </summary>
        public int StealMismatchBundleCount => BundleResults.Count(r => r.HasStealMismatch);

        /// <summary>
        /// Gets the total number of stealable instructions across all bundles (advisory only).
        /// </summary>
        public int TotalStealableInstructionCount => BundleResults.Sum(r => r.StealableInstructionCount);

        /// <summary>
        /// Gets advisory mismatches between compiler analysis and original metadata.
        /// Does not affect admissibility classification.
        /// </summary>
        public IReadOnlyList<StealabilityVerdict> StealMismatches => BundleResults
            .SelectMany(r => r.StealVerdicts)
            .Where(v => v.IsStealable != v.OriginalMetadataValue)
            .ToList();

        /// <summary>
        /// Gets the number of bundles with valid typed-slot facts.
        /// </summary>
        public int TypedSlotValidBundleCount => BundleResults.Count(r => r.TypedSlotFactsValid);

        /// <summary>
        /// Gets the number of bundles whose typed-slot facts failed compiler-side validation.
        /// </summary>
        public int TypedSlotInvalidBundleCount => TotalBundleCount - TypedSlotValidBundleCount;

        /// <summary>
        /// Gets a value indicating whether all bundles have valid typed-slot facts.
        /// </summary>
        public bool AllTypedSlotFactsValid => BundleResults.All(r => r.TypedSlotFactsValid);

        /// <summary>
        /// Gets the number of bundles with non-empty typed-slot facts.
        /// </summary>
        public int TypedSlotEmittedBundleCount => BundleResults.Count(r => !r.TypedSlotFacts.IsEmpty);
    }
}
