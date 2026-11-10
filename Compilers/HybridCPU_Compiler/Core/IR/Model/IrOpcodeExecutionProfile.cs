using System;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Centralized compiler-facing execution metadata derived from ISA opcode information.
    /// </summary>
    public sealed record IrOpcodeExecutionProfile(
        HybridCpuOpcode Opcode,
        IrResourceClass ResourceClass,
        IrLatencyClass LatencyClass,
        byte MinimumLatencyCycles,
        [property: Obsolete("Compiler-side LegalSlots are structurally allowed slots only; use StructurallyAllowedSlots.", false)]
        IrIssueSlotMask LegalSlots,
        IrSerializationKind Serialization,
        IrStructuralResource StructuralResources,
        IrSlotClass DerivedSlotClass,
        IrSlotBindingKind DerivedBindingKind)
    {
        /// <summary>
        /// Gets a value indicating whether the opcode must execute alone in a cycle group.
        /// </summary>
        public bool RequiresExclusiveCycle => (Serialization & IrSerializationKind.ExclusiveCycle) != 0;

        public IrIssueSlotMask StructurallyAllowedSlots => LegalSlots;
    }
}
