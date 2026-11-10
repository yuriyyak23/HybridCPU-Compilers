using System;
using System.Collections.Generic;
using System.Numerics;

namespace HybridCPU.Compiler.Core.IR
{
    public static partial class HybridCpuSlotModel
    {
        /// <summary>
        /// Searches the legal physical slot placements for one candidate group without changing scheduler order.
        /// </summary>
        [Obsolete(
            "Compiler-side placement search consumes structurally allowed slot facts only; use SearchStructuralAssignments.",
            false)]
        public static IrBundlePlacementSearchResult SearchAssignments(IReadOnlyList<IrIssueSlotMask> legalSlots)
        {
            return SearchStructuralAssignments(legalSlots, previousInstructionSlots: null);
        }

        /// <summary>
        /// Searches the legal physical slot placements for one candidate group with adjacent-bundle context.
        /// </summary>
        [Obsolete(
            "Compiler-side placement search consumes structurally allowed slot facts only; use SearchStructuralAssignments.",
            false)]
        public static IrBundlePlacementSearchResult SearchAssignments(IReadOnlyList<IrIssueSlotMask> legalSlots, IReadOnlyList<int>? previousInstructionSlots)
        {
            return SearchStructuralAssignments(legalSlots, previousInstructionSlots, default);
        }

        /// <summary>
        /// Searches physical slot placements for structural slot facts without changing scheduler order.
        /// </summary>
        public static IrBundlePlacementSearchResult SearchStructuralAssignments(IReadOnlyList<IrIssueSlotMask> structurallyAllowedSlots)
        {
            return SearchStructuralAssignments(structurallyAllowedSlots, previousInstructionSlots: null);
        }

        /// <summary>
        /// Searches physical slot placements for structural slot facts with adjacent-bundle context.
        /// </summary>
        public static IrBundlePlacementSearchResult SearchStructuralAssignments(IReadOnlyList<IrIssueSlotMask> structurallyAllowedSlots, IReadOnlyList<int>? previousInstructionSlots)
        {
            return SearchStructuralAssignments(structurallyAllowedSlots, previousInstructionSlots, default);
        }

        internal static IrBundlePlacementSearchResult SearchStructuralAssignments(
            IReadOnlyList<IrIssueSlotMask> structurallyAllowedSlots,
            IReadOnlyList<int>? previousInstructionSlots,
            HybridCpuBackendPlacementTieBreakContext tieBreakContext)
        {
            ArgumentNullException.ThrowIfNull(structurallyAllowedSlots);

            IrSlotAssignmentAnalysis analysis = AnalyzeStructuralAssignment(structurallyAllowedSlots);
            if (!analysis.HasStructuralPlacement || structurallyAllowedSlots.Count == 0)
            {
                return new IrBundlePlacementSearchResult(
                    analysis,
                    Array.Empty<IrBundlePlacementCandidate>(),
                    Array.Empty<int>(),
                    IrBundlePlacementQuality.Empty,
                    IrBundleTransitionQuality.Empty,
                    IrBundlePlacementSearchSummary.Empty);
            }

            List<IrBundlePlacementCandidate> evaluatedPlacements = EnumerateStructuralPlacementCandidates(structurallyAllowedSlots);
            if (evaluatedPlacements.Count == 0)
            {
                return new IrBundlePlacementSearchResult(
                    analysis,
                    Array.Empty<IrBundlePlacementCandidate>(),
                    Array.Empty<int>(),
                    IrBundlePlacementQuality.Empty,
                    IrBundleTransitionQuality.Empty,
                    IrBundlePlacementSearchSummary.Empty);
            }

            List<IrBundlePlacementCandidate> paretoOptimalPlacements = new List<IrBundlePlacementCandidate>();
            IrBundlePlacementCandidate? bestCandidate = null;
            IrBundleTransitionQuality bestTransitionQuality = IrBundleTransitionQuality.Empty;

            foreach (IrBundlePlacementCandidate candidate in evaluatedPlacements)
            {
                AddToParetoFrontier(paretoOptimalPlacements, candidate);

                IrBundleTransitionQuality candidateTransitionQuality = IrBundleTransitionQuality.Create(previousInstructionSlots, candidate.InstructionSlots);
                if (bestCandidate is null || HybridCpuBundlePlacementPolicy.IsBetterPlacement(candidate.Quality, candidateTransitionQuality, candidate.InstructionSlots, bestCandidate.Quality, bestTransitionQuality, bestCandidate.InstructionSlots, tieBreakContext))
                {
                    bestCandidate = candidate;
                    bestTransitionQuality = candidateTransitionQuality;
                }
            }

            if (bestCandidate is null)
            {
                return new IrBundlePlacementSearchResult(
                    analysis,
                    Array.Empty<IrBundlePlacementCandidate>(),
                    Array.Empty<int>(),
                    IrBundlePlacementQuality.Empty,
                    IrBundleTransitionQuality.Empty,
                    IrBundlePlacementSearchSummary.Empty);
            }

            return new IrBundlePlacementSearchResult(
                analysis,
                paretoOptimalPlacements,
                bestCandidate.InstructionSlots,
                bestCandidate.Quality,
                bestTransitionQuality,
                new IrBundlePlacementSearchSummary(
                    evaluatedPlacements.Count,
                    paretoOptimalPlacements.Count,
                    evaluatedPlacements.Count - paretoOptimalPlacements.Count));
        }

