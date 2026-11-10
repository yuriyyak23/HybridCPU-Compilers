using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HybridCPU.Compiler.Core.IR.Telemetry;
using HybridCPU.Compiler.Core.IR.Resources;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Materializes physical bundles from an existing local schedule without changing scheduler policy.
    /// </summary>
    public sealed class HybridCpuBundleFormer
    {
        private const string EmptySlotReason = "No scheduled instruction occupied this slot in the current cycle, so the bundler left an internal NOP.";

        private readonly HybridCpuInstructionLegalityChecker _legalityChecker = new();
        private readonly bool _useClassFirstBinding;

        /// <summary>
        /// Optional observational metrics collector. It cannot affect placement selection.
        /// </summary>
        public CompilerScheduleMetricsCollectorV1? MetricsCollector { get; set; }

        public CompilerResourceShadowCollectorV1? ResourceShadowCollector
        {
            get => _legalityChecker.ResourceShadowCollector;
            set => _legalityChecker.ResourceShadowCollector = value;
        }

        /// <summary>
        /// Telemetry profile reader supplying advisory Stage 6 placement signals.
        /// </summary>
        public TelemetryProfileReader? ProfileReader { get; set; }

        /// <summary>
        /// Enables bounded certificate-aware placement tie-breaks.
        /// Default: <c>false</c>.
        /// </summary>
        public bool UseCertificateAwareCoalescingTieBreaks { get; set; }

        /// <summary>
        /// Virtual thread ID for the current program being materialized.
        /// Default: <c>-1</c> (unknown).
        /// </summary>
        public int VirtualThreadId { get; set; } = -1;

        /// <summary>
        /// Marks the current bundle formation path as coordinator-special.
        /// Default: <c>false</c>.
        /// </summary>
        public bool TreatAsCoordinatorPath { get; set; }

        /// <summary>
        /// Initializes a new instance with the legacy slot-search path.
        /// </summary>
        public HybridCpuBundleFormer() : this(useClassFirstBinding: false) { }

        /// <summary>
        /// Initializes a new instance with an optional class-first binding path.
        /// </summary>
        /// <param name="useClassFirstBinding">
        /// When <see langword="true"/>, the direct <c>MaterializeBundle</c> path attempts
        /// deterministic late-lane binding before falling back to exhaustive search.
        /// </param>
        public HybridCpuBundleFormer(bool useClassFirstBinding)
        {
            _useClassFirstBinding = useClassFirstBinding;
        }

        /// <summary>
        /// Materializes bundles for a scheduled IR program.
        /// </summary>
        public IrProgramBundlingResult BundleProgram(IrProgramSchedule programSchedule)
            => BundleProgram(CancellationToken.None, programSchedule);

        /// <summary>Checks caller cancellation between materialization units; searches remain synchronous.</summary>
        public IrProgramBundlingResult BundleProgram(CancellationToken cancellationToken, IrProgramSchedule programSchedule)
        {
            ArgumentNullException.ThrowIfNull(programSchedule);
            cancellationToken.ThrowIfCancellationRequested();

            if (ShouldAttemptWholeProgramGlobalPlacement(programSchedule))
            {
                if (TryBundleProgramGlobally(programSchedule, cancellationToken, out IrProgramBundlingResult? programResult))
                {
                    return programResult!;
                }

                MetricsCollector?.RecordFallback("PlacementWholeProgramGlobalNoPlacement");
            }
            else
            {
                MetricsCollector?.RecordPlacementCapHit("PlacementWholeProgramGlobalBundleCap");
            }

            List<IrBasicBlockBundlingResult> blockResults = new List<IrBasicBlockBundlingResult>(programSchedule.BlockSchedules.Count);
            foreach (IrBasicBlockSchedule blockSchedule in programSchedule.BlockSchedules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                blockResults.Add(BundleBlock(cancellationToken, blockSchedule));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new IrProgramBundlingResult(programSchedule, blockResults);
        }

        /// <summary>
        /// Materializes bundles for one scheduled basic block.
        /// </summary>
        public IrBasicBlockBundlingResult BundleBlock(IrBasicBlockSchedule blockSchedule)
            => BundleBlock(CancellationToken.None, blockSchedule);

        public IrBasicBlockBundlingResult BundleBlock(CancellationToken cancellationToken, IrBasicBlockSchedule blockSchedule)
        {
            ArgumentNullException.ThrowIfNull(blockSchedule);
            cancellationToken.ThrowIfCancellationRequested();

            bool allowLookaheadSearch = ShouldAttemptBlockLookahead(blockSchedule);
            if (!allowLookaheadSearch)
            {
                MetricsCollector?.RecordPlacementCapHit("PlacementBlockLookaheadBundleCap");
            }
            List<IrMaterializedBundle> bundles = new List<IrMaterializedBundle>(blockSchedule.CycleGroups.Count);
            IReadOnlyList<int>? previousInstructionSlots = null;
            for (int cycleGroupIndex = 0; cycleGroupIndex < blockSchedule.CycleGroups.Count;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IrScheduleCycleGroup cycleGroup = blockSchedule.CycleGroups[cycleGroupIndex];
                ValidateCycleSearchWitness(cycleGroup);
                if (allowLookaheadSearch
                    && cycleGroupIndex + 1 < blockSchedule.CycleGroups.Count
                    && TryMaterializeBlockGlobalLookahead(
                        blockSchedule,
                        cycleGroupIndex,
                        previousInstructionSlots,
                        out IrMaterializedBundle? blockGlobalBundle))
                {
                    bundles.Add(blockGlobalBundle!);
                    previousInstructionSlots = blockGlobalBundle!.SlotAssignment.InstructionSlots;
                    cycleGroupIndex++;
                    continue;
                }

                if (allowLookaheadSearch
                    && cycleGroupIndex + 2 < blockSchedule.CycleGroups.Count
                    && TryMaterializeAdjacentBundleTripletLookahead(
                        blockSchedule,
                        cycleGroup,
                        blockSchedule.CycleGroups[cycleGroupIndex + 1],
                        blockSchedule.CycleGroups[cycleGroupIndex + 2],
                        previousInstructionSlots,
                        out IrMaterializedBundle? tripletLeadBundle))
                {
                    bundles.Add(tripletLeadBundle!);
                    previousInstructionSlots = tripletLeadBundle!.SlotAssignment.InstructionSlots;
                    cycleGroupIndex++;
                    continue;
                }

                if (allowLookaheadSearch
                    && cycleGroupIndex + 1 < blockSchedule.CycleGroups.Count
                    && TryMaterializeAdjacentBundlePair(
                        blockSchedule,
                        cycleGroup,
                        blockSchedule.CycleGroups[cycleGroupIndex + 1],
                        previousInstructionSlots,
                        out IrMaterializedBundle? firstBundle,
                        out IrMaterializedBundle? secondBundle))
                {
                    bundles.Add(firstBundle!);
                    bundles.Add(secondBundle!);
                    previousInstructionSlots = secondBundle!.SlotAssignment.InstructionSlots;
                    cycleGroupIndex += 2;
                    continue;
                }

                IrMaterializedBundle bundle = MaterializeBundle(blockSchedule, cycleGroup, previousInstructionSlots);
                bundles.Add(bundle);
                previousInstructionSlots = bundle.SlotAssignment.InstructionSlots;
                cycleGroupIndex++;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new IrBasicBlockBundlingResult(blockSchedule, ExpandScheduledCycleGaps(blockSchedule, bundles, cancellationToken));
        }

        private static void ValidateCycleSearchWitness(IrScheduleCycleGroup cycleGroup)
        {
            IrCyclePlacementWitness? witness = cycleGroup.PlacementWitness;
            if (witness is null) return;

            if (!string.Equals(
                    witness.MachineDescriptionKey,
                    HybridCpuMachineDescriptionV1.Default.Key,
                    StringComparison.Ordinal) ||
                witness.InstructionIndexes.Count != cycleGroup.Instructions.Count ||
                witness.InstructionSlots.Count != cycleGroup.Instructions.Count)
            {
                throw new InvalidOperationException(
                    $"Stale Phase 02 placement witness at cycle {cycleGroup.Cycle}.");
            }

            int occupiedSlots = 0;
            for (int index = 0; index < cycleGroup.Instructions.Count; index++)
            {
                IrInstruction instruction = cycleGroup.Instructions[index];
                int slot = witness.InstructionSlots[index];
                if (witness.InstructionIndexes[index] != instruction.Index ||
                    slot < 0 ||
                    slot >= HybridCpuMachineDescriptionV1.Default.Width ||
                    (((uint)instruction.Annotation.StructurallyAllowedSlots & (1u << slot)) == 0) ||
                    (occupiedSlots & (1 << slot)) != 0)
                {
                    throw new InvalidOperationException(
                        $"Phase 02 placement witness does not match immutable membership at cycle {cycleGroup.Cycle}.");
                }

                occupiedSlots |= 1 << slot;
            }
        }

        private static bool ShouldAttemptWholeProgramGlobalPlacement(IrProgramSchedule programSchedule)
        {
            return CountProgramCycleGroups(programSchedule) <= HybridCpuBundlingPolicyV1.MaximumWholeProgramPlacementBundles;
        }

        private static bool ShouldAttemptBlockLookahead(IrBasicBlockSchedule blockSchedule)
        {
            return blockSchedule.CycleGroups.Count <= HybridCpuBundlingPolicyV1.MaximumBlockLookaheadBundles;
        }

        private static int CountProgramCycleGroups(IrProgramSchedule programSchedule)
        {
            int totalCycleGroups = 0;
            for (int blockIndex = 0; blockIndex < programSchedule.BlockSchedules.Count; blockIndex++)
            {
                totalCycleGroups += programSchedule.BlockSchedules[blockIndex].CycleGroups.Count;
            }

            return totalCycleGroups;
        }

        private bool TryBundleProgramGlobally(IrProgramSchedule programSchedule, CancellationToken cancellationToken, out IrProgramBundlingResult? programResult)
        {
            ArgumentNullException.ThrowIfNull(programSchedule);

            IReadOnlyList<IReadOnlyList<IrIssueSlotMask>>[] programStructurallyAllowedSlots = new IReadOnlyList<IReadOnlyList<IrIssueSlotMask>>[programSchedule.BlockSchedules.Count];
            IrCandidateBundleAnalysis[][] legalityAnalyses = new IrCandidateBundleAnalysis[programSchedule.BlockSchedules.Count][];
            IrBundlePlacementSearchResult[][] localSearchResults = new IrBundlePlacementSearchResult[programSchedule.BlockSchedules.Count][];
            HybridCpuBackendPlacementTieBreakContext tieBreakContext = CreatePlacementTieBreakContext();
            for (int blockIndex = 0; blockIndex < programSchedule.BlockSchedules.Count; blockIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IrBasicBlockSchedule blockSchedule = programSchedule.BlockSchedules[blockIndex];
                IReadOnlyList<IrIssueSlotMask>[] blockStructurallyAllowedSlots = new IReadOnlyList<IrIssueSlotMask>[blockSchedule.CycleGroups.Count];
                IrCandidateBundleAnalysis[] blockLegalityAnalyses = new IrCandidateBundleAnalysis[blockSchedule.CycleGroups.Count];
                IrBundlePlacementSearchResult[] blockLocalSearchResults = new IrBundlePlacementSearchResult[blockSchedule.CycleGroups.Count];
                for (int bundleIndex = 0; bundleIndex < blockSchedule.CycleGroups.Count; bundleIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IrScheduleCycleGroup cycleGroup = blockSchedule.CycleGroups[bundleIndex];
                    blockLegalityAnalyses[bundleIndex] = AnalyzeLegality(blockSchedule, cycleGroup);
                    IReadOnlyList<IrIssueSlotMask> bundleStructurallyAllowedSlots = GetStructurallyAllowedSlotMasks(cycleGroup.Instructions);
                    blockStructurallyAllowedSlots[bundleIndex] = bundleStructurallyAllowedSlots;
                    blockLocalSearchResults[bundleIndex] = HybridCpuSlotModel.SearchStructuralAssignments(bundleStructurallyAllowedSlots, previousInstructionSlots: null, tieBreakContext);
                }

                programStructurallyAllowedSlots[blockIndex] = blockStructurallyAllowedSlots;
                legalityAnalyses[blockIndex] = blockLegalityAnalyses;
                localSearchResults[blockIndex] = blockLocalSearchResults;
            }

            IrProgramPlacementSearchResult programSearch = HybridCpuSlotModel.SearchProgramStructuralAssignments(programStructurallyAllowedSlots, previousInstructionSlots: null, tieBreakContext);
            cancellationToken.ThrowIfCancellationRequested();
            if (!programSearch.HasStructuralPlacement || programSearch.BestPlacement is null)
            {
                MetricsCollector?.RecordFallback("PlacementWholeProgramGlobalSearchFailed");
                programResult = null;
                return false;
            }

            List<IrBasicBlockBundlingResult> blockResults = new List<IrBasicBlockBundlingResult>(programSchedule.BlockSchedules.Count);
            for (int blockIndex = 0; blockIndex < programSchedule.BlockSchedules.Count; blockIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IrBasicBlockSchedule blockSchedule = programSchedule.BlockSchedules[blockIndex];
                IrBasicBlockPlacementCandidate blockPlacement = programSearch.BestPlacement.BlockPlacements[blockIndex];
                List<IrMaterializedBundle> bundles = new List<IrMaterializedBundle>(blockSchedule.CycleGroups.Count);
                for (int bundleIndex = 0; bundleIndex < blockSchedule.CycleGroups.Count; bundleIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IrBundleTransitionQuality transitionQuality = bundleIndex == 0
                        ? blockPlacement.IncomingTransitionQuality
                        : blockPlacement.CrossBundleTransitionQualities[bundleIndex - 1];
                    IrMaterializedSlotAssignment slotAssignment = CreateSlotAssignment(
                        programSearch.BlockAnalyses[blockIndex][bundleIndex],
                        blockPlacement.BundleInstructionSlots[bundleIndex],
                        blockPlacement.BundlePlacementQualities[bundleIndex],
                        localSearchResults[blockIndex][bundleIndex].Summary,
                        transitionQuality);
                    bundles.Add(MaterializeBundle(blockSchedule.CycleGroups[bundleIndex], legalityAnalyses[blockIndex][bundleIndex], slotAssignment));
                }

                blockResults.Add(new IrBasicBlockBundlingResult(
                    blockSchedule, ExpandScheduledCycleGaps(blockSchedule, bundles, cancellationToken)));
            }

            cancellationToken.ThrowIfCancellationRequested();
            programResult = new IrProgramBundlingResult(programSchedule, blockResults);
            return true;
        }

        private IReadOnlyList<IrMaterializedBundle> ExpandScheduledCycleGaps(
            IrBasicBlockSchedule blockSchedule,
            IReadOnlyList<IrMaterializedBundle> materialized,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (materialized.Count == blockSchedule.ScheduleLength)
            {
                return materialized;
            }

            Dictionary<int, IrMaterializedBundle> byCycle = materialized.ToDictionary(static bundle => bundle.Cycle);
            List<IrMaterializedBundle> expanded = new List<IrMaterializedBundle>(blockSchedule.ScheduleLength);
            for (int cycle = 0; cycle < blockSchedule.ScheduleLength; cycle++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (byCycle.TryGetValue(cycle, out IrMaterializedBundle? bundle))
                {
                    expanded.Add(bundle);
                    continue;
                }

                IrCandidateBundleAnalysis legality = _legalityChecker.AnalyzeCandidateBundle([]);
                IrBundlePlacementSearchResult placement = HybridCpuSlotModel.SearchStructuralAssignments(
                    [], previousInstructionSlots: null, CreatePlacementTieBreakContext());
                if (!legality.IsStructurallyAdmissible || !placement.HasStructuralPlacement)
                {
                    throw new InvalidOperationException(
                        $"Cannot materialize required empty bundle for block {blockSchedule.BlockId} at cycle {cycle}.");
                }

                IrScheduleCycleGroup emptyCycle = new IrScheduleCycleGroup(cycle, [], legality);
                expanded.Add(MaterializeBundle(emptyCycle, legality, placement.MaterializeBestAssignment()));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return expanded;
        }

        private bool TryMaterializeBlockGlobalLookahead(
            IrBasicBlockSchedule blockSchedule,
            int cycleGroupIndex,
            IReadOnlyList<int>? previousInstructionSlots,
            out IrMaterializedBundle? firstBundle)
        {
            ArgumentNullException.ThrowIfNull(blockSchedule);

            int remainingBundleCount = blockSchedule.CycleGroups.Count - cycleGroupIndex;
            IrScheduleCycleGroup[] remainingCycleGroups = new IrScheduleCycleGroup[remainingBundleCount];
            IrCandidateBundleAnalysis[] legalityAnalyses = new IrCandidateBundleAnalysis[remainingBundleCount];
            IReadOnlyList<IrIssueSlotMask>[] bundleStructurallyAllowedSlots = new IReadOnlyList<IrIssueSlotMask>[remainingBundleCount];
            IrBundlePlacementSearchResult[] localSearchResults = new IrBundlePlacementSearchResult[remainingBundleCount];
            HybridCpuBackendPlacementTieBreakContext tieBreakContext = CreatePlacementTieBreakContext();
            for (int remainingIndex = 0; remainingIndex < remainingBundleCount; remainingIndex++)
            {
                IrScheduleCycleGroup currentCycleGroup = blockSchedule.CycleGroups[cycleGroupIndex + remainingIndex];
                remainingCycleGroups[remainingIndex] = currentCycleGroup;
                legalityAnalyses[remainingIndex] = AnalyzeLegality(blockSchedule, currentCycleGroup);
                IReadOnlyList<IrIssueSlotMask> currentStructurallyAllowedSlots = GetStructurallyAllowedSlotMasks(currentCycleGroup.Instructions);
                bundleStructurallyAllowedSlots[remainingIndex] = currentStructurallyAllowedSlots;
                IrBundlePlacementSearchResult localSearch = HybridCpuSlotModel.SearchStructuralAssignments(currentStructurallyAllowedSlots, previousInstructionSlots: null, tieBreakContext);
                localSearchResults[remainingIndex] = localSearch;
            }

            IrGlobalBasicBlockPlacementSearchResult blockSearch = HybridCpuSlotModel.SearchGlobalBasicBlockStructuralAssignments(bundleStructurallyAllowedSlots, previousInstructionSlots, tieBreakContext);
            if (!blockSearch.HasStructuralPlacement || blockSearch.BestPlacement is null)
            {
                MetricsCollector?.RecordFallback("PlacementBlockGlobalLookaheadFailed");
                firstBundle = null;
                return false;
            }

            IrBasicBlockPlacementCandidate bestPlacement = blockSearch.BestPlacement;
            firstBundle = MaterializeBundle(
                remainingCycleGroups[0],
                legalityAnalyses[0],
                CreateSlotAssignment(
                    blockSearch.BundleAnalyses[0],
                    bestPlacement.BundleInstructionSlots[0],
                    bestPlacement.BundlePlacementQualities[0],
                    localSearchResults[0].Summary,
                    bestPlacement.IncomingTransitionQuality));

            return true;
        }

        private bool TryMaterializeAdjacentBundleTripletLookahead(
            IrBasicBlockSchedule blockSchedule,
            IrScheduleCycleGroup firstCycleGroup,
            IrScheduleCycleGroup secondCycleGroup,
            IrScheduleCycleGroup thirdCycleGroup,
            IReadOnlyList<int>? previousInstructionSlots,
            out IrMaterializedBundle? firstBundle)
        {
            ArgumentNullException.ThrowIfNull(blockSchedule);
            ArgumentNullException.ThrowIfNull(firstCycleGroup);
            ArgumentNullException.ThrowIfNull(secondCycleGroup);
            ArgumentNullException.ThrowIfNull(thirdCycleGroup);

            IrCandidateBundleAnalysis firstLegalityAnalysis = AnalyzeLegality(blockSchedule, firstCycleGroup);
            _ = AnalyzeLegality(blockSchedule, secondCycleGroup);
            _ = AnalyzeLegality(blockSchedule, thirdCycleGroup);

            IReadOnlyList<IrIssueSlotMask> firstStructurallyAllowedSlots = GetStructurallyAllowedSlotMasks(firstCycleGroup.Instructions);
            IReadOnlyList<IrIssueSlotMask> secondStructurallyAllowedSlots = GetStructurallyAllowedSlotMasks(secondCycleGroup.Instructions);
            IReadOnlyList<IrIssueSlotMask> thirdStructurallyAllowedSlots = GetStructurallyAllowedSlotMasks(thirdCycleGroup.Instructions);
            HybridCpuBackendPlacementTieBreakContext tieBreakContext = CreatePlacementTieBreakContext();
            IrAdjacentBundleTripletPlacementSearchResult tripletSearch = HybridCpuSlotModel.SearchAdjacentBundleTripletStructuralAssignments(
                firstStructurallyAllowedSlots,
                secondStructurallyAllowedSlots,
                thirdStructurallyAllowedSlots,
                previousInstructionSlots,
                tieBreakContext);

            if (!tripletSearch.HasStructuralPlacement || tripletSearch.BestPlacementTriplet is null)
            {
                MetricsCollector?.RecordFallback("PlacementTripletLookaheadFailed");
                firstBundle = null;
                return false;
            }

            IrAdjacentBundleTripletPlacementCandidate bestPlacementTriplet = tripletSearch.BestPlacementTriplet;
            IrBundlePlacementSearchResult firstBundleSearch = HybridCpuSlotModel.SearchStructuralAssignments(firstStructurallyAllowedSlots, previousInstructionSlots, tieBreakContext);
            firstBundle = MaterializeBundle(
                firstCycleGroup,
                firstLegalityAnalysis,
                CreateSlotAssignment(
                    tripletSearch.FirstBundleAnalysis,
                    bestPlacementTriplet.FirstInstructionSlots,
                    bestPlacementTriplet.FirstPlacementQuality,
                    firstBundleSearch.Summary,
                    bestPlacementTriplet.IncomingTransitionQuality));

            return true;
        }

        private bool TryMaterializeAdjacentBundlePair(
            IrBasicBlockSchedule blockSchedule,
            IrScheduleCycleGroup firstCycleGroup,
            IrScheduleCycleGroup secondCycleGroup,
            IReadOnlyList<int>? previousInstructionSlots,
            out IrMaterializedBundle? firstBundle,
            out IrMaterializedBundle? secondBundle)
        {
            ArgumentNullException.ThrowIfNull(blockSchedule);
            ArgumentNullException.ThrowIfNull(firstCycleGroup);
            ArgumentNullException.ThrowIfNull(secondCycleGroup);

            IrCandidateBundleAnalysis firstLegalityAnalysis = AnalyzeLegality(blockSchedule, firstCycleGroup);
            IrCandidateBundleAnalysis secondLegalityAnalysis = AnalyzeLegality(blockSchedule, secondCycleGroup);
            IReadOnlyList<IrIssueSlotMask> firstStructurallyAllowedSlots = GetStructurallyAllowedSlotMasks(firstCycleGroup.Instructions);
            IReadOnlyList<IrIssueSlotMask> secondStructurallyAllowedSlots = GetStructurallyAllowedSlotMasks(secondCycleGroup.Instructions);
            HybridCpuBackendPlacementTieBreakContext tieBreakContext = CreatePlacementTieBreakContext();
            IrAdjacentBundlePlacementSearchResult pairSearch = HybridCpuSlotModel.SearchAdjacentBundleStructuralAssignments(
                firstStructurallyAllowedSlots,
                secondStructurallyAllowedSlots,
                previousInstructionSlots,
                tieBreakContext);

            if (!pairSearch.HasStructuralPlacement || pairSearch.BestPlacementPair is null)
            {
                MetricsCollector?.RecordFallback("PlacementPairLookaheadFailed");
                firstBundle = null;
                secondBundle = null;
                return false;
            }

            IrAdjacentBundlePlacementCandidate bestPlacementPair = pairSearch.BestPlacementPair;
            IrBundlePlacementSearchResult firstBundleSearch = HybridCpuSlotModel.SearchStructuralAssignments(firstStructurallyAllowedSlots, previousInstructionSlots, tieBreakContext);
            IrBundlePlacementSearchResult secondBundleSearch = HybridCpuSlotModel.SearchStructuralAssignments(secondStructurallyAllowedSlots, bestPlacementPair.FirstInstructionSlots, tieBreakContext);

            firstBundle = MaterializeBundle(
                firstCycleGroup,
                firstLegalityAnalysis,
                CreateSlotAssignment(
                    pairSearch.FirstBundleAnalysis,
                    bestPlacementPair.FirstInstructionSlots,
                    bestPlacementPair.FirstPlacementQuality,
                    firstBundleSearch.Summary,
                    bestPlacementPair.IncomingTransitionQuality));

            secondBundle = MaterializeBundle(
                secondCycleGroup,
                secondLegalityAnalysis,
                CreateSlotAssignment(
                    pairSearch.SecondBundleAnalysis,
                    bestPlacementPair.SecondInstructionSlots,
                    bestPlacementPair.SecondPlacementQuality,
                    secondBundleSearch.Summary,
                    bestPlacementPair.TransitionQuality));

            return true;
        }

        private IrMaterializedBundle MaterializeBundle(
            IrBasicBlockSchedule blockSchedule,
            IrScheduleCycleGroup cycleGroup,
            IReadOnlyList<int>? previousInstructionSlots)
        {
            IrCandidateBundleAnalysis legalityAnalysis = AnalyzeLegality(blockSchedule, cycleGroup);

            // Phase 04: class-first deterministic binding path
            if (_useClassFirstBinding
                && legalityAnalysis.ClassCapacityResult?.IsWithinCapacity == true)
            {
                IrLateLaneBindingResult bindingResult = HybridCpuLateLaneBinder.BindLanes(
                    cycleGroup.Instructions,
                    legalityAnalysis.ClassCapacityResult);

                if (bindingResult.BindingSuccess)
                {
                    IrMaterializedSlotAssignment slotAssignment = CreateClassFirstSlotAssignment(
                        legalityAnalysis.SlotAnalysis,
                        bindingResult,
                        previousInstructionSlots);

                    return MaterializeBundleWithBindingMetadata(
                        cycleGroup, legalityAnalysis, slotAssignment, bindingResult);
                }
                // Binding failed — fall through to legacy exhaustive search
                MetricsCollector?.RecordFallback("PlacementClassFirstBindingFailed");
            }

            IrBundlePlacementSearchResult searchResult = HybridCpuSlotModel.SearchStructuralAssignments(GetStructurallyAllowedSlotMasks(cycleGroup.Instructions), previousInstructionSlots, CreatePlacementTieBreakContext());
            if (!searchResult.HasStructuralPlacement)
            {
                MetricsCollector?.RecordFallback("PlacementLocalSearchFailed");
                throw new InvalidOperationException($"Cannot materialize physical slots for block {blockSchedule.BlockId} at cycle {cycleGroup.Cycle} because the candidate group has no legal slot assignment.");
            }

            return MaterializeBundle(cycleGroup, legalityAnalysis, searchResult.MaterializeBestAssignment());
        }

        private IrCandidateBundleAnalysis AnalyzeLegality(IrBasicBlockSchedule blockSchedule, IrScheduleCycleGroup cycleGroup)
        {
            IrCandidateBundleAnalysis legalityAnalysis = _legalityChecker.AnalyzeCandidateBundle(cycleGroup.Instructions);
            if (!legalityAnalysis.IsStructurallyAdmissible)
            {
                throw new InvalidOperationException($"Cannot materialize an illegal cycle group for block {blockSchedule.BlockId} at cycle {cycleGroup.Cycle}.");
            }

            return legalityAnalysis;
        }

        private static IrMaterializedSlotAssignment CreateSlotAssignment(
            IrSlotAssignmentAnalysis analysis,
            IReadOnlyList<int> instructionSlots,
            IrBundlePlacementQuality quality,
            IrBundlePlacementSearchSummary searchSummary,
            IrBundleTransitionQuality transitionQuality)
        {
            return new IrMaterializedSlotAssignment(
                analysis,
                instructionSlots.ToArray(),
                quality,
                searchSummary,
                transitionQuality);
        }

        private static IrMaterializedBundle MaterializeBundle(
            IrScheduleCycleGroup cycleGroup,
            IrCandidateBundleAnalysis legalityAnalysis,
            IrMaterializedSlotAssignment slotAssignment)
        {

            IrMaterializedBundleSlot[] slots = new IrMaterializedBundleSlot[HybridCpuSlotModel.SlotCount];
            for (int slotIndex = 0; slotIndex < slots.Length; slotIndex++)
            {
                slots[slotIndex] = new IrMaterializedBundleSlot(
                    slotIndex,
                    Instruction: null,
                    OrderInCycle: null,
                    InstructionLegalSlots: IrIssueSlotMask.None,
                    EmptyReason: EmptySlotReason);
            }

            for (int orderInCycle = 0; orderInCycle < cycleGroup.Instructions.Count; orderInCycle++)
            {
                IrInstruction instruction = cycleGroup.Instructions[orderInCycle];
                int assignedSlot = slotAssignment.InstructionSlots[orderInCycle];
                slots[assignedSlot] = new IrMaterializedBundleSlot(
                    assignedSlot,
                    instruction,
                    orderInCycle,
                    instruction.Annotation.StructurallyAllowedSlots);
            }

            return new IrMaterializedBundle(cycleGroup.Cycle, cycleGroup, legalityAnalysis, slotAssignment, slots);
        }

        private static IrMaterializedSlotAssignment CreateClassFirstSlotAssignment(
            IrSlotAssignmentAnalysis analysis,
            IrLateLaneBindingResult bindingResult,
            IReadOnlyList<int>? previousInstructionSlots)
        {
            IrBundlePlacementQuality quality = IrBundlePlacementQuality.Create(
                bindingResult.AssignedLanes, HybridCpuSlotModel.SlotCount);

            IrBundlePlacementSearchSummary searchSummary = new IrBundlePlacementSearchSummary(
                EvaluatedPlacementCount: 1,
                ParetoOptimalPlacementCount: 1,
                DominatedPlacementCount: 0);

            IrBundleTransitionQuality transitionQuality = previousInstructionSlots is not null
                ? IrBundleTransitionQuality.Create(previousInstructionSlots, bindingResult.AssignedLanes)
                : IrBundleTransitionQuality.Empty;

            return new IrMaterializedSlotAssignment(
                analysis,
                bindingResult.AssignedLanes.ToArray(),
                quality,
                searchSummary,
                transitionQuality);
        }

        private static IrMaterializedBundle MaterializeBundleWithBindingMetadata(
            IrScheduleCycleGroup cycleGroup,
            IrCandidateBundleAnalysis legalityAnalysis,
            IrMaterializedSlotAssignment slotAssignment,
            IrLateLaneBindingResult bindingResult)
        {
            IrMaterializedBundleSlot[] slots = new IrMaterializedBundleSlot[HybridCpuSlotModel.SlotCount];
            for (int slotIndex = 0; slotIndex < slots.Length; slotIndex++)
            {
                slots[slotIndex] = new IrMaterializedBundleSlot(
                    slotIndex,
                    Instruction: null,
                    OrderInCycle: null,
                    InstructionLegalSlots: IrIssueSlotMask.None,
                    EmptyReason: EmptySlotReason);
            }

            for (int orderInCycle = 0; orderInCycle < cycleGroup.Instructions.Count; orderInCycle++)
            {
                IrInstruction instruction = cycleGroup.Instructions[orderInCycle];
                int assignedSlot = slotAssignment.InstructionSlots[orderInCycle];
                slots[assignedSlot] = new IrMaterializedBundleSlot(
                    assignedSlot,
                    instruction,
                    orderInCycle,
                    instruction.Annotation.StructurallyAllowedSlots,
                    EmptyReason: null,
                    BindingKind: bindingResult.BindingKinds[orderInCycle],
                    AssignedClass: instruction.Annotation.RequiredSlotClass);
            }

            return new IrMaterializedBundle(cycleGroup.Cycle, cycleGroup, legalityAnalysis, slotAssignment, slots);
        }

        private static IReadOnlyList<IrIssueSlotMask> GetStructurallyAllowedSlotMasks(IReadOnlyList<IrInstruction> instructions)
        {
            List<IrIssueSlotMask> structurallyAllowedSlots = new List<IrIssueSlotMask>(instructions.Count);
            foreach (IrInstruction instruction in instructions)
            {
                structurallyAllowedSlots.Add(instruction.Annotation.StructurallyAllowedSlots);
            }

            return structurallyAllowedSlots;
        }

        private HybridCpuBackendPlacementTieBreakContext CreatePlacementTieBreakContext()
        {
            double registerGroupPressure = 0.0;
            if (UseCertificateAwareCoalescingTieBreaks && ProfileReader is { HasProfile: true })
            {
                registerGroupPressure = ProfileReader.GetCertificateRegisterGroupPressureForBackendShaping(VirtualThreadId, TreatAsCoordinatorPath);
            }

            return new HybridCpuBackendPlacementTieBreakContext(
                UseCertificateAwareCoalescingTieBreaks,
                TreatAsCoordinatorPath,
                VirtualThreadId,
                registerGroupPressure);
        }
    }
}
