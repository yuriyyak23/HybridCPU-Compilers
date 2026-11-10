using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Local schedule for one basic block without physical slot placement.
    /// </summary>
    public sealed class IrBasicBlockSchedule
    {
        private readonly Dictionary<int, IrScheduledInstruction> _scheduledInstructionsByIndex = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="IrBasicBlockSchedule"/> class.
        /// </summary>
        public IrBasicBlockSchedule(
            IrBasicBlock block,
            IrBasicBlockSchedulingDag dag,
            IReadOnlyList<IrScheduleCycleGroup> cycleGroups,
            IReadOnlyList<IrScheduledInstruction> scheduledInstructions)
        {
            ArgumentNullException.ThrowIfNull(block);
            ArgumentNullException.ThrowIfNull(dag);
            ArgumentNullException.ThrowIfNull(cycleGroups);
            ArgumentNullException.ThrowIfNull(scheduledInstructions);

            Block = block;
            Dag = dag;
            CycleGroups = cycleGroups;
            ScheduledInstructions = scheduledInstructions;

            foreach (IrScheduledInstruction instruction in scheduledInstructions)
            {
                _scheduledInstructionsByIndex[instruction.InstructionIndex] = instruction;
            }
        }

        /// <summary>
        /// Gets the scheduled block.
        /// </summary>
        public IrBasicBlock Block { get; }

        /// <summary>
        /// Gets the block identifier.
        /// </summary>
        public int BlockId => Block.Id;

        /// <summary>
        /// Gets the DAG used by the local scheduler.
        /// </summary>
        public IrBasicBlockSchedulingDag Dag { get; }

        /// <summary>
        /// Gets cycle groups selected by the local scheduler.
        /// </summary>
        public IReadOnlyList<IrScheduleCycleGroup> CycleGroups { get; }

        /// <summary>
        /// Gets per-instruction schedule placements.
        /// </summary>
        public IReadOnlyList<IrScheduledInstruction> ScheduledInstructions { get; }

        /// <summary>
        /// Gets the number of cycles covered by the block schedule.
        /// </summary>
        public int ScheduleLength => CycleGroups.Count == 0 ? 0 : CycleGroups[^1].Cycle + 1;

        /// <summary>
        /// Tries to get one scheduled instruction placement.
        /// </summary>
        public bool TryGetScheduledInstruction(int instructionIndex, out IrScheduledInstruction? scheduledInstruction)
        {
            return _scheduledInstructionsByIndex.TryGetValue(instructionIndex, out scheduledInstruction);
        }

        /// <summary>
        /// Returns the scheduled cycle for one instruction.
        /// </summary>
        public int GetCycleForInstruction(int instructionIndex)
        {
            if (!TryGetScheduledInstruction(instructionIndex, out IrScheduledInstruction? scheduledInstruction) || scheduledInstruction is null)
            {
                throw new ArgumentException($"Instruction {instructionIndex} is not present in block schedule {BlockId}.", nameof(instructionIndex));
            }

            return scheduledInstruction.Cycle;
        }
    }

    /// <summary>
    /// Cycle group selected by the local scheduler before bundle formation.
    /// </summary>
    public sealed record IrScheduleCycleGroup(
        int Cycle,
        IReadOnlyList<IrInstruction> Instructions,
        IrCandidateBundleAnalysis LegalityAnalysis,
        IrCyclePlacementWitness? PlacementWitness = null,
        IrCycleSearchSummaryV1? CycleSearchSummary = null);

    /// <summary>
    /// Scheduler placement metadata for one instruction.
    /// </summary>
    public sealed record IrScheduledInstruction(
        int InstructionIndex,
        IrInstruction Instruction,
        int Cycle,
        int OrderInCycle,
        int ReadyCycle,
        int CriticalPathLengthCycles);
}
