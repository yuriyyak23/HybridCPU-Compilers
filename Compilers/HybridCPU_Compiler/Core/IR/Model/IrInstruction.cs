using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Normalized instruction-level IR node derived from an encoded VLIW instruction.
    /// </summary>
    public sealed record IrInstruction(
        int Index,
        byte VirtualThreadId,
        ulong EncodedAddress,
        HybridCpuOpcode Opcode,
        HybridCpuDataType DataType,
        byte PredicateMask,
        ushort Immediate,
        uint StreamLength,
        ushort Stride,
        ushort RowStride,
        bool Indexed,
        bool Is2D,
        bool Reduction,
        bool TailAgnostic,
        bool MaskAgnostic,
        IReadOnlyList<IrOperand> Operands,
    IrInstructionAnnotation Annotation,
    IrSourceSpan? SourceSpan = null)
    {
        // ── ISA v4 Phase 02: canonical instruction classification ─────────────────────
        // These properties are computed from the opcode by the IR builder via
        // InstructionClassifier.Classify(Opcode). Stealability policy is carried separately
        // through Annotation/slot metadata rather than as an instruction field.

        /// <summary>
        /// Canonical ISA v4 instruction class.
        /// Determines pipeline routing and slot class assignment.
        /// </summary>
        public HybridCpuInstructionClass InstructionClass { get; init; } = HybridCpuInstructionClass.ScalarAlu;

        /// <summary>
        /// Canonical ISA v4 serialization class.
        /// Determines ordering and side-effect isolation requirements.
        /// </summary>
        public HybridCpuSerializationClass SerializationClass { get; init; } = HybridCpuSerializationClass.Free;

        /// <summary>
        /// Descriptor sideband for canonical lane6 DmaStreamCompute emission.
        /// </summary>
        public object? DmaStreamComputeDescriptor { get; init; }

        /// <summary>
        /// Descriptor sideband for L7-SDC decode/projector validation.
        /// Phase 04 keeps compiler emission disabled; this field is transport metadata only.
        /// </summary>
        public object? AcceleratorCommandDescriptor { get; init; }

        /// <summary>Exact authority-free footprint projection for a validated external descriptor.</summary>
        public IrExternalDescriptorEvidenceV1? ExternalDescriptorEvidence { get; init; }

        /// <summary>
        /// Compiler-owned positive MTILE helper sideband recovered from the direct MTILE carrier.
        /// Runtime-owned legality remains the final ISA authority.
        /// </summary>
        public CompilerMatrixTileEmissionPlan? MatrixTileEmission { get; init; }

        /// <summary>
        /// Compiler-owned positive VLOAD/VSTORE helper sideband recovered from the direct
        /// vector transfer carrier. Runtime-owned legality remains the final ISA authority.
        /// </summary>
        public CompilerVectorTransferEmissionPlan? VectorTransferEmission { get; init; }

        /// <summary>Frontend-neutral deterministic identity; never a frontend object handle.</summary>
        public string StableIdentity { get; init; } = string.Empty;

        public IrCanonicalTypeV1 CanonicalType { get; init; } = new(IrCanonicalValueKind.Unknown, 0, false);

        public IrInstructionSemanticsV1 Semantics { get; init; } = IrInstructionSemanticsV1.Unknown;

        public IrSourceOriginChainV1 OriginChain { get; init; } = new(1, Array.Empty<IrSourceOriginLinkV1>());

        public IrSideEffectSummaryV1 SideEffects { get; init; } = new(
            IrCanonicalMemoryEffectV1.Unknown,
            IrArchitecturalEffectKind.Unknown);
    }
}
