using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Result of an explicit Stage 6 adjacent-bundle triplet placement search.
    /// </summary>
    public sealed class IrAdjacentBundleTripletPlacementSearchResult
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="IrAdjacentBundleTripletPlacementSearchResult"/> class.
        /// </summary>
        public IrAdjacentBundleTripletPlacementSearchResult(
            IrSlotAssignmentAnalysis firstBundleAnalysis,
            IrSlotAssignmentAnalysis secondBundleAnalysis,
            IrSlotAssignmentAnalysis thirdBundleAnalysis,
            IReadOnlyList<IrAdjacentBundleTripletPlacementCandidate> paretoOptimalPlacementTriplets,
            IrAdjacentBundleTripletPlacementCandidate? bestPlacementTriplet,
            IrAdjacentBundleTripletPlacementSearchSummary summary)
        {
            ArgumentNullException.ThrowIfNull(firstBundleAnalysis);
            ArgumentNullException.ThrowIfNull(secondBundleAnalysis);
            ArgumentNullException.ThrowIfNull(thirdBundleAnalysis);
            ArgumentNullException.ThrowIfNull(paretoOptimalPlacementTriplets);
            ArgumentNullException.ThrowIfNull(summary);

            FirstBundleAnalysis = firstBundleAnalysis;
            SecondBundleAnalysis = secondBundleAnalysis;
            ThirdBundleAnalysis = thirdBundleAnalysis;
            ParetoOptimalPlacementTriplets = paretoOptimalPlacementTriplets.Count == 0 ? Array.Empty<IrAdjacentBundleTripletPlacementCandidate>() : paretoOptimalPlacementTriplets.ToArray();
            BestPlacementTriplet = bestPlacementTriplet;
            Summary = summary;
        }

        /// <summary>
        /// Gets the slot-feasibility analysis for the first bundle.
        /// </summary>
        public IrSlotAssignmentAnalysis FirstBundleAnalysis { get; }

        /// <summary>
        /// Gets the slot-feasibility analysis for the second bundle.
        /// </summary>
        public IrSlotAssignmentAnalysis SecondBundleAnalysis { get; }

        /// <summary>
        /// Gets the slot-feasibility analysis for the third bundle.
        /// </summary>
        public IrSlotAssignmentAnalysis ThirdBundleAnalysis { get; }

        /// <summary>
        /// Gets the non-dominated adjacent-bundle triplets discovered during search.
        /// </summary>
        public IReadOnlyList<IrAdjacentBundleTripletPlacementCandidate> ParetoOptimalPlacementTriplets { get; }

        /// <summary>
        /// Gets the best deterministic adjacent-bundle triplet chosen by the search.
        /// </summary>
        public IrAdjacentBundleTripletPlacementCandidate? BestPlacementTriplet { get; }

        /// <summary>
        /// Gets summary metrics for the adjacent-bundle triplet search.
        /// </summary>
        public IrAdjacentBundleTripletPlacementSearchSummary Summary { get; }

        /// <summary>
        /// Gets a value indicating whether all three bundles have a structural adjacent placement triplet.
        /// </summary>
        public bool HasStructuralPlacement => FirstBundleAnalysis.HasStructuralPlacement
            && SecondBundleAnalysis.HasStructuralPlacement
            && ThirdBundleAnalysis.HasStructuralPlacement
            && BestPlacementTriplet is not null;

        /// <summary>
        /// Legacy compiler-side structural placement predicate.
        /// </summary>
        [Obsolete(
            "Compiler-side HasLegalAssignment is structural placement evidence only; use HasStructuralPlacement.",
            false)]
        public bool HasLegalAssignment => HasStructuralPlacement;
    }
}