        /// <summary>
        /// Searches legal placement pairs for two adjacent bundles without changing scheduler order.
        /// </summary>
        [Obsolete(
            "Compiler-side placement search consumes structurally allowed slot facts only; use SearchAdjacentBundleStructuralAssignments.",
            false)]
        public static IrAdjacentBundlePlacementSearchResult SearchAdjacentBundleAssignments(
            IReadOnlyList<IrIssueSlotMask> firstBundleLegalSlots,
            IReadOnlyList<IrIssueSlotMask> secondBundleLegalSlots)
        {
            return SearchAdjacentBundleStructuralAssignments(firstBundleLegalSlots, secondBundleLegalSlots, previousInstructionSlots: null);
        }

        /// <summary>
        /// Searches legal placement pairs for two adjacent bundles with incoming previous-bundle context without changing scheduler order.
        /// </summary>
        [Obsolete(
            "Compiler-side placement search consumes structurally allowed slot facts only; use SearchAdjacentBundleStructuralAssignments.",
            false)]
        public static IrAdjacentBundlePlacementSearchResult SearchAdjacentBundleAssignments(
            IReadOnlyList<IrIssueSlotMask> firstBundleLegalSlots,
            IReadOnlyList<IrIssueSlotMask> secondBundleLegalSlots,
            IReadOnlyList<int>? previousInstructionSlots)
        {
            return SearchAdjacentBundleStructuralAssignments(firstBundleLegalSlots, secondBundleLegalSlots, previousInstructionSlots, default);
        }

        /// <summary>
        /// Searches structural placement pairs for two adjacent bundles without changing scheduler order.
        /// </summary>
        public static IrAdjacentBundlePlacementSearchResult SearchAdjacentBundleStructuralAssignments(
            IReadOnlyList<IrIssueSlotMask> firstBundleStructurallyAllowedSlots,
            IReadOnlyList<IrIssueSlotMask> secondBundleStructurallyAllowedSlots)
        {
            return SearchAdjacentBundleStructuralAssignments(
                firstBundleStructurallyAllowedSlots,
                secondBundleStructurallyAllowedSlots,
                previousInstructionSlots: null);
        }

        /// <summary>
        /// Searches structural placement pairs for two adjacent bundles with incoming previous-bundle context.
        /// </summary>
        public static IrAdjacentBundlePlacementSearchResult SearchAdjacentBundleStructuralAssignments(
            IReadOnlyList<IrIssueSlotMask> firstBundleStructurallyAllowedSlots,
            IReadOnlyList<IrIssueSlotMask> secondBundleStructurallyAllowedSlots,
            IReadOnlyList<int>? previousInstructionSlots)
        {
            return SearchAdjacentBundleStructuralAssignments(
                firstBundleStructurallyAllowedSlots,
                secondBundleStructurallyAllowedSlots,
                previousInstructionSlots,
                default);
        }

