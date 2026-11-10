using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Bundle materialization result for one scheduled IR program.
    /// </summary>
    public sealed class IrProgramBundlingResult
    {
        private readonly Dictionary<int, IrBasicBlockBundlingResult> _blockResultsById = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="IrProgramBundlingResult"/> class.
        /// </summary>
        public IrProgramBundlingResult(IrProgramSchedule programSchedule, IReadOnlyList<IrBasicBlockBundlingResult> blockResults)
        {
            ArgumentNullException.ThrowIfNull(programSchedule);
            ArgumentNullException.ThrowIfNull(blockResults);

            ProgramSchedule = programSchedule;
            BlockResults = blockResults;
            Quality = HybridCpuBundlingQualityEvaluator.EvaluateProgram(blockResults);

            foreach (IrBasicBlockBundlingResult blockResult in blockResults)
            {
                _blockResultsById[blockResult.BlockId] = blockResult;
            }
        }

        /// <summary>
        /// Gets the source program schedule consumed by bundle formation.
        /// </summary>
        public IrProgramSchedule ProgramSchedule { get; }

        /// <summary>
        /// Gets the scheduled IR program.
        /// </summary>
        public IrProgram Program => ProgramSchedule.Program;

        /// <summary>
        /// Gets per-block bundle materialization results.
        /// </summary>
        public IReadOnlyList<IrBasicBlockBundlingResult> BlockResults { get; }

        /// <summary>
        /// Gets aggregate Stage 6 quality metrics for the materialized program bundle stream.
        /// </summary>
        public IrProgramBundlingQuality Quality { get; }

        /// <summary>
        /// Tries to get one block bundling result by block identifier.
        /// </summary>
        public bool TryGetBlockResult(int blockId, out IrBasicBlockBundlingResult? blockResult)
        {
            return _blockResultsById.TryGetValue(blockId, out blockResult);
        }
    }
}
