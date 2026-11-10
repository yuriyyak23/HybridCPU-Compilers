using System;
using System.Collections.Generic;
using HybridCPU.Compiler.Core.IR.Telemetry;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Aggregates local Stage 6 placement metrics into block-level and program-level quality summaries.
    /// Optionally uses runtime NOP density feedback for quality gap analysis.
    /// </summary>
    public sealed class HybridCpuBundlingQualityEvaluator
    {
        /// <summary>
        /// Telemetry profile reader for runtime NOP density feedback.
        /// When set, <see cref="EvaluateQualityGap"/> compares compiler-time and runtime NOP densities.
        /// </summary>
        public TelemetryProfileReader? ProfileReader { get; set; }
        /// <summary>
        /// Evaluates one materialized basic block.
        /// </summary>
        public IrBasicBlockBundlingQuality EvaluateBlock(IrBasicBlockBundlingResult blockResult)
        {
            ArgumentNullException.ThrowIfNull(blockResult);
            return EvaluateBlock(blockResult.BlockId, blockResult.Bundles);
        }

        /// <summary>
        /// Evaluates one materialized program.
        /// </summary>
        public IrProgramBundlingQuality EvaluateProgram(IrProgramBundlingResult programResult)
        {
            ArgumentNullException.ThrowIfNull(programResult);
            return EvaluateProgram(programResult.BlockResults);
        }

        internal static IrBasicBlockBundlingQuality EvaluateBlock(int blockId, IReadOnlyList<IrMaterializedBundle> bundles)
        {
            ArgumentNullException.ThrowIfNull(bundles);

            int issuedInstructionCount = 0;
            int nopSlotCount = 0;
            int compactBundleCount = 0;
            int orderPreservingBundleCount = 0;
            int occupiedSlotSpanSum = 0;
            int internalGapCount = 0;
            int largestInternalGapSum = 0;
            int orderInversionCount = 0;
            int leadingEmptySlotCount = 0;
            int trailingEmptySlotCount = 0;
            int slotIndexSum = 0;
            int constrainedSlotDisplacementCost = 0;
            int crossBundleOverlappingLaneCount = 0;
            int crossBundleReusedLaneCount = 0;
            int crossBundleSlotDriftCost = 0;
            int placementSearchEvaluatedCount = 0;
            int placementSearchParetoOptimalCount = 0;
            int placementSearchDominatedCount = 0;
            int ambiguousBundleCount = 0;

            foreach (IrMaterializedBundle bundle in bundles)
            {
                IrBundlePlacementQuality placementQuality = bundle.PlacementQuality;
                IrBundlePlacementSearchSummary placementSearchSummary = bundle.PlacementSearchSummary;
                issuedInstructionCount += bundle.IssuedInstructionCount;
                nopSlotCount += bundle.NopCount;
                occupiedSlotSpanSum += placementQuality.OccupiedSlotSpan;
                internalGapCount += placementQuality.InternalGapCount;
                largestInternalGapSum += placementQuality.LargestInternalGap;
                orderInversionCount += placementQuality.OrderInversionCount;
                leadingEmptySlotCount += placementQuality.LeadingEmptySlotCount;
                trailingEmptySlotCount += placementQuality.TrailingEmptySlotCount;
                slotIndexSum += placementQuality.SlotIndexSum;
                constrainedSlotDisplacementCost += placementQuality.ConstrainedSlotDisplacementCost;
                crossBundleOverlappingLaneCount += bundle.TransitionQuality.OverlappingLaneCount;
                crossBundleReusedLaneCount += bundle.TransitionQuality.ReusedLaneCount;
                crossBundleSlotDriftCost += bundle.TransitionQuality.SlotDriftCost;
                placementSearchEvaluatedCount += placementSearchSummary.EvaluatedPlacementCount;
                placementSearchParetoOptimalCount += placementSearchSummary.ParetoOptimalPlacementCount;
                placementSearchDominatedCount += placementSearchSummary.DominatedPlacementCount;

                if (placementQuality.InternalGapCount == 0)
                {
                    compactBundleCount++;
                }

                if (placementQuality.OrderInversionCount == 0)
                {
                    orderPreservingBundleCount++;
                }

                if (placementSearchSummary.ParetoOptimalPlacementCount > 1)
                {
                    ambiguousBundleCount++;
                }
            }

            return new IrBasicBlockBundlingQuality(
                BlockId: blockId,
                BundleCount: bundles.Count,
                IssuedInstructionCount: issuedInstructionCount,
                NopSlotCount: nopSlotCount,
                CompactBundleCount: compactBundleCount,
                OrderPreservingBundleCount: orderPreservingBundleCount,
                OccupiedSlotSpanSum: occupiedSlotSpanSum,
                InternalGapCount: internalGapCount,
                LargestInternalGapSum: largestInternalGapSum,
                OrderInversionCount: orderInversionCount,
                LeadingEmptySlotCount: leadingEmptySlotCount,
                TrailingEmptySlotCount: trailingEmptySlotCount,
                SlotIndexSum: slotIndexSum,
                ConstrainedSlotDisplacementCost: constrainedSlotDisplacementCost,
                CrossBundleOverlappingLaneCount: crossBundleOverlappingLaneCount,
                CrossBundleReusedLaneCount: crossBundleReusedLaneCount,
                CrossBundleSlotDriftCost: crossBundleSlotDriftCost,
                PlacementSearchEvaluatedCount: placementSearchEvaluatedCount,
                PlacementSearchParetoOptimalCount: placementSearchParetoOptimalCount,
                PlacementSearchDominatedCount: placementSearchDominatedCount,
                AmbiguousBundleCount: ambiguousBundleCount);
        }

        internal static IrProgramBundlingQuality EvaluateProgram(IReadOnlyList<IrBasicBlockBundlingResult> blockResults)
        {
            ArgumentNullException.ThrowIfNull(blockResults);

            List<IrBasicBlockBundlingQuality> blockQualities = new List<IrBasicBlockBundlingQuality>(blockResults.Count);
            int bundleCount = 0;
            int issuedInstructionCount = 0;
            int nopSlotCount = 0;
            int compactBundleCount = 0;
            int orderPreservingBundleCount = 0;
            int occupiedSlotSpanSum = 0;
            int internalGapCount = 0;
            int largestInternalGapSum = 0;
            int orderInversionCount = 0;
            int leadingEmptySlotCount = 0;
            int trailingEmptySlotCount = 0;
            int slotIndexSum = 0;
            int constrainedSlotDisplacementCost = 0;
            int crossBundleOverlappingLaneCount = 0;
            int crossBundleReusedLaneCount = 0;
            int crossBundleSlotDriftCost = 0;
            int placementSearchEvaluatedCount = 0;
            int placementSearchParetoOptimalCount = 0;
            int placementSearchDominatedCount = 0;
            int ambiguousBundleCount = 0;

            foreach (IrBasicBlockBundlingResult blockResult in blockResults)
            {
                IrBasicBlockBundlingQuality blockQuality = blockResult.Quality;
                blockQualities.Add(blockQuality);
                bundleCount += blockQuality.BundleCount;
                issuedInstructionCount += blockQuality.IssuedInstructionCount;
                nopSlotCount += blockQuality.NopSlotCount;
                compactBundleCount += blockQuality.CompactBundleCount;
                orderPreservingBundleCount += blockQuality.OrderPreservingBundleCount;
                occupiedSlotSpanSum += blockQuality.OccupiedSlotSpanSum;
                internalGapCount += blockQuality.InternalGapCount;
                largestInternalGapSum += blockQuality.LargestInternalGapSum;
                orderInversionCount += blockQuality.OrderInversionCount;
                leadingEmptySlotCount += blockQuality.LeadingEmptySlotCount;
                trailingEmptySlotCount += blockQuality.TrailingEmptySlotCount;
                slotIndexSum += blockQuality.SlotIndexSum;
                constrainedSlotDisplacementCost += blockQuality.ConstrainedSlotDisplacementCost;
                crossBundleOverlappingLaneCount += blockQuality.CrossBundleOverlappingLaneCount;
                crossBundleReusedLaneCount += blockQuality.CrossBundleReusedLaneCount;
                crossBundleSlotDriftCost += blockQuality.CrossBundleSlotDriftCost;
                placementSearchEvaluatedCount += blockQuality.PlacementSearchEvaluatedCount;
                placementSearchParetoOptimalCount += blockQuality.PlacementSearchParetoOptimalCount;
                placementSearchDominatedCount += blockQuality.PlacementSearchDominatedCount;
                ambiguousBundleCount += blockQuality.AmbiguousBundleCount;
            }

            return new IrProgramBundlingQuality(
                BlockQualities: blockQualities,
                BundleCount: bundleCount,
                IssuedInstructionCount: issuedInstructionCount,
                NopSlotCount: nopSlotCount,
                CompactBundleCount: compactBundleCount,
                OrderPreservingBundleCount: orderPreservingBundleCount,
                OccupiedSlotSpanSum: occupiedSlotSpanSum,
                InternalGapCount: internalGapCount,
                LargestInternalGapSum: largestInternalGapSum,
                OrderInversionCount: orderInversionCount,
                LeadingEmptySlotCount: leadingEmptySlotCount,
                TrailingEmptySlotCount: trailingEmptySlotCount,
                SlotIndexSum: slotIndexSum,
                ConstrainedSlotDisplacementCost: constrainedSlotDisplacementCost,
                CrossBundleOverlappingLaneCount: crossBundleOverlappingLaneCount,
                CrossBundleReusedLaneCount: crossBundleReusedLaneCount,
                CrossBundleSlotDriftCost: crossBundleSlotDriftCost,
                PlacementSearchEvaluatedCount: placementSearchEvaluatedCount,
                PlacementSearchParetoOptimalCount: placementSearchParetoOptimalCount,
                PlacementSearchDominatedCount: placementSearchDominatedCount,
                AmbiguousBundleCount: ambiguousBundleCount);
        }

        /// <summary>
        /// Computes a quality-gap assessment comparing compiler-time NOP density with runtime feedback.
        /// Returns a positive value when runtime NOP density exceeds compiler estimates, indicating
        /// room for improved bundle packing.
        /// Returns 0.0 when no profile is available or runtime density is at or below compiler density.
        /// </summary>
        /// <param name="programQuality">Compiler-time quality metrics.</param>
        /// <returns>Quality gap (0.0 = no gap, positive = improvement opportunity).</returns>
        public double EvaluateQualityGap(IrProgramBundlingQuality programQuality)
        {
            ArgumentNullException.ThrowIfNull(programQuality);

            if (ProfileReader is null || !ProfileReader.HasProfile)
                return 0.0;

            double runtimeNopDensity = ProfileReader.GetNopDensity();

            // Compiler-time NOP density
            double compilerNopDensity = programQuality.BundleCount > 0
                ? (double)programQuality.NopSlotCount / (programQuality.BundleCount * HybridCpuSlotModel.SlotCount)
                : 0.0;

            // Gap is how much worse runtime is compared to compiler expectations
            double gap = runtimeNopDensity - compilerNopDensity;
            return Math.Max(0.0, gap);
        }

        /// <summary>
        /// Returns the runtime-adjusted NOP density target.
        /// If profile shows runtime NOP density significantly above compiler density,
        /// returns the runtime density as the baseline to beat.
        /// Without profile, returns 0.0 (no guidance).
        /// </summary>
        public double GetRuntimeNopDensityBaseline()
        {
            if (ProfileReader is null || !ProfileReader.HasProfile)
                return 0.0;

            return ProfileReader.GetNopDensity();
        }
    }
}
