using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    public static partial class HybridCpuSlotModel
    {
        /// <summary>
        /// Total number of issue slots currently modeled by the compiler layer.
        /// </summary>
        public const int SlotCount = 8;
        public const string StructuralMaskContract = "hybridcpu.structural-slot-masks/v2-scalar-lsu";

        /// <summary>
        /// Returns the structurally allowed slot mask for an instruction resource class.
        /// </summary>
        public static IrIssueSlotMask GetStructurallyAllowedSlots(IrResourceClass resourceClass, HybridCpuOpcodeInfo? opcodeInfo = null)
        {
            return resourceClass switch
            {
                IrResourceClass.VectorAlu => IrIssueSlotMask.Vector,
                IrResourceClass.LoadStore => IrIssueSlotMask.Lsu,
                IrResourceClass.ControlFlow => IrIssueSlotMask.Control,
                IrResourceClass.System => IrIssueSlotMask.System,
                IrResourceClass.DmaStream => IrIssueSlotMask.DmaStream,
                _ => IrIssueSlotMask.Scalar
            };
        }

        /// <summary>
        /// Compatibility alias for structural slot masks.
        /// </summary>
        [Obsolete(
            "Compiler-side LegalSlots are structurally allowed slots only; use GetStructurallyAllowedSlots.",
            false)]
        public static IrIssueSlotMask GetLegalSlots(IrResourceClass resourceClass, HybridCpuOpcodeInfo? opcodeInfo = null)
        {
            return GetStructurallyAllowedSlots(resourceClass, opcodeInfo);
        }

        /// <summary>
        /// Checks whether a candidate group has at least one valid slot assignment.
        /// </summary>
        [Obsolete(
            "Compiler-side HasLegalAssignment is structural placement evidence only; use HasStructuralPlacement.",
            false)]
        public static bool HasLegalAssignment(IReadOnlyList<IrIssueSlotMask> legalSlots)
        {
            return HasStructuralPlacement(legalSlots);
        }

        /// <summary>
        /// Checks whether a candidate group has at least one structural slot placement.
        /// </summary>
        public static bool HasStructuralPlacement(IReadOnlyList<IrIssueSlotMask> structurallyAllowedSlots)
        {
            return AnalyzeStructuralAssignment(structurallyAllowedSlots).HasStructuralPlacement;
        }

        /// <summary>
        /// Analyzes slot feasibility for a candidate group without assigning physical slots.
        /// </summary>
        [Obsolete(
            "Compiler-side assignment analysis consumes structurally allowed slot facts only; use AnalyzeStructuralAssignment.",
            false)]
        public static IrSlotAssignmentAnalysis AnalyzeAssignment(IReadOnlyList<IrIssueSlotMask> legalSlots)
        {
            return AnalyzeStructuralAssignment(legalSlots);
        }

        /// <summary>
        /// Analyzes structural slot feasibility for a candidate group without assigning physical slots.
        /// </summary>
        public static IrSlotAssignmentAnalysis AnalyzeStructuralAssignment(IReadOnlyList<IrIssueSlotMask> structurallyAllowedSlots)
        {
            ArgumentNullException.ThrowIfNull(structurallyAllowedSlots);

            IrIssueSlotMask combinedMask = IrIssueSlotMask.None;
            foreach (IrIssueSlotMask slotMask in structurallyAllowedSlots)
            {
                combinedMask |= slotMask;
            }

            if (structurallyAllowedSlots.Count == 0)
            {
                return new IrSlotAssignmentAnalysis(0, IrIssueSlotMask.None, 0, true, Array.Empty<IrIssueSlotMask>());
            }

            if (structurallyAllowedSlots.Count > SlotCount)
            {
                return new IrSlotAssignmentAnalysis(structurallyAllowedSlots.Count, combinedMask, GetSlotCount(combinedMask), false, structurallyAllowedSlots);
            }

            List<int> order = BuildStructuralAssignmentOrder(structurallyAllowedSlots);
            bool[] usedSlots = new bool[SlotCount];
            bool hasStructuralAssignment = TryAssignStructuralSlots(order, 0, structurallyAllowedSlots, usedSlots);
            return new IrSlotAssignmentAnalysis(structurallyAllowedSlots.Count, combinedMask, GetSlotCount(combinedMask), hasStructuralAssignment, structurallyAllowedSlots);
        }

        /// <summary>
        /// Materializes one deterministic physical slot assignment for a legal candidate group.
        /// </summary>
        [Obsolete(
            "Compiler-side materialization consumes structurally allowed slot facts only; use MaterializeStructuralAssignment.",
            false)]
        public static IrMaterializedSlotAssignment MaterializeAssignment(IReadOnlyList<IrIssueSlotMask> legalSlots)
        {
            return MaterializeStructuralAssignment(legalSlots, previousInstructionSlots: null);
        }

        /// <summary>
        /// Materializes one deterministic physical slot assignment for a legal candidate group with adjacent-bundle context.
        /// </summary>
        [Obsolete(
            "Compiler-side materialization consumes structurally allowed slot facts only; use MaterializeStructuralAssignment.",
            false)]
        public static IrMaterializedSlotAssignment MaterializeAssignment(IReadOnlyList<IrIssueSlotMask> legalSlots, IReadOnlyList<int>? previousInstructionSlots)
        {
            return MaterializeStructuralAssignment(legalSlots, previousInstructionSlots);
        }

        /// <summary>
        /// Materializes one deterministic physical slot assignment for structural slot facts.
        /// </summary>
        public static IrMaterializedSlotAssignment MaterializeStructuralAssignment(IReadOnlyList<IrIssueSlotMask> structurallyAllowedSlots)
        {
            return MaterializeStructuralAssignment(structurallyAllowedSlots, previousInstructionSlots: null);
        }

        /// <summary>
        /// Materializes one deterministic physical slot assignment for structural slot facts with adjacent-bundle context.
        /// </summary>
        public static IrMaterializedSlotAssignment MaterializeStructuralAssignment(IReadOnlyList<IrIssueSlotMask> structurallyAllowedSlots, IReadOnlyList<int>? previousInstructionSlots)
        {
            return SearchStructuralAssignments(structurallyAllowedSlots, previousInstructionSlots).MaterializeBestAssignment();
        }

        internal static IrMaterializedSlotAssignment MaterializeStructuralAssignment(
            IReadOnlyList<IrIssueSlotMask> structurallyAllowedSlots,
            IReadOnlyList<int>? previousInstructionSlots,
            HybridCpuBackendPlacementTieBreakContext tieBreakContext)
        {
            return SearchStructuralAssignments(structurallyAllowedSlots, previousInstructionSlots, tieBreakContext).MaterializeBestAssignment();
        }
    }
}
