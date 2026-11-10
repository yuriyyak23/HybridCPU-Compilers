using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Describes why a bundle was flagged as safety-mask incompatible.
    /// </summary>
    public enum SafetyMaskIncompatibilityReason
    {
        /// <summary>No incompatibility detected.</summary>
        None = 0,

        /// <summary>Two instructions in the same bundle have overlapping write-group resource masks.</summary>
        WriteWriteConflict = 1,

        /// <summary>A read in one instruction and a write in another target the same register group.</summary>
        ReadWriteConflict = 2,

        /// <summary>Two instructions share a structural resource beyond the modeled capacity.</summary>
        StructuralResourceConflict = 3,

        /// <summary>Two memory-accessing instructions may alias in the same bundle.</summary>
        MemoryDomainConflict = 4
    }

    /// <summary>
    /// Records one intra-bundle safety-mask conflict between two instructions.
    /// </summary>
    public sealed record SafetyMaskConflict(
        int FirstInstructionIndex,
        int SecondInstructionIndex,
        SafetyMaskIncompatibilityReason Reason);

    /// <summary>
    /// Diagnostic result for one materialized bundle against the compiler-side SafetyMask model.
    /// </summary>
    public record SafetyMaskDiagnosticResult(
        bool IsCompatible,
        IrSafetyMask128 AggregateMask,
        IReadOnlyList<SafetyMaskConflict> Conflicts)
    {
        /// <summary>
        /// Gets the number of detected conflicts.
        /// </summary>
        public int ConflictCount => Conflicts.Count;

#pragma warning disable CS0618
        internal SafetyMaskCompatibilityResult ToCompatibilityResult()
        {
            return this as SafetyMaskCompatibilityResult
                ?? new SafetyMaskCompatibilityResult(IsCompatible, AggregateMask, Conflicts);
        }
    }

    /// <summary>
    /// Compatibility alias for callers that still depend on the older result naming.
    /// </summary>
    [Obsolete("Compatibility alias (REF-C3). Use SafetyMaskDiagnosticResult.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed record SafetyMaskCompatibilityResult(
        bool IsCompatible,
        IrSafetyMask128 AggregateMask,
        IReadOnlyList<SafetyMaskConflict> Conflicts)
        : SafetyMaskDiagnosticResult(IsCompatible, AggregateMask, Conflicts);

    /// <summary>
    /// Checks bundle-level structural diagnostics against the compiler-side <see cref="IrSafetyMask128"/> model.
    /// </summary>
    /// <remarks>
    /// This is a static preflight: the compiler builds a per-instruction resource mask
    /// from IR metadata and checks pairwise conflicts within each bundle.
    /// The compiler guarantees structural admissibility; runtime remains authoritative
    /// for dynamic admissibility.
    /// </remarks>
    public sealed class SafetyMaskDiagnosticChecker
    {
        /// <summary>
        /// Checks one materialized bundle for intra-bundle safety-mask diagnostics.
        /// </summary>
        public SafetyMaskDiagnosticResult CheckBundle(IrMaterializedBundle bundle)
        {
            ArgumentNullException.ThrowIfNull(bundle);

            List<(int InstructionIndex, IrSafetyMask128 Mask)> instructionMasks = new List<(int InstructionIndex, IrSafetyMask128 Mask)>();
            IrSafetyMask128 aggregate = IrSafetyMask128.Zero;

            foreach (IrMaterializedBundleSlot slot in bundle.Slots)
            {
                if (slot.Instruction is null)
                {
                    continue;
                }

                IrSafetyMask128 mask = BuildInstructionMask(slot.Instruction);
                instructionMasks.Add((slot.Instruction.Index, mask));
                aggregate = aggregate | mask;
            }

            List<SafetyMaskConflict> conflicts = new List<SafetyMaskConflict>();

            for (int i = 0; i < instructionMasks.Count; i++)
            {
                for (int j = i + 1; j < instructionMasks.Count; j++)
                {
                    SafetyMaskIncompatibilityReason reason = ClassifyConflict(
                        instructionMasks[i].Mask,
                        instructionMasks[j].Mask);

                    if (reason != SafetyMaskIncompatibilityReason.None)
                    {
                        conflicts.Add(new SafetyMaskConflict(
                            instructionMasks[i].InstructionIndex,
                            instructionMasks[j].InstructionIndex,
                            reason));
                    }
                }
            }

            return new SafetyMaskDiagnosticResult(
                IsCompatible: conflicts.Count == 0,
                AggregateMask: aggregate,
                Conflicts: conflicts);
        }

        /// <summary>
        /// Checks all bundles in a block bundling result.
        /// </summary>
        public IReadOnlyList<SafetyMaskDiagnosticResult> CheckBlock(IrBasicBlockBundlingResult blockResult)
        {
            ArgumentNullException.ThrowIfNull(blockResult);

            List<SafetyMaskDiagnosticResult> results = new List<SafetyMaskDiagnosticResult>(blockResult.Bundles.Count);
            foreach (IrMaterializedBundle bundle in blockResult.Bundles)
            {
                results.Add(CheckBundle(bundle));
            }

            return results;
        }

        /// <summary>
        /// Builds a compiler-side <see cref="IrSafetyMask128"/> for one IR instruction
        /// from its annotation and operand metadata.
        /// </summary>
        public static IrSafetyMask128 BuildInstructionMask(IrInstruction instruction)
        {
            IrInstructionAnnotation annotation = instruction.Annotation;
            ulong low = 0;

            // Register reads (bits 0-15): uses → read groups
            foreach (IrOperand use in annotation.Uses)
            {
                if (IsArchitecturalRegister(use))
                {
                    int group = (int)(use.Value / 4) & 0xF;
                    low |= 1UL << group;
                }
            }

            // Register writes (bits 16-31): defs → write groups
            foreach (IrOperand def in annotation.Defs)
            {
                if (IsArchitecturalRegister(def))
                {
                    int group = (int)(def.Value / 4) & 0xF;
                    low |= 1UL << (16 + group);
                }
            }

            // Memory domain bits (bits 32-47)
            if (annotation.MemoryReadRegion is not null)
            {
                int domainBit = (int)(annotation.MemoryReadRegion.Address >> 28) & 0xF;
                low |= 1UL << (32 + domainBit);
            }

            if (annotation.MemoryWriteRegion is not null)
            {
                int domainBit = (int)(annotation.MemoryWriteRegion.Address >> 28) & 0xF;
                low |= 1UL << (32 + domainBit);
            }

            // LSU channels (bits 48-50)
            if (annotation.ResourceClass == IrResourceClass.LoadStore)
            {
                if (annotation.MemoryReadRegion is not null)
                {
                    low |= 1UL << 48; // Load
                }

                if (annotation.MemoryWriteRegion is not null)
                {
                    low |= 1UL << 49; // Store
                }
            }

            return new IrSafetyMask128(low, 0);
        }

        private static bool IsArchitecturalRegister(IrOperand operand) =>
            operand.Kind is IrOperandKind.ArchitecturalRegister or IrOperandKind.Pointer;

        private static SafetyMaskIncompatibilityReason ClassifyConflict(IrSafetyMask128 a, IrSafetyMask128 b)
        {
            // Write-Write conflict (bits 16-31)
            ulong aWrites = (a.Low >> 16) & 0xFFFFUL;
            ulong bWrites = (b.Low >> 16) & 0xFFFFUL;
            if ((aWrites & bWrites) != 0)
            {
                return SafetyMaskIncompatibilityReason.WriteWriteConflict;
            }

            // Read-Write / Write-Read conflict (bits 0-15 vs 16-31)
            ulong aReads = a.Low & 0xFFFFUL;
            ulong bReads = b.Low & 0xFFFFUL;
            if ((aReads & bWrites) != 0 || (aWrites & bReads) != 0)
            {
                return SafetyMaskIncompatibilityReason.ReadWriteConflict;
            }

            // Memory domain conflict (bits 32-47): overlapping write domains
            ulong aDomains = (a.Low >> 32) & 0xFFFFUL;
            ulong bDomains = (b.Low >> 32) & 0xFFFFUL;
            ulong aIsStore = (a.Low >> 49) & 1UL;
            ulong bIsStore = (b.Low >> 49) & 1UL;
            if ((aIsStore | bIsStore) != 0 && (aDomains & bDomains) != 0)
            {
                return SafetyMaskIncompatibilityReason.MemoryDomainConflict;
            }

            // High-bits / structural (future extension)
            if ((a.High & b.High) != 0)
            {
                return SafetyMaskIncompatibilityReason.StructuralResourceConflict;
            }

            return SafetyMaskIncompatibilityReason.None;
        }
    }

    /// <summary>
    /// Compatibility alias for callers that still depend on the older checker naming.
    /// </summary>
    [Obsolete("Compatibility alias (REF-C3). Use SafetyMaskDiagnosticChecker.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class SafetyMaskCompatibilityChecker
    {
        private readonly SafetyMaskDiagnosticChecker _inner = new();

        public SafetyMaskCompatibilityResult CheckBundle(IrMaterializedBundle bundle)
        {
            SafetyMaskDiagnosticResult result = _inner.CheckBundle(bundle);
            return result.ToCompatibilityResult();
        }

        public IReadOnlyList<SafetyMaskCompatibilityResult> CheckBlock(IrBasicBlockBundlingResult blockResult)
        {
            IReadOnlyList<SafetyMaskDiagnosticResult> diagnostics = _inner.CheckBlock(blockResult);
            List<SafetyMaskCompatibilityResult> results = new List<SafetyMaskCompatibilityResult>(diagnostics.Count);
            foreach (SafetyMaskDiagnosticResult diagnostic in diagnostics)
            {
                results.Add(diagnostic.ToCompatibilityResult());
            }

            return results;
        }

        public static IrSafetyMask128 BuildInstructionMask(IrInstruction instruction)
        {
            return SafetyMaskDiagnosticChecker.BuildInstructionMask(instruction);
        }
    }
#pragma warning restore CS0618
}
