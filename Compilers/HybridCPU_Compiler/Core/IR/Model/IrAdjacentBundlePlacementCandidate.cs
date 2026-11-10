using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// One legal adjacent-bundle placement pair evaluated during Stage 6 pair search.
    /// </summary>
    public sealed record IrAdjacentBundlePlacementCandidate
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="IrAdjacentBundlePlacementCandidate"/> class.
        /// </summary>
        public IrAdjacentBundlePlacementCandidate(
            IReadOnlyList<int> firstInstructionSlots,
            IReadOnlyList<int> secondInstructionSlots,
            IrBundlePlacementQuality firstPlacementQuality,
            IrBundlePlacementQuality secondPlacementQuality,
            IrBundleTransitionQuality incomingTransitionQuality,
            IrBundleTransitionQuality transitionQuality,
            IrAdjacentBundlePlacementQuality quality)
        {
            ArgumentNullException.ThrowIfNull(firstInstructionSlots);
            ArgumentNullException.ThrowIfNull(secondInstructionSlots);
            ArgumentNullException.ThrowIfNull(firstPlacementQuality);
            ArgumentNullException.ThrowIfNull(secondPlacementQuality);
            ArgumentNullException.ThrowIfNull(incomingTransitionQuality);
            ArgumentNullException.ThrowIfNull(transitionQuality);
            ArgumentNullException.ThrowIfNull(quality);

            FirstInstructionSlots = firstInstructionSlots.Count == 0 ? Array.Empty<int>() : firstInstructionSlots.ToArray();
            SecondInstructionSlots = secondInstructionSlots.Count == 0 ? Array.Empty<int>() : secondInstructionSlots.ToArray();
            FirstPlacementQuality = firstPlacementQuality;
            SecondPlacementQuality = secondPlacementQuality;
            IncomingTransitionQuality = incomingTransitionQuality;
            TransitionQuality = transitionQuality;
            Quality = quality;
        }

        /// <summary>
        /// Gets the physical slot chosen for each instruction in the first bundle.
        /// </summary>
        public IReadOnlyList<int> FirstInstructionSlots { get; }

        /// <summary>
        /// Gets the physical slot chosen for each instruction in the second bundle.
        /// </summary>
        public IReadOnlyList<int> SecondInstructionSlots { get; }

        /// <summary>
        /// Gets the local placement quality of the first bundle.
        /// </summary>
        public IrBundlePlacementQuality FirstPlacementQuality { get; }

        /// <summary>
        /// Gets the local placement quality of the second bundle.
        /// </summary>
        public IrBundlePlacementQuality SecondPlacementQuality { get; }

        /// <summary>
        /// Gets previous-bundle transition metrics for the pair's first bundle.
        /// </summary>
        public IrBundleTransitionQuality IncomingTransitionQuality { get; }

        /// <summary>
        /// Gets adjacent-bundle transition metrics for the pair.
        /// </summary>
        public IrBundleTransitionQuality TransitionQuality { get; }

        /// <summary>
        /// Gets the aggregate pair quality for the adjacent placement window.
        /// </summary>
        public IrAdjacentBundlePlacementQuality Quality { get; }
    }
}
