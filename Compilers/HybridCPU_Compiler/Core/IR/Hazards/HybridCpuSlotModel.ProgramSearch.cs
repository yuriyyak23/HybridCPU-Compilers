using System;
using System.Collections.Generic;
using System.Numerics;

namespace HybridCPU.Compiler.Core.IR
{
    public static partial class HybridCpuSlotModel
    {
        /// <summary>
        /// Searches legal placement triplets for three adjacent bundles without changing scheduler order.
        /// </summary>
        [Obsolete(
            "Compiler-side placement search consumes structurally allowed slot facts only; use SearchAdjacentBundleTripletStructuralAssignments.",
            false)]
        public static IrAdjacentBundleTripletPlacementSearchResult SearchAdjacentBundleTripletAssignments(
            IReadOnlyList<IrIssueSlotMask> firstBundleLegalSlots,
            IReadOnlyList<IrIssueSlotMask> secondBundleLegalSlots,
            IReadOnlyList<IrIssueSlotMask> thirdBundleLegalSlots)
        {
            return SearchAdjacentBundleTripletStructuralAssignments(firstBundleLegalSlots, secondBundleLegalSlots, thirdBundleLegalSlots, previousInstructionSlots: null);
        }

        /// <summary>
        /// Searches legal placement triplets for three adjacent bundles with incoming previous-bundle context without changing scheduler order.
        /// </summary>
        [Obsolete(
            "Compiler-side placement search consumes structurally allowed slot facts only; use SearchAdjacentBundleTripletStructuralAssignments.",
            false)]
        public static IrAdjacentBundleTripletPlacementSearchResult SearchAdjacentBundleTripletAssignments(
            IReadOnlyList<IrIssueSlotMask> firstBundleLegalSlots,
            IReadOnlyList<IrIssueSlotMask> secondBundleLegalSlots,
            IReadOnlyList<IrIssueSlotMask> thirdBundleLegalSlots,
            IReadOnlyList<int>? previousInstructionSlots)
        {
            return SearchAdjacentBundleTripletStructuralAssignments(firstBundleLegalSlots, secondBundleLegalSlots, thirdBundleLegalSlots, previousInstructionSlots, default);
        }

        /// <summary>
        /// Searches structural placement triplets for three adjacent bundles without changing scheduler order.
        /// </summary>
        public static IrAdjacentBundleTripletPlacementSearchResult SearchAdjacentBundleTripletStructuralAssignments(
            IReadOnlyList<IrIssueSlotMask> firstBundleStructurallyAllowedSlots,
            IReadOnlyList<IrIssueSlotMask> secondBundleStructurallyAllowedSlots,
            IReadOnlyList<IrIssueSlotMask> thirdBundleStructurallyAllowedSlots)
        {
            return SearchAdjacentBundleTripletStructuralAssignments(
                firstBundleStructurallyAllowedSlots,
                secondBundleStructurallyAllowedSlots,
                thirdBundleStructurallyAllowedSlots,
                previousInstructionSlots: null);
        }

        /// <summary>
        /// Searches structural placement triplets for three adjacent bundles with incoming previous-bundle context.
        /// </summary>
        public static IrAdjacentBundleTripletPlacementSearchResult SearchAdjacentBundleTripletStructuralAssignments(
            IReadOnlyList<IrIssueSlotMask> firstBundleStructurallyAllowedSlots,
            IReadOnlyList<IrIssueSlotMask> secondBundleStructurallyAllowedSlots,
            IReadOnlyList<IrIssueSlotMask> thirdBundleStructurallyAllowedSlots,
            IReadOnlyList<int>? previousInstructionSlots)
        {
            return SearchAdjacentBundleTripletStructuralAssignments(
                firstBundleStructurallyAllowedSlots,
                secondBundleStructurallyAllowedSlots,
                thirdBundleStructurallyAllowedSlots,
                previousInstructionSlots,
                default);
        }

