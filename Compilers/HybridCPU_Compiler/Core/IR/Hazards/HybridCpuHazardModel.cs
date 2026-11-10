using System;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Centralized hazard metadata model for HybridCPU opcodes at the compiler layer.
    /// </summary>
    public static class HybridCpuHazardModel
    {
        /// <summary>
        /// Returns the compiler-visible execution profile for an opcode.
        /// </summary>
        public static IrOpcodeExecutionProfile GetExecutionProfile(HybridCpuOpcode opcode)
        {
            HybridCpuOpcodeInfo? opcodeInfo = HybridCpuOpcodeSemantics.GetOpcodeInfo(opcode);
            bool matrixTileMemory = HybridCpuOpcodeSemantics.IsMatrixTileMemoryOpcode(opcode);
            IrResourceClass resourceClass = ClassifyResource(opcode, opcodeInfo);
            // Tile transfers share the lane-6 transport resource, but retain
            // their memory latency/ordering semantics rather than DMA system serialization.
            IrResourceClass semanticClass = matrixTileMemory ? IrResourceClass.LoadStore : resourceClass;
            IrSerializationKind serialization = ClassifySerialization(opcode, semanticClass);
            IrLatencyClass latencyClass = ClassifyLatency(semanticClass, serialization);
            byte minimumLatencyCycles = opcodeInfo?.ExecutionLatency ?? GetFallbackLatencyCycles(resourceClass, latencyClass);
            IrIssueSlotMask structurallyAllowedSlots = HybridCpuSlotModel.GetStructurallyAllowedSlots(resourceClass, opcodeInfo);
            IrStructuralResource structuralResources = ClassifyStructuralResources(opcode, opcodeInfo, resourceClass);
            IrSlotClass derivedSlotClass = matrixTileMemory
                ? IrSlotClass.MatrixTileStreamClass : IrSlotClassMapping.ToSlotClass(resourceClass);
            IrSlotBindingKind derivedBindingKind = matrixTileMemory
                ? IrSlotBindingKind.HardPinned : IrSlotClassMapping.DerivePinningKind(resourceClass, serialization);

            return new IrOpcodeExecutionProfile(
                Opcode: opcode,
                ResourceClass: resourceClass,
                LatencyClass: latencyClass,
                MinimumLatencyCycles: minimumLatencyCycles,
                LegalSlots: structurallyAllowedSlots,
                Serialization: serialization,
                StructuralResources: structuralResources,
                DerivedSlotClass: derivedSlotClass,
                DerivedBindingKind: derivedBindingKind);
        }

        private static IrResourceClass ClassifyResource(HybridCpuOpcode opcode, HybridCpuOpcodeInfo? opcodeInfo)
        {
            if (HybridCpuOpcodeSemantics.IsDmaStreamComputeOpcode(opcode) ||
                HybridCpuOpcodeSemantics.IsMatrixTileMemoryOpcode(opcode))
            {
                return IrResourceClass.DmaStream;
            }

            if (HybridCpuOpcodeSemantics.IsLoadStoreOpcode(opcode, opcodeInfo))
            {
                return IrResourceClass.LoadStore;
            }

            if (opcodeInfo.HasValue)
            {
                HybridCpuInstructionClass instructionClass = opcodeInfo.Value.InstructionClass;
                if (instructionClass == HybridCpuInstructionClass.ControlFlow)
                {
                    return IrResourceClass.ControlFlow;
                }

                if (instructionClass is HybridCpuInstructionClass.System or
                    HybridCpuInstructionClass.Csr or
                    HybridCpuInstructionClass.SmtVt or
                    HybridCpuInstructionClass.Vmx)
                {
                    return IrResourceClass.System;
                }

                if (opcodeInfo.Value.IsVector)
                {
                    return IrResourceClass.VectorAlu;
                }

                if (instructionClass == HybridCpuInstructionClass.ScalarAlu)
                {
                    return IrResourceClass.ScalarAlu;
                }
            }

            if (IsControlFlowInstruction(opcode, opcodeInfo))
            {
                return IrResourceClass.ControlFlow;
            }

            if (HybridCpuOpcodeSemantics.IsSystemInstruction(opcode, opcodeInfo))
            {
                return IrResourceClass.System;
            }

            if (HybridCpuOpcodeSemantics.IsVectorInstruction(opcode, opcodeInfo))
            {
                return IrResourceClass.VectorAlu;
            }

            return IrResourceClass.ScalarAlu;
        }

        private static IrLatencyClass ClassifyLatency(IrResourceClass resourceClass, IrSerializationKind serialization)
        {
            if (resourceClass == IrResourceClass.LoadStore)
            {
                return IrLatencyClass.LoadUse;
            }

            if (resourceClass == IrResourceClass.ControlFlow)
            {
                return IrLatencyClass.ControlFlow;
            }

            if (resourceClass == IrResourceClass.DmaStream)
            {
                return IrLatencyClass.Serialized;
            }

            if (resourceClass == IrResourceClass.System || serialization != IrSerializationKind.None)
            {
                return IrLatencyClass.Serialized;
            }

            return resourceClass == IrResourceClass.VectorAlu ? IrLatencyClass.Vector : IrLatencyClass.SingleCycle;
        }

        private static IrSerializationKind ClassifySerialization(HybridCpuOpcode opcode, IrResourceClass resourceClass)
        {
            if (IsBarrierLike(opcode))
            {
                return IrSerializationKind.BarrierBoundary | IrSerializationKind.ExclusiveCycle;
            }

            if (resourceClass == IrResourceClass.ControlFlow)
            {
                return IrSerializationKind.ControlFlowBoundary | IrSerializationKind.ExclusiveCycle;
            }

            if (resourceClass == IrResourceClass.System)
            {
                return IrSerializationKind.SystemBoundary | IrSerializationKind.ExclusiveCycle;
            }

            if (resourceClass == IrResourceClass.DmaStream)
            {
                return IrSerializationKind.SystemBoundary | IrSerializationKind.ExclusiveCycle;
            }

            return IrSerializationKind.None;
        }

        private static IrStructuralResource ClassifyStructuralResources(HybridCpuOpcode opcode, HybridCpuOpcodeInfo? opcodeInfo, IrResourceClass resourceClass)
        {
            return HybridCpuStructuralResourceModel.ClassifyResources(opcode, opcodeInfo, resourceClass);
        }

        private static byte GetFallbackLatencyCycles(IrResourceClass resourceClass, IrLatencyClass latencyClass)
        {
            return latencyClass switch
            {
                IrLatencyClass.LoadUse => 4,
                IrLatencyClass.Vector => 2,
                IrLatencyClass.ControlFlow => 1,
                IrLatencyClass.Serialized => (byte)(resourceClass == IrResourceClass.DmaStream ? 8 : 1),
                _ => resourceClass == IrResourceClass.ScalarAlu ? (byte)1 : (byte)2
            };
        }

        private static bool IsControlFlowInstruction(HybridCpuOpcode opcode, HybridCpuOpcodeInfo? opcodeInfo)
        {
            HybridCpuOpcodeInfo? semanticOpcodeInfo = opcodeInfo ?? HybridCpuOpcodeSemantics.GetOpcodeInfo(opcode);
            if (semanticOpcodeInfo.HasValue && semanticOpcodeInfo.Value.IsControlFlow)
            {
                return true;
            }

            return HybridCpuOpcodeSemantics.TryResolveRetainedCompatibilityControlFlowKind(
                opcode,
                out _);
        }

        private static bool IsVectorInstruction(HybridCpuOpcode opcode, HybridCpuOpcodeInfo? opcodeInfo)
        {
            return HybridCpuOpcodeSemantics.IsVectorInstruction(opcode, opcodeInfo);
        }

        private static bool IsSystemInstruction(HybridCpuOpcode opcode, HybridCpuOpcodeInfo? opcodeInfo)
        {
            return HybridCpuOpcodeSemantics.IsSystemInstruction(opcode, opcodeInfo);
        }

        private static bool IsBarrierLike(HybridCpuOpcode opcode)
        {
            return HybridCpuOpcodeSemantics.IsBarrierLike(opcode);
        }
    }
}
