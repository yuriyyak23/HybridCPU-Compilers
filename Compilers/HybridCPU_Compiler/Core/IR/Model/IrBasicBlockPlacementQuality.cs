using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Aggregate quality metrics for one whole-block Stage 6 placement candidate.
    /// </summary>
    public sealed record IrBasicBlockPlacementQuality(
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
        /// Gets the quality metrics for an empty basic-block placement candidate.
        /// </summary>
        public static IrBasicBlockPlacementQuality Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        /// <summary>
        /// Aggregates per-bundle placement metrics and cross-bundle transition metrics into one whole-block quality value.
        /// </summary>
        public static IrBasicBlockPlacementQuality Create(
            IReadOnlyList<IrBundlePlacementQuality> bundlePlacementQualities,
            IrBundleTransitionQuality incomingTransitionQuality,
            IReadOnlyList<IrBundleTransitionQuality> crossBundleTransitionQualities)
        {
            ArgumentNullException.ThrowIfNull(bundlePlacementQualities);
            ArgumentNullException.ThrowIfNull(incomingTransitionQuality);
            ArgumentNullException.ThrowIfNull(crossBundleTransitionQualities);

            int occupiedSlotSpanSum = 0;
            int internalGapCount = 0;
            int orderInversionCount = 0;
            int largestInternalGapSum = 0;
            int leadingEmptySlotCount = 0;
            int constrainedSlotDisplacementCost = 0;
            int slotIndexSum = 0;
            foreach (IrBundlePlacementQuality bundleQuality in bundlePlacementQualities)
            {
                ArgumentNullException.ThrowIfNull(bundleQuality);
                occupiedSlotSpanSum += bundleQuality.OccupiedSlotSpan;
                internalGapCount += bundleQuality.InternalGapCount;
                orderInversionCount += bundleQuality.OrderInversionCount;
                largestInternalGapSum += bundleQuality.LargestInternalGap;
                leadingEmptySlotCount += bundleQuality.LeadingEmptySlotCount;
                constrainedSlotDisplacementCost += bundleQuality.ConstrainedSlotDisplacementCost;
                slotIndexSum += bundleQuality.SlotIndexSum;
            }

            int crossBundleOverlappingLaneCount = 0;
            int crossBundleReusedLaneCount = 0;
            int crossBundleSlotDriftCost = 0;
            foreach (IrBundleTransitionQuality transitionQuality in crossBundleTransitionQualities)
            {
                ArgumentNullException.ThrowIfNull(transitionQuality);
                crossBundleOverlappingLaneCount += transitionQuality.OverlappingLaneCount;
                crossBundleReusedLaneCount += transitionQuality.ReusedLaneCount;
                crossBundleSlotDriftCost += transitionQuality.SlotDriftCost;
            }

            return new IrBasicBlockPlacementQuality(
                OccupiedSlotSpanSum: occupiedSlotSpanSum,
                InternalGapCount: internalGapCount,
                OrderInversionCount: orderInversionCount,
                LargestInternalGapSum: largestInternalGapSum,
                LeadingEmptySlotCount: leadingEmptySlotCount,
                ConstrainedSlotDisplacementCost: constrainedSlotDisplacementCost,
                SlotIndexSum: slotIndexSum,
                IncomingOverlappingLaneCount: incomingTransitionQuality.OverlappingLaneCount,
                IncomingReusedLaneCount: incomingTransitionQuality.ReusedLaneCount,
                IncomingSlotDriftCost: incomingTransitionQuality.SlotDriftCost,
                CrossBundleOverlappingLaneCount: crossBundleOverlappingLaneCount,
                CrossBundleReusedLaneCount: crossBundleReusedLaneCount,
                CrossBundleSlotDriftCost: crossBundleSlotDriftCost);
        }
    }
}
