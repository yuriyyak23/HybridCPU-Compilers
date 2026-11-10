using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Aggregates pairwise instruction dependences for a basic block without introducing scheduling.
    /// </summary>
    public sealed class HybridCpuBasicBlockDependencyAnalyzer
    {
        private readonly HybridCpuDependencyAnalyzer _dependencyAnalyzer = new();

        /// <summary>
        /// Builds a dependence graph for a single basic block.
        /// </summary>
        public IrBasicBlockDependencyGraph AnalyzeBlock(IrBasicBlock block)
        {
            ArgumentNullException.ThrowIfNull(block);

            HashSet<IrInstructionDependency> dependencies = new HashSet<IrInstructionDependency>();
            AddPreciseRegisterDependencies(block, dependencies);
            AddVirtualValueDependencies(block, dependencies);
            AddLoadAdjacentScalarFollowThroughDependencies(block, dependencies);

            for (int producerIndex = 0; producerIndex < block.Instructions.Count; producerIndex++)
            {
                for (int consumerIndex = producerIndex + 1; consumerIndex < block.Instructions.Count; consumerIndex++)
                {
                    foreach (IrInstructionDependency dependency in _dependencyAnalyzer.AnalyzeMemoryPair(
                                 block.Instructions[producerIndex], block.Instructions[consumerIndex]))
                        dependencies.Add(dependency);
                }
            }
            AddControlAndSerializationFrontier(block, dependencies, _dependencyAnalyzer);

            IrInstructionDependency[] orderedDependencies = dependencies
                .OrderBy(d => d.ProducerInstructionIndex)
                .ThenBy(d => d.ConsumerInstructionIndex)
                .ThenBy(d => d.Kind)
                .ThenBy(d => d.RelatedOperandKind)
                .ThenBy(d => d.RelatedOperandValue)
                .ToArray();

            return new IrBasicBlockDependencyGraph(block.Id, block.Instructions, orderedDependencies);
        }

        private static void AddControlAndSerializationFrontier(
            IrBasicBlock block,
            ICollection<IrInstructionDependency> dependencies,
            HybridCpuDependencyAnalyzer analyzer)
        {
            IrInstruction? lastControl = null;
            IrInstruction? lastSerializationBoundary = null;
            List<IrInstruction> sinceSerializationBoundary = new List<IrInstruction>();
            Dictionary<IrIssueSlotMask, IrInstruction> lastPinnedBySlot = new Dictionary<IrIssueSlotMask, IrInstruction>();
            foreach (IrInstruction current in block.Instructions)
            {
                if (lastControl is not null) AddPair(lastControl, current, IrInstructionDependencyKind.Control);

                bool serializationBoundary = current.Annotation.Serialization != IrSerializationKind.None;
                if (lastSerializationBoundary is not null)
                    AddPair(lastSerializationBoundary, current, IrInstructionDependencyKind.Serialization);
                if (serializationBoundary)
                {
                    foreach (IrInstruction prior in sinceSerializationBoundary)
                        AddPair(prior, current, IrInstructionDependencyKind.Serialization);
                    sinceSerializationBoundary.Clear();
                    lastSerializationBoundary = current;
                }
                else
                {
                    sinceSerializationBoundary.Add(current);
                }

                if (current.Annotation.BindingKind == IrSlotBindingKind.HardPinned)
                {
                    uint mask = (uint)current.Annotation.StructurallyAllowedSlots;
                    if (BitOperations.IsPow2(mask))
                    {
                        IrIssueSlotMask slot = current.Annotation.StructurallyAllowedSlots;
                        if (lastPinnedBySlot.TryGetValue(slot, out IrInstruction? prior) &&
                            HybridCpuDependencyAnalyzer.IsPinnedLaneDependency(prior, current))
                            AddPair(prior, current, IrInstructionDependencyKind.Serialization);
                        if (current.Annotation.ControlFlowKind == IrControlFlowKind.None &&
                            !current.Annotation.IsBarrierLike)
                            lastPinnedBySlot[slot] = current;
                    }
                }

                if (current.Annotation.ControlFlowKind != IrControlFlowKind.None || current.Annotation.IsBarrierLike)
                    lastControl = current;
            }

            void AddPair(IrInstruction producer, IrInstruction consumer, IrInstructionDependencyKind kind)
            {
                if (producer.Index == consumer.Index) return;
                foreach (IrInstructionDependency dependency in analyzer.AnalyzeControlPair(producer, consumer))
                    if (dependency.Kind == kind) dependencies.Add(dependency);
            }
        }

        private static void AddVirtualValueDependencies(
            IrBasicBlock block,
            ICollection<IrInstructionDependency> dependencies)
        {
            Dictionary<string, IrInstruction> lastDefinitions = new Dictionary<string, IrInstruction>(StringComparer.Ordinal);
            Dictionary<string, Dictionary<int, IrInstruction>> liveUses = new Dictionary<string, Dictionary<int, IrInstruction>>(StringComparer.Ordinal);
            foreach (IrInstruction instruction in block.Instructions)
            {
                foreach (IrOperand use in instruction.Annotation.Uses.Where(static operand => operand.Kind == IrOperandKind.VirtualValue))
                {
                    if (lastDefinitions.TryGetValue(use.Name, out IrInstruction? producer))
                        dependencies.Add(new(IrInstructionDependencyKind.RegisterRaw, producer.Index, instruction.Index,
                            HybridCpuDependencyLatencyMatrix.ResolveRegisterRawLatency(producer, instruction),
                            use.Kind, use.Value, DominantEffectKind: IrHazardEffectKind.RegisterData));
                    if (!liveUses.TryGetValue(use.Name, out Dictionary<int, IrInstruction>? uses))
                        liveUses[use.Name] = uses = [];
                    uses[instruction.Index] = instruction;
                }

                foreach (IrOperand definition in instruction.Annotation.Defs.Where(static operand => operand.Kind == IrOperandKind.VirtualValue))
                {
                    if (liveUses.TryGetValue(definition.Name, out Dictionary<int, IrInstruction>? uses))
                    {
                        foreach (IrInstruction priorUse in uses.Values.Where(prior => prior.Index != instruction.Index))
                            dependencies.Add(new(IrInstructionDependencyKind.RegisterWar, priorUse.Index, instruction.Index,
                                HybridCpuDependencyLatencyMatrix.ResolveRegisterWarLatency(priorUse, instruction),
                                definition.Kind, definition.Value, DominantEffectKind: IrHazardEffectKind.RegisterData));
                        uses.Clear();
                    }
                    if (lastDefinitions.TryGetValue(definition.Name, out IrInstruction? priorDefinition) &&
                        priorDefinition.Index != instruction.Index)
                        dependencies.Add(new(IrInstructionDependencyKind.RegisterWaw, priorDefinition.Index, instruction.Index,
                            HybridCpuDependencyLatencyMatrix.ResolveRegisterWawLatency(priorDefinition, instruction),
                            definition.Kind, definition.Value, DominantEffectKind: IrHazardEffectKind.RegisterData));
                    lastDefinitions[definition.Name] = instruction;
                }
            }
        }

        /// <summary>
        /// Builds dependence graphs for all blocks in a program.
        /// </summary>
        public IReadOnlyList<IrBasicBlockDependencyGraph> AnalyzeProgram(IrProgram program)
        {
            ArgumentNullException.ThrowIfNull(program);

            List<IrBasicBlockDependencyGraph> blockGraphs = new List<IrBasicBlockDependencyGraph>(program.BasicBlocks.Count);
            foreach (IrBasicBlock block in program.BasicBlocks)
            {
                blockGraphs.Add(AnalyzeBlock(block));
            }

            return blockGraphs;
        }

        /// <summary>
        /// Builds a program-level dependence graph that combines intra-block and inter-block results.
        /// </summary>
        public IrProgramDependencyGraph AnalyzeProgramGraph(IrProgram program)
        {
            ArgumentNullException.ThrowIfNull(program);

            HybridCpuProgramDependencyAnalyzer programAnalyzer = new HybridCpuProgramDependencyAnalyzer();
            return programAnalyzer.AnalyzeProgram(program);
        }

        private static void AddPreciseRegisterDependencies(
            IrBasicBlock block,
            ICollection<IrInstructionDependency> dependencies)
        {
            Dictionary<HybridCpuRegisterDependencyKey, IrInstruction> lastDefinitions = new Dictionary<HybridCpuRegisterDependencyKey, IrInstruction>();
            Dictionary<HybridCpuRegisterDependencyKey, Dictionary<int, IrInstruction>> liveUsesSinceLastDefinition = new Dictionary<HybridCpuRegisterDependencyKey, Dictionary<int, IrInstruction>>();

            foreach (IrInstruction instruction in block.Instructions)
            {
                foreach (IrOperand use in instruction.Annotation.Uses)
                {
                    if (!HybridCpuDependencyOperandClassifier.IsRegisterOperand(use))
                    {
                        continue;
                    }

                    foreach (HybridCpuRegisterDependencyKey key in EnumerateRegisterDependencyKeys(instruction, use))
                    {
                        if (lastDefinitions.TryGetValue(key, out IrInstruction? producer))
                        {
                            dependencies.Add(new IrInstructionDependency(
                                Kind: IrInstructionDependencyKind.RegisterRaw,
                                ProducerInstructionIndex: producer.Index,
                                ConsumerInstructionIndex: instruction.Index,
                                MinimumLatencyCycles: HybridCpuDependencyLatencyMatrix.ResolveRegisterRawLatency(producer, instruction),
                                RelatedOperandKind: use.Kind,
                                RelatedOperandValue: use.Value,
                                DominantEffectKind: IrHazardEffectKind.RegisterData));
                        }

                        TrackLiveUse(liveUsesSinceLastDefinition, key, instruction);
                    }
                }

                foreach (IrOperand def in instruction.Annotation.Defs)
                {
                    if (!HybridCpuDependencyOperandClassifier.IsRegisterOperand(def))
                    {
                        continue;
                    }

                    foreach (HybridCpuRegisterDependencyKey key in EnumerateRegisterDependencyKeys(instruction, def))
                    {
                        if (liveUsesSinceLastDefinition.TryGetValue(key, out Dictionary<int, IrInstruction>? priorUses))
                        {
                            foreach (IrInstruction priorUse in priorUses.Values)
                            {
                                if (priorUse.Index == instruction.Index)
                                {
                                    continue;
                                }

                                dependencies.Add(new IrInstructionDependency(
                                    Kind: IrInstructionDependencyKind.RegisterWar,
                                    ProducerInstructionIndex: priorUse.Index,
                                    ConsumerInstructionIndex: instruction.Index,
                                    MinimumLatencyCycles: HybridCpuDependencyLatencyMatrix.ResolveRegisterWarLatency(priorUse, instruction),
                                    RelatedOperandKind: def.Kind,
                                    RelatedOperandValue: def.Value,
                                    DominantEffectKind: IrHazardEffectKind.RegisterData));
                            }

                            priorUses.Clear();
                        }

                        if (lastDefinitions.TryGetValue(key, out IrInstruction? priorDefinition) &&
                            priorDefinition.Index != instruction.Index)
                        {
                            dependencies.Add(new IrInstructionDependency(
                                Kind: IrInstructionDependencyKind.RegisterWaw,
                                ProducerInstructionIndex: priorDefinition.Index,
                                ConsumerInstructionIndex: instruction.Index,
                                MinimumLatencyCycles: HybridCpuDependencyLatencyMatrix.ResolveRegisterWawLatency(priorDefinition, instruction),
                                RelatedOperandKind: def.Kind,
                                RelatedOperandValue: def.Value,
                                DominantEffectKind: IrHazardEffectKind.RegisterData));
                        }

                        lastDefinitions[key] = instruction;
                    }
                }
            }
        }

        private static void TrackLiveUse(
            IDictionary<HybridCpuRegisterDependencyKey, Dictionary<int, IrInstruction>> liveUsesSinceLastDefinition,
            HybridCpuRegisterDependencyKey key,
            IrInstruction instruction)
        {
            if (!liveUsesSinceLastDefinition.TryGetValue(key, out Dictionary<int, IrInstruction>? uses))
            {
                uses = new Dictionary<int, IrInstruction>();
                liveUsesSinceLastDefinition[key] = uses;
            }

            uses[instruction.Index] = instruction;
        }

        private static void AddLoadAdjacentScalarFollowThroughDependencies(
            IrBasicBlock block,
            ICollection<IrInstructionDependency> dependencies)
        {
            for (int instructionIndex = 0; instructionIndex + 1 < block.Instructions.Count; instructionIndex++)
            {
                IrInstruction producer = block.Instructions[instructionIndex];
                IrInstruction consumer = block.Instructions[instructionIndex + 1];
                if (!ShouldSerializeLoadAdjacentScalarFollowThrough(producer, consumer))
                {
                    continue;
                }

                dependencies.Add(new IrInstructionDependency(
                    Kind: IrInstructionDependencyKind.Serialization,
                    ProducerInstructionIndex: producer.Index,
                    ConsumerInstructionIndex: consumer.Index,
                    MinimumLatencyCycles: HybridCpuDependencyLatencyMatrix.ResolveLoadAdjacentScalarFollowThroughLatency(producer, consumer),
                    StructuralResources: producer.Annotation.StructuralResources | consumer.Annotation.StructuralResources,
                    DominantEffectKind: IrHazardEffectKind.MemoryBank));
            }
        }

        private static bool ShouldSerializeLoadAdjacentScalarFollowThrough(IrInstruction producer, IrInstruction consumer)
        {
            return producer.VirtualThreadId == consumer.VirtualThreadId &&
                   producer.Annotation.ResourceClass == IrResourceClass.LoadStore &&
                   consumer.Annotation.ResourceClass == IrResourceClass.ScalarAlu;
        }

        private static IEnumerable<HybridCpuRegisterDependencyKey> EnumerateRegisterDependencyKeys(IrInstruction instruction, IrOperand operand)
        {
            yield return HybridCpuRegisterDependencyGuard.GetVirtualThreadLocalKey(instruction, operand);

            if (HybridCpuRegisterDependencyGuard.TryGetCrossVirtualThreadGuardKey(instruction, operand, out HybridCpuRegisterDependencyKey sharedKey))
            {
                yield return sharedKey;
            }
        }
    }
}
