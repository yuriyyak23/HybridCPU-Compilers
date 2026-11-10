using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    public sealed partial class HybridCpuIrBuilder
    {
        private static IReadOnlyList<IrProgramLabel> BuildLabels(
            byte virtualThreadId,
            IReadOnlyList<IrInstruction> instructions,
            IReadOnlyList<IrBasicBlock> blocks,
            IReadOnlyList<IrLabelDeclaration>? labelDeclarations,
            IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations)
        {
            if (instructions.Count == 0)
            {
                return Array.Empty<IrProgramLabel>();
            }

            int[] blockMap = BuildInstructionToBlockMap(blocks);
            List<IrProgramLabel> labels = new List<IrProgramLabel>();
            HashSet<int> occupiedInstructionIndices = new HashSet<int>();

            AddDeclaredLabels(labels, occupiedInstructionIndices, instructions, blockMap, labelDeclarations);
            AddSyntheticEntryLabel(virtualThreadId, labels, occupiedInstructionIndices, instructions, blockMap);
            AddSyntheticBranchTargetLabels(labels, occupiedInstructionIndices, instructions, blockMap, entryPointDeclarations);

            return labels;
        }

        private static IReadOnlyList<IrBasicBlock> ApplyPrimaryLabels(IReadOnlyList<IrBasicBlock> blocks, IReadOnlyList<IrProgramLabel> labels)
        {
            if (blocks.Count == 0 || labels.Count == 0)
            {
                return blocks;
            }

            string?[] primaryLabels = new string?[blocks.Count];
            bool[] hasExplicitLabel = new bool[blocks.Count];
            for (int index = 0; index < labels.Count; index++)
            {
                IrProgramLabel label = labels[index];
                IrBasicBlock block = blocks[label.BlockId];
                if (label.InstructionIndex != block.StartInstructionIndex)
                {
                    continue;
                }

                if (primaryLabels[label.BlockId] is null || (!label.IsSynthetic && !hasExplicitLabel[label.BlockId]))
                {
                    primaryLabels[label.BlockId] = label.Name;
                    hasExplicitLabel[label.BlockId] = !label.IsSynthetic;
                }
            }

            List<IrBasicBlock> updatedBlocks = new List<IrBasicBlock>(blocks.Count);
            for (int index = 0; index < blocks.Count; index++)
            {
                updatedBlocks.Add(blocks[index] with { PrimaryLabel = primaryLabels[index] });
            }

            return updatedBlocks;
        }

        private static IReadOnlyList<IrEntryPointMetadata> BuildEntryPoints(
            byte virtualThreadId,
            IReadOnlyList<IrInstruction> instructions,
            IReadOnlyList<IrBasicBlock> blocks,
            IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations)
        {
            if (instructions.Count == 0)
            {
                return Array.Empty<IrEntryPointMetadata>();
            }

            int[] blockMap = BuildInstructionToBlockMap(blocks);
            List<IrEntryPointMetadata> entryPoints = new List<IrEntryPointMetadata>
            {
                CreateEntryPoint($"entry_vt{virtualThreadId}", IrEntryPointKind.ProgramEntry, 0, instructions, blockMap, isSynthetic: true, sourceSpan: null)
            };

            if (entryPointDeclarations is null)
            {
                return entryPoints;
            }

            for (int index = 0; index < entryPointDeclarations.Count; index++)
            {
                IrEntryPointDeclaration declaration = entryPointDeclarations[index];
                entryPoints.Add(CreateEntryPoint(declaration.Name, declaration.Kind, declaration.InstructionIndex, instructions, blockMap, isSynthetic: false, declaration.SourceSpan));
            }

            return entryPoints;
        }

        private static void AddDeclaredLabels(
            ICollection<IrProgramLabel> labels,
            ISet<int> occupiedInstructionIndices,
            IReadOnlyList<IrInstruction> instructions,
            IReadOnlyList<int> blockMap,
            IReadOnlyList<IrLabelDeclaration>? labelDeclarations)
        {
            if (labelDeclarations is null)
            {
                return;
            }

            for (int index = 0; index < labelDeclarations.Count; index++)
            {
                IrLabelDeclaration declaration = labelDeclarations[index];
                labels.Add(CreateLabel(declaration.Name, declaration.InstructionIndex, instructions, blockMap, isSynthetic: false, isEntryLabel: false, declaration.SourceSpan));
                occupiedInstructionIndices.Add(declaration.InstructionIndex);
            }
        }

        private static void AddSyntheticEntryLabel(
            byte virtualThreadId,
            ICollection<IrProgramLabel> labels,
            ISet<int> occupiedInstructionIndices,
            IReadOnlyList<IrInstruction> instructions,
            IReadOnlyList<int> blockMap)
        {
            if (occupiedInstructionIndices.Contains(0))
            {
                return;
            }

            labels.Add(CreateLabel($"entry_vt{virtualThreadId}", 0, instructions, blockMap, isSynthetic: true, isEntryLabel: true, sourceSpan: null));
            occupiedInstructionIndices.Add(0);
        }

        private static void AddSyntheticBranchTargetLabels(
            ICollection<IrProgramLabel> labels,
            ISet<int> occupiedInstructionIndices,
            IReadOnlyList<IrInstruction> instructions,
            IReadOnlyList<int> blockMap,
            IReadOnlyList<IrEntryPointDeclaration>? entryPointDeclarations)
        {
            SortedSet<int> targetInstructionIndices = new SortedSet<int>();
            for (int index = 0; index < instructions.Count; index++)
            {
                int? targetIndex = instructions[index].Annotation.ResolvedBranchTargetInstructionIndex;
                if (targetIndex.HasValue)
                {
                    targetInstructionIndices.Add(targetIndex.Value);
                }
            }

            if (entryPointDeclarations is not null)
            {
                for (int index = 0; index < entryPointDeclarations.Count; index++)
                {
                    targetInstructionIndices.Add(entryPointDeclarations[index].InstructionIndex);
                }
            }

            foreach (int instructionIndex in targetInstructionIndices)
            {
                if (occupiedInstructionIndices.Contains(instructionIndex))
                {
                    continue;
                }

                ulong address = instructions[instructionIndex].EncodedAddress;
                labels.Add(CreateLabel($"L_{address:X4}", instructionIndex, instructions, blockMap, isSynthetic: true, isEntryLabel: false, sourceSpan: null));
                occupiedInstructionIndices.Add(instructionIndex);
            }
        }

        private static IrProgramLabel CreateLabel(
            string name,
            int instructionIndex,
            IReadOnlyList<IrInstruction> instructions,
            IReadOnlyList<int> blockMap,
            bool isSynthetic,
            bool isEntryLabel,
            IrSourceSpan? sourceSpan)
        {
            ValidateMetadataInstructionIndex(instructionIndex, instructions.Count, name);
            return new IrProgramLabel(
                Name: name,
                InstructionIndex: instructionIndex,
                Address: instructions[instructionIndex].EncodedAddress,
                BlockId: blockMap[instructionIndex],
                IsSynthetic: isSynthetic,
                IsEntryLabel: isEntryLabel,
                SectionName: null,
                FunctionName: null,
                SourceSpan: sourceSpan);
        }

        private static IrEntryPointMetadata CreateEntryPoint(
            string name,
            IrEntryPointKind kind,
            int instructionIndex,
            IReadOnlyList<IrInstruction> instructions,
            IReadOnlyList<int> blockMap,
            bool isSynthetic,
            IrSourceSpan? sourceSpan)
        {
            ValidateMetadataInstructionIndex(instructionIndex, instructions.Count, name);
            return new IrEntryPointMetadata(
                Name: name,
                Kind: kind,
                InstructionIndex: instructionIndex,
                Address: instructions[instructionIndex].EncodedAddress,
                BlockId: blockMap[instructionIndex],
                IsSynthetic: isSynthetic,
                SectionName: null,
                FunctionName: null,
                SourceSpan: sourceSpan);
        }

        private static void ValidateMetadataInstructionIndex(int instructionIndex, int instructionCount, string metadataName)
        {
            if ((uint)instructionIndex >= (uint)instructionCount)
            {
                throw new InvalidOperationException($"IR metadata '{metadataName}' points outside the current instruction stream.");
            }
        }
    }
}
