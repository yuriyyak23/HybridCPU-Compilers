using System;
using System.Collections.Generic;
using System.Numerics;

namespace HybridCPU.Compiler.Core.IR
{
    public static partial class HybridCpuSlotModel
    {
        private static List<int> BuildStructuralAssignmentOrder(IReadOnlyList<IrIssueSlotMask> structurallyAllowedSlots)
        {
            List<int> order = new List<int>(structurallyAllowedSlots.Count);
            for (int index = 0; index < structurallyAllowedSlots.Count; index++)
            {
                if (structurallyAllowedSlots[index] == IrIssueSlotMask.None)
                {
                    return order;
                }

                order.Add(index);
            }

            order.Sort((left, right) =>
            {
                int comparison = GetSlotCount(structurallyAllowedSlots[left]).CompareTo(GetSlotCount(structurallyAllowedSlots[right]));
                return comparison != 0 ? comparison : left.CompareTo(right);
            });

            return order;
        }

        private static bool TryAssignStructuralSlots(IReadOnlyList<int> order, int orderIndex, IReadOnlyList<IrIssueSlotMask> structurallyAllowedSlots, bool[] usedSlots)
        {
            if (orderIndex >= order.Count)
            {
                return true;
            }

            IrIssueSlotMask slotMask = structurallyAllowedSlots[order[orderIndex]];
            for (int slot = 0; slot < SlotCount; slot++)
            {
                if (!AllowsSlot(slotMask, slot) || usedSlots[slot])
                {
                    continue;
                }

                usedSlots[slot] = true;
                if (TryAssignStructuralSlots(order, orderIndex + 1, structurallyAllowedSlots, usedSlots))
                {
                    return true;
                }

                usedSlots[slot] = false;
            }

            return false;
        }

        private static List<IrBundlePlacementCandidate> EnumerateStructuralPlacementCandidates(IReadOnlyList<IrIssueSlotMask> structurallyAllowedSlots)
        {
            List<int> order = BuildStructuralAssignmentOrder(structurallyAllowedSlots);
            bool[] usedSlots = new bool[SlotCount];
            int[] candidateSlots = new int[structurallyAllowedSlots.Count];
            Array.Fill(candidateSlots, -1);
            List<IrBundlePlacementCandidate> evaluatedPlacements = new List<IrBundlePlacementCandidate>();
            TryCollectStructuralAssignments(order, 0, structurallyAllowedSlots, usedSlots, candidateSlots, evaluatedPlacements);
            return evaluatedPlacements;
        }

        private static bool TryCollectStructuralAssignments(
            IReadOnlyList<int> order,
            int orderIndex,
            IReadOnlyList<IrIssueSlotMask> structurallyAllowedSlots,
            bool[] usedSlots,
            int[] assignedSlots,
            List<IrBundlePlacementCandidate> evaluatedPlacements)
        {
            if (orderIndex >= order.Count)
            {
                IrBundlePlacementQuality candidateQuality = IrBundlePlacementQuality.CreateForStructuralSlotFacts(assignedSlots, structurallyAllowedSlots, SlotCount);
                evaluatedPlacements.Add(new IrBundlePlacementCandidate((int[])assignedSlots.Clone(), candidateQuality));

                return true;
            }

            int instructionIndex = order[orderIndex];
            IrIssueSlotMask slotMask = structurallyAllowedSlots[instructionIndex];
            bool foundAssignment = false;
            for (int slot = 0; slot < SlotCount; slot++)
            {
                if (!AllowsSlot(slotMask, slot) || usedSlots[slot])
                {
                    continue;
                }

                usedSlots[slot] = true;
                assignedSlots[instructionIndex] = slot;
                foundAssignment |= TryCollectStructuralAssignments(
                    order,
                    orderIndex + 1,
                    structurallyAllowedSlots,
                    usedSlots,
                    assignedSlots,
                    evaluatedPlacements);

                assignedSlots[instructionIndex] = -1;
                usedSlots[slot] = false;
            }

            return foundAssignment;
        }

