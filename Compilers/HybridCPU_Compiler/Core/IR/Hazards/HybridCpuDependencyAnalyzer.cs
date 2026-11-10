using System;
using System.Collections.Generic;
using System.Numerics;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Produces scheduler-ready pairwise dependences without introducing scheduling decisions.
    /// </summary>
    public sealed class HybridCpuDependencyAnalyzer
    {
        /// <summary>
        /// Analyzes directional dependences from an earlier instruction to a later instruction.
        /// </summary>
        public IReadOnlyList<IrInstructionDependency> AnalyzePair(IrInstruction producer, IrInstruction consumer)
        {
            ArgumentNullException.ThrowIfNull(producer);
            ArgumentNullException.ThrowIfNull(consumer);

            List<IrInstructionDependency> dependencies = new List<IrInstructionDependency>();

            AddRegisterDependencies(producer, consumer, dependencies);
            AddMemoryDependencies(producer, consumer, dependencies);
            AddControlDependencies(producer, consumer, dependencies);

            return dependencies;
        }

        internal IReadOnlyList<IrInstructionDependency> AnalyzeMemoryPair(
            IrInstruction producer, IrInstruction consumer)
        {
            List<IrInstructionDependency> dependencies = new List<IrInstructionDependency>(1);
            AddMemoryDependencies(producer, consumer, dependencies);
            return dependencies;
        }

        internal IReadOnlyList<IrInstructionDependency> AnalyzeControlPair(
            IrInstruction producer, IrInstruction consumer)
        {
            List<IrInstructionDependency> dependencies = new List<IrInstructionDependency>(2);
            AddControlDependencies(producer, consumer, dependencies);
            return dependencies;
        }

        private static void AddRegisterDependencies(IrInstruction producer, IrInstruction consumer, ICollection<IrInstructionDependency> dependencies)
        {
            if (producer.VirtualThreadId != consumer.VirtualThreadId &&
                !HybridCpuRegisterDependencyGuard.ShouldPreserveCrossVirtualThreadRegisterDependencies(producer, consumer))
            {
                return;
            }

            foreach (IrOperand definedOperand in producer.Annotation.Defs)
            {
                if (!HybridCpuDependencyOperandClassifier.IsRegisterOperand(definedOperand))
                {
                    continue;
                }

                if (ContainsMatchingOperand(consumer.Annotation.Uses, definedOperand))
                {
                    dependencies.Add(new IrInstructionDependency(
                        Kind: IrInstructionDependencyKind.RegisterRaw,
                        ProducerInstructionIndex: producer.Index,
                        ConsumerInstructionIndex: consumer.Index,
                        MinimumLatencyCycles: HybridCpuDependencyLatencyMatrix.ResolveRegisterRawLatency(producer, consumer),
                        RelatedOperandKind: definedOperand.Kind,
                        RelatedOperandValue: definedOperand.Value,
                        DominantEffectKind: IrHazardEffectKind.RegisterData));
                }

                if (ContainsMatchingOperand(consumer.Annotation.Defs, definedOperand))
                {
                    dependencies.Add(new IrInstructionDependency(
                        Kind: IrInstructionDependencyKind.RegisterWaw,
                        ProducerInstructionIndex: producer.Index,
                        ConsumerInstructionIndex: consumer.Index,
                        MinimumLatencyCycles: HybridCpuDependencyLatencyMatrix.ResolveRegisterWawLatency(producer, consumer),
                        RelatedOperandKind: definedOperand.Kind,
                        RelatedOperandValue: definedOperand.Value,
                        DominantEffectKind: IrHazardEffectKind.RegisterData));
                }
            }

            foreach (IrOperand usedOperand in producer.Annotation.Uses)
            {
                if (!HybridCpuDependencyOperandClassifier.IsRegisterOperand(usedOperand))
                {
                    continue;
                }

                if (ContainsMatchingOperand(consumer.Annotation.Defs, usedOperand))
                {
                    dependencies.Add(new IrInstructionDependency(
                        Kind: IrInstructionDependencyKind.RegisterWar,
                        ProducerInstructionIndex: producer.Index,
                        ConsumerInstructionIndex: consumer.Index,
                        MinimumLatencyCycles: HybridCpuDependencyLatencyMatrix.ResolveRegisterWarLatency(producer, consumer),
                        RelatedOperandKind: usedOperand.Kind,
                        RelatedOperandValue: usedOperand.Value,
                        DominantEffectKind: IrHazardEffectKind.RegisterData));
                }
            }
        }

        private static void AddMemoryDependencies(IrInstruction producer, IrInstruction consumer, ICollection<IrInstructionDependency> dependencies)
        {
            IrMemoryRegion? producerRead = producer.Annotation.MemoryReadRegion;
            IrMemoryRegion? producerWrite = producer.Annotation.MemoryWriteRegion;
            IrMemoryRegion? consumerRead = consumer.Annotation.MemoryReadRegion;
            IrMemoryRegion? consumerWrite = consumer.Annotation.MemoryWriteRegion;

            if (TryClassifyMemoryDependency(producer, consumer, producerWrite, consumerRead, out IrMemoryDependencyPrecision writeReadPrecision) ||
                TryClassifyMemoryDependency(producer, consumer, producerRead, consumerWrite, out writeReadPrecision) ||
                TryClassifyMemoryDependency(producer, consumer, producerWrite, consumerWrite, out writeReadPrecision))
            {
                dependencies.Add(new IrInstructionDependency(
                    Kind: IrInstructionDependencyKind.Memory,
                    ProducerInstructionIndex: producer.Index,
                    ConsumerInstructionIndex: consumer.Index,
                    MinimumLatencyCycles: HybridCpuDependencyLatencyMatrix.ResolveMemoryLatency(producer, consumer, writeReadPrecision),
                    MemoryPrecision: writeReadPrecision,
                    DominantEffectKind: IrHazardEffectKind.MemoryBank));
            }
        }

        private static void AddControlDependencies(IrInstruction producer, IrInstruction consumer, ICollection<IrInstructionDependency> dependencies)
        {
            byte controlLatency = Math.Max((byte)1, producer.Annotation.MinimumLatencyCycles);
            if (producer.Annotation.ControlFlowKind != IrControlFlowKind.None || producer.Annotation.IsBarrierLike)
            {
                IrHazardEffectKind controlEffectKind = producer.Annotation.ControlFlowKind != IrControlFlowKind.None
                    ? IrHazardEffectKind.ControlFlow
                    : IrHazardEffectKind.SystemBarrier;

                dependencies.Add(new IrInstructionDependency(
                    Kind: IrInstructionDependencyKind.Control,
                    ProducerInstructionIndex: producer.Index,
                    ConsumerInstructionIndex: consumer.Index,
                    MinimumLatencyCycles: HybridCpuDependencyLatencyMatrix.ResolveControlLatency(producer, consumer),
                    StructuralResources: producer.Annotation.StructuralResources,
                    DominantEffectKind: controlEffectKind));
            }

            bool hasSerializationBoundary = producer.Annotation.Serialization != IrSerializationKind.None || consumer.Annotation.Serialization != IrSerializationKind.None;
            bool hasPinnedLaneDependency = IsPinnedLaneDependency(producer, consumer);
            if (hasSerializationBoundary || hasPinnedLaneDependency)
            {
                dependencies.Add(new IrInstructionDependency(
                    Kind: IrInstructionDependencyKind.Serialization,
                    ProducerInstructionIndex: producer.Index,
                    ConsumerInstructionIndex: consumer.Index,
                    MinimumLatencyCycles: HybridCpuDependencyLatencyMatrix.ResolveSerializationLatency(producer, consumer, hasPinnedLaneDependency),
                    StructuralResources: producer.Annotation.StructuralResources | consumer.Annotation.StructuralResources,
                    DominantEffectKind: ClassifySerializationEffectKind(producer, consumer, hasPinnedLaneDependency)));
            }
        }

        private static bool ContainsMatchingOperand(IReadOnlyList<IrOperand> operands, IrOperand candidate)
        {
            foreach (IrOperand operand in operands)
            {
                if (!HybridCpuDependencyOperandClassifier.IsRegisterOperand(operand))
                {
                    continue;
                }

                if (operand.Kind == candidate.Kind && operand.Value == candidate.Value)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryClassifyMemoryDependency(
            IrInstruction producer,
            IrInstruction consumer,
            IrMemoryRegion? first,
            IrMemoryRegion? second,
            out IrMemoryDependencyPrecision precision)
        {
            if (first is null || second is null)
            {
                precision = IrMemoryDependencyPrecision.None;
                return false;
            }

            if (CanPruneMemoryDependencyByDomainTags(producer.Annotation.DomainTag, consumer.Annotation.DomainTag) ||
                CanPruneMemoryDependencyByCapabilityRoot(producer, consumer, first, second))
            {
                precision = IrMemoryDependencyPrecision.None;
                return false;
            }

            if (!RegionsOverlap(first, second))
            {
                precision = IrMemoryDependencyPrecision.None;
                return false;
            }

            precision = UsesApproximateMemoryAccess(producer) || UsesApproximateMemoryAccess(consumer)
                ? IrMemoryDependencyPrecision.May
                : IrMemoryDependencyPrecision.Must;
            return true;
        }

        private static bool UsesApproximateMemoryAccess(IrInstruction instruction)
        {
            return instruction.Indexed ||
                   instruction.Is2D ||
                   instruction.Stride > 1 ||
                   instruction.RowStride > 0;
        }

        private static bool RegionsOverlap(IrMemoryRegion left, IrMemoryRegion right)
        {
            ulong leftLength = left.Length == 0 ? 1UL : left.Length;
            ulong rightLength = right.Length == 0 ? 1UL : right.Length;
            ulong leftEnd = left.Address + leftLength;
            ulong rightEnd = right.Address + rightLength;
            return left.Address < rightEnd && right.Address < leftEnd;
        }

        private static bool CanPruneMemoryDependencyByDomainTags(ulong producerDomainTag, ulong consumerDomainTag)
        {
            return producerDomainTag != 0 &&
                consumerDomainTag != 0 &&
                (producerDomainTag & consumerDomainTag) == 0;
        }

        private static bool CanPruneMemoryDependencyByCapabilityRoot(
            IrInstruction producer,
            IrInstruction consumer,
            IrMemoryRegion first,
            IrMemoryRegion second)
        {
            _ = producer;
            _ = consumer;
            _ = first;
            _ = second;
            return false;
        }

        private static IrHazardEffectKind ClassifySerializationEffectKind(IrInstruction producer, IrInstruction consumer, bool hasPinnedLaneDependency)
        {
            if (HasSystemBarrierEffect(producer) || HasSystemBarrierEffect(consumer))
            {
                return IrHazardEffectKind.SystemBarrier;
            }

            if (hasPinnedLaneDependency)
            {
                return IrHazardEffectKind.PinnedLane;
            }

            return IrHazardEffectKind.ControlFlow;
        }

        private static bool HasSystemBarrierEffect(IrInstruction instruction)
        {
            return instruction.Annotation.IsBarrierLike ||
                   instruction.Annotation.ResourceClass == IrResourceClass.System ||
                   (instruction.Annotation.Serialization & (IrSerializationKind.BarrierBoundary | IrSerializationKind.SystemBoundary)) != 0;
        }

        internal static bool IsPinnedLaneDependency(IrInstruction producer, IrInstruction consumer)
        {
            if (producer.Annotation.BindingKind != IrSlotBindingKind.HardPinned ||
                consumer.Annotation.BindingKind != IrSlotBindingKind.HardPinned ||
                HasSystemBarrierEffect(producer) ||
                HasSystemBarrierEffect(consumer) ||
                producer.Annotation.ControlFlowKind != IrControlFlowKind.None ||
                consumer.Annotation.ControlFlowKind != IrControlFlowKind.None)
            {
                return false;
            }

            return TryGetSinglePinnedSlot(producer.Annotation.StructurallyAllowedSlots, out IrIssueSlotMask producerSlot) &&
                   TryGetSinglePinnedSlot(consumer.Annotation.StructurallyAllowedSlots, out IrIssueSlotMask consumerSlot) &&
                   producerSlot == consumerSlot;
        }

        private static bool TryGetSinglePinnedSlot(IrIssueSlotMask structurallyAllowedSlots, out IrIssueSlotMask singleSlot)
        {
            uint slotMask = (uint)structurallyAllowedSlots;
            if (slotMask == 0 || BitOperations.PopCount(slotMask) != 1)
            {
                singleSlot = IrIssueSlotMask.None;
                return false;
            }

            singleSlot = structurallyAllowedSlots;
            return true;
        }
    }
}
