using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Evaluates local schedule quality against a legality-correct program-order baseline.
    /// </summary>
    public sealed class HybridCpuLocalScheduleQualityEvaluator
    {
        private readonly HybridCpuProgramOrderLocalScheduler _programOrderScheduler = new();

        /// <summary>
        /// Evaluates one scheduled basic block against a legal program-order baseline.
        /// </summary>
        public IrBasicBlockScheduleQuality EvaluateBlock(IrBasicBlockSchedule blockSchedule, IrProgramDependencyGraph dependencyGraph)
        {
            ArgumentNullException.ThrowIfNull(blockSchedule);
            ArgumentNullException.ThrowIfNull(dependencyGraph);

            IrBasicBlockSchedule programOrderBaseline = _programOrderScheduler.ScheduleBlock(blockSchedule.Block, dependencyGraph);
            return new IrBasicBlockScheduleQuality(
                BlockId: blockSchedule.BlockId,
                ScheduledLength: blockSchedule.ScheduleLength,
                ProgramOrderScheduleLength: programOrderBaseline.ScheduleLength);
        }

        /// <summary>
        /// Evaluates one scheduled program against legal program-order block baselines.
        /// </summary>
        public IrProgramScheduleQuality EvaluateProgram(IrProgramSchedule schedule)
        {
            ArgumentNullException.ThrowIfNull(schedule);

            List<IrBasicBlockScheduleQuality> blockQualities = new List<IrBasicBlockScheduleQuality>(schedule.BlockSchedules.Count);
            foreach (IrBasicBlockSchedule blockSchedule in schedule.BlockSchedules)
            {
                blockQualities.Add(EvaluateBlock(blockSchedule, schedule.DependencyGraph));
            }

            return new IrProgramScheduleQuality(blockQualities);
        }
    }
}
