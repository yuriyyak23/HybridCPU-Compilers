using System;
using System.Collections.Generic;
using System.Linq;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Result of a scalable global Stage 6 basic-block placement search.
    /// </summary>
    public sealed class IrGlobalBasicBlockPlacementSearchResult
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="IrGlobalBasicBlockPlacementSearchResult"/> class.
        /// </summary>
        public IrGlobalBasicBlockPlacementSearchResult(
            IReadOnlyList<IrSlotAssignmentAnalysis> bundleAnalyses,
            IrBasicBlockPlacementCandidate? bestPlacement,
            IrGlobalBasicBlockPlacementSearchSummary summary)
        {
            ArgumentNullException.ThrowIfNull(bundleAnalyses);
            ArgumentNullException.ThrowIfNull(summary);

            BundleAnalyses = bundleAnalyses.Count == 0 ? Array.Empty<IrSlotAssignmentAnalysis>() : bundleAnalyses.ToArray();
            BestPlacement = bestPlacement;
            Summary = summary;
        }

        /// <summary>
        /// Gets the slot-feasibility analyses for each bundle in the searched block.
        /// </summary>
        public IReadOnlyList<IrSlotAssignmentAnalysis> BundleAnalyses { get; }

        /// <summary>
        /// Gets the best deterministic whole-block placement chosen by the scalable global search.
        /// </summary>
        public IrBasicBlockPlacementCandidate? BestPlacement { get; }

        /// <summary>
        /// Gets summary metrics for the scalable global whole-block search.
        /// </summary>
        public IrGlobalBasicBlockPlacementSearchSummary Summary { get; }

        /// <summary>
        /// Gets a value indicating whether every bundle in the searched block has a structural placement.
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
