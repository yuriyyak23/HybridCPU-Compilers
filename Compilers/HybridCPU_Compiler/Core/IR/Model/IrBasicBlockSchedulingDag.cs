using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Scheduler-facing dependence DAG for one basic block.
    /// </summary>
    public sealed class IrBasicBlockSchedulingDag
    {
        private readonly Dictionary<int, IrSchedulingNode> _nodesByInstructionIndex = new();
        private readonly Dictionary<IrHazardEffectKind, List<IrInstructionDependency>> _dependenciesByEffectKind = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="IrBasicBlockSchedulingDag"/> class.
        /// </summary>
        public IrBasicBlockSchedulingDag(
            int blockId,
            IReadOnlyList<IrInstruction> instructions,
            IReadOnlyList<IrSchedulingNode> nodes,
            IReadOnlyList<IrInstructionDependency> dependencies)
        {
            ArgumentNullException.ThrowIfNull(instructions);
            ArgumentNullException.ThrowIfNull(nodes);
            ArgumentNullException.ThrowIfNull(dependencies);

            BlockId = blockId;
            Instructions = instructions;
            Nodes = nodes;
            Dependencies = dependencies;

            foreach (IrSchedulingNode node in nodes)
            {
                _nodesByInstructionIndex[node.InstructionIndex] = node;
            }

            foreach (IrInstructionDependency dependency in dependencies)
            {
                if (!_dependenciesByEffectKind.TryGetValue(dependency.DominantEffectKind, out List<IrInstructionDependency>? effectDependencies))
                {
                    effectDependencies = new List<IrInstructionDependency>();
                    _dependenciesByEffectKind[dependency.DominantEffectKind] = effectDependencies;
                }

                effectDependencies.Add(dependency);
            }
        }

        /// <summary>
        /// Gets the block identifier.
        /// </summary>
        public int BlockId { get; }

        /// <summary>
        /// Gets the instructions covered by this scheduling DAG.
        /// </summary>
        public IReadOnlyList<IrInstruction> Instructions { get; }

        /// <summary>
        /// Gets scheduler-facing instruction nodes.
        /// </summary>
        public IReadOnlyList<IrSchedulingNode> Nodes { get; }

        /// <summary>
        /// Gets all intra-block dependences represented by this DAG.
        /// </summary>
        public IReadOnlyList<IrInstructionDependency> Dependencies { get; }

        /// <summary>
        /// Tries to get a scheduler node for one instruction index.
        /// </summary>
        public bool TryGetNode(int instructionIndex, out IrSchedulingNode? node)
        {
            return _nodesByInstructionIndex.TryGetValue(instructionIndex, out node);
        }

        /// <summary>
        /// Returns incoming dependences for one instruction in the DAG.
        /// </summary>
        public IReadOnlyList<IrInstructionDependency> GetIncomingDependencies(int instructionIndex)
        {
            return _nodesByInstructionIndex.TryGetValue(instructionIndex, out IrSchedulingNode? node)
                ? node.IncomingDependencies
                : Array.Empty<IrInstructionDependency>();
        }

        /// <summary>
        /// Returns outgoing dependences for one instruction in the DAG.
        /// </summary>
        public IReadOnlyList<IrInstructionDependency> GetOutgoingDependencies(int instructionIndex)
        {
            return _nodesByInstructionIndex.TryGetValue(instructionIndex, out IrSchedulingNode? node)
                ? node.OutgoingDependencies
                : Array.Empty<IrInstructionDependency>();
        }

        /// <summary>
        /// Returns dependences classified by dominant typed effect within the DAG.
        /// </summary>
        public IReadOnlyList<IrInstructionDependency> GetDependencies(IrHazardEffectKind dominantEffectKind)
        {
            return _dependenciesByEffectKind.TryGetValue(dominantEffectKind, out List<IrInstructionDependency>? dependencies)
                ? dependencies
                : Array.Empty<IrInstructionDependency>();
        }
    }

    /// <summary>
    /// Scheduler-facing node for one instruction inside a block DAG.
    /// </summary>
    public sealed record IrSchedulingNode(
        int InstructionIndex,
        IrInstruction Instruction,
        IReadOnlyList<IrInstructionDependency> IncomingDependencies,
        IReadOnlyList<IrInstructionDependency> OutgoingDependencies,
        int CriticalPathLengthCycles);
}
