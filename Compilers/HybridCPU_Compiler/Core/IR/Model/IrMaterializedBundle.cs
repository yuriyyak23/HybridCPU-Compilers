using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Physical bundle materialized from one scheduler-selected cycle group.
    /// </summary>
    public sealed class IrMaterializedBundle
    {
        private readonly Dictionary<int, IrMaterializedBundleSlot> _slotsByInstructionIndex = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="IrMaterializedBundle"/> class.
        /// </summary>
        public IrMaterializedBundle(
            int cycle,
            IrScheduleCycleGroup cycleGroup,
            IrCandidateBundleAnalysis legalityAnalysis,
            IrMaterializedSlotAssignment slotAssignment,
            IReadOnlyList<IrMaterializedBundleSlot> slots)
        {
            ArgumentNullException.ThrowIfNull(cycleGroup);
            ArgumentNullException.ThrowIfNull(legalityAnalysis);
            ArgumentNullException.ThrowIfNull(slotAssignment);
            ArgumentNullException.ThrowIfNull(slots);

            Cycle = cycle;
            CycleGroup = cycleGroup;
            LegalityAnalysis = legalityAnalysis;
            SlotAssignment = slotAssignment;
            Slots = slots;

            foreach (IrMaterializedBundleSlot slot in slots)
            {
                if (!slot.IsStructuralPlacement)
                {
                    throw new ArgumentException($"Instruction {slot.Instruction?.Index} was placed into illegal slot {slot.SlotIndex}.", nameof(slots));
                }

                if (slot.Instruction is not null)
                {
                    _slotsByInstructionIndex[slot.Instruction.Index] = slot;
                }
            }
        }

        /// <summary>
        /// Gets the source cycle selected by the scheduler.
        /// </summary>
        public int Cycle { get; }

        /// <summary>
        /// Gets the scheduler-selected cycle group consumed by the bundler.
        /// </summary>
        public IrScheduleCycleGroup CycleGroup { get; }

        /// <summary>
        /// Gets the legality analysis revalidated during bundle formation.
        /// </summary>
        public IrCandidateBundleAnalysis LegalityAnalysis { get; }

        /// <summary>
        /// Gets the physical slot assignment chosen for the bundle.
        /// </summary>
        public IrMaterializedSlotAssignment SlotAssignment { get; }

        /// <summary>
        /// Gets compactness and gap metrics for the chosen physical placement.
        /// </summary>
        public IrBundlePlacementQuality PlacementQuality => SlotAssignment.Quality;

        /// <summary>
        /// Gets search-summary metrics for the explicit Stage 6 placement search.
        /// </summary>
        public IrBundlePlacementSearchSummary PlacementSearchSummary => SlotAssignment.SearchSummary;

        /// <summary>
        /// Gets adjacent-bundle continuity metrics for the chosen placement.
        /// </summary>
        public IrBundleTransitionQuality TransitionQuality => SlotAssignment.TransitionQuality;

        /// <summary>
        /// Gets the physical bundle slots.
        /// </summary>
        public IReadOnlyList<IrMaterializedBundleSlot> Slots { get; }

        /// <summary>
        /// Gets the number of instructions issued in the bundle.
        /// </summary>
        public int IssuedInstructionCount => CycleGroup.Instructions.Count;

        /// <summary>
        /// Gets the number of internal NOP slots in the bundle.
        /// </summary>
        public int NopCount => Slots.Count - IssuedInstructionCount;

        /// <summary>
        /// Tries to locate the physical slot used by one scheduled instruction.
        /// </summary>
        public bool TryGetSlotForInstruction(int instructionIndex, out IrMaterializedBundleSlot? slot)
        {
            return _slotsByInstructionIndex.TryGetValue(instructionIndex, out slot);
        }
    }
}
