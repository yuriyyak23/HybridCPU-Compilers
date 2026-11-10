using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Builds scheduler-facing DAGs for basic blocks from the Stage 4 dependence query surface.
    /// </summary>
    public sealed class HybridCpuBasicBlockSchedulingDagBuilder
    {
        /// <summary>
        /// Builds a scheduler-facing DAG for one basic block.
        /// </summary>
        public IrBasicBlockSchedulingDag BuildBlockDag(IrBasicBlock block, IrProgramDependencyGraph dependencyGraph)
        {
            ArgumentNullException.ThrowIfNull(block);
            ArgumentNullException.ThrowIfNull(dependencyGraph);

            if (!dependencyGraph.TryGetBlockGraph(block.Id, out IrBasicBlockDependencyGraph? blockGraph) || blockGraph is null)
            {
                throw new InvalidOperationException($"Program dependence graph does not contain block {block.Id}.");
            }

            Dictionary<int, int> criticalPathByInstruction = BuildCriticalPathMap(blockGraph);
            List<IrSchedulingNode> nodes = new List<IrSchedulingNode>(blockGraph.Instructions.Count);
            foreach (IrInstruction instruction in blockGraph.Instructions)
            {
                IReadOnlyList<IrInstructionDependency> incomingDependencies = blockGraph.GetIncomingDependencies(instruction.Index);
                IReadOnlyList<IrInstructionDependency> outgoingDependencies = blockGraph.GetOutgoingDependencies(instruction.Index);
                nodes.Add(new IrSchedulingNode(
                    InstructionIndex: instruction.Index,
                    Instruction: instruction,
                    IncomingDependencies: incomingDependencies,
                    OutgoingDependencies: outgoingDependencies,
                    CriticalPathLengthCycles: criticalPathByInstruction[instruction.Index]));
            }

            return new IrBasicBlockSchedulingDag(block.Id, blockGraph.Instructions, nodes, blockGraph.Dependencies);
        }

        private static Dictionary<int, int> BuildCriticalPathMap(IrBasicBlockDependencyGraph blockGraph)
        {
            Dictionary<int, int> criticalPathByInstruction = new Dictionary<int, int>(blockGraph.Instructions.Count);
            for (int index = blockGraph.Instructions.Count - 1; index >= 0; index--)
            {
                IrInstruction instruction = blockGraph.Instructions[index];
                int criticalPathLengthCycles = 1;
                foreach (IrInstructionDependency dependency in blockGraph.GetOutgoingDependencies(instruction.Index))
                {
                    if (!criticalPathByInstruction.TryGetValue(dependency.ConsumerInstructionIndex, out int consumerCriticalPathLengthCycles))
                    {
                        consumerCriticalPathLengthCycles = 1;
                    }

                    criticalPathLengthCycles = Math.Max(
                        criticalPathLengthCycles,
                        dependency.MinimumLatencyCycles + consumerCriticalPathLengthCycles);
                }

                criticalPathByInstruction[instruction.Index] = criticalPathLengthCycles;
            }

            return criticalPathByInstruction;
        }
    }
}