        private static bool TryCollectBundlePlacementCandidates(
            IReadOnlyList<IReadOnlyList<IrIssueSlotMask>> bundleStructurallyAllowedSlots,
            out IrSlotAssignmentAnalysis[] bundleAnalyses,
            out List<IrBundlePlacementCandidate>[] bundleCandidates)
        {
            bundleAnalyses = new IrSlotAssignmentAnalysis[bundleStructurallyAllowedSlots.Count];
            bundleCandidates = new List<IrBundlePlacementCandidate>[bundleStructurallyAllowedSlots.Count];
            for (int bundleIndex = 0; bundleIndex < bundleStructurallyAllowedSlots.Count; bundleIndex++)
            {
                IReadOnlyList<IrIssueSlotMask> currentBundleStructurallyAllowedSlots = bundleStructurallyAllowedSlots[bundleIndex];
                ArgumentNullException.ThrowIfNull(currentBundleStructurallyAllowedSlots);

                IrSlotAssignmentAnalysis analysis = AnalyzeStructuralAssignment(currentBundleStructurallyAllowedSlots);
                bundleAnalyses[bundleIndex] = analysis;
                if (!analysis.HasStructuralPlacement || currentBundleStructurallyAllowedSlots.Count == 0)
                {
                    return false;
                }

                List<IrBundlePlacementCandidate> evaluatedCandidates = EnumerateStructuralPlacementCandidates(currentBundleStructurallyAllowedSlots);
                if (evaluatedCandidates.Count == 0)
                {
                    return false;
                }

                bundleCandidates[bundleIndex] = evaluatedCandidates;
            }

            return true;
        }

        private static List<IrBasicBlockPlacementCandidate> CreateInitialGlobalStates(
            IReadOnlyList<IrBundlePlacementCandidate> firstBundleCandidates,
            IReadOnlyList<int>? previousInstructionSlots,
            ref int evaluatedTransitionCount,
            ref int retainedStateCount)
        {
            List<IrBasicBlockPlacementCandidate> retainedStates = new List<IrBasicBlockPlacementCandidate>(firstBundleCandidates.Count);
            foreach (IrBundlePlacementCandidate firstBundleCandidate in firstBundleCandidates)
            {
                evaluatedTransitionCount++;
                retainedStates.Add(CreateInitialBasicBlockPlacementCandidate(firstBundleCandidate, previousInstructionSlots));
            }

            retainedStateCount += retainedStates.Count;
            return retainedStates;
        }

        private static IrBasicBlockPlacementCandidate CreateInitialBasicBlockPlacementCandidate(
            IrBundlePlacementCandidate firstBundleCandidate,
            IReadOnlyList<int>? previousInstructionSlots)
        {
            IrBundleTransitionQuality incomingTransitionQuality = IrBundleTransitionQuality.Create(previousInstructionSlots, firstBundleCandidate.InstructionSlots);
            IrBasicBlockPlacementQuality quality = IrBasicBlockPlacementQuality.Create(
                new[] { firstBundleCandidate.Quality },
                incomingTransitionQuality,
                Array.Empty<IrBundleTransitionQuality>());
            return new IrBasicBlockPlacementCandidate(
                new[] { firstBundleCandidate.InstructionSlots },
                new[] { firstBundleCandidate.Quality },
                incomingTransitionQuality,
                Array.Empty<IrBundleTransitionQuality>(),
                quality);
        }

