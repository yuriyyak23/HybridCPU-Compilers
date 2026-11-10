using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Aggregated inter-block dependence view for one program control-flow graph.
    /// </summary>
    public sealed class IrInterBlockDependencyGraph
    {
        private readonly Dictionary<(int SourceBlockId, int TargetBlockId), List<IrInterBlockDependency>> _edgeDependencies = new();
        private readonly Dictionary<int, List<IrInterBlockDependency>> _incomingBlockDependencies = new();
        private readonly Dictionary<int, List<IrInterBlockDependency>> _outgoingBlockDependencies = new();
        private readonly Dictionary<int, List<IrInterBlockDependency>> _incomingInstructionDependencies = new();
        private readonly Dictionary<int, List<IrInterBlockDependency>> _outgoingInstructionDependencies = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="IrInterBlockDependencyGraph"/> class.
        /// </summary>
        public IrInterBlockDependencyGraph(
            IReadOnlyList<IrControlFlowEdge> Edges,
            IReadOnlyList<IrInterBlockDependency> Dependencies)
        {
            ArgumentNullException.ThrowIfNull(Edges);
            ArgumentNullException.ThrowIfNull(Dependencies);

            this.Edges = Edges;
            this.Dependencies = Dependencies;

            foreach (IrInterBlockDependency dependency in Dependencies)
            {
                Add(_edgeDependencies, (dependency.SourceBlockId, dependency.TargetBlockId), dependency);
                Add(_incomingBlockDependencies, dependency.TargetBlockId, dependency);
                Add(_outgoingBlockDependencies, dependency.SourceBlockId, dependency);
                Add(_incomingInstructionDependencies, dependency.Dependency.ConsumerInstructionIndex, dependency);
                Add(_outgoingInstructionDependencies, dependency.Dependency.ProducerInstructionIndex, dependency);
            }
        }

        /// <summary>
        /// Gets the CFG edges analyzed by the inter-block dependence layer.
        /// </summary>
        public IReadOnlyList<IrControlFlowEdge> Edges { get; }

        /// <summary>
        /// Gets the inter-block dependences discovered across CFG edges.
        /// </summary>
        public IReadOnlyList<IrInterBlockDependency> Dependencies { get; }

        /// <summary>
        /// Returns dependences carried across one CFG edge.
        /// </summary>
        public IReadOnlyList<IrInterBlockDependency> GetDependenciesForEdge(int sourceBlockId, int targetBlockId)
        {
            return _edgeDependencies.TryGetValue((sourceBlockId, targetBlockId), out List<IrInterBlockDependency>? dependencies)
                ? dependencies
                : Array.Empty<IrInterBlockDependency>();
        }

        /// <summary>
        /// Returns dependences entering a basic block from its predecessors.
        /// </summary>
        public IReadOnlyList<IrInterBlockDependency> GetIncomingDependencies(int blockId)
        {
            return _incomingBlockDependencies.TryGetValue(blockId, out List<IrInterBlockDependency>? dependencies)
                ? dependencies
                : Array.Empty<IrInterBlockDependency>();
        }

        /// <summary>
        /// Returns dependences leaving a basic block toward its successors.
        /// </summary>
        public IReadOnlyList<IrInterBlockDependency> GetOutgoingDependencies(int blockId)
        {
            return _outgoingBlockDependencies.TryGetValue(blockId, out List<IrInterBlockDependency>? dependencies)
                ? dependencies
                : Array.Empty<IrInterBlockDependency>();
        }

        /// <summary>
        /// Returns dependences targeting a specific instruction across block boundaries.
        /// </summary>
        public IReadOnlyList<IrInterBlockDependency> GetIncomingDependenciesForInstruction(int consumerInstructionIndex)
        {
            return _incomingInstructionDependencies.TryGetValue(consumerInstructionIndex, out List<IrInterBlockDependency>? dependencies)
                ? dependencies
                : Array.Empty<IrInterBlockDependency>();
        }

        /// <summary>
        /// Returns dependences sourced by a specific instruction across block boundaries.
        /// </summary>
        public IReadOnlyList<IrInterBlockDependency> GetOutgoingDependenciesForInstruction(int producerInstructionIndex)
        {
            return _outgoingInstructionDependencies.TryGetValue(producerInstructionIndex, out List<IrInterBlockDependency>? dependencies)
                ? dependencies
                : Array.Empty<IrInterBlockDependency>();
        }

        private static void Add<TKey>(Dictionary<TKey, List<IrInterBlockDependency>> map, TKey key, IrInterBlockDependency dependency)
            where TKey : notnull
        {
            if (!map.TryGetValue(key, out List<IrInterBlockDependency>? dependencies))
            {
                dependencies = new List<IrInterBlockDependency>();
                map[key] = dependencies;
            }

            dependencies.Add(dependency);
        }
    }
}
