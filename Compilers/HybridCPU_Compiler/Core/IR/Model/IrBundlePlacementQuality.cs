using System;
using System.Collections.Generic;
using System.Numerics;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Quality metrics for one materialized Stage 6 slot placement.
    /// </summary>
    public sealed record IrBundlePlacementQuality(
        int OccupiedSlotCount,
        int OccupiedSlotSpan,
        int InternalGapCount,
        int LargestInternalGap,
        int OrderInversionCount,
        int LeadingEmptySlotCount,
        int TrailingEmptySlotCount,
        int SlotIndexSum,
        int ConstrainedSlotDisplacementCost)
    {
        /// <summary>
        /// Gets the quality metrics for an empty assignment.
        /// </summary>
        public static IrBundlePlacementQuality Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

        /// <summary>
        /// Computes quality metrics for one assigned physical slot set.
        /// </summary>
        public static IrBundlePlacementQuality Create(IReadOnlyList<int> assignedSlots, int slotCount)
        {
            ArgumentNullException.ThrowIfNull(assignedSlots);

            IrIssueSlotMask[] structurallyAllowedSlots = new IrIssueSlotMask[assignedSlots.Count];
            Array.Fill(structurallyAllowedSlots, IrIssueSlotMask.All);
            return CreateForStructuralSlotFacts(assignedSlots, structurallyAllowedSlots, slotCount);
        }

        /// <summary>
        /// Computes quality metrics for one assigned physical slot set against the instructions' legal-slot masks.
        /// </summary>
        [Obsolete(
            "Compiler-side placement quality consumes structurally allowed slot facts only; use CreateForStructuralSlotFacts.",
            false)]
        public static IrBundlePlacementQuality Create(IReadOnlyList<int> assignedSlots, IReadOnlyList<IrIssueSlotMask> legalSlots, int slotCount)
        {
            return CreateForStructuralSlotFacts(assignedSlots, legalSlots, slotCount);
        }

        /// <summary>
        /// Computes quality metrics for one assigned physical slot set against structural slot facts.
        /// </summary>
        public static IrBundlePlacementQuality CreateForStructuralSlotFacts(
            IReadOnlyList<int> assignedSlots,
            IReadOnlyList<IrIssueSlotMask> structurallyAllowedSlots,
            int slotCount)
        {
            ArgumentNullException.ThrowIfNull(assignedSlots);
            ArgumentNullException.ThrowIfNull(structurallyAllowedSlots);
            if (slotCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(slotCount));
            }

            if (assignedSlots.Count != structurallyAllowedSlots.Count)
            {
                throw new ArgumentException("Assigned slot count must match structural slot fact count.", nameof(structurallyAllowedSlots));
            }

            int occupiedSlotCount = 0;
            int minSlot = slotCount;
            int maxSlot = -1;
            int slotIndexSum = 0;
            int orderInversionCount = 0;
            int constrainedSlotDisplacementCost = 0;
            bool[] occupied = new bool[slotCount];

            for (int instructionIndex = 0; instructionIndex < assignedSlots.Count; instructionIndex++)
            {
                int slot = assignedSlots[instructionIndex];
                if (slot < 0)
                {
                    continue;
                }

                occupiedSlotCount++;
                occupied[slot] = true;
                slotIndexSum += slot;
                constrainedSlotDisplacementCost += GetConstrainedSlotDisplacementCost(structurallyAllowedSlots[instructionIndex], slot, slotCount);
                if (slot < minSlot)
                {
                    minSlot = slot;
                }

                if (slot > maxSlot)
                {
                    maxSlot = slot;
                }
            }

            if (occupiedSlotCount == 0)
            {
                return Empty;
            }

            int occupiedSlotSpan = (maxSlot - minSlot) + 1;
            int internalGapCount = 0;
            int largestInternalGap = 0;
            int currentGapLength = 0;
            for (int slot = minSlot; slot <= maxSlot; slot++)
            {
                if (!occupied[slot])
                {
                    internalGapCount++;
                    currentGapLength++;
                    if (currentGapLength > largestInternalGap)
                    {
                        largestInternalGap = currentGapLength;
                    }
                }
                else
                {
                    currentGapLength = 0;
                }
            }

            for (int leftIndex = 0; leftIndex < assignedSlots.Count; leftIndex++)
            {
                int leftSlot = assignedSlots[leftIndex];
                if (leftSlot < 0)
                {
                    continue;
                }

                for (int rightIndex = leftIndex + 1; rightIndex < assignedSlots.Count; rightIndex++)
                {
                    int rightSlot = assignedSlots[rightIndex];
                    if (rightSlot >= 0 && leftSlot > rightSlot)
                    {
                        orderInversionCount++;
                    }
                }
            }

            return new IrBundlePlacementQuality(
                occupiedSlotCount,
                occupiedSlotSpan,
                internalGapCount,
                largestInternalGap,
                orderInversionCount,
                minSlot,
                (slotCount - 1) - maxSlot,
                slotIndexSum,
                constrainedSlotDisplacementCost);
        }

        private static int GetConstrainedSlotDisplacementCost(IrIssueSlotMask structurallyAllowedSlots, int assignedSlot, int slotCount)
        {
            if (assignedSlot <= 0)
            {
                return 0;
            }

            int structurallyAllowedSlotCount = BitOperations.PopCount((uint)structurallyAllowedSlots);
            if (structurallyAllowedSlotCount <= 0)
            {
                return 0;
            }

            uint lowerSlotMask = ((uint)1 << assignedSlot) - 1u;
            int lowerStructurallyAllowedSlotCount = BitOperations.PopCount((uint)structurallyAllowedSlots & lowerSlotMask);
            int constraintWeight = Math.Max(1, (slotCount - structurallyAllowedSlotCount) + 1);
            return lowerStructurallyAllowedSlotCount * constraintWeight;
        }
    }
}
