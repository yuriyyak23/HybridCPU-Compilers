using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Compiler-facing dependence query surface for one IR program.
    /// </summary>
    public sealed class IrProgramDependencyGraph
    {
        private readonly Dictionary<int, IrBasicBlockDependencyGraph> _blockGraphsById = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="IrProgramDependencyGraph"/> class.
        /// </summary>
        public IrProgramDependencyGraph(
            IReadOnlyList<IrBasicBlockDependencyGraph> BlockGraphs,
            IrInterBlockDependencyGraph InterBlockGraph)
        {
            ArgumentNullException.ThrowIfNull(BlockGraphs);
            ArgumentNullException.ThrowIfNull(InterBlockGraph);

            this.BlockGraphs = BlockGraphs;
            this.InterBlockGraph = InterBlockGraph;

            foreach (IrBasicBlockDependencyGraph graph in BlockGraphs)
            {
                _blockGraphsById[graph.BlockId] = graph;
            }
        }

        /// <summary>
        /// Gets intra-block dependence graphs for the program.
        /// </summary>
        public IReadOnlyList<IrBasicBlockDependencyGraph> BlockGraphs { get; }

        /// <summary>
        /// Gets inter-block dependences carried across CFG edges.
        /// </summary>
        public IrInterBlockDependencyGraph InterBlockGraph { get; }

        /// <summary>
        /// Tries to get a block-level dependence graph by block identifier.
        /// </summary>
        public bool TryGetBlockGraph(int blockId, out IrBasicBlockDependencyGraph? graph)
        {
            return _blockGraphsById.TryGetValue(blockId, out graph);
        }

        /// <summary>
        /// Returns all incoming dependences for an instruction across intra-block and inter-block views.
        /// </summary>
        public IReadOnlyList<IrInstructionDependency> GetIncomingDependencies(int consumerInstructionIndex)
        {
            List<IrInstructionDependency> dependencies = new List<IrInstructionDependency>();
            foreach (IrBasicBlockDependencyGraph graph in BlockGraphs)
            {
                dependencies.AddRange(graph.GetIncomingDependencies(consumerInstructionIndex));
            }

            foreach (IrInterBlockDependency dependency in InterBlockGraph.GetIncomingDependenciesForInstruction(consumerInstructionIndex))
            {
                dependencies.Add(dependency.Dependency);
            }

            return dependencies;
        }

        /// <summary>
        /// Returns all outgoing dependences for an instruction across intra-block and inter-block views.
        /// </summary>
        public IReadOnlyList<IrInstructionDependency> GetOutgoingDependencies(int producerInstructionIndex)
        {
            List<IrInstructionDependency> dependencies = new List<IrInstructionDependency>();
            foreach (IrBasicBlockDependencyGraph graph in BlockGraphs)
            {
                dependencies.AddRange(graph.GetOutgoingDependencies(producerInstructionIndex));
            }

            foreach (IrInterBlockDependency dependency in InterBlockGraph.GetOutgoingDependenciesForInstruction(producerInstructionIndex))
            {
                dependencies.Add(dependency.Dependency);
            }

            return dependencies;
        }

        /// <summary>
        /// Returns inter-block dependences carried along one CFG edge.
        /// </summary>
        public IReadOnlyList<IrInterBlockDependency> GetEdgeDependencies(int sourceBlockId, int targetBlockId)
        {
            return InterBlockGraph.GetDependenciesForEdge(sourceBlockId, targetBlockId);
        }
    }
}
