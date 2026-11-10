using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Result of an explicit Stage 6 physical slot placement search.
    /// </summary>
    public sealed class IrBundlePlacementSearchResult
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="IrBundlePlacementSearchResult"/> class.
        /// </summary>
        public IrBundlePlacementSearchResult(
            IrSlotAssignmentAnalysis analysis,
            IReadOnlyList<IrBundlePlacementCandidate> paretoOptimalPlacements,
            IReadOnlyList<int> bestInstructionSlots,
            IrBundlePlacementQuality bestQuality,
            IrBundleTransitionQuality bestTransitionQuality,
            IrBundlePlacementSearchSummary summary)
        {
            ArgumentNullException.ThrowIfNull(analysis);
            ArgumentNullException.ThrowIfNull(paretoOptimalPlacements);
            ArgumentNullException.ThrowIfNull(bestInstructionSlots);
            ArgumentNullException.ThrowIfNull(bestQuality);
            ArgumentNullException.ThrowIfNull(bestTransitionQuality);
            ArgumentNullException.ThrowIfNull(summary);

            Analysis = analysis;
            ParetoOptimalPlacements = paretoOptimalPlacements.Count == 0 ? Array.Empty<IrBundlePlacementCandidate>() : paretoOptimalPlacements.ToArray();
            BestInstructionSlots = bestInstructionSlots.Count == 0 ? Array.Empty<int>() : bestInstructionSlots.ToArray();
            BestQuality = bestQuality;
            BestTransitionQuality = bestTransitionQuality;
            Summary = summary;
        }

        /// <summary>
        /// Gets the slot-feasibility analysis that precedes physical materialization.
        /// </summary>
        public IrSlotAssignmentAnalysis Analysis { get; }

        /// <summary>
        /// Gets the non-dominated legal placements discovered during search.
        /// </summary>
        public IReadOnlyList<IrBundlePlacementCandidate> ParetoOptimalPlacements { get; }

        /// <summary>
        /// Gets the best deterministic placement chosen from the search result.
        /// </summary>
        public IReadOnlyList<int> BestInstructionSlots { get; }

        /// <summary>
        /// Gets the quality metrics for the best deterministic placement.
        /// </summary>
        public IrBundlePlacementQuality BestQuality { get; }

        /// <summary>
        /// Gets adjacent-bundle continuity metrics for the best deterministic placement.
        /// </summary>
        public IrBundleTransitionQuality BestTransitionQuality { get; }

        /// <summary>
        /// Gets summary metrics for the placement search.
        /// </summary>
        public IrBundlePlacementSearchSummary Summary { get; }

        /// <summary>
        /// Gets a value indicating whether the candidate group has a structural physical slot placement.
        /// </summary>
        public bool HasStructuralPlacement => Analysis.HasStructuralPlacement;

        /// <summary>
        /// Legacy compiler-side structural placement predicate.
        /// </summary>
        [Obsolete(
            "Compiler-side HasLegalAssignment is structural placement evidence only; use HasStructuralPlacement.",
            false)]
        public bool HasLegalAssignment => HasStructuralPlacement;

        /// <summary>
        /// Materializes the selected deterministic slot assignment.
        /// </summary>
        public IrMaterializedSlotAssignment MaterializeBestAssignment()
        {
            return new IrMaterializedSlotAssignment(Analysis, BestInstructionSlots, BestQuality, Summary, BestTransitionQuality);
        }
    }
}
