using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Reason why an instruction was classified as stealable or non-stealable.
    /// </summary>
    public enum StealabilityReason
    {
        /// <summary>The instruction is eligible for cross-thread stealing.</summary>
        Eligible = 0,

        /// <summary>The instruction requires an exclusive cycle and cannot be shared.</summary>
        ExclusiveCycleRequired = 1,

        /// <summary>Control-flow instructions are non-stealable because they affect the thread's PC.</summary>
        ControlFlowInstruction = 2,

        /// <summary>System instructions access privileged state and are non-stealable.</summary>
        SystemInstruction = 3,

        /// <summary>Barrier-like instructions enforce ordering and are non-stealable.</summary>
        BarrierInstruction = 4,

        /// <summary>Instructions that may trap cannot be safely migrated.</summary>
        MayTrapInstruction = 5
    }

    /// <summary>
    /// Advisory compiler-derived verdict for one instruction's steal eligibility.
    /// Does not affect structural admissibility classification.
    /// </summary>
    public sealed record StealabilityVerdict(
        int InstructionIndex,
        bool IsStealable,
        StealabilityReason Reason)
    {
        /// <summary>
         /// Gets the original metadata-carried value before compiler analysis.
         /// </summary>
        public bool OriginalMetadataValue { get; init; }
    }

    /// <summary>
    /// Advisory metadata producer: derives stealability per instruction from compiler-side
    /// analysis rather than blindly forwarding encoded metadata.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Output is advisory only. Verdicts do not affect structural admissibility
    /// classification (see <see cref="HybridCpuBundleBuilder"/>). They are consumed
    /// by bounded scheduling heuristics and diagnostic mismatch reporting.
    /// </para>
    /// <para>
    /// An instruction is stealable when all of the following hold:
    /// </para>
    /// <list type="bullet">
    ///   <item>It does not require an exclusive cycle (no serialization).</item>
    ///   <item>It is not a control-flow instruction (branches, returns, stops).</item>
    ///   <item>It is not a system/barrier instruction.</item>
    ///   <item>It does not may-trap (to avoid cross-thread exception routing).</item>
    /// </list>
    /// </remarks>
    public sealed class HybridCpuStealabilityAnalyzer
    {
        /// <summary>
        /// Analyzes a single instruction and returns a compiler-derived steal verdict.
        /// </summary>
        public StealabilityVerdict AnalyzeInstruction(IrInstruction instruction)
        {
            ArgumentNullException.ThrowIfNull(instruction);

            IrInstructionAnnotation annotation = instruction.Annotation;
            bool originalMetadataValue = annotation.StealabilityHint;

            if (annotation.IsBarrierLike)
            {
                return new StealabilityVerdict(instruction.Index, IsStealable: false, StealabilityReason.BarrierInstruction)
                {
                    OriginalMetadataValue = originalMetadataValue
                };
            }

            if (annotation.ControlFlowKind != IrControlFlowKind.None)
            {
                return new StealabilityVerdict(instruction.Index, IsStealable: false, StealabilityReason.ControlFlowInstruction)
                {
                    OriginalMetadataValue = originalMetadataValue
                };
            }

            if (annotation.ResourceClass == IrResourceClass.System)
            {
                return new StealabilityVerdict(instruction.Index, IsStealable: false, StealabilityReason.SystemInstruction)
                {
                    OriginalMetadataValue = originalMetadataValue
                };
            }

            if ((annotation.Serialization & IrSerializationKind.ExclusiveCycle) != 0)
            {
                return new StealabilityVerdict(instruction.Index, IsStealable: false, StealabilityReason.ExclusiveCycleRequired)
                {
                    OriginalMetadataValue = originalMetadataValue
                };
            }

            if (annotation.MayTrap)
            {
                return new StealabilityVerdict(instruction.Index, IsStealable: false, StealabilityReason.MayTrapInstruction)
                {
                    OriginalMetadataValue = originalMetadataValue
                };
            }

            return new StealabilityVerdict(instruction.Index, IsStealable: true, StealabilityReason.Eligible)
            {
                OriginalMetadataValue = originalMetadataValue
            };
        }

        /// <summary>
        /// Analyzes all instructions in a bundle and returns per-instruction verdicts.
        /// </summary>
        public IReadOnlyList<StealabilityVerdict> AnalyzeBundle(IrMaterializedBundle bundle)
        {
            ArgumentNullException.ThrowIfNull(bundle);

            List<StealabilityVerdict> verdicts = new List<StealabilityVerdict>(bundle.IssuedInstructionCount);
            foreach (IrMaterializedBundleSlot slot in bundle.Slots)
            {
                if (slot.Instruction is not null)
                {
                    verdicts.Add(AnalyzeInstruction(slot.Instruction));
                }
            }

            return verdicts;
        }

        /// <summary>
        /// Analyzes all instructions in a program and returns per-instruction verdicts.
        /// </summary>
        public IReadOnlyList<StealabilityVerdict> AnalyzeProgram(IrProgram program)
        {
            ArgumentNullException.ThrowIfNull(program);

            List<StealabilityVerdict> verdicts = new List<StealabilityVerdict>(program.Instructions.Count);
            foreach (IrInstruction instruction in program.Instructions)
            {
                verdicts.Add(AnalyzeInstruction(instruction));
            }

            return verdicts;
        }
    }
}
