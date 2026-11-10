using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Transition metrics between two adjacent materialized bundles.
    /// </summary>
    public sealed record IrBundleTransitionQuality(
        int OverlappingLaneCount,
        int ReusedLaneCount,
        int SlotDriftCost)
    {
        /// <summary>
        /// Gets the transition metrics for a bundle that has no predecessor context.
        /// </summary>
        public static IrBundleTransitionQuality Empty { get; } = new(0, 0, 0);

        /// <summary>
        /// Computes transition metrics against the previous bundle's issued lanes.
        /// </summary>
        public static IrBundleTransitionQuality Create(IReadOnlyList<int>? previousInstructionSlots, IReadOnlyList<int> currentInstructionSlots)
        {
            ArgumentNullException.ThrowIfNull(currentInstructionSlots);

            if (previousInstructionSlots is null || previousInstructionSlots.Count == 0 || currentInstructionSlots.Count == 0)
            {
                return Empty;
            }

            int overlappingLaneCount = Math.Min(previousInstructionSlots.Count, currentInstructionSlots.Count);
            int reusedLaneCount = 0;
            int slotDriftCost = 0;

            for (int laneIndex = 0; laneIndex < overlappingLaneCount; laneIndex++)
            {
                int previousSlot = previousInstructionSlots[laneIndex];
                int currentSlot = currentInstructionSlots[laneIndex];
                if (previousSlot == currentSlot)
                {
                    reusedLaneCount++;
                }

                slotDriftCost += Math.Abs(currentSlot - previousSlot);
            }

            return new IrBundleTransitionQuality(overlappingLaneCount, reusedLaneCount, slotDriftCost);
        }
    }
}