        internal static IrAdjacentBundleTripletPlacementSearchResult SearchAdjacentBundleTripletStructuralAssignments(
            IReadOnlyList<IrIssueSlotMask> firstBundleStructurallyAllowedSlots,
            IReadOnlyList<IrIssueSlotMask> secondBundleStructurallyAllowedSlots,
            IReadOnlyList<IrIssueSlotMask> thirdBundleStructurallyAllowedSlots,
            IReadOnlyList<int>? previousInstructionSlots,
            HybridCpuBackendPlacementTieBreakContext tieBreakContext)
        {
            ArgumentNullException.ThrowIfNull(firstBundleStructurallyAllowedSlots);
            ArgumentNullException.ThrowIfNull(secondBundleStructurallyAllowedSlots);
            ArgumentNullException.ThrowIfNull(thirdBundleStructurallyAllowedSlots);

            IrSlotAssignmentAnalysis firstAnalysis = AnalyzeStructuralAssignment(firstBundleStructurallyAllowedSlots);
            IrSlotAssignmentAnalysis secondAnalysis = AnalyzeStructuralAssignment(secondBundleStructurallyAllowedSlots);
            IrSlotAssignmentAnalysis thirdAnalysis = AnalyzeStructuralAssignment(thirdBundleStructurallyAllowedSlots);
            if (!firstAnalysis.HasStructuralPlacement
                || !secondAnalysis.HasStructuralPlacement
                || !thirdAnalysis.HasStructuralPlacement
                || firstBundleStructurallyAllowedSlots.Count == 0
                || secondBundleStructurallyAllowedSlots.Count == 0
                || thirdBundleStructurallyAllowedSlots.Count == 0)
            {
                return new IrAdjacentBundleTripletPlacementSearchResult(
                    firstAnalysis,
                    secondAnalysis,
                    thirdAnalysis,
                    Array.Empty<IrAdjacentBundleTripletPlacementCandidate>(),
                    bestPlacementTriplet: null,
                    IrAdjacentBundleTripletPlacementSearchSummary.Empty);
            }

            List<IrBundlePlacementCandidate> firstBundleCandidates = EnumerateStructuralPlacementCandidates(firstBundleStructurallyAllowedSlots);
            List<IrBundlePlacementCandidate> secondBundleCandidates = EnumerateStructuralPlacementCandidates(secondBundleStructurallyAllowedSlots);
            List<IrBundlePlacementCandidate> thirdBundleCandidates = EnumerateStructuralPlacementCandidates(thirdBundleStructurallyAllowedSlots);
            if (firstBundleCandidates.Count == 0 || secondBundleCandidates.Count == 0 || thirdBundleCandidates.Count == 0)
            {
                return new IrAdjacentBundleTripletPlacementSearchResult(
                    firstAnalysis,
                    secondAnalysis,
                    thirdAnalysis,
                    Array.Empty<IrAdjacentBundleTripletPlacementCandidate>(),
                    bestPlacementTriplet: null,
                    IrAdjacentBundleTripletPlacementSearchSummary.Empty);
            }

            List<IrAdjacentBundleTripletPlacementCandidate> paretoOptimalPlacementTriplets = new List<IrAdjacentBundleTripletPlacementCandidate>();
            IrAdjacentBundleTripletPlacementCandidate? bestPlacementTriplet = null;
            int evaluatedPlacementTripletCount = 0;
            foreach (IrBundlePlacementCandidate firstBundleCandidate in firstBundleCandidates)
            {
                foreach (IrBundlePlacementCandidate secondBundleCandidate in secondBundleCandidates)
                {
                    foreach (IrBundlePlacementCandidate thirdBundleCandidate in thirdBundleCandidates)
                    {
                        evaluatedPlacementTripletCount++;
                        IrBundleTransitionQuality incomingTransitionQuality = IrBundleTransitionQuality.Create(previousInstructionSlots, firstBundleCandidate.InstructionSlots);
                        IrBundleTransitionQuality firstTransitionQuality = IrBundleTransitionQuality.Create(firstBundleCandidate.InstructionSlots, secondBundleCandidate.InstructionSlots);
                        IrBundleTransitionQuality secondTransitionQuality = IrBundleTransitionQuality.Create(secondBundleCandidate.InstructionSlots, thirdBundleCandidate.InstructionSlots);
                        IrAdjacentBundleTripletPlacementQuality tripletQuality = IrAdjacentBundleTripletPlacementQuality.Create(
                            firstBundleCandidate.Quality,
                            secondBundleCandidate.Quality,
                            thirdBundleCandidate.Quality,
                            incomingTransitionQuality,
                            firstTransitionQuality,
                            secondTransitionQuality);
                        IrAdjacentBundleTripletPlacementCandidate tripletCandidate = new IrAdjacentBundleTripletPlacementCandidate(
                            firstBundleCandidate.InstructionSlots,
                            secondBundleCandidate.InstructionSlots,
                            thirdBundleCandidate.InstructionSlots,
                            firstBundleCandidate.Quality,
                            secondBundleCandidate.Quality,
                            thirdBundleCandidate.Quality,
                            incomingTransitionQuality,
                            firstTransitionQuality,
                            secondTransitionQuality,
                            tripletQuality);

                        AddToParetoFrontier(paretoOptimalPlacementTriplets, tripletCandidate);
                        if (bestPlacementTriplet is null || HybridCpuAdjacentBundleTripletPlacementPolicy.IsBetterPlacement(tripletCandidate, bestPlacementTriplet, tieBreakContext))
                        {
                            bestPlacementTriplet = tripletCandidate;
                        }
                    }
                }
            }

            return new IrAdjacentBundleTripletPlacementSearchResult(
                firstAnalysis,
                secondAnalysis,
                thirdAnalysis,
                paretoOptimalPlacementTriplets,
                bestPlacementTriplet,
                new IrAdjacentBundleTripletPlacementSearchSummary(
                    evaluatedPlacementTripletCount,
                    paretoOptimalPlacementTriplets.Count,
                    evaluatedPlacementTripletCount - paretoOptimalPlacementTriplets.Count));
        }

