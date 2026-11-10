using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Aggregate Stage 6 quality metrics for one materialized program bundle stream.
    /// </summary>
    public sealed record IrProgramBundlingQuality(
        IReadOnlyList<IrBasicBlockBundlingQuality> BlockQualities,
        int BundleCount,
        int IssuedInstructionCount,
        int NopSlotCount,
        int CompactBundleCount,
        int OrderPreservingBundleCount,
        int OccupiedSlotSpanSum,
        int InternalGapCount,
        int LargestInternalGapSum,
        int OrderInversionCount,
        int LeadingEmptySlotCount,
        int TrailingEmptySlotCount,
        int SlotIndexSum,
        int ConstrainedSlotDisplacementCost,
        int CrossBundleOverlappingLaneCount,
        int CrossBundleReusedLaneCount,
        int CrossBundleSlotDriftCost,
        int PlacementSearchEvaluatedCount,
        int PlacementSearchParetoOptimalCount,
        int PlacementSearchDominatedCount,
        int AmbiguousBundleCount)
    {
        /// <summary>
        /// Gets the number of blocks that contributed materialized bundles.
        /// </summary>
        public int BlockCount => BlockQualities.Count;

        /// <summary>
        /// Gets the average number of issued instructions per bundle.
        /// </summary>
        public double AverageIssuedInstructionsPerBundle => BundleCount == 0 ? 0d : (double)IssuedInstructionCount / BundleCount;

        /// <summary>
        /// Gets the average occupied slot span per bundle.
        /// </summary>
        public double AverageOccupiedSlotSpan => BundleCount == 0 ? 0d : (double)OccupiedSlotSpanSum / BundleCount;

        /// <summary>
        /// Gets the average largest internal gap size per bundle.
        /// </summary>
        public double AverageLargestInternalGap => BundleCount == 0 ? 0d : (double)LargestInternalGapSum / BundleCount;

        /// <summary>
        /// Gets the average adjacent-bundle slot drift per overlapping issued lane.
        /// </summary>
        public double AverageCrossBundleSlotDriftPerOverlappingLane => CrossBundleOverlappingLaneCount == 0 ? 0d : (double)CrossBundleSlotDriftCost / CrossBundleOverlappingLaneCount;

        /// <summary>
        /// Gets the average number of legal placements explored per bundle.
        /// </summary>
        public double AverageEvaluatedPlacementsPerBundle => BundleCount == 0 ? 0d : (double)PlacementSearchEvaluatedCount / BundleCount;

        /// <summary>
        /// Gets the average number of Pareto-optimal placements kept per bundle.
        /// </summary>
        public double AverageParetoOptimalPlacementsPerBundle => BundleCount == 0 ? 0d : (double)PlacementSearchParetoOptimalCount / BundleCount;

        /// <summary>
        /// Gets the ratio of reused lanes across adjacent bundles.
        /// </summary>
        public double CrossBundleLaneReuseRatio => CrossBundleOverlappingLaneCount == 0 ? 0d : (double)CrossBundleReusedLaneCount / CrossBundleOverlappingLaneCount;

        /// <summary>
        /// Gets the ratio of issued instructions to total physical slots across the program.
        /// </summary>
        public double SlotUtilization => BundleCount == 0 ? 0d : (double)IssuedInstructionCount / (BundleCount * HybridCpuSlotModel.SlotCount);

        /// <summary>
        /// Gets a value indicating whether any bundle in the program required scheduler-order inversion.
        /// </summary>
        public bool HasOrderInversions => OrderInversionCount > 0;

        /// <summary>
        /// Gets a value indicating whether every bundle stayed compact without internal slot gaps.
        /// </summary>
        public bool IsFullyCompact => BundleCount == 0 || CompactBundleCount == BundleCount;

        /// <summary>
        /// Gets a value indicating whether every bundle preserved scheduler order in its chosen physical slots.
        /// </summary>
        public bool IsFullyOrderPreserving => BundleCount == 0 || OrderPreservingBundleCount == BundleCount;

        /// <summary>
        /// Gets a value indicating whether the program has adjacent-bundle continuity observations.
        /// </summary>
        public bool HasCrossBundleTransitions => CrossBundleOverlappingLaneCount > 0;

        /// <summary>
        /// Gets a value indicating whether any bundle in the program kept more than one Pareto-optimal placement candidate.
        /// </summary>
        public bool HasAmbiguousBundles => AmbiguousBundleCount > 0;
    }
}