        internal static IrAdjacentBundlePlacementSearchResult SearchAdjacentBundleStructuralAssignments(
            IReadOnlyList<IrIssueSlotMask> firstBundleStructurallyAllowedSlots,
            IReadOnlyList<IrIssueSlotMask> secondBundleStructurallyAllowedSlots,
            IReadOnlyList<int>? previousInstructionSlots,
            HybridCpuBackendPlacementTieBreakContext tieBreakContext)
        {
            ArgumentNullException.ThrowIfNull(firstBundleStructurallyAllowedSlots);
            ArgumentNullException.ThrowIfNull(secondBundleStructurallyAllowedSlots);

            IrSlotAssignmentAnalysis firstAnalysis = AnalyzeStructuralAssignment(firstBundleStructurallyAllowedSlots);
            IrSlotAssignmentAnalysis secondAnalysis = AnalyzeStructuralAssignment(secondBundleStructurallyAllowedSlots);
            if (!firstAnalysis.HasStructuralPlacement ||
                !secondAnalysis.HasStructuralPlacement ||
                firstBundleStructurallyAllowedSlots.Count == 0 ||
                secondBundleStructurallyAllowedSlots.Count == 0)
            {
                return new IrAdjacentBundlePlacementSearchResult(
                    firstAnalysis,
                    secondAnalysis,
                    Array.Empty<IrAdjacentBundlePlacementCandidate>(),
                    bestPlacementPair: null,
                    IrAdjacentBundlePlacementSearchSummary.Empty);
            }

            List<IrBundlePlacementCandidate> firstBundleCandidates = EnumerateStructuralPlacementCandidates(firstBundleStructurallyAllowedSlots);
            List<IrBundlePlacementCandidate> secondBundleCandidates = EnumerateStructuralPlacementCandidates(secondBundleStructurallyAllowedSlots);
            if (firstBundleCandidates.Count == 0 || secondBundleCandidates.Count == 0)
            {
                return new IrAdjacentBundlePlacementSearchResult(
                    firstAnalysis,
                    secondAnalysis,
                    Array.Empty<IrAdjacentBundlePlacementCandidate>(),
                    bestPlacementPair: null,
                    IrAdjacentBundlePlacementSearchSummary.Empty);
            }

            List<IrAdjacentBundlePlacementCandidate> paretoOptimalPlacementPairs = new List<IrAdjacentBundlePlacementCandidate>();
            IrAdjacentBundlePlacementCandidate? bestPlacementPair = null;
            int evaluatedPlacementPairCount = 0;
            foreach (IrBundlePlacementCandidate firstBundleCandidate in firstBundleCandidates)
            {
                foreach (IrBundlePlacementCandidate secondBundleCandidate in secondBundleCandidates)
                {
                    evaluatedPlacementPairCount++;
                    IrBundleTransitionQuality incomingTransitionQuality = IrBundleTransitionQuality.Create(previousInstructionSlots, firstBundleCandidate.InstructionSlots);
                    IrBundleTransitionQuality transitionQuality = IrBundleTransitionQuality.Create(firstBundleCandidate.InstructionSlots, secondBundleCandidate.InstructionSlots);
                    IrAdjacentBundlePlacementQuality pairQuality = IrAdjacentBundlePlacementQuality.Create(
                        firstBundleCandidate.Quality,
                        secondBundleCandidate.Quality,
                        incomingTransitionQuality,
                        transitionQuality);
                    IrAdjacentBundlePlacementCandidate pairCandidate = new IrAdjacentBundlePlacementCandidate(
                        firstBundleCandidate.InstructionSlots,
                        secondBundleCandidate.InstructionSlots,
                        firstBundleCandidate.Quality,
                        secondBundleCandidate.Quality,
                        incomingTransitionQuality,
                        transitionQuality,
                        pairQuality);

                    AddToParetoFrontier(paretoOptimalPlacementPairs, pairCandidate);
                    if (bestPlacementPair is null || HybridCpuAdjacentBundlePlacementPolicy.IsBetterPlacement(pairCandidate, bestPlacementPair, tieBreakContext))
                    {
                        bestPlacementPair = pairCandidate;
                    }
                }
            }

            return new IrAdjacentBundlePlacementSearchResult(
                firstAnalysis,
                secondAnalysis,
                paretoOptimalPlacementPairs,
                bestPlacementPair,
                new IrAdjacentBundlePlacementSearchSummary(
                    evaluatedPlacementPairCount,
                    paretoOptimalPlacementPairs.Count,
                    evaluatedPlacementPairCount - paretoOptimalPlacementPairs.Count));
        }
    }
}