        /// <summary>
        /// Searches legal placements for a whole basic-block bundle sequence without changing scheduler order.
        /// </summary>
        [Obsolete(
            "Compiler-side placement search consumes structurally allowed slot facts only; use SearchBasicBlockStructuralAssignments.",
            false)]
        public static IrBasicBlockPlacementSearchResult SearchBasicBlockAssignments(IReadOnlyList<IReadOnlyList<IrIssueSlotMask>> bundleLegalSlots)
        {
            return SearchBasicBlockStructuralAssignments(bundleLegalSlots, previousInstructionSlots: null);
        }

        /// <summary>
        /// Searches legal placements for a whole basic-block bundle sequence with incoming previous-bundle context without changing scheduler order.
        /// </summary>
        [Obsolete(
            "Compiler-side placement search consumes structurally allowed slot facts only; use SearchBasicBlockStructuralAssignments.",
            false)]
        public static IrBasicBlockPlacementSearchResult SearchBasicBlockAssignments(
            IReadOnlyList<IReadOnlyList<IrIssueSlotMask>> bundleLegalSlots,
            IReadOnlyList<int>? previousInstructionSlots)
        {
            return SearchBasicBlockStructuralAssignments(bundleLegalSlots, previousInstructionSlots);
        }

        /// <summary>
        /// Searches structural placements for a whole basic-block bundle sequence without changing scheduler order.
        /// </summary>
        public static IrBasicBlockPlacementSearchResult SearchBasicBlockStructuralAssignments(IReadOnlyList<IReadOnlyList<IrIssueSlotMask>> bundleStructurallyAllowedSlots)
        {
            return SearchBasicBlockStructuralAssignments(bundleStructurallyAllowedSlots, previousInstructionSlots: null);
        }

