using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// One legal adjacent-bundle triplet placement evaluated during Stage 6 window search.
    /// </summary>
    public sealed record IrAdjacentBundleTripletPlacementCandidate
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="IrAdjacentBundleTripletPlacementCandidate"/> class.
        /// </summary>
        public IrAdjacentBundleTripletPlacementCandidate(
            IReadOnlyList<int> firstInstructionSlots,
            IReadOnlyList<int> secondInstructionSlots,
            IReadOnlyList<int> thirdInstructionSlots,
            IrBundlePlacementQuality firstPlacementQuality,
            IrBundlePlacementQuality secondPlacementQuality,
            IrBundlePlacementQuality thirdPlacementQuality,
            IrBundleTransitionQuality incomingTransitionQuality,
            IrBundleTransitionQuality firstTransitionQuality,
            IrBundleTransitionQuality secondTransitionQuality,
            IrAdjacentBundleTripletPlacementQuality quality)
        {
            ArgumentNullException.ThrowIfNull(firstInstructionSlots);
            ArgumentNullException.ThrowIfNull(secondInstructionSlots);
            ArgumentNullException.ThrowIfNull(thirdInstructionSlots);
            ArgumentNullException.ThrowIfNull(firstPlacementQuality);
            ArgumentNullException.ThrowIfNull(secondPlacementQuality);
            ArgumentNullException.ThrowIfNull(thirdPlacementQuality);
            ArgumentNullException.ThrowIfNull(incomingTransitionQuality);
            ArgumentNullException.ThrowIfNull(firstTransitionQuality);
            ArgumentNullException.ThrowIfNull(secondTransitionQuality);
            ArgumentNullException.ThrowIfNull(quality);

            FirstInstructionSlots = firstInstructionSlots.Count == 0 ? Array.Empty<int>() : firstInstructionSlots.ToArray();
            SecondInstructionSlots = secondInstructionSlots.Count == 0 ? Array.Empty<int>() : secondInstructionSlots.ToArray();
            ThirdInstructionSlots = thirdInstructionSlots.Count == 0 ? Array.Empty<int>() : thirdInstructionSlots.ToArray();
            FirstPlacementQuality = firstPlacementQuality;
            SecondPlacementQuality = secondPlacementQuality;
            ThirdPlacementQuality = thirdPlacementQuality;
            IncomingTransitionQuality = incomingTransitionQuality;
            FirstTransitionQuality = firstTransitionQuality;
            SecondTransitionQuality = secondTransitionQuality;
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
        /// Gets the physical slot chosen for each instruction in the third bundle.
        /// </summary>
        public IReadOnlyList<int> ThirdInstructionSlots { get; }

        /// <summary>
        /// Gets the local placement quality of the first bundle.
        /// </summary>
        public IrBundlePlacementQuality FirstPlacementQuality { get; }

        /// <summary>
        /// Gets the local placement quality of the second bundle.
        /// </summary>
        public IrBundlePlacementQuality SecondPlacementQuality { get; }

        /// <summary>
        /// Gets the local placement quality of the third bundle.
        /// </summary>
        public IrBundlePlacementQuality ThirdPlacementQuality { get; }

        /// <summary>
        /// Gets previous-bundle transition metrics for the triplet's first bundle.
        /// </summary>
        public IrBundleTransitionQuality IncomingTransitionQuality { get; }

        /// <summary>
        /// Gets transition metrics between the first and second bundles.
        /// </summary>
        public IrBundleTransitionQuality FirstTransitionQuality { get; }

        /// <summary>
        /// Gets transition metrics between the second and third bundles.
        /// </summary>
        public IrBundleTransitionQuality SecondTransitionQuality { get; }

        /// <summary>
        /// Gets the aggregate quality for the adjacent triplet window.
        /// </summary>
        public IrAdjacentBundleTripletPlacementQuality Quality { get; }
    }
}
