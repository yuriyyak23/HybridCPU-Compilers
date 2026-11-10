using System;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Aggregate quality metrics for one adjacent-bundle placement pair.
    /// </summary>
    public sealed record IrAdjacentBundlePlacementQuality(
        int OccupiedSlotSpanSum,
        int InternalGapCount,
        int OrderInversionCount,
        int LargestInternalGapSum,
        int LeadingEmptySlotCount,
        int ConstrainedSlotDisplacementCost,
        int SlotIndexSum,
        int IncomingOverlappingLaneCount,
        int IncomingReusedLaneCount,
        int IncomingSlotDriftCost,
        int CrossBundleOverlappingLaneCount,
        int CrossBundleReusedLaneCount,
        int CrossBundleSlotDriftCost)
    {
        /// <summary>
        /// Gets the quality metrics for an empty adjacent-bundle placement pair.
        /// </summary>
        public static IrAdjacentBundlePlacementQuality Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        /// <summary>
        /// Aggregates per-bundle placement metrics and adjacent-bundle transition metrics into one pair-quality value.
        /// </summary>
        public static IrAdjacentBundlePlacementQuality Create(
            IrBundlePlacementQuality firstQuality,
            IrBundlePlacementQuality secondQuality,
            IrBundleTransitionQuality incomingTransitionQuality,
            IrBundleTransitionQuality transitionQuality)
        {
            ArgumentNullException.ThrowIfNull(firstQuality);
            ArgumentNullException.ThrowIfNull(secondQuality);
            ArgumentNullException.ThrowIfNull(incomingTransitionQuality);
            ArgumentNullException.ThrowIfNull(transitionQuality);

            return new IrAdjacentBundlePlacementQuality(
                OccupiedSlotSpanSum: firstQuality.OccupiedSlotSpan + secondQuality.OccupiedSlotSpan,
                InternalGapCount: firstQuality.InternalGapCount + secondQuality.InternalGapCount,
                OrderInversionCount: firstQuality.OrderInversionCount + secondQuality.OrderInversionCount,
                LargestInternalGapSum: firstQuality.LargestInternalGap + secondQuality.LargestInternalGap,
                LeadingEmptySlotCount: firstQuality.LeadingEmptySlotCount + secondQuality.LeadingEmptySlotCount,
                ConstrainedSlotDisplacementCost: firstQuality.ConstrainedSlotDisplacementCost + secondQuality.ConstrainedSlotDisplacementCost,
                SlotIndexSum: firstQuality.SlotIndexSum + secondQuality.SlotIndexSum,
                IncomingOverlappingLaneCount: incomingTransitionQuality.OverlappingLaneCount,
                IncomingReusedLaneCount: incomingTransitionQuality.ReusedLaneCount,
                IncomingSlotDriftCost: incomingTransitionQuality.SlotDriftCost,
                CrossBundleOverlappingLaneCount: transitionQuality.OverlappingLaneCount,
                CrossBundleReusedLaneCount: transitionQuality.ReusedLaneCount,
                CrossBundleSlotDriftCost: transitionQuality.SlotDriftCost);
        }
    }
}
