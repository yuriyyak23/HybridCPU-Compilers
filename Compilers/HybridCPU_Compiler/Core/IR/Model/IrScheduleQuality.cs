using System;
using System.Collections.Generic;
using System.Linq;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Machine-checkable quality assessment for one local basic-block schedule.
    /// </summary>
    public sealed record IrBasicBlockScheduleQuality(
        int BlockId,
        int ScheduledLength,
        int ProgramOrderScheduleLength)
    {
        /// <summary>
        /// Gets the cycle improvement over the legal program-order baseline.
        /// </summary>
        public int CycleImprovement => ProgramOrderScheduleLength - ScheduledLength;

        /// <summary>
        /// Gets a value indicating whether the local schedule beats legal program order.
        /// </summary>
        public bool ImprovesOverProgramOrder => CycleImprovement > 0;
    }

    /// <summary>
    /// Machine-checkable quality assessment for one local program schedule.
    /// </summary>
    public sealed class IrProgramScheduleQuality
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="IrProgramScheduleQuality"/> class.
        /// </summary>
        public IrProgramScheduleQuality(IReadOnlyList<IrBasicBlockScheduleQuality> blockQualities)
        {
            ArgumentNullException.ThrowIfNull(blockQualities);

            BlockQualities = blockQualities;
            ScheduledLength = blockQualities.Sum(quality => quality.ScheduledLength);
            ProgramOrderScheduleLength = blockQualities.Sum(quality => quality.ProgramOrderScheduleLength);
        }

        /// <summary>
        /// Gets per-block quality assessments.
        /// </summary>
        public IReadOnlyList<IrBasicBlockScheduleQuality> BlockQualities { get; }

        /// <summary>
        /// Gets the total scheduled block-cycle count across the program.
        /// </summary>
        public int ScheduledLength { get; }

        /// <summary>
        /// Gets the total legal program-order block-cycle count across the program.
        /// </summary>
        public int ProgramOrderScheduleLength { get; }

        /// <summary>
        /// Gets the total cycle improvement over legal program order.
        /// </summary>
        public int CycleImprovement => ProgramOrderScheduleLength - ScheduledLength;

        /// <summary>
        /// Gets a value indicating whether the local schedule beats legal program order.
        /// </summary>
        public bool ImprovesOverProgramOrder => CycleImprovement > 0;
    }
}
