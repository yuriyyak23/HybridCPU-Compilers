using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Deterministic local Stage 6 placement policy for selecting one legal bundle layout without rescheduling.
    /// </summary>
    internal static class HybridCpuBundlePlacementPolicy
    {
        public static bool Dominates(IrBundlePlacementCandidate candidate, IrBundlePlacementCandidate other)
        {
            bool strictlyBetter = false;
            if (!IsNoWorse(candidate.Quality.OccupiedSlotSpan, other.Quality.OccupiedSlotSpan, ref strictlyBetter))
            {
                return false;
            }

            if (!IsNoWorse(candidate.Quality.InternalGapCount, other.Quality.InternalGapCount, ref strictlyBetter))
            {
                return false;
            }

            if (!IsNoWorse(candidate.Quality.OrderInversionCount, other.Quality.OrderInversionCount, ref strictlyBetter))
            {
                return false;
            }

            if (!IsNoWorse(candidate.Quality.LargestInternalGap, other.Quality.LargestInternalGap, ref strictlyBetter))
            {
                return false;
            }

            if (!IsNoWorse(candidate.Quality.LeadingEmptySlotCount, other.Quality.LeadingEmptySlotCount, ref strictlyBetter))
            {
                return false;
            }

            if (!IsNoWorse(candidate.Quality.ConstrainedSlotDisplacementCost, other.Quality.ConstrainedSlotDisplacementCost, ref strictlyBetter))
            {
                return false;
            }

            if (!IsNoWorse(candidate.Quality.SlotIndexSum, other.Quality.SlotIndexSum, ref strictlyBetter))
            {
                return false;
            }

            if (strictlyBetter)
            {
                return true;
            }

            return CompareAssignedSlots(candidate.InstructionSlots, other.InstructionSlots) < 0;
        }

        public static bool IsBetterPlacement(
            IrBundlePlacementQuality candidateQuality,
            IReadOnlyList<int> candidateSlots,
            IrBundlePlacementQuality currentQuality,
            IReadOnlyList<int> currentSlots)
        {
            return IsBetterPlacement(
                candidateQuality,
                IrBundleTransitionQuality.Empty,
                candidateSlots,
                currentQuality,
                IrBundleTransitionQuality.Empty,
                currentSlots);
        }

        public static bool IsBetterPlacement(
            IrBundlePlacementQuality candidateQuality,
            IrBundleTransitionQuality candidateTransitionQuality,
            IReadOnlyList<int> candidateSlots,
            IrBundlePlacementQuality currentQuality,
            IrBundleTransitionQuality currentTransitionQuality,
            IReadOnlyList<int> currentSlots,
            HybridCpuBackendPlacementTieBreakContext tieBreakContext = default)
        {
            int comparison = candidateQuality.OccupiedSlotSpan.CompareTo(currentQuality.OccupiedSlotSpan);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            comparison = candidateQuality.InternalGapCount.CompareTo(currentQuality.InternalGapCount);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            comparison = candidateQuality.OrderInversionCount.CompareTo(currentQuality.OrderInversionCount);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            comparison = candidateQuality.LargestInternalGap.CompareTo(currentQuality.LargestInternalGap);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            comparison = candidateTransitionQuality.SlotDriftCost.CompareTo(currentTransitionQuality.SlotDriftCost);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            if (tieBreakContext.PreferLowerCoalescingFootprint)
            {
                comparison = candidateTransitionQuality.OverlappingLaneCount.CompareTo(currentTransitionQuality.OverlappingLaneCount);
                if (comparison != 0)
                {
                    return comparison < 0;
                }

                comparison = candidateTransitionQuality.ReusedLaneCount.CompareTo(currentTransitionQuality.ReusedLaneCount);
                if (comparison != 0)
                {
                    return comparison < 0;
                }
            }

            comparison = currentTransitionQuality.ReusedLaneCount.CompareTo(candidateTransitionQuality.ReusedLaneCount);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            comparison = currentTransitionQuality.OverlappingLaneCount.CompareTo(candidateTransitionQuality.OverlappingLaneCount);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            comparison = candidateQuality.LeadingEmptySlotCount.CompareTo(currentQuality.LeadingEmptySlotCount);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            comparison = candidateQuality.ConstrainedSlotDisplacementCost.CompareTo(currentQuality.ConstrainedSlotDisplacementCost);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            comparison = candidateQuality.SlotIndexSum.CompareTo(currentQuality.SlotIndexSum);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            return CompareAssignedSlots(candidateSlots, currentSlots) < 0;
        }

        private static bool IsNoWorse(int candidateMetric, int otherMetric, ref bool strictlyBetter)
        {
            if (candidateMetric > otherMetric)
            {
                return false;
            }

            if (candidateMetric < otherMetric)
            {
                strictlyBetter = true;
            }

            return true;
        }

        private static int CompareAssignedSlots(IReadOnlyList<int> candidateSlots, IReadOnlyList<int> currentSlots)
        {
            int count = Math.Min(candidateSlots.Count, currentSlots.Count);
            for (int instructionIndex = 0; instructionIndex < count; instructionIndex++)
            {
                int comparison = candidateSlots[instructionIndex].CompareTo(currentSlots[instructionIndex]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return candidateSlots.Count.CompareTo(currentSlots.Count);
        }
    }
}
