using System;
using System.Collections.Generic;
using System.Linq;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Aggregates carried dependences across control-flow graph edges without introducing scheduling policy.
    /// </summary>
    public sealed class HybridCpuInterBlockDependencyAnalyzer
    {
        private readonly HybridCpuDependencyAnalyzer _dependencyAnalyzer = new();

        /// <summary>
        /// Builds inter-block dependences for the control-flow edges of a program.
        /// </summary>
        public IrInterBlockDependencyGraph AnalyzeProgram(IrProgram program)
        {
            ArgumentNullException.ThrowIfNull(program);

            Dictionary<int, IrBasicBlock> blocksById = program.BasicBlocks.ToDictionary(block => block.Id);
            Dictionary<int, BoundarySummary> summariesById = blocksById.ToDictionary(entry => entry.Key, entry => BuildBoundarySummary(entry.Value));
            HashSet<IrInterBlockDependency> dependencies = new HashSet<IrInterBlockDependency>();

            foreach (IrControlFlowEdge edge in program.ControlFlowGraph.Edges)
            {
                BoundarySummary sourceSummary = summariesById[edge.SourceBlockId];
                BoundarySummary targetSummary = summariesById[edge.TargetBlockId];

                AddRegisterCarryDependencies(edge, sourceSummary, targetSummary, dependencies);
                AddMemoryCarryDependencies(edge, sourceSummary, targetSummary, dependencies);
                AddControlBoundaryDependencies(edge, sourceSummary, targetSummary, dependencies);
            }

            return new IrInterBlockDependencyGraph(program.ControlFlowGraph.Edges, dependencies.OrderBy(d => d.SourceBlockId).ThenBy(d => d.TargetBlockId).ThenBy(d => d.Dependency.ProducerInstructionIndex).ThenBy(d => d.Dependency.ConsumerInstructionIndex).ToArray());
        }

        private void AddRegisterCarryDependencies(
            IrControlFlowEdge edge,
            BoundarySummary sourceSummary,
            BoundarySummary targetSummary,
            ICollection<IrInterBlockDependency> dependencies)
        {
            foreach ((HybridCpuRegisterDependencyKey operand, IrInstruction sourceDefinition) in sourceSummary.ExitDefinitions)
            {
                if (targetSummary.EntryExposedUses.TryGetValue(operand, out IrInstruction? targetUse))
                {
                    AddFilteredDependencies(edge, sourceDefinition, targetUse, dependencies, IrInstructionDependencyKind.RegisterRaw);
                }

                if (targetSummary.EntryDefinitions.TryGetValue(operand, out IrInstruction? targetDefinition))
                {
                    AddFilteredDependencies(edge, sourceDefinition, targetDefinition, dependencies, IrInstructionDependencyKind.RegisterWaw);
                }
            }

            foreach ((HybridCpuRegisterDependencyKey operand, IrInstruction sourceUse) in sourceSummary.ExitUses)
            {
                if (targetSummary.EntryDefinitions.TryGetValue(operand, out IrInstruction? targetDefinition))
                {
                    AddFilteredDependencies(edge, sourceUse, targetDefinition, dependencies, IrInstructionDependencyKind.RegisterWar);
                }
            }
        }

        private void AddMemoryCarryDependencies(
            IrControlFlowEdge edge,
            BoundarySummary sourceSummary,
            BoundarySummary targetSummary,
            ICollection<IrInterBlockDependency> dependencies)
        {
            foreach (IrInstruction sourceInstruction in sourceSummary.MemoryRelevantInstructions)
            {
                foreach (IrInstruction targetInstruction in targetSummary.MemoryRelevantInstructions)
                {
                    AddFilteredDependencies(edge, sourceInstruction, targetInstruction, dependencies, IrInstructionDependencyKind.Memory);
                }
            }
        }

        private void AddControlBoundaryDependencies(
            IrControlFlowEdge edge,
            BoundarySummary sourceSummary,
            BoundarySummary targetSummary,
            ICollection<IrInterBlockDependency> dependencies)
        {
            foreach (IrInstruction sourceInstruction in sourceSummary.ExitBoundaryInstructions)
            {
                foreach (IrInstruction targetInstruction in targetSummary.Block.Instructions)
                {
                    AddFilteredDependencies(
                        edge,
                        sourceInstruction,
                        targetInstruction,
                        dependencies,
                        IrInstructionDependencyKind.Control,
                        IrInstructionDependencyKind.Serialization);
                }
            }
        }

        private void AddFilteredDependencies(
            IrControlFlowEdge edge,
            IrInstruction sourceInstruction,
            IrInstruction targetInstruction,
            ICollection<IrInterBlockDependency> dependencies,
            params IrInstructionDependencyKind[] allowedKinds)
        {
            foreach (IrInstructionDependency dependency in _dependencyAnalyzer.AnalyzePair(sourceInstruction, targetInstruction))
            {
                if (Array.IndexOf(allowedKinds, dependency.Kind) < 0)
                {
                    continue;
                }

                dependencies.Add(new IrInterBlockDependency(
                    SourceBlockId: edge.SourceBlockId,
                    TargetBlockId: edge.TargetBlockId,
                    EdgeKind: edge.Kind,
                    Dependency: dependency with
                    {
                        MinimumLatencyCycles = HybridCpuDependencyLatencyMatrix.ResolveInterBlockLatency(
                            dependency.MinimumLatencyCycles,
                            edge.Kind,
                            dependency.Kind)
                    }));
            }
        }

        private static BoundarySummary BuildBoundarySummary(IrBasicBlock block)
        {
            Dictionary<HybridCpuRegisterDependencyKey, IrInstruction> exitDefinitions = new Dictionary<HybridCpuRegisterDependencyKey, IrInstruction>();
            Dictionary<HybridCpuRegisterDependencyKey, IrInstruction> exitUses = new Dictionary<HybridCpuRegisterDependencyKey, IrInstruction>();
            Dictionary<HybridCpuRegisterDependencyKey, IrInstruction> entryDefinitions = new Dictionary<HybridCpuRegisterDependencyKey, IrInstruction>();
            Dictionary<HybridCpuRegisterDependencyKey, IrInstruction> entryExposedUses = new Dictionary<HybridCpuRegisterDependencyKey, IrInstruction>();
            HashSet<HybridCpuRegisterDependencyKey> seenDefinitions = new HashSet<HybridCpuRegisterDependencyKey>();
            List<IrInstruction> memoryRelevantInstructions = new List<IrInstruction>();
            List<IrInstruction> exitBoundaryInstructions = new List<IrInstruction>();

            foreach (IrInstruction instruction in block.Instructions)
            {
                foreach (IrOperand operand in instruction.Annotation.Uses)
                {
                    if (!HybridCpuDependencyOperandClassifier.IsRegisterOperand(operand))
                    {
                        continue;
                    }

                    foreach (HybridCpuRegisterDependencyKey key in EnumerateRegisterDependencyKeys(instruction, operand))
                    {
                        if (!seenDefinitions.Contains(key) && !entryExposedUses.ContainsKey(key))
                        {
                            entryExposedUses[key] = instruction;
                        }

                        exitUses[key] = instruction;
                    }
                }

                foreach (IrOperand operand in instruction.Annotation.Defs)
                {
                    if (!HybridCpuDependencyOperandClassifier.IsRegisterOperand(operand))
                    {
                        continue;
                    }

                    foreach (HybridCpuRegisterDependencyKey key in EnumerateRegisterDependencyKeys(instruction, operand))
                    {
                        if (!entryDefinitions.ContainsKey(key))
                        {
                            entryDefinitions[key] = instruction;
                        }

                        seenDefinitions.Add(key);
                        exitDefinitions[key] = instruction;
                    }
                }

                if (instruction.Annotation.MemoryReadRegion is not null || instruction.Annotation.MemoryWriteRegion is not null)
                {
                    memoryRelevantInstructions.Add(instruction);
                }

                if (instruction.Annotation.ControlFlowKind != IrControlFlowKind.None || instruction.Annotation.Serialization != IrSerializationKind.None)
                {
                    exitBoundaryInstructions.Add(instruction);
                }
            }

            return new BoundarySummary(
                block,
                exitDefinitions,
                exitUses,
                entryDefinitions,
                entryExposedUses,
                memoryRelevantInstructions,
                exitBoundaryInstructions);
        }

        private static IEnumerable<HybridCpuRegisterDependencyKey> EnumerateRegisterDependencyKeys(IrInstruction instruction, IrOperand operand)
        {
            yield return HybridCpuRegisterDependencyGuard.GetVirtualThreadLocalKey(instruction, operand);

            if (HybridCpuRegisterDependencyGuard.TryGetCrossVirtualThreadGuardKey(instruction, operand, out HybridCpuRegisterDependencyKey sharedKey))
            {
                yield return sharedKey;
            }
        }

        private sealed class BoundarySummary
        {
            public BoundarySummary(
                IrBasicBlock block,
                IReadOnlyDictionary<HybridCpuRegisterDependencyKey, IrInstruction> exitDefinitions,
                IReadOnlyDictionary<HybridCpuRegisterDependencyKey, IrInstruction> exitUses,
                IReadOnlyDictionary<HybridCpuRegisterDependencyKey, IrInstruction> entryDefinitions,
                IReadOnlyDictionary<HybridCpuRegisterDependencyKey, IrInstruction> entryExposedUses,
                IReadOnlyList<IrInstruction> memoryRelevantInstructions,
                IReadOnlyList<IrInstruction> exitBoundaryInstructions)
            {
                Block = block;
                ExitDefinitions = exitDefinitions;
                ExitUses = exitUses;
                EntryDefinitions = entryDefinitions;
                EntryExposedUses = entryExposedUses;
                MemoryRelevantInstructions = memoryRelevantInstructions;
                ExitBoundaryInstructions = exitBoundaryInstructions;
            }

            public IrBasicBlock Block { get; }

            public IReadOnlyDictionary<HybridCpuRegisterDependencyKey, IrInstruction> ExitDefinitions { get; }

            public IReadOnlyDictionary<HybridCpuRegisterDependencyKey, IrInstruction> ExitUses { get; }

            public IReadOnlyDictionary<HybridCpuRegisterDependencyKey, IrInstruction> EntryDefinitions { get; }

            public IReadOnlyDictionary<HybridCpuRegisterDependencyKey, IrInstruction> EntryExposedUses { get; }

            public IReadOnlyList<IrInstruction> MemoryRelevantInstructions { get; }

            public IReadOnlyList<IrInstruction> ExitBoundaryInstructions { get; }
        }
    }
}