        /// <summary>
        /// Searches structural placements for a whole basic-block bundle sequence with incoming previous-bundle context.
        /// </summary>
        public static IrBasicBlockPlacementSearchResult SearchBasicBlockStructuralAssignments(
            IReadOnlyList<IReadOnlyList<IrIssueSlotMask>> bundleStructurallyAllowedSlots,
            IReadOnlyList<int>? previousInstructionSlots)
        {
            ArgumentNullException.ThrowIfNull(bundleStructurallyAllowedSlots);

            if (bundleStructurallyAllowedSlots.Count == 0)
            {
                return new IrBasicBlockPlacementSearchResult(
                    Array.Empty<IrSlotAssignmentAnalysis>(),
                    Array.Empty<IrBasicBlockPlacementCandidate>(),
                    bestPlacement: null,
                    IrBasicBlockPlacementSearchSummary.Empty);
            }

            IrSlotAssignmentAnalysis[] bundleAnalyses = new IrSlotAssignmentAnalysis[bundleStructurallyAllowedSlots.Count];
            List<IrBundlePlacementCandidate>[] bundleCandidates = new List<IrBundlePlacementCandidate>[bundleStructurallyAllowedSlots.Count];
            for (int bundleIndex = 0; bundleIndex < bundleStructurallyAllowedSlots.Count; bundleIndex++)
            {
                IReadOnlyList<IrIssueSlotMask> currentBundleStructurallyAllowedSlots = bundleStructurallyAllowedSlots[bundleIndex];
                ArgumentNullException.ThrowIfNull(currentBundleStructurallyAllowedSlots);

                IrSlotAssignmentAnalysis analysis = AnalyzeStructuralAssignment(currentBundleStructurallyAllowedSlots);
                bundleAnalyses[bundleIndex] = analysis;
                if (!analysis.HasStructuralPlacement || currentBundleStructurallyAllowedSlots.Count == 0)
                {
                    return new IrBasicBlockPlacementSearchResult(
                        bundleAnalyses,
                        Array.Empty<IrBasicBlockPlacementCandidate>(),
                        bestPlacement: null,
                        IrBasicBlockPlacementSearchSummary.Empty);
                }

                List<IrBundlePlacementCandidate> evaluatedCandidates = EnumerateStructuralPlacementCandidates(currentBundleStructurallyAllowedSlots);
                if (evaluatedCandidates.Count == 0)
                {
                    return new IrBasicBlockPlacementSearchResult(
                        bundleAnalyses,
                        Array.Empty<IrBasicBlockPlacementCandidate>(),
                        bestPlacement: null,
                        IrBasicBlockPlacementSearchSummary.Empty);
                }

                bundleCandidates[bundleIndex] = evaluatedCandidates;
            }

            List<IrBasicBlockPlacementCandidate> paretoOptimalPlacements = new List<IrBasicBlockPlacementCandidate>();
            IrBasicBlockPlacementCandidate? bestPlacement = null;
            int evaluatedPlacementCount = 0;
            CollectBasicBlockPlacementCandidates(
                bundleCandidates,
                previousInstructionSlots,
                bundleIndex: 0,
                new List<IrBundlePlacementCandidate>(bundleCandidates.Length),
                paretoOptimalPlacements,
                ref bestPlacement,
                ref evaluatedPlacementCount);

            return new IrBasicBlockPlacementSearchResult(
                bundleAnalyses,
                paretoOptimalPlacements,
                bestPlacement,
                new IrBasicBlockPlacementSearchSummary(
                    evaluatedPlacementCount,
                    paretoOptimalPlacements.Count,
                    evaluatedPlacementCount - paretoOptimalPlacements.Count));
        }

