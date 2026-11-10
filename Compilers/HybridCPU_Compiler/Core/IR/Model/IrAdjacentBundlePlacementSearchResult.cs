using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Result of an explicit Stage 6 adjacent-bundle placement search.
    /// </summary>
    public sealed class IrAdjacentBundlePlacementSearchResult
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="IrAdjacentBundlePlacementSearchResult"/> class.
        /// </summary>
        public IrAdjacentBundlePlacementSearchResult(
            IrSlotAssignmentAnalysis firstBundleAnalysis,
            IrSlotAssignmentAnalysis secondBundleAnalysis,
            IReadOnlyList<IrAdjacentBundlePlacementCandidate> paretoOptimalPlacementPairs,
            IrAdjacentBundlePlacementCandidate? bestPlacementPair,
            IrAdjacentBundlePlacementSearchSummary summary)
        {
            ArgumentNullException.ThrowIfNull(firstBundleAnalysis);
            ArgumentNullException.ThrowIfNull(secondBundleAnalysis);
            ArgumentNullException.ThrowIfNull(paretoOptimalPlacementPairs);
            ArgumentNullException.ThrowIfNull(summary);

            FirstBundleAnalysis = firstBundleAnalysis;
            SecondBundleAnalysis = secondBundleAnalysis;
            ParetoOptimalPlacementPairs = paretoOptimalPlacementPairs.Count == 0 ? Array.Empty<IrAdjacentBundlePlacementCandidate>() : paretoOptimalPlacementPairs.ToArray();
            BestPlacementPair = bestPlacementPair;
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
        /// Gets the non-dominated adjacent-bundle placement pairs discovered during search.
        /// </summary>
        public IReadOnlyList<IrAdjacentBundlePlacementCandidate> ParetoOptimalPlacementPairs { get; }

        /// <summary>
        /// Gets the best deterministic adjacent-bundle placement pair chosen by the search.
        /// </summary>
        public IrAdjacentBundlePlacementCandidate? BestPlacementPair { get; }

        /// <summary>
        /// Gets summary metrics for the adjacent-bundle placement search.
        /// </summary>
        public IrAdjacentBundlePlacementSearchSummary Summary { get; }

        /// <summary>
        /// Gets a value indicating whether both bundles have a structural adjacent placement pair.
        /// </summary>
        public bool HasStructuralPlacement =>
            FirstBundleAnalysis.HasStructuralPlacement &&
            SecondBundleAnalysis.HasStructuralPlacement &&
            BestPlacementPair is not null;

        /// <summary>
        /// Legacy compiler-side structural placement predicate.
        /// </summary>
        [Obsolete(
            "Compiler-side HasLegalAssignment is structural placement evidence only; use HasStructuralPlacement.",
            false)]
        public bool HasLegalAssignment => HasStructuralPlacement;
    }
}
