using System;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Aggregate quality metrics for one adjacent-bundle triplet placement window.
    /// </summary>
    public sealed record IrAdjacentBundleTripletPlacementQuality(
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
        /// Gets the quality metrics for an empty adjacent-bundle triplet window.
        /// </summary>
        public static IrAdjacentBundleTripletPlacementQuality Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        /// <summary>
        /// Aggregates per-bundle placement metrics and window transition metrics into one triplet-quality value.
        /// </summary>
        public static IrAdjacentBundleTripletPlacementQuality Create(
            IrBundlePlacementQuality firstQuality,
            IrBundlePlacementQuality secondQuality,
            IrBundlePlacementQuality thirdQuality,
            IrBundleTransitionQuality incomingTransitionQuality,
            IrBundleTransitionQuality firstTransitionQuality,
            IrBundleTransitionQuality secondTransitionQuality)
        {
            ArgumentNullException.ThrowIfNull(firstQuality);
            ArgumentNullException.ThrowIfNull(secondQuality);
            ArgumentNullException.ThrowIfNull(thirdQuality);
            ArgumentNullException.ThrowIfNull(incomingTransitionQuality);
            ArgumentNullException.ThrowIfNull(firstTransitionQuality);
            ArgumentNullException.ThrowIfNull(secondTransitionQuality);

            return new IrAdjacentBundleTripletPlacementQuality(
                OccupiedSlotSpanSum: firstQuality.OccupiedSlotSpan + secondQuality.OccupiedSlotSpan + thirdQuality.OccupiedSlotSpan,
                InternalGapCount: firstQuality.InternalGapCount + secondQuality.InternalGapCount + thirdQuality.InternalGapCount,
                OrderInversionCount: firstQuality.OrderInversionCount + secondQuality.OrderInversionCount + thirdQuality.OrderInversionCount,
                LargestInternalGapSum: firstQuality.LargestInternalGap + secondQuality.LargestInternalGap + thirdQuality.LargestInternalGap,
                LeadingEmptySlotCount: firstQuality.LeadingEmptySlotCount + secondQuality.LeadingEmptySlotCount + thirdQuality.LeadingEmptySlotCount,
                ConstrainedSlotDisplacementCost: firstQuality.ConstrainedSlotDisplacementCost + secondQuality.ConstrainedSlotDisplacementCost + thirdQuality.ConstrainedSlotDisplacementCost,
                SlotIndexSum: firstQuality.SlotIndexSum + secondQuality.SlotIndexSum + thirdQuality.SlotIndexSum,
                IncomingOverlappingLaneCount: incomingTransitionQuality.OverlappingLaneCount,
                IncomingReusedLaneCount: incomingTransitionQuality.ReusedLaneCount,
                IncomingSlotDriftCost: incomingTransitionQuality.SlotDriftCost,
                CrossBundleOverlappingLaneCount: firstTransitionQuality.OverlappingLaneCount + secondTransitionQuality.OverlappingLaneCount,
                CrossBundleReusedLaneCount: firstTransitionQuality.ReusedLaneCount + secondTransitionQuality.ReusedLaneCount,
                CrossBundleSlotDriftCost: firstTransitionQuality.SlotDriftCost + secondTransitionQuality.SlotDriftCost);
        }
    }
}
