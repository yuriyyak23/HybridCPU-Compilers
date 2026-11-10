using System;
using System.Collections.Generic;
using System.Linq;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Result of a scalable global Stage 6 program placement search.
    /// </summary>
    public sealed class IrProgramPlacementSearchResult
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="IrProgramPlacementSearchResult"/> class.
        /// </summary>
        public IrProgramPlacementSearchResult(
            IReadOnlyList<IReadOnlyList<IrSlotAssignmentAnalysis>> blockAnalyses,
            IrProgramPlacementCandidate? bestPlacement,
            IrProgramPlacementSearchSummary summary)
        {
            ArgumentNullException.ThrowIfNull(blockAnalyses);
            ArgumentNullException.ThrowIfNull(summary);

            BlockAnalyses = blockAnalyses.Count == 0
                ? Array.Empty<IReadOnlyList<IrSlotAssignmentAnalysis>>()
                : blockAnalyses.Select(blockAnalysis => blockAnalysis.Count == 0 ? Array.Empty<IrSlotAssignmentAnalysis>() : blockAnalysis.ToArray()).ToArray();
            BestPlacement = bestPlacement;
            Summary = summary;
        }

        /// <summary>
        /// Gets the slot-feasibility analyses for each bundle grouped by block.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<IrSlotAssignmentAnalysis>> BlockAnalyses { get; }

        /// <summary>
        /// Gets the best deterministic whole-program placement chosen by the scalable global search.
        /// </summary>
        public IrProgramPlacementCandidate? BestPlacement { get; }

        /// <summary>
        /// Gets summary metrics for the scalable global whole-program search.
        /// </summary>
        public IrProgramPlacementSearchSummary Summary { get; }

        /// <summary>
        /// Gets a value indicating whether every bundle in the searched program has a structural placement.
        /// </summary>
        public bool HasStructuralPlacement => BlockAnalyses.SelectMany(blockAnalysis => blockAnalysis).All(analysis => analysis.HasStructuralPlacement) && BestPlacement is not null;

        /// <summary>
        /// Legacy compiler-side structural placement predicate.
        /// </summary>
        [Obsolete(
            "Compiler-side HasLegalAssignment is structural placement evidence only; use HasStructuralPlacement.",
            false)]
        public bool HasLegalAssignment => HasStructuralPlacement;
    }
}
