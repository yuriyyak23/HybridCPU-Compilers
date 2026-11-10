using System;
using System.Collections.Generic;
using System.Linq;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// One legal whole-program placement candidate evaluated during scalable Stage 6 program-global search.
    /// </summary>
    public sealed record IrProgramPlacementCandidate
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="IrProgramPlacementCandidate"/> class.
        /// </summary>
        public IrProgramPlacementCandidate(
            IReadOnlyList<IrBasicBlockPlacementCandidate> blockPlacements,
            IrBasicBlockPlacementQuality quality)
        {
            ArgumentNullException.ThrowIfNull(blockPlacements);
            ArgumentNullException.ThrowIfNull(quality);

            BlockPlacements = blockPlacements.Count == 0 ? Array.Empty<IrBasicBlockPlacementCandidate>() : blockPlacements.ToArray();
            Quality = quality;
        }

        /// <summary>
        /// Gets the chosen placements for each block in program order.
        /// </summary>
        public IReadOnlyList<IrBasicBlockPlacementCandidate> BlockPlacements { get; }

        /// <summary>
        /// Gets the aggregate quality across the whole program placement.
        /// </summary>
        public IrBasicBlockPlacementQuality Quality { get; }

        /// <summary>
        /// Gets the chosen slots grouped by block then by bundle.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<IReadOnlyList<int>>> BlockBundleInstructionSlots =>
            BlockPlacements.Select(blockPlacement => blockPlacement.BundleInstructionSlots).ToArray();
    }
}