        private static IrBasicBlockPlacementCandidate AppendBasicBlockPlacementCandidate(
            IrBasicBlockPlacementCandidate currentPlacement,
            IrBundlePlacementCandidate nextBundleCandidate)
        {
            IReadOnlyList<int>[] bundleInstructionSlots = new IReadOnlyList<int>[currentPlacement.BundleInstructionSlots.Count + 1];
            IrBundlePlacementQuality[] bundlePlacementQualities = new IrBundlePlacementQuality[currentPlacement.BundlePlacementQualities.Count + 1];
            IrBundleTransitionQuality[] crossBundleTransitionQualities = new IrBundleTransitionQuality[currentPlacement.CrossBundleTransitionQualities.Count + 1];
            for (int bundleIndex = 0; bundleIndex < currentPlacement.BundleInstructionSlots.Count; bundleIndex++)
            {
                bundleInstructionSlots[bundleIndex] = currentPlacement.BundleInstructionSlots[bundleIndex];
                bundlePlacementQualities[bundleIndex] = currentPlacement.BundlePlacementQualities[bundleIndex];
            }

            for (int transitionIndex = 0; transitionIndex < currentPlacement.CrossBundleTransitionQualities.Count; transitionIndex++)
            {
                crossBundleTransitionQualities[transitionIndex] = currentPlacement.CrossBundleTransitionQualities[transitionIndex];
            }

            bundleInstructionSlots[^1] = nextBundleCandidate.InstructionSlots;
            bundlePlacementQualities[^1] = nextBundleCandidate.Quality;
            crossBundleTransitionQualities[^1] = IrBundleTransitionQuality.Create(currentPlacement.BundleInstructionSlots[^1], nextBundleCandidate.InstructionSlots);
            IrBasicBlockPlacementQuality quality = IrBasicBlockPlacementQuality.Create(
                bundlePlacementQualities,
                currentPlacement.IncomingTransitionQuality,
                crossBundleTransitionQualities);
            return new IrBasicBlockPlacementCandidate(
                bundleInstructionSlots,
                bundlePlacementQualities,
                currentPlacement.IncomingTransitionQuality,
                crossBundleTransitionQualities,
                quality);
        }

        private static IReadOnlyList<IReadOnlyList<IrSlotAssignmentAnalysis>> SplitBlockAnalyses(
            IReadOnlyList<IrSlotAssignmentAnalysis> flatAnalyses,
            IReadOnlyList<int> blockBundleCounts)
        {
            IReadOnlyList<IrSlotAssignmentAnalysis>[] blockAnalyses = new IReadOnlyList<IrSlotAssignmentAnalysis>[blockBundleCounts.Count];
            int analysisOffset = 0;
            for (int blockIndex = 0; blockIndex < blockBundleCounts.Count; blockIndex++)
            {
                IrSlotAssignmentAnalysis[] currentBlockAnalyses = new IrSlotAssignmentAnalysis[blockBundleCounts[blockIndex]];
                for (int bundleIndex = 0; bundleIndex < currentBlockAnalyses.Length; bundleIndex++)
                {
                    currentBlockAnalyses[bundleIndex] = flatAnalyses[analysisOffset + bundleIndex];
                }

                blockAnalyses[blockIndex] = currentBlockAnalyses;
                analysisOffset += currentBlockAnalyses.Length;
            }

            return blockAnalyses;
        }

        private static IrBasicBlockPlacementCandidate SliceBlockPlacementCandidate(
            IrBasicBlockPlacementCandidate flatPlacement,
            int bundleStartIndex,
            int bundleCount)
        {
            IReadOnlyList<int>[] bundleInstructionSlots = new IReadOnlyList<int>[bundleCount];
            IrBundlePlacementQuality[] bundlePlacementQualities = new IrBundlePlacementQuality[bundleCount];
            IrBundleTransitionQuality[] crossBundleTransitionQualities = bundleCount <= 1
                ? Array.Empty<IrBundleTransitionQuality>()
                : new IrBundleTransitionQuality[bundleCount - 1];
            for (int bundleIndex = 0; bundleIndex < bundleCount; bundleIndex++)
            {
                bundleInstructionSlots[bundleIndex] = flatPlacement.BundleInstructionSlots[bundleStartIndex + bundleIndex];
                bundlePlacementQualities[bundleIndex] = flatPlacement.BundlePlacementQualities[bundleStartIndex + bundleIndex];
                if (bundleIndex > 0)
                {
                    crossBundleTransitionQualities[bundleIndex - 1] = flatPlacement.CrossBundleTransitionQualities[bundleStartIndex + bundleIndex - 1];
                }
            }

            IrBundleTransitionQuality incomingTransitionQuality = bundleStartIndex == 0
                ? flatPlacement.IncomingTransitionQuality
                : flatPlacement.CrossBundleTransitionQualities[bundleStartIndex - 1];
            IrBasicBlockPlacementQuality quality = IrBasicBlockPlacementQuality.Create(
                bundlePlacementQualities,
                incomingTransitionQuality,
                crossBundleTransitionQualities);
            return new IrBasicBlockPlacementCandidate(
                bundleInstructionSlots,
                bundlePlacementQualities,
                incomingTransitionQuality,
                crossBundleTransitionQualities,
                quality);
        }

