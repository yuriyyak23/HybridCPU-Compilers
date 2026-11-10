using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Local scheduling result for one IR program.
    /// </summary>
    public sealed class IrProgramSchedule
    {
        private readonly Dictionary<int, IrBasicBlockSchedule> _blockSchedulesById = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="IrProgramSchedule"/> class.
        /// </summary>
        public IrProgramSchedule(
            IrProgram program,
            IrProgramDependencyGraph dependencyGraph,
            IReadOnlyList<IrBasicBlockSchedule> blockSchedules)
        {
            ArgumentNullException.ThrowIfNull(program);
            ArgumentNullException.ThrowIfNull(dependencyGraph);
            ArgumentNullException.ThrowIfNull(blockSchedules);

            DependencyGraph = dependencyGraph;
            BlockSchedules = blockSchedules;
            ValueAnalysis = new HybridCpuValueLivenessPressureAnalyzerV1().Analyze(program, blockSchedules);
            Program = ValueAnalysis.Status == IrValueAnalysisStatus.Complete
                ? program with
                {
                    Contract = program.Contract with
                    {
                        DerivedFacts = program.Contract.DerivedFacts.WithValueAnalysisCurrent()
                    }
                }
                : program;
            SchedulingRegions = HybridCpuSchedulingRegionFactoryV1.CreateBasicBlockRegions(Program, dependencyGraph);

            foreach (IrBasicBlockSchedule blockSchedule in blockSchedules)
            {
                _blockSchedulesById[blockSchedule.BlockId] = blockSchedule;
            }
        }

        /// <summary>
        /// Gets the program that was scheduled.
        /// </summary>
        public IrProgram Program { get; }

        /// <summary>
        /// Gets the dependence graph consumed by the scheduler.
        /// </summary>
        public IrProgramDependencyGraph DependencyGraph { get; }

        /// <summary>
        /// Gets local block schedules for the program.
        /// </summary>
        public IReadOnlyList<IrBasicBlockSchedule> BlockSchedules { get; }

        /// <summary>Default-off expansion seam; Phase 06 contains exactly one BB-only region per block.</summary>
        public IReadOnlyList<IrSchedulingRegionV1> SchedulingRegions { get; }

        /// <summary>Observational value/liveness/pressure facts; never a scheduling permission.</summary>
        public IrValueAnalysisReportV1 ValueAnalysis { get; }

        /// <summary>
        /// Tries to get one block schedule by block identifier.
        /// </summary>
        public bool TryGetBlockSchedule(int blockId, out IrBasicBlockSchedule? blockSchedule)
        {
            return _blockSchedulesById.TryGetValue(blockId, out blockSchedule);
        }
    }
}