        /// <summary>
        /// Searches scalable global placements for a whole basic-block bundle sequence without changing scheduler order.
        /// </summary>
        [Obsolete(
            "Compiler-side placement search consumes structurally allowed slot facts only; use SearchGlobalBasicBlockStructuralAssignments.",
            false)]
        public static IrGlobalBasicBlockPlacementSearchResult SearchGlobalBasicBlockAssignments(IReadOnlyList<IReadOnlyList<IrIssueSlotMask>> bundleLegalSlots)
        {
            return SearchGlobalBasicBlockStructuralAssignments(bundleLegalSlots, previousInstructionSlots: null);
        }

        /// <summary>
        /// Searches scalable global placements for a whole basic-block bundle sequence with incoming previous-bundle context without changing scheduler order.
        /// </summary>
        [Obsolete(
            "Compiler-side placement search consumes structurally allowed slot facts only; use SearchGlobalBasicBlockStructuralAssignments.",
            false)]
        public static IrGlobalBasicBlockPlacementSearchResult SearchGlobalBasicBlockAssignments(
            IReadOnlyList<IReadOnlyList<IrIssueSlotMask>> bundleLegalSlots,
            IReadOnlyList<int>? previousInstructionSlots)
        {
            return SearchGlobalBasicBlockStructuralAssignments(bundleLegalSlots, previousInstructionSlots, default);
        }

        /// <summary>
        /// Searches scalable global structural placements for a whole basic-block bundle sequence.
        /// </summary>
        public static IrGlobalBasicBlockPlacementSearchResult SearchGlobalBasicBlockStructuralAssignments(IReadOnlyList<IReadOnlyList<IrIssueSlotMask>> bundleStructurallyAllowedSlots)
        {
            return SearchGlobalBasicBlockStructuralAssignments(bundleStructurallyAllowedSlots, previousInstructionSlots: null);
        }

        /// <summary>
        /// Searches scalable global structural placements with incoming previous-bundle context.
        /// </summary>
        public static IrGlobalBasicBlockPlacementSearchResult SearchGlobalBasicBlockStructuralAssignments(
            IReadOnlyList<IReadOnlyList<IrIssueSlotMask>> bundleStructurallyAllowedSlots,
            IReadOnlyList<int>? previousInstructionSlots)
        {
            return SearchGlobalBasicBlockStructuralAssignments(bundleStructurallyAllowedSlots, previousInstructionSlots, default);
        }

