using System;
using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Canonical relation kinds between compiler admissibility vocabulary and
    /// runtime reject/diagnostic families.
    /// </summary>
    public enum AdmissibilityRuntimeVocabularyRelationKind : byte
    {
        /// <summary>
        /// Compiler preflight passed and runtime may still encounter later dynamic
        /// or legality-service outcomes. This is intentionally not a runtime reject twin.
        /// </summary>
        RuntimeContinuation = 0,

        /// <summary>
        /// Compiler inadmissibility has a proven direct scheduler-visible structural twin.
        /// </summary>
        DirectSchedulerStructuralTwin = 1,

        /// <summary>
        /// Compiler diagnostic is adjacent to a scheduler-visible family, but is not the
        /// authoritative typed-slot contract and must not be described as a direct twin.
        /// </summary>
        CompatibilityAdjacentSchedulerFamily = 2,

        /// <summary>
        /// Compiler-side structural invalidity must be caught before mainline runtime
        /// scheduling and therefore has no direct scheduler reject twin.
        /// </summary>
        CompilerOnlyStructuralInvalidity = 3
    }

    /// <summary>
    /// Reviewer-readable closure entry for one compiler admissibility classification.
    /// </summary>
    public readonly record struct AdmissibilityRuntimeVocabularyRelation(
        AdmissibilityClassification Classification,
        AdmissibilityRuntimeVocabularyRelationKind RelationKind,
        TypedSlotRejectReason? SchedulerRejectReason,
        RejectKind? RuntimeDiagnosticRejectKind)
    {
        /// <summary>
        /// Gets a value indicating whether this compiler classification has a proven
        /// direct scheduler-visible structural twin.
        /// </summary>
        public bool HasDirectSchedulerTwin =>
            RelationKind == AdmissibilityRuntimeVocabularyRelationKind.DirectSchedulerStructuralTwin
            && SchedulerRejectReason.HasValue;
    }

    /// <summary>
    /// Canonical compiler-side closure surface for P3.6 vocabulary convergence.
    /// This intentionally does not flatten compiler preflight, scheduler rejects,
    /// and verifier diagnostics into one shared enum. It records only proven
    /// direct twins and current compatibility-adjacent relationships.
    /// </summary>
    public static class AdmissibilityRuntimeVocabulary
    {
        /// <summary>
        /// Describe the current relation between one compiler admissibility
        /// classification and runtime-visible reject/diagnostic families.
        /// </summary>
        public static AdmissibilityRuntimeVocabularyRelation Describe(
            AdmissibilityClassification classification)
        {
            return classification switch
            {
                AdmissibilityClassification.StructurallyAdmissible => new(
                    classification,
                    AdmissibilityRuntimeVocabularyRelationKind.RuntimeContinuation,
                    SchedulerRejectReason: null,
                    RuntimeDiagnosticRejectKind: null),

                AdmissibilityClassification.SafetyMaskConflict => new(
                    classification,
                    AdmissibilityRuntimeVocabularyRelationKind.CompatibilityAdjacentSchedulerFamily,
                    SchedulerRejectReason: TypedSlotRejectReason.ResourceConflict,
                    RuntimeDiagnosticRejectKind: null),

                AdmissibilityClassification.TypedSlotClassCapacityExceeded => new(
                    classification,
                    AdmissibilityRuntimeVocabularyRelationKind.DirectSchedulerStructuralTwin,
                    SchedulerRejectReason: TypedSlotRejectReason.StaticClassOvercommit,
                    RuntimeDiagnosticRejectKind: null),

                AdmissibilityClassification.TypedSlotAliasedLaneConflict => new(
                    classification,
                    AdmissibilityRuntimeVocabularyRelationKind.CompilerOnlyStructuralInvalidity,
                    SchedulerRejectReason: null,
                    RuntimeDiagnosticRejectKind: RejectKind.CrossLaneConflict),

                AdmissibilityClassification.TypedSlotFactsInvalid => new(
                    classification,
                    AdmissibilityRuntimeVocabularyRelationKind.CompilerOnlyStructuralInvalidity,
                    SchedulerRejectReason: null,
                    RuntimeDiagnosticRejectKind: null),

                _ => throw new ArgumentOutOfRangeException(nameof(classification), classification, null)
            };
        }

        /// <summary>
        /// Try to resolve the proven direct scheduler-visible structural twin for a
        /// compiler admissibility classification.
        /// </summary>
        public static bool TryGetDirectSchedulerRejectTwin(
            AdmissibilityClassification classification,
            out TypedSlotRejectReason rejectReason)
        {
            AdmissibilityRuntimeVocabularyRelation relation = Describe(classification);
            if (relation.HasDirectSchedulerTwin)
            {
                rejectReason = relation.SchedulerRejectReason!.Value;
                return true;
            }

            rejectReason = TypedSlotRejectReason.None;
            return false;
        }

        /// <summary>
        /// Try to resolve the proven direct compiler structural twin for a
        /// scheduler-visible reject reason. Only true structural twins are surfaced here.
        /// </summary>
        public static bool TryGetDirectCompilerStructuralTwin(
            TypedSlotRejectReason rejectReason,
            out AdmissibilityClassification classification)
        {
            switch (rejectReason)
            {
                case TypedSlotRejectReason.StaticClassOvercommit:
                    classification = AdmissibilityClassification.TypedSlotClassCapacityExceeded;
                    return true;

                default:
                    classification = AdmissibilityClassification.StructurallyAdmissible;
                    return false;
            }
        }
    }
}
