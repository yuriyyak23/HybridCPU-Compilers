using System;
using System.Collections.Generic;
using System.Linq;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Result of an explicit Stage 6 basic-block placement search.
    /// </summary>
    public sealed class IrBasicBlockPlacementSearchResult
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="IrBasicBlockPlacementSearchResult"/> class.
        /// </summary>
        public IrBasicBlockPlacementSearchResult(
            IReadOnlyList<IrSlotAssignmentAnalysis> bundleAnalyses,
            IReadOnlyList<IrBasicBlockPlacementCandidate> paretoOptimalPlacements,
            IrBasicBlockPlacementCandidate? bestPlacement,
            IrBasicBlockPlacementSearchSummary summary)
        {
            ArgumentNullException.ThrowIfNull(bundleAnalyses);
            ArgumentNullException.ThrowIfNull(paretoOptimalPlacements);
            ArgumentNullException.ThrowIfNull(summary);

            BundleAnalyses = bundleAnalyses.Count == 0 ? Array.Empty<IrSlotAssignmentAnalysis>() : bundleAnalyses.ToArray();
            ParetoOptimalPlacements = paretoOptimalPlacements.Count == 0 ? Array.Empty<IrBasicBlockPlacementCandidate>() : paretoOptimalPlacements.ToArray();
            BestPlacement = bestPlacement;
            Summary = summary;
        }

        /// <summary>
        /// Gets the slot-feasibility analyses for each bundle in the searched block window.
        /// </summary>
        public IReadOnlyList<IrSlotAssignmentAnalysis> BundleAnalyses { get; }

        /// <summary>
        /// Gets the non-dominated whole-block placements discovered during search.
        /// </summary>
        public IReadOnlyList<IrBasicBlockPlacementCandidate> ParetoOptimalPlacements { get; }

        /// <summary>
        /// Gets the best deterministic whole-block placement chosen by the search.
        /// </summary>
        public IrBasicBlockPlacementCandidate? BestPlacement { get; }

        /// <summary>
        /// Gets summary metrics for the whole-block placement search.
        /// </summary>
        public IrBasicBlockPlacementSearchSummary Summary { get; }

        /// <summary>
        /// Gets a value indicating whether every bundle in the searched block window has a structural placement.
        /// </summary>
        public bool HasStructuralPlacement => BundleAnalyses.All(analysis => analysis.HasStructuralPlacement) && BestPlacement is not null;

        /// <summary>
        /// Legacy compiler-side structural placement predicate.
        /// </summary>
        [Obsolete(
            "Compiler-side HasLegalAssignment is structural placement evidence only; use HasStructuralPlacement.",
            false)]
        public bool HasLegalAssignment => HasStructuralPlacement;
    }
}