        private static void CollectBasicBlockPlacementCandidates(
            IReadOnlyList<List<IrBundlePlacementCandidate>> bundleCandidates,
            IReadOnlyList<int>? previousInstructionSlots,
            int bundleIndex,
            List<IrBundlePlacementCandidate> selectedBundleCandidates,
            List<IrBasicBlockPlacementCandidate> paretoOptimalPlacements,
            ref IrBasicBlockPlacementCandidate? bestPlacement,
            ref int evaluatedPlacementCount)
        {
            if (bundleIndex >= bundleCandidates.Count)
            {
                evaluatedPlacementCount++;
                IrBasicBlockPlacementCandidate blockCandidate = CreateBasicBlockPlacementCandidate(selectedBundleCandidates, previousInstructionSlots);
                AddToParetoFrontier(paretoOptimalPlacements, blockCandidate);
                if (bestPlacement is null || HybridCpuBasicBlockPlacementPolicy.IsBetterPlacement(blockCandidate, bestPlacement))
                {
                    bestPlacement = blockCandidate;
                }

                return;
            }

            foreach (IrBundlePlacementCandidate bundleCandidate in bundleCandidates[bundleIndex])
            {
                selectedBundleCandidates.Add(bundleCandidate);
                CollectBasicBlockPlacementCandidates(
                    bundleCandidates,
                    previousInstructionSlots,
                    bundleIndex + 1,
                    selectedBundleCandidates,
                    paretoOptimalPlacements,
                    ref bestPlacement,
                    ref evaluatedPlacementCount);
                selectedBundleCandidates.RemoveAt(selectedBundleCandidates.Count - 1);
            }
        }

        private static IrBasicBlockPlacementCandidate CreateBasicBlockPlacementCandidate(
            IReadOnlyList<IrBundlePlacementCandidate> selectedBundleCandidates,
            IReadOnlyList<int>? previousInstructionSlots)
        {
            IReadOnlyList<int>[] bundleInstructionSlots = new IReadOnlyList<int>[selectedBundleCandidates.Count];
            IrBundlePlacementQuality[] bundlePlacementQualities = new IrBundlePlacementQuality[selectedBundleCandidates.Count];
            IrBundleTransitionQuality[] crossBundleTransitionQualities = selectedBundleCandidates.Count <= 1
                ? Array.Empty<IrBundleTransitionQuality>()
                : new IrBundleTransitionQuality[selectedBundleCandidates.Count - 1];
            for (int bundleIndex = 0; bundleIndex < selectedBundleCandidates.Count; bundleIndex++)
            {
                bundleInstructionSlots[bundleIndex] = selectedBundleCandidates[bundleIndex].InstructionSlots;
                bundlePlacementQualities[bundleIndex] = selectedBundleCandidates[bundleIndex].Quality;
                if (bundleIndex > 0)
                {
                    crossBundleTransitionQualities[bundleIndex - 1] = IrBundleTransitionQuality.Create(
                        selectedBundleCandidates[bundleIndex - 1].InstructionSlots,
                        selectedBundleCandidates[bundleIndex].InstructionSlots);
                }
            }

            IrBundleTransitionQuality incomingTransitionQuality = IrBundleTransitionQuality.Create(previousInstructionSlots, selectedBundleCandidates[0].InstructionSlots);
            IrBasicBlockPlacementQuality quality = IrBasicBlockPlacementQuality.Create(
                bundlePlacementQualities,
                incomingTransitionQuality,
                crossBundleTransitionQualities);
            return new IrBasicBlockPlacementCandidate(
                bundleInstructionSlots,
                bundlePlacementQualities,
                incomingTransitionQuality,
                crossBundleTransitionQualities,
                quality);
        }

