using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// One legal physical slot placement evaluated during Stage 6 placement search.
    /// </summary>
    public sealed record IrBundlePlacementCandidate
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="IrBundlePlacementCandidate"/> class.
        /// </summary>
        public IrBundlePlacementCandidate(IReadOnlyList<int> instructionSlots, IrBundlePlacementQuality quality)
        {
            ArgumentNullException.ThrowIfNull(instructionSlots);
            ArgumentNullException.ThrowIfNull(quality);

            InstructionSlots = instructionSlots.Count == 0 ? Array.Empty<int>() : instructionSlots.ToArray();
            Quality = quality;
        }

        /// <summary>
        /// Gets the physical slot chosen for each instruction in scheduler order.
        /// </summary>
        public IReadOnlyList<int> InstructionSlots { get; }

        /// <summary>
        /// Gets the quality metrics for this legal placement.
        /// </summary>
        public IrBundlePlacementQuality Quality { get; }
    }
}