        internal static IrGlobalBasicBlockPlacementSearchResult SearchGlobalBasicBlockStructuralAssignments(
            IReadOnlyList<IReadOnlyList<IrIssueSlotMask>> bundleStructurallyAllowedSlots,
            IReadOnlyList<int>? previousInstructionSlots,
            HybridCpuBackendPlacementTieBreakContext tieBreakContext)
        {
            ArgumentNullException.ThrowIfNull(bundleStructurallyAllowedSlots);

            if (bundleStructurallyAllowedSlots.Count == 0)
            {
                return new IrGlobalBasicBlockPlacementSearchResult(
                    Array.Empty<IrSlotAssignmentAnalysis>(),
                    bestPlacement: null,
                    IrGlobalBasicBlockPlacementSearchSummary.Empty);
            }

            if (!TryCollectBundlePlacementCandidates(bundleStructurallyAllowedSlots, out IrSlotAssignmentAnalysis[] bundleAnalyses, out List<IrBundlePlacementCandidate>[] bundleCandidates))
            {
                return new IrGlobalBasicBlockPlacementSearchResult(
                    bundleAnalyses,
                    bestPlacement: null,
                    IrGlobalBasicBlockPlacementSearchSummary.Empty);
            }

            int evaluatedTransitionCount = 0;
            int retainedStateCount = 0;
            List<IrBasicBlockPlacementCandidate> retainedStates = CreateInitialGlobalStates(bundleCandidates[0], previousInstructionSlots, ref evaluatedTransitionCount, ref retainedStateCount);
            for (int bundleIndex = 1; bundleIndex < bundleCandidates.Length; bundleIndex++)
            {
                IrBasicBlockPlacementCandidate[] nextStates = new IrBasicBlockPlacementCandidate[bundleCandidates[bundleIndex].Count];
                for (int candidateIndex = 0; candidateIndex < bundleCandidates[bundleIndex].Count; candidateIndex++)
                {
                    IrBundlePlacementCandidate currentBundleCandidate = bundleCandidates[bundleIndex][candidateIndex];
                    for (int stateIndex = 0; stateIndex < retainedStates.Count; stateIndex++)
                    {
                        evaluatedTransitionCount++;
                        IrBasicBlockPlacementCandidate extendedPlacement = AppendBasicBlockPlacementCandidate(retainedStates[stateIndex], currentBundleCandidate);
                        if (nextStates[candidateIndex] is null || HybridCpuBasicBlockPlacementPolicy.IsBetterPlacement(extendedPlacement, nextStates[candidateIndex], tieBreakContext))
                        {
                            nextStates[candidateIndex] = extendedPlacement;
                        }
                    }
                }

                retainedStates = new List<IrBasicBlockPlacementCandidate>(nextStates.Length);
                foreach (IrBasicBlockPlacementCandidate state in nextStates)
                {
                    retainedStates.Add(state);
                }

                retainedStateCount += retainedStates.Count;
            }

            IrBasicBlockPlacementCandidate? bestPlacement = null;
            foreach (IrBasicBlockPlacementCandidate retainedState in retainedStates)
            {
                if (bestPlacement is null || HybridCpuBasicBlockPlacementPolicy.IsBetterPlacement(retainedState, bestPlacement, tieBreakContext))
                {
                    bestPlacement = retainedState;
                }
            }

            return new IrGlobalBasicBlockPlacementSearchResult(
                bundleAnalyses,
                bestPlacement,
                new IrGlobalBasicBlockPlacementSearchSummary(
                    bundleCandidates.Length,
                    evaluatedTransitionCount,
                    retainedStateCount));
        }

        /// <summary>
        /// Searches scalable global placements for a whole program without changing scheduler order.
        /// </summary>
        [Obsolete(
            "Compiler-side placement search consumes structurally allowed slot facts only; use SearchProgramStructuralAssignments.",
            false)]
        public static IrProgramPlacementSearchResult SearchProgramAssignments(IReadOnlyList<IReadOnlyList<IReadOnlyList<IrIssueSlotMask>>> programLegalSlots)
        {
            return SearchProgramStructuralAssignments(programLegalSlots, previousInstructionSlots: null);
        }

        /// <summary>
        /// Searches scalable global placements for a whole program with incoming previous-bundle context without changing scheduler order.
        /// </summary>
        [Obsolete(
            "Compiler-side placement search consumes structurally allowed slot facts only; use SearchProgramStructuralAssignments.",
            false)]
        public static IrProgramPlacementSearchResult SearchProgramAssignments(
            IReadOnlyList<IReadOnlyList<IReadOnlyList<IrIssueSlotMask>>> programLegalSlots,
            IReadOnlyList<int>? previousInstructionSlots)
        {
            return SearchProgramStructuralAssignments(programLegalSlots, previousInstructionSlots, default);
        }

        /// <summary>
        /// Searches scalable global structural placements for a whole program.
        /// </summary>
        public static IrProgramPlacementSearchResult SearchProgramStructuralAssignments(IReadOnlyList<IReadOnlyList<IReadOnlyList<IrIssueSlotMask>>> programStructurallyAllowedSlots)
        {
            return SearchProgramStructuralAssignments(programStructurallyAllowedSlots, previousInstructionSlots: null);
        }

        /// <summary>
        /// Searches scalable global structural placements for a whole program with incoming previous-bundle context.
        /// </summary>
        public static IrProgramPlacementSearchResult SearchProgramStructuralAssignments(
            IReadOnlyList<IReadOnlyList<IReadOnlyList<IrIssueSlotMask>>> programStructurallyAllowedSlots,
            IReadOnlyList<int>? previousInstructionSlots)
        {
            return SearchProgramStructuralAssignments(programStructurallyAllowedSlots, previousInstructionSlots, default);
        }

