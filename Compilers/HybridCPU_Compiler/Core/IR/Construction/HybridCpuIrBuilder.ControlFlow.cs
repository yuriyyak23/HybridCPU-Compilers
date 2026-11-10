using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    public sealed partial class HybridCpuIrBuilder
    {
        private static ControlFlowGraph BuildControlFlowGraph(
            List<IrInstruction> instructions,
            IReadOnlyList<IrLabelDeclaration>? labelDeclarations,
            IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations,
            IReadOnlyList<IrControlFlowTargetReference>? controlFlowTargetReferences)
        {
            ResolveSymbolicControlFlowTargets(
                instructions,
                labelDeclarations,
                entryPointDeclarations,
                controlFlowTargetReferences);
            ResolveBranchTargets(instructions);

            List<IrBasicBlock> blocks = BuildBasicBlocks(instructions, labelDeclarations, entryPointDeclarations);
            List<IrControlFlowEdge> edges = BuildEdges(blocks);
            IReadOnlyList<IrBasicBlock> enrichedBlocks = EnrichBlocks(blocks, edges);

            return new ControlFlowGraph(enrichedBlocks, edges);
        }

        private static void ResolveSymbolicControlFlowTargets(
            List<IrInstruction> instructions,
            IReadOnlyList<IrLabelDeclaration>? labelDeclarations,
            IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations,
            IReadOnlyList<IrControlFlowTargetReference>? controlFlowTargetReferences)
        {
            if (controlFlowTargetReferences is null || controlFlowTargetReferences.Count == 0)
            {
                return;
            }

            Dictionary<string, int> symbolMap = BuildControlFlowSymbolMap(
                labelDeclarations,
                entryPointDeclarations);

            for (int index = 0; index < controlFlowTargetReferences.Count; index++)
            {
                IrControlFlowTargetReference reference = controlFlowTargetReferences[index];
                ValidateMetadataInstructionIndex(
                    reference.InstructionIndex,
                    instructions.Count,
                    $"control-flow target reference '{reference.TargetName}'");

                IrInstruction instruction = instructions[reference.InstructionIndex];
                if (instruction.Annotation.ControlFlowKind is not (
                        IrControlFlowKind.ConditionalBranch or
                        IrControlFlowKind.UnconditionalBranch))
                {
                    throw new InvalidOperationException(
                        $"Symbolic control-flow target '{reference.TargetName}' is attached to non-branch opcode '{instruction.Opcode}'.");
                }

                if (!symbolMap.TryGetValue(reference.TargetName, out int targetInstructionIndex))
                {
                    throw new InvalidOperationException(
                        $"Unresolved symbolic control-flow target '{reference.TargetName}' for instruction {reference.InstructionIndex}.");
                }

                IrInstructionAnnotation annotation = instruction.Annotation;
                instructions[reference.InstructionIndex] = instruction with
                {
                    Annotation = annotation with
                    {
                        BranchTargetSymbolName = reference.TargetName,
                        EncodedBranchTarget = checked((ulong)targetInstructionIndex * EncodedInstructionSizeBytes),
                        ResolvedBranchTargetInstructionIndex = targetInstructionIndex
                    }
                };
            }
        }

        private static Dictionary<string, int> BuildControlFlowSymbolMap(
            IReadOnlyList<IrLabelDeclaration>? labelDeclarations,
            IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations)
        {
            Dictionary<string, int> symbolMap = new Dictionary<string, int>(StringComparer.Ordinal);
            AddControlFlowSymbols(symbolMap, labelDeclarations);
            AddControlFlowSymbols(symbolMap, entryPointDeclarations);
            return symbolMap;
        }

        private static void AddControlFlowSymbols(
            IDictionary<string, int> symbolMap,
            IReadOnlyList<IrLabelDeclaration>? labelDeclarations)
        {
            if (labelDeclarations is null)
            {
                return;
            }

            for (int index = 0; index < labelDeclarations.Count; index++)
            {
                AddControlFlowSymbol(
                    symbolMap,
                    labelDeclarations[index].Name,
                    labelDeclarations[index].InstructionIndex);
            }
        }

        private static void AddControlFlowSymbols(
            IDictionary<string, int> symbolMap,
            IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations)
        {
            if (entryPointDeclarations is null)
            {
                return;
            }

            for (int index = 0; index < entryPointDeclarations.Count; index++)
            {
                AddControlFlowSymbol(
                    symbolMap,
                    entryPointDeclarations[index].Name,
                    entryPointDeclarations[index].InstructionIndex);
            }
        }

        private static void AddControlFlowSymbol(
            IDictionary<string, int> symbolMap,
            string name,
            int instructionIndex)
        {
            if (symbolMap.TryGetValue(name, out int existingInstructionIndex))
            {
                if (existingInstructionIndex != instructionIndex)
                {
                    throw new InvalidOperationException(
                        $"Control-flow symbol '{name}' is ambiguous across instruction indices {existingInstructionIndex} and {instructionIndex}.");
                }

                return;
            }

            symbolMap.Add(name, instructionIndex);
        }

        private static void ResolveBranchTargets(List<IrInstruction> instructions)
        {
            for (int index = 0; index < instructions.Count; index++)
            {
                IrInstruction instruction = instructions[index];
                IrInstructionAnnotation annotation = instruction.Annotation;
                if (!annotation.EncodedBranchTarget.HasValue)
                {
                    continue;
                }

                int? resolvedTargetIndex = TryResolveInstructionIndex(annotation.EncodedBranchTarget.Value, instructions.Count);
                if (resolvedTargetIndex == annotation.ResolvedBranchTargetInstructionIndex)
                {
                    continue;
                }

                instructions[index] = instruction with
                {
                    Annotation = annotation with
                    {
                        ResolvedBranchTargetInstructionIndex = resolvedTargetIndex
                    }
                };
            }
        }

        private static List<IrBasicBlock> BuildBasicBlocks(
            IReadOnlyList<IrInstruction> instructions,
            IReadOnlyList<IrLabelDeclaration>? labelDeclarations,
            IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations)
        {
            List<IrBasicBlock> blocks = new List<IrBasicBlock>();
            if (instructions.Count == 0)
            {
                return blocks;
            }

            List<int> leaders = CollectLeaderIndices(instructions, labelDeclarations, entryPointDeclarations);
            for (int leaderIndex = 0; leaderIndex < leaders.Count; leaderIndex++)
            {
                int startIndex = leaders[leaderIndex];
                int endIndex = leaderIndex + 1 < leaders.Count ? leaders[leaderIndex + 1] - 1 : instructions.Count - 1;
                blocks.Add(CreateBasicBlock(leaderIndex, startIndex, endIndex, instructions));
            }

            return blocks;
        }

        private static List<int> CollectLeaderIndices(
            IReadOnlyList<IrInstruction> instructions,
            IReadOnlyList<IrLabelDeclaration>? labelDeclarations,
            IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations)
        {
            SortedSet<int> leaders = new SortedSet<int> { 0 };
            for (int index = 0; index < instructions.Count; index++)
            {
                IrInstruction instruction = instructions[index];
                if (instruction.Annotation.ResolvedBranchTargetInstructionIndex.HasValue)
                {
                    leaders.Add(instruction.Annotation.ResolvedBranchTargetInstructionIndex.Value);
                }

                if (IsBlockTerminator(instruction) && index + 1 < instructions.Count)
                {
                    leaders.Add(index + 1);
                }
            }

            AddDeclaredLeaders(leaders, labelDeclarations);
            AddDeclaredEntryLeaders(leaders, entryPointDeclarations);

            return new List<int>(leaders);
        }

        private static void AddDeclaredLeaders(SortedSet<int> leaders, IReadOnlyList<IrLabelDeclaration>? labelDeclarations)
        {
            if (labelDeclarations is null)
            {
                return;
            }

            for (int index = 0; index < labelDeclarations.Count; index++)
            {
                leaders.Add(labelDeclarations[index].InstructionIndex);
            }
        }

        private static void AddDeclaredEntryLeaders(SortedSet<int> leaders, IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations)
        {
            if (entryPointDeclarations is null)
            {
                return;
            }

            for (int index = 0; index < entryPointDeclarations.Count; index++)
            {
                leaders.Add(entryPointDeclarations[index].InstructionIndex);
            }
        }

        private static IrBasicBlock CreateBasicBlock(int blockId, int startIndex, int endIndex, IReadOnlyList<IrInstruction> instructions)
        {
            List<IrInstruction> blockInstructions = new List<IrInstruction>(endIndex - startIndex + 1);
            for (int index = startIndex; index <= endIndex; index++)
            {
                blockInstructions.Add(instructions[index]);
            }

            IrInstruction terminator = blockInstructions[blockInstructions.Count - 1];
            bool hasUnresolvedControlTransfer = terminator.Annotation.ControlFlowKind is IrControlFlowKind.ConditionalBranch or IrControlFlowKind.UnconditionalBranch
                && (terminator.Annotation.EncodedBranchTarget.HasValue ||
                    terminator.Annotation.BranchTargetSymbolName is not null)
                && !terminator.Annotation.ResolvedBranchTargetInstructionIndex.HasValue;

            return new IrBasicBlock(
                Id: blockId,
                StartInstructionIndex: startIndex,
                EndInstructionIndex: endIndex,
                StartAddress: blockInstructions[0].EncodedAddress,
                EndAddress: blockInstructions[blockInstructions.Count - 1].EncodedAddress,
                HasUnresolvedControlTransfer: hasUnresolvedControlTransfer,
                Instructions: blockInstructions,
                PredecessorBlockIds: Array.Empty<int>(),
                SuccessorBlockIds: Array.Empty<int>(),
                ExitBlock: false,
                BarrierBoundary: terminator.Annotation.IsBarrierLike,
                PrimaryLabel: null,
                LabelNames: Array.Empty<string>(),
                SectionName: null,
                FunctionName: null,
                SourceSpan: CreateBlockSourceSpan(blockInstructions));
        }

        private static List<IrControlFlowEdge> BuildEdges(IReadOnlyList<IrBasicBlock> blocks)
        {
            List<IrControlFlowEdge> edges = new List<IrControlFlowEdge>();
            if (blocks.Count == 0)
            {
                return edges;
            }

            int[] blockIdByInstructionIndex = BuildInstructionToBlockMap(blocks);

            for (int index = 0; index < blocks.Count; index++)
            {
                IrBasicBlock currentBlock = blocks[index];
                IrInstruction terminator = currentBlock.Instructions[currentBlock.Instructions.Count - 1];

                if (HasFallthroughEdge(terminator.Annotation.ControlFlowKind) && index + 1 < blocks.Count)
                {
                    edges.Add(new IrControlFlowEdge(currentBlock.Id, blocks[index + 1].Id, IrControlFlowEdgeKind.Fallthrough));
                }

                int? targetInstructionIndex = terminator.Annotation.ResolvedBranchTargetInstructionIndex;
                if (!targetInstructionIndex.HasValue)
                {
                    continue;
                }

                int targetBlockId = blockIdByInstructionIndex[targetInstructionIndex.Value];
                if (targetBlockId < 0)
                {
                    continue;
                }

                edges.Add(new IrControlFlowEdge(currentBlock.Id, targetBlockId, IrControlFlowEdgeKind.Branch));
            }

            return edges;
        }

        private static IReadOnlyList<IrBasicBlock> EnrichBlocks(IReadOnlyList<IrBasicBlock> blocks, IReadOnlyList<IrControlFlowEdge> edges)
        {
            HashSet<int>[] predecessorSets = new HashSet<int>[blocks.Count];
            HashSet<int>[] successorSets = new HashSet<int>[blocks.Count];
            for (int index = 0; index < blocks.Count; index++)
            {
                predecessorSets[index] = new HashSet<int>();
                successorSets[index] = new HashSet<int>();
            }

            foreach (IrControlFlowEdge edge in edges)
            {
                successorSets[edge.SourceBlockId].Add(edge.TargetBlockId);
                predecessorSets[edge.TargetBlockId].Add(edge.SourceBlockId);
            }

            List<IrBasicBlock> enrichedBlocks = new List<IrBasicBlock>(blocks.Count);
            for (int index = 0; index < blocks.Count; index++)
            {
                IrBasicBlock block = blocks[index];
                enrichedBlocks.Add(block with
                {
                    PredecessorBlockIds = CreateSortedList(predecessorSets[index]),
                    SuccessorBlockIds = CreateSortedList(successorSets[index]),
                    ExitBlock = successorSets[index].Count == 0
                });
            }

            return enrichedBlocks;
        }

        private static int[] BuildInstructionToBlockMap(IReadOnlyList<IrBasicBlock> blocks)
        {
            if (blocks.Count == 0)
            {
                return Array.Empty<int>();
            }

            int maxInstructionIndex = blocks[blocks.Count - 1].EndInstructionIndex;
            int[] map = new int[maxInstructionIndex + 1];
            Array.Fill(map, -1);

            foreach (IrBasicBlock block in blocks)
            {
                for (int instructionIndex = block.StartInstructionIndex; instructionIndex <= block.EndInstructionIndex; instructionIndex++)
                {
                    map[instructionIndex] = block.Id;
                }
            }

            return map;
        }

        private static int? TryResolveInstructionIndex(ulong encodedTarget, int instructionCount)
        {
            if (instructionCount <= 0)
            {
                return null;
            }

            if (encodedTarget % EncodedInstructionSizeBytes == 0)
            {
                ulong instructionIndex = encodedTarget / EncodedInstructionSizeBytes;
                if (instructionIndex < (ulong)instructionCount)
                {
                    return (int)instructionIndex;
                }
            }

            if (encodedTarget < (ulong)instructionCount)
            {
                return (int)encodedTarget;
            }

            return null;
        }

        private static IReadOnlyList<int> CreateSortedList(HashSet<int> values)
        {
            if (values.Count == 0)
            {
                return Array.Empty<int>();
            }

            List<int> ordered = new List<int>(values);
            ordered.Sort();
            return ordered;
        }

        private static IrSourceSpan? CreateBlockSourceSpan(IReadOnlyList<IrInstruction> blockInstructions)
        {
            if (blockInstructions.Count == 0)
            {
                return null;
            }

            IrSourceSpan? first = blockInstructions[0].SourceSpan;
            IrSourceSpan? last = blockInstructions[blockInstructions.Count - 1].SourceSpan;
            if (first is null || last is null)
            {
                return null;
            }

            for (int index = 1; index < blockInstructions.Count; index++)
            {
                IrSourceSpan? current = blockInstructions[index].SourceSpan;
                if (current is null || !string.Equals(current.DocumentName, first.DocumentName, StringComparison.Ordinal))
                {
                    return null;
                }
            }

            return new IrSourceSpan(
                first.DocumentName,
                first.StartLine,
                first.StartColumn,
                last.EndLine,
                last.EndColumn,
                first.StartOffset,
                last.EndOffset - first.StartOffset);
        }
    }
}
