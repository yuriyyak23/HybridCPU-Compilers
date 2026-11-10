using System;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Compiler-side typed-slot/admission descriptor derived from canonical opcode facts.
    /// </summary>
    /// <remarks>
    /// This descriptor mirrors existing compiler/runtime vocabulary; it does not make
    /// compiler metadata authoritative over runtime legality.
    /// </remarks>
    public readonly record struct IrTypedSlotAdmissionDescriptor(
        IrResourceClass ResourceClass,
        IrSlotClass RequiredSlotClass,
        IrSlotBindingKind BindingKind,
        [property: Obsolete("Compiler-side LegalSlots are structurally allowed slots only; use StructurallyAllowedSlots.", false)]
        IrIssueSlotMask LegalSlots)
    {
        public IrIssueSlotMask StructurallyAllowedSlots => LegalSlots;

        public static IrTypedSlotAdmissionDescriptor FromExecutionProfile(IrOpcodeExecutionProfile profile) =>
            new(
                profile.ResourceClass,
                profile.DerivedSlotClass,
                profile.DerivedBindingKind,
                profile.StructurallyAllowedSlots);
    }

    /// <summary>
    /// Compiler-side slot metadata detached from ISA operands and decode payload.
    /// </summary>
    /// <remarks>
    /// <paramref name="VirtualThreadId"/> is core ownership metadata.
    /// <paramref name="StealabilityHint"/> is an advisory runtime hint propagated from
    /// encoded metadata; it does not affect structural admissibility or lane binding.
    /// <paramref name="AdmissionDescriptor"/> carries the canonical typed-slot/admission
    /// descriptor once IR construction has derived it from opcode semantics.
    /// </remarks>
    public readonly record struct IrSlotMetadata(
        byte VirtualThreadId,
        bool StealabilityHint = false,
        IrTypedSlotAdmissionDescriptor? AdmissionDescriptor = null,
        object? DmaStreamComputeDescriptor = null,
        object? AcceleratorCommandDescriptor = null,
        IrExternalDescriptorEvidenceV1? ExternalDescriptorEvidence = null,
        object? MatrixTileNumericPolicy = null,
        object? MatrixTileLayoutPolicy = null)
    {
        public static IrSlotMetadata DefaultForVirtualThread(byte virtualThreadId) =>
            new(virtualThreadId);

        public static IrSlotMetadata FromInstructionMetadata(IrInstructionSlotMetadata metadata) =>
            new(
                metadata.VirtualThreadId,
                metadata.Stealable,
                DmaStreamComputeDescriptor: metadata.DmaStreamComputeDescriptor,
                AcceleratorCommandDescriptor: metadata.AcceleratorCommandDescriptor,
                ExternalDescriptorEvidence: metadata.ExternalDescriptorEvidence,
                MatrixTileNumericPolicy: metadata.MatrixTileNumericPolicy,
                MatrixTileLayoutPolicy: metadata.MatrixTileLayoutPolicy);

        public IrSlotMetadata WithAdmissionDescriptor(IrOpcodeExecutionProfile profile) =>
            this with { AdmissionDescriptor = IrTypedSlotAdmissionDescriptor.FromExecutionProfile(profile) };

        public IrSlotMetadata WithAcceleratorDescriptor(object descriptor)
        {
            System.ArgumentNullException.ThrowIfNull(descriptor);
            return this with { AcceleratorCommandDescriptor = descriptor };
        }
    }
}
