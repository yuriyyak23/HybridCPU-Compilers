using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Deterministic Stage 6 placement policy for adjacent-bundle triplet search without rescheduling.
    /// </summary>
    internal static class HybridCpuAdjacentBundleTripletPlacementPolicy
    {
        public static bool Dominates(IrAdjacentBundleTripletPlacementCandidate candidate, IrAdjacentBundleTripletPlacementCandidate other)
        {
            bool strictlyBetter = false;
            if (!IsNoWorse(candidate.Quality.OccupiedSlotSpanSum, other.Quality.OccupiedSlotSpanSum, ref strictlyBetter))
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

            if (!IsNoWorse(candidate.Quality.LargestInternalGapSum, other.Quality.LargestInternalGapSum, ref strictlyBetter))
            {
                return false;
            }

            if (!IsNoWorse(candidate.Quality.IncomingSlotDriftCost, other.Quality.IncomingSlotDriftCost, ref strictlyBetter))
            {
                return false;
            }

            if (!IsNoWorse(candidate.Quality.CrossBundleSlotDriftCost, other.Quality.CrossBundleSlotDriftCost, ref strictlyBetter))
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

            int comparison = CompareAssignedSlots(candidate.FirstInstructionSlots, other.FirstInstructionSlots);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            comparison = CompareAssignedSlots(candidate.SecondInstructionSlots, other.SecondInstructionSlots);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            return CompareAssignedSlots(candidate.ThirdInstructionSlots, other.ThirdInstructionSlots) < 0;
        }

        public static bool IsBetterPlacement(
            IrAdjacentBundleTripletPlacementCandidate candidate,
            IrAdjacentBundleTripletPlacementCandidate current,
            HybridCpuBackendPlacementTieBreakContext tieBreakContext = default)
        {
            int comparison = candidate.Quality.OccupiedSlotSpanSum.CompareTo(current.Quality.OccupiedSlotSpanSum);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            comparison = candidate.Quality.InternalGapCount.CompareTo(current.Quality.InternalGapCount);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            comparison = candidate.Quality.OrderInversionCount.CompareTo(current.Quality.OrderInversionCount);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            comparison = candidate.Quality.LargestInternalGapSum.CompareTo(current.Quality.LargestInternalGapSum);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            comparison = candidate.Quality.IncomingSlotDriftCost.CompareTo(current.Quality.IncomingSlotDriftCost);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            comparison = candidate.Quality.CrossBundleSlotDriftCost.CompareTo(current.Quality.CrossBundleSlotDriftCost);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            if (tieBreakContext.PreferLowerCoalescingFootprint)
            {
                comparison = candidate.Quality.IncomingOverlappingLaneCount.CompareTo(current.Quality.IncomingOverlappingLaneCount);
                if (comparison != 0)
                {
                    return comparison < 0;
                }

                comparison = candidate.Quality.CrossBundleOverlappingLaneCount.CompareTo(current.Quality.CrossBundleOverlappingLaneCount);
                if (comparison != 0)
                {
                    return comparison < 0;
                }

                comparison = candidate.Quality.IncomingReusedLaneCount.CompareTo(current.Quality.IncomingReusedLaneCount);
                if (comparison != 0)
                {
                    return comparison < 0;
                }

                comparison = candidate.Quality.CrossBundleReusedLaneCount.CompareTo(current.Quality.CrossBundleReusedLaneCount);
                if (comparison != 0)
                {
                    return comparison < 0;
                }
            }

            comparison = candidate.Quality.LeadingEmptySlotCount.CompareTo(current.Quality.LeadingEmptySlotCount);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            comparison = candidate.Quality.ConstrainedSlotDisplacementCost.CompareTo(current.Quality.ConstrainedSlotDisplacementCost);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            comparison = candidate.Quality.SlotIndexSum.CompareTo(current.Quality.SlotIndexSum);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            comparison = current.Quality.IncomingReusedLaneCount.CompareTo(candidate.Quality.IncomingReusedLaneCount);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            comparison = current.Quality.CrossBundleReusedLaneCount.CompareTo(candidate.Quality.CrossBundleReusedLaneCount);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            comparison = current.Quality.IncomingOverlappingLaneCount.CompareTo(candidate.Quality.IncomingOverlappingLaneCount);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            comparison = current.Quality.CrossBundleOverlappingLaneCount.CompareTo(candidate.Quality.CrossBundleOverlappingLaneCount);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            comparison = CompareAssignedSlots(candidate.FirstInstructionSlots, current.FirstInstructionSlots);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            comparison = CompareAssignedSlots(candidate.SecondInstructionSlots, current.SecondInstructionSlots);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            return CompareAssignedSlots(candidate.ThirdInstructionSlots, current.ThirdInstructionSlots) < 0;
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
