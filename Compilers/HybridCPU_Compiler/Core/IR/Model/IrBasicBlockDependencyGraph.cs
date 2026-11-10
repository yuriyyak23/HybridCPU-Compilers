using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Aggregated dependence view for one basic block.
    /// </summary>
    public sealed class IrBasicBlockDependencyGraph
    {
        private readonly Dictionary<int, List<IrInstructionDependency>> _incomingDependencies = new();
        private readonly Dictionary<int, List<IrInstructionDependency>> _outgoingDependencies = new();
        private readonly Dictionary<IrInstructionDependencyKind, List<IrInstructionDependency>> _dependenciesByKind = new();
        private readonly Dictionary<IrHazardEffectKind, List<IrInstructionDependency>> _dependenciesByEffectKind = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="IrBasicBlockDependencyGraph"/> class.
        /// </summary>
        public IrBasicBlockDependencyGraph(
            int BlockId,
            IReadOnlyList<IrInstruction> Instructions,
            IReadOnlyList<IrInstructionDependency> Dependencies)
        {
            ArgumentNullException.ThrowIfNull(Instructions);
            ArgumentNullException.ThrowIfNull(Dependencies);

            this.BlockId = BlockId;
            this.Instructions = Instructions;
            this.Dependencies = Dependencies;

            foreach (IrInstructionDependency dependency in Dependencies)
            {
                Add(_incomingDependencies, dependency.ConsumerInstructionIndex, dependency);
                Add(_outgoingDependencies, dependency.ProducerInstructionIndex, dependency);
                Add(_dependenciesByKind, dependency.Kind, dependency);
                Add(_dependenciesByEffectKind, dependency.DominantEffectKind, dependency);
            }
        }

        /// <summary>
        /// Gets the block identifier.
        /// </summary>
        public int BlockId { get; }

        /// <summary>
        /// Gets the instructions covered by the block graph.
        /// </summary>
        public IReadOnlyList<IrInstruction> Instructions { get; }

        /// <summary>
        /// Gets the aggregated intra-block dependences.
        /// </summary>
        public IReadOnlyList<IrInstructionDependency> Dependencies { get; }

        /// <summary>
        /// Returns dependences entering a specific instruction in this block.
        /// </summary>
        public IReadOnlyList<IrInstructionDependency> GetIncomingDependencies(int consumerInstructionIndex)
        {
            return _incomingDependencies.TryGetValue(consumerInstructionIndex, out List<IrInstructionDependency>? dependencies)
                ? dependencies
                : Array.Empty<IrInstructionDependency>();
        }

        /// <summary>
        /// Returns dependences leaving a specific instruction in this block.
        /// </summary>
        public IReadOnlyList<IrInstructionDependency> GetOutgoingDependencies(int producerInstructionIndex)
        {
            return _outgoingDependencies.TryGetValue(producerInstructionIndex, out List<IrInstructionDependency>? dependencies)
                ? dependencies
                : Array.Empty<IrInstructionDependency>();
        }

        /// <summary>
        /// Returns dependences of one kind within the block.
        /// </summary>
        public IReadOnlyList<IrInstructionDependency> GetDependencies(IrInstructionDependencyKind kind)
        {
            return _dependenciesByKind.TryGetValue(kind, out List<IrInstructionDependency>? dependencies)
                ? dependencies
                : Array.Empty<IrInstructionDependency>();
        }

        /// <summary>
        /// Returns dependences classified by dominant typed effect within the block.
        /// </summary>
        public IReadOnlyList<IrInstructionDependency> GetDependencies(IrHazardEffectKind dominantEffectKind)
        {
            return _dependenciesByEffectKind.TryGetValue(dominantEffectKind, out List<IrInstructionDependency>? dependencies)
                ? dependencies
                : Array.Empty<IrInstructionDependency>();
        }

        private static void Add<TKey>(Dictionary<TKey, List<IrInstructionDependency>> map, TKey key, IrInstructionDependency dependency)
            where TKey : notnull
        {
            if (!map.TryGetValue(key, out List<IrInstructionDependency>? dependencies))
            {
                dependencies = new List<IrInstructionDependency>();
                map[key] = dependencies;
            }

            dependencies.Add(dependency);
        }
    }
}
