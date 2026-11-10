using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR;

public sealed partial class HybridCpuIrBuilder
{
    private static void ApplyCanonicalContracts(IList<IrInstruction> instructions)
    {
        for (int index = 0; index < instructions.Count; index++)
        {
            IrInstruction instruction = instructions[index];
            HybridCpuOpcodeInfo? opcodeInfo = HybridCpuOpcodeSemantics.GetOpcodeInfo(instruction.Opcode);
            if (opcodeInfo is null && instruction.Opcode != HybridCpuOpcode.Nope)
                throw new ArgumentException($"Unknown opcode {instruction.Opcode} has no validated Canonical IR semantics.", nameof(instructions));
            opcodeInfo ??= new HybridCpuOpcodeInfo(
                (uint)HybridCpuOpcode.Nope,
                nameof(HybridCpuOpcode.Nope),
                HybridCpuOpcodeCategory.Scalar,
                0,
                HybridCpuInstructionFlags.None,
                1,
                0,
                HybridCpuInstructionClass.ScalarAlu,
                HybridCpuSerializationClass.Free);

            IrSourceOriginChainV1 origin = IrSourceOriginChainV1.Native(
                instruction.Index,
                instruction.EncodedAddress,
                instruction.SourceSpan);
            IrInstruction canonical = instruction with
            {
                CanonicalType = CreateType(instruction.DataType),
                Semantics = IrInstructionSemanticsV1.Native(instruction.Annotation.MayTrap),
                OriginChain = origin,
                SideEffects = CreateSideEffects(instruction, opcodeInfo.Value)
            };
            instructions[index] = canonical with
            {
                StableIdentity = IrCanonicalIdentityV1.Create(canonical, origin)
            };
        }
    }

    private static IrCanonicalTypeV1 CreateType(HybridCpuDataType dataType)
    {
        int width = checked(HybridCpuDataTypes.SizeOf(dataType) * 8);
        bool floating = dataType is HybridCpuDataType.FLOAT8_E4M3 or HybridCpuDataType.FLOAT8_E5M2 or
            HybridCpuDataType.FLOAT16 or HybridCpuDataType.BFLOAT16 or HybridCpuDataType.FLOAT32 or HybridCpuDataType.FLOAT64;
        bool signed = dataType is HybridCpuDataType.INT8 or HybridCpuDataType.INT16 or HybridCpuDataType.INT32 or HybridCpuDataType.INT64;
        return new(floating ? IrCanonicalValueKind.FloatingPoint : IrCanonicalValueKind.Integer, width, signed);
    }

    private static IrSideEffectSummaryV1 CreateSideEffects(IrInstruction instruction, HybridCpuOpcodeInfo opcodeInfo)
    {
        IrMemoryEffectKind memoryKind = IrMemoryEffectKind.None;
        if (opcodeInfo.Flags.HasFlag(HybridCpuInstructionFlags.MemoryRead)) memoryKind |= IrMemoryEffectKind.Read;
        if (opcodeInfo.Flags.HasFlag(HybridCpuInstructionFlags.MemoryWrite)) memoryKind |= IrMemoryEffectKind.Write;
        if (opcodeInfo.Flags.HasFlag(HybridCpuInstructionFlags.Atomic)) memoryKind |= IrMemoryEffectKind.Atomic;
        if (instruction.Opcode == HybridCpuOpcode.FENCE) memoryKind |= IrMemoryEffectKind.Fence;
        IrCanonicalMemoryEffectV1 memory = memoryKind == IrMemoryEffectKind.None
            ? IrCanonicalMemoryEffectV1.None
            : new(
                memoryKind,
                IrAddressSpaceIdentity.Generic,
                memoryKind.HasFlag(IrMemoryEffectKind.Atomic)
                    ? IrMemoryOrdering.SequentiallyConsistent
                    : memoryKind.HasFlag(IrMemoryEffectKind.Fence)
                        ? IrMemoryOrdering.AcquireRelease
                        : IrMemoryOrdering.NotAtomic,
                instruction.Annotation.MemoryReadRegion,
                instruction.Annotation.MemoryWriteRegion);

        IrArchitecturalEffectKind architectural = IrArchitecturalEffectKind.None;
        if (instruction.Annotation.ControlFlowKind != IrControlFlowKind.None) architectural |= IrArchitecturalEffectKind.Control;
        if (instruction.Annotation.MayTrap) architectural |= IrArchitecturalEffectKind.TrapOrFault;
        if (memoryKind.HasFlag(IrMemoryEffectKind.Fence)) architectural |= IrArchitecturalEffectKind.Fence;
        if (instruction.Annotation.ResourceClass == IrResourceClass.System) architectural |= IrArchitecturalEffectKind.SpecialArchitecturalState;
        string opcodeName = instruction.Opcode.ToString();
        if (opcodeName.Contains("CALL", StringComparison.OrdinalIgnoreCase)) architectural |= IrArchitecturalEffectKind.Call;
        if (opcodeName.Contains("RET", StringComparison.OrdinalIgnoreCase)) architectural |= IrArchitecturalEffectKind.Return;
        return new(memory, architectural);
    }
}
