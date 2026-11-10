using System;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Shared compiler-side opcode semantics helpers.
    /// Keeps IR construction, hazard modeling, and structural resource classification
    /// aligned on the same ISA/runtime surface without reopening larger pipeline design.
    /// </summary>
    internal static class HybridCpuOpcodeSemantics
    {
        public static HybridCpuOpcodeInfo? GetOpcodeInfo(HybridCpuOpcode opcode)
        {
            return HybridCpuOpcodeCatalog.GetInfo(NormalizeSemanticOpcode(opcode));
        }

        public static HybridCpuOpcode NormalizeSemanticOpcode(HybridCpuOpcode opcode) => opcode;

        public static bool IsLoadStoreOpcode(HybridCpuOpcode opcode, HybridCpuOpcodeInfo? opcodeInfo = null)
        {
            if (IsDmaStreamComputeOpcode(opcode))
            {
                return false;
            }

            HybridCpuOpcodeInfo? resolvedOpcodeInfo = ResolveOpcodeInfo(opcode, opcodeInfo);
            if (resolvedOpcodeInfo.HasValue &&
                resolvedOpcodeInfo.Value.InstructionClass is HybridCpuInstructionClass.Memory or HybridCpuInstructionClass.Atomic)
            {
                return true;
            }

            return TryResolveRetainedCompatibilityScalarMemoryDirection(opcode, out _) ||
                TryResolveRetainedCompatibilityVectorTransferDirection(opcode, out _);
        }

        public static bool UsesLoadStoreReadPath(HybridCpuOpcode opcode, HybridCpuOpcodeInfo? opcodeInfo = null)
        {
            if (IsDmaStreamComputeOpcode(opcode))
            {
                return false;
            }

            HybridCpuOpcodeInfo? resolvedOpcodeInfo = ResolveOpcodeInfo(opcode, opcodeInfo);
            if (!IsLoadStoreOpcode(opcode, resolvedOpcodeInfo))
            {
                return false;
            }

            if (resolvedOpcodeInfo.HasValue)
            {
                HybridCpuOpcodeInfo publishedOpcodeInfo = resolvedOpcodeInfo.Value;
                if (publishedOpcodeInfo.InstructionClass == HybridCpuInstructionClass.Atomic)
                {
                    return (publishedOpcodeInfo.Flags & HybridCpuInstructionFlags.MemoryRead) != 0 ||
                        (publishedOpcodeInfo.Flags & HybridCpuInstructionFlags.MemoryWrite) != 0;
                }

                if (publishedOpcodeInfo.InstructionClass == HybridCpuInstructionClass.Memory &&
                    publishedOpcodeInfo.Category == HybridCpuOpcodeCategory.Vector)
                {
                    return publishedOpcodeInfo.SerializationClass != HybridCpuSerializationClass.MemoryOrdered;
                }

                return (publishedOpcodeInfo.Flags & HybridCpuInstructionFlags.MemoryRead) != 0;
            }

            if (TryResolveRetainedCompatibilityVectorTransferDirection(opcode, out bool isVectorWriteContour))
            {
                return !isVectorWriteContour;
            }

            return TryResolveRetainedCompatibilityScalarMemoryDirection(opcode, out bool isWriteContour) &&
                !isWriteContour;
        }

        public static bool UsesLoadStoreWritePath(HybridCpuOpcode opcode, HybridCpuOpcodeInfo? opcodeInfo = null)
        {
            if (IsDmaStreamComputeOpcode(opcode))
            {
                return false;
            }

            HybridCpuOpcodeInfo? resolvedOpcodeInfo = ResolveOpcodeInfo(opcode, opcodeInfo);
            if (!IsLoadStoreOpcode(opcode, resolvedOpcodeInfo))
            {
                return false;
            }

            if (resolvedOpcodeInfo.HasValue)
            {
                HybridCpuOpcodeInfo publishedOpcodeInfo = resolvedOpcodeInfo.Value;
                if (publishedOpcodeInfo.InstructionClass == HybridCpuInstructionClass.Memory &&
                    publishedOpcodeInfo.Category == HybridCpuOpcodeCategory.Vector)
                {
                    return publishedOpcodeInfo.SerializationClass == HybridCpuSerializationClass.MemoryOrdered;
                }

                return (publishedOpcodeInfo.Flags & HybridCpuInstructionFlags.MemoryWrite) != 0;
            }

            if (TryResolveRetainedCompatibilityVectorTransferDirection(opcode, out bool isVectorWriteContour))
            {
                return isVectorWriteContour;
            }

            return TryResolveRetainedCompatibilityScalarMemoryDirection(opcode, out bool isWriteContour) &&
                isWriteContour;
        }

        public static bool UsesAddressGeneration(HybridCpuOpcode opcode, HybridCpuOpcodeInfo? opcodeInfo = null)
        {
            return UsesLoadStoreReadPath(opcode, opcodeInfo) || UsesLoadStoreWritePath(opcode, opcodeInfo);
        }

        public static bool IsDmaStreamComputeOpcode(HybridCpuOpcode opcode) =>
            opcode == HybridCpuOpcode.DmaStreamCompute;

        public static bool IsMatrixTileMemoryOpcode(HybridCpuOpcode opcode) =>
            opcode is HybridCpuOpcode.MTILE_LOAD or HybridCpuOpcode.MTILE_STORE;

        public static bool IsVectorInstruction(HybridCpuOpcode opcode, HybridCpuOpcodeInfo? opcodeInfo = null)
        {
            HybridCpuOpcodeInfo? resolvedOpcodeInfo = ResolveOpcodeInfo(opcode, opcodeInfo);
            if (resolvedOpcodeInfo.HasValue)
            {
                return resolvedOpcodeInfo.Value.IsVector;
            }

            return TryResolveRetainedCompatibilityVectorTransferDirection(opcode, out _);
        }

        public static bool IsSystemInstruction(HybridCpuOpcode opcode, HybridCpuOpcodeInfo? opcodeInfo = null)
        {
            HybridCpuOpcodeInfo? resolvedOpcodeInfo = ResolveOpcodeInfo(opcode, opcodeInfo);
            if (resolvedOpcodeInfo.HasValue &&
                resolvedOpcodeInfo.Value.InstructionClass is HybridCpuInstructionClass.System or
                    HybridCpuInstructionClass.Csr or
                    HybridCpuInstructionClass.SmtVt or
                    HybridCpuInstructionClass.Vmx)
            {
                return true;
            }

            return false;
        }

        public static bool IsBarrierLike(HybridCpuOpcode opcode)
        {
            HybridCpuOpcodeInfo? resolvedOpcodeInfo = GetOpcodeInfo(opcode);
            if (resolvedOpcodeInfo.HasValue)
            {
                if (resolvedOpcodeInfo.Value.InstructionClass == HybridCpuInstructionClass.System)
                {
                    HybridCpuInstructionFlags flags = resolvedOpcodeInfo.Value.Flags;

                    return !resolvedOpcodeInfo.Value.IsVector &&
                           resolvedOpcodeInfo.Value.OperandCount == 0 &&
                           (flags & HybridCpuInstructionFlags.Privileged) == 0;
                }

                if (resolvedOpcodeInfo.Value.InstructionClass == HybridCpuInstructionClass.Atomic)
                {
                    HybridCpuInstructionFlags flags = resolvedOpcodeInfo.Value.Flags;

                    return (resolvedOpcodeInfo.Value.OperandCount == 1 &&
                            (flags & HybridCpuInstructionFlags.MemoryRead) != 0 &&
                            (flags & HybridCpuInstructionFlags.MemoryWrite) == 0)
                        || (resolvedOpcodeInfo.Value.OperandCount == 2 &&
                            (flags & HybridCpuInstructionFlags.MemoryRead) == 0 &&
                            (flags & HybridCpuInstructionFlags.MemoryWrite) != 0);
                }

                return false;
            }

            return false;
        }

        public static bool IsSignedDivideTrapContour(
            HybridCpuOpcode opcode,
            HybridCpuOpcodeInfo? opcodeInfo = null)
        {
            HybridCpuOpcodeInfo? resolvedOpcodeInfo = ResolveOpcodeInfo(opcode, opcodeInfo);
            if (!resolvedOpcodeInfo.HasValue ||
                resolvedOpcodeInfo.Value.InstructionClass != HybridCpuInstructionClass.ScalarAlu)
            {
                return false;
            }

            if (resolvedOpcodeInfo.Value.IsVector)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(opcode),
                    opcode,
                    "Vector opcodes do not use the compiler scalar trap contour.");
            }

            return opcode == HybridCpuOpcode.DIV;
        }


        internal static (HybridCpuOpcode Opcode, ulong PackedCompareRegisters) NormalizeRetainedConditionalWrapperForEmission(
            HybridCpuOpcode opcode, ulong packedCompareRegisters)
        {
            return (opcode, packedCompareRegisters);

            //!! to delete

        }

        internal static bool TryResolveRetainedCompatibilityControlFlowKind(
            HybridCpuOpcode opcode,
            out IrControlFlowKind controlFlowKind)
        {
            controlFlowKind = IrControlFlowKind.None;
            return false;
        }

        internal static bool TryResolveRetainedCompatibilityScalarMemoryDirection(
            HybridCpuOpcode opcode,
            out bool isWriteContour)
        {
            isWriteContour = false;
            return false;
        }

        internal static bool TryResolveRetainedCompatibilityVectorTransferDirection(
            HybridCpuOpcode opcode,
            out bool isWriteContour)
        {
            isWriteContour = opcode == HybridCpuOpcode.VSTORE;

            return opcode is HybridCpuOpcode.VLOAD or HybridCpuOpcode.VSTORE;
        }

        private static HybridCpuOpcodeInfo? ResolveOpcodeInfo(HybridCpuOpcode opcode, HybridCpuOpcodeInfo? opcodeInfo)
        {
            return opcodeInfo ?? GetOpcodeInfo(opcode);
        }

        private static ulong SwapPackedCompareRegisters(ulong packedRegisters)
        {
            if (!HybridCpuInstructionWord.TryUnpackArchRegs(
                    packedRegisters,
                    out byte rd,
                    out byte rs1,
                    out byte rs2))
            {
                return packedRegisters;
            }

            return HybridCpuInstructionWord.PackArchRegs(rd, rs2, rs1);
        }
    }
}