        internal static IrProgramPlacementSearchResult SearchProgramStructuralAssignments(
            IReadOnlyList<IReadOnlyList<IReadOnlyList<IrIssueSlotMask>>> programStructurallyAllowedSlots,
            IReadOnlyList<int>? previousInstructionSlots,
            HybridCpuBackendPlacementTieBreakContext tieBreakContext)
        {
            ArgumentNullException.ThrowIfNull(programStructurallyAllowedSlots);

            if (programStructurallyAllowedSlots.Count == 0)
            {
                return new IrProgramPlacementSearchResult(
                    Array.Empty<IReadOnlyList<IrSlotAssignmentAnalysis>>(),
                    bestPlacement: null,
                    IrProgramPlacementSearchSummary.Empty);
            }

            int[] blockBundleCounts = new int[programStructurallyAllowedSlots.Count];
            List<IReadOnlyList<IrIssueSlotMask>> flattenedBundleStructurallyAllowedSlots = new List<IReadOnlyList<IrIssueSlotMask>>();
            for (int blockIndex = 0; blockIndex < programStructurallyAllowedSlots.Count; blockIndex++)
            {
                IReadOnlyList<IReadOnlyList<IrIssueSlotMask>> blockStructurallyAllowedSlots = programStructurallyAllowedSlots[blockIndex];
                ArgumentNullException.ThrowIfNull(blockStructurallyAllowedSlots);
                blockBundleCounts[blockIndex] = blockStructurallyAllowedSlots.Count;
                foreach (IReadOnlyList<IrIssueSlotMask> bundleStructurallyAllowedSlots in blockStructurallyAllowedSlots)
                {
                    flattenedBundleStructurallyAllowedSlots.Add(bundleStructurallyAllowedSlots);
                }
            }

            IrGlobalBasicBlockPlacementSearchResult globalBlockSearch = SearchGlobalBasicBlockStructuralAssignments(flattenedBundleStructurallyAllowedSlots, previousInstructionSlots, tieBreakContext);
            IReadOnlyList<IReadOnlyList<IrSlotAssignmentAnalysis>> blockAnalyses = SplitBlockAnalyses(globalBlockSearch.BundleAnalyses, blockBundleCounts);
            if (!globalBlockSearch.HasStructuralPlacement || globalBlockSearch.BestPlacement is null)
            {
                return new IrProgramPlacementSearchResult(
                    blockAnalyses,
                    bestPlacement: null,
                    new IrProgramPlacementSearchSummary(
                        programStructurallyAllowedSlots.Count,
                        flattenedBundleStructurallyAllowedSlots.Count,
                        globalBlockSearch.Summary.EvaluatedTransitionCount,
                        globalBlockSearch.Summary.RetainedStateCount));
            }

            IrBasicBlockPlacementCandidate[] blockPlacements = new IrBasicBlockPlacementCandidate[blockBundleCounts.Length];
            int bundleStartIndex = 0;
            for (int blockIndex = 0; blockIndex < blockBundleCounts.Length; blockIndex++)
            {
                blockPlacements[blockIndex] = SliceBlockPlacementCandidate(globalBlockSearch.BestPlacement, bundleStartIndex, blockBundleCounts[blockIndex]);
                bundleStartIndex += blockBundleCounts[blockIndex];
            }

            return new IrProgramPlacementSearchResult(
                blockAnalyses,
                new IrProgramPlacementCandidate(blockPlacements, globalBlockSearch.BestPlacement.Quality),
                new IrProgramPlacementSearchSummary(
                    programStructurallyAllowedSlots.Count,
                    flattenedBundleStructurallyAllowedSlots.Count,
                    globalBlockSearch.Summary.EvaluatedTransitionCount,
                    globalBlockSearch.Summary.RetainedStateCount));
        }
    }
}
