using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Bundle materialization result for one scheduled basic block.
    /// </summary>
    public sealed class IrBasicBlockBundlingResult
    {
        private readonly Dictionary<int, IrMaterializedBundle> _bundlesByCycle = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="IrBasicBlockBundlingResult"/> class.
        /// </summary>
        public IrBasicBlockBundlingResult(IrBasicBlockSchedule blockSchedule, IReadOnlyList<IrMaterializedBundle> bundles)
        {
            ArgumentNullException.ThrowIfNull(blockSchedule);
            ArgumentNullException.ThrowIfNull(bundles);

            BlockSchedule = blockSchedule;
            Bundles = bundles;
            Quality = HybridCpuBundlingQualityEvaluator.EvaluateBlock(blockSchedule.BlockId, bundles);

            foreach (IrMaterializedBundle bundle in bundles)
            {
                _bundlesByCycle[bundle.Cycle] = bundle;
            }
        }

        /// <summary>
        /// Gets the source block schedule consumed by bundle formation.
        /// </summary>
        public IrBasicBlockSchedule BlockSchedule { get; }

        /// <summary>
        /// Gets the scheduled block.
        /// </summary>
        public IrBasicBlock Block => BlockSchedule.Block;

        /// <summary>
        /// Gets the block identifier.
        /// </summary>
        public int BlockId => BlockSchedule.BlockId;

        /// <summary>
        /// Gets the materialized bundles for the block.
        /// </summary>
        public IReadOnlyList<IrMaterializedBundle> Bundles { get; }

        /// <summary>
        /// Gets aggregate Stage 6 quality metrics for the materialized block bundle stream.
        /// </summary>
        public IrBasicBlockBundlingQuality Quality { get; }

        /// <summary>
        /// Tries to get the bundle for one scheduled cycle.
        /// </summary>
        public bool TryGetBundle(int cycle, out IrMaterializedBundle? bundle)
        {
            return _bundlesByCycle.TryGetValue(cycle, out bundle);
        }
    }
}