        private static void AddToParetoFrontier(List<IrBundlePlacementCandidate> paretoOptimalPlacements, IrBundlePlacementCandidate candidate)
        {
            for (int index = paretoOptimalPlacements.Count - 1; index >= 0; index--)
            {
                IrBundlePlacementCandidate existingCandidate = paretoOptimalPlacements[index];
                if (HybridCpuBundlePlacementPolicy.Dominates(existingCandidate, candidate))
                {
                    return;
                }

                if (HybridCpuBundlePlacementPolicy.Dominates(candidate, existingCandidate))
                {
                    paretoOptimalPlacements.RemoveAt(index);
                }
            }

            paretoOptimalPlacements.Add(candidate);
        }

        private static void AddToParetoFrontier(List<IrAdjacentBundlePlacementCandidate> paretoOptimalPlacementPairs, IrAdjacentBundlePlacementCandidate candidate)
        {
            for (int index = paretoOptimalPlacementPairs.Count - 1; index >= 0; index--)
            {
                IrAdjacentBundlePlacementCandidate existingCandidate = paretoOptimalPlacementPairs[index];
                if (HybridCpuAdjacentBundlePlacementPolicy.Dominates(existingCandidate, candidate))
                {
                    return;
                }

                if (HybridCpuAdjacentBundlePlacementPolicy.Dominates(candidate, existingCandidate))
                {
                    paretoOptimalPlacementPairs.RemoveAt(index);
                }
            }

            paretoOptimalPlacementPairs.Add(candidate);
        }

        private static void AddToParetoFrontier(List<IrAdjacentBundleTripletPlacementCandidate> paretoOptimalPlacementTriplets, IrAdjacentBundleTripletPlacementCandidate candidate)
        {
            for (int index = paretoOptimalPlacementTriplets.Count - 1; index >= 0; index--)
            {
                IrAdjacentBundleTripletPlacementCandidate existingCandidate = paretoOptimalPlacementTriplets[index];
                if (HybridCpuAdjacentBundleTripletPlacementPolicy.Dominates(existingCandidate, candidate))
                {
                    return;
                }

                if (HybridCpuAdjacentBundleTripletPlacementPolicy.Dominates(candidate, existingCandidate))
                {
                    paretoOptimalPlacementTriplets.RemoveAt(index);
                }
            }

            paretoOptimalPlacementTriplets.Add(candidate);
        }

        private static void AddToParetoFrontier(List<IrBasicBlockPlacementCandidate> paretoOptimalPlacements, IrBasicBlockPlacementCandidate candidate)
        {
            for (int index = paretoOptimalPlacements.Count - 1; index >= 0; index--)
            {
                IrBasicBlockPlacementCandidate existingCandidate = paretoOptimalPlacements[index];
                if (HybridCpuBasicBlockPlacementPolicy.Dominates(existingCandidate, candidate))
                {
                    return;
                }

                if (HybridCpuBasicBlockPlacementPolicy.Dominates(candidate, existingCandidate))
                {
                    paretoOptimalPlacements.RemoveAt(index);
                }
            }

            paretoOptimalPlacements.Add(candidate);
        }

        private static bool AllowsSlot(IrIssueSlotMask slotMask, int slot)
        {
            return (((int)slotMask >> slot) & 1) != 0;
        }

        private static int GetSlotCount(IrIssueSlotMask slotMask)
        {
            int mask = (int)slotMask;
            int count = 0;

            while (mask != 0)
            {
                count += mask & 1;
                mask >>= 1;
            }

            return count;
        }
    }
}
