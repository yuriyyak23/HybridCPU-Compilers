using System;
using System.Collections.Generic;
using System.Linq;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// One legal whole-block placement candidate evaluated during Stage 6 block-global search.
    /// </summary>
    public sealed record IrBasicBlockPlacementCandidate
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="IrBasicBlockPlacementCandidate"/> class.
        /// </summary>
        public IrBasicBlockPlacementCandidate(
            IReadOnlyList<IReadOnlyList<int>> bundleInstructionSlots,
            IReadOnlyList<IrBundlePlacementQuality> bundlePlacementQualities,
            IrBundleTransitionQuality incomingTransitionQuality,
            IReadOnlyList<IrBundleTransitionQuality> crossBundleTransitionQualities,
            IrBasicBlockPlacementQuality quality)
        {
            ArgumentNullException.ThrowIfNull(bundleInstructionSlots);
            ArgumentNullException.ThrowIfNull(bundlePlacementQualities);
            ArgumentNullException.ThrowIfNull(incomingTransitionQuality);
            ArgumentNullException.ThrowIfNull(crossBundleTransitionQualities);
            ArgumentNullException.ThrowIfNull(quality);

            BundleInstructionSlots = bundleInstructionSlots.Count == 0
                ? Array.Empty<IReadOnlyList<int>>()
                : bundleInstructionSlots.Select(bundleSlots => bundleSlots.Count == 0 ? Array.Empty<int>() : bundleSlots.ToArray()).ToArray();
            BundlePlacementQualities = bundlePlacementQualities.Count == 0 ? Array.Empty<IrBundlePlacementQuality>() : bundlePlacementQualities.ToArray();
            IncomingTransitionQuality = incomingTransitionQuality;
            CrossBundleTransitionQualities = crossBundleTransitionQualities.Count == 0 ? Array.Empty<IrBundleTransitionQuality>() : crossBundleTransitionQualities.ToArray();
            Quality = quality;
        }

        /// <summary>
        /// Gets the physical slots chosen for each bundle in program order.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<int>> BundleInstructionSlots { get; }

        /// <summary>
        /// Gets the local placement quality of each bundle in program order.
        /// </summary>
        public IReadOnlyList<IrBundlePlacementQuality> BundlePlacementQualities { get; }

        /// <summary>
        /// Gets previous-bundle transition metrics for the first bundle in the block candidate.
        /// </summary>
        public IrBundleTransitionQuality IncomingTransitionQuality { get; }

        /// <summary>
        /// Gets adjacent-bundle transition metrics across the chosen block candidate.
        /// </summary>
        public IReadOnlyList<IrBundleTransitionQuality> CrossBundleTransitionQualities { get; }

        /// <summary>
        /// Gets the aggregate quality for the whole-block placement candidate.
        /// </summary>
        public IrBasicBlockPlacementQuality Quality { get; }
    }
}
