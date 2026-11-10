using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    public sealed partial class HybridCpuIrBuilder
    {
        private static OwnedProgramArtifacts BuildProgramContainers(
            byte virtualThreadId,
            IReadOnlyList<IrBasicBlock> blocks,
            IReadOnlyList<IrProgramLabel> labels,
            IReadOnlyList<IrEntryPointMetadata> entryPoints,
            IReadOnlyList<IrSectionDeclaration>? sectionDeclarations,
            IReadOnlyList<IrFunctionDeclaration>? functionDeclarations)
        {
            if (blocks.Count == 0)
            {
                return new OwnedProgramArtifacts(
                    blocks,
                    labels,
                    entryPoints,
                    Array.Empty<IrSection>(),
                    Array.Empty<IrFunction>(),
                    new IrProgramSymbols(
                        labels,
                        entryPoints,
                        Array.Empty<IrSection>(),
                        Array.Empty<IrFunction>(),
                        Array.Empty<IrSectionSymbolGroup>(),
                        Array.Empty<IrFunctionSymbolGroup>()));
            }

            string sectionName = $"vt{virtualThreadId}_text";
            IReadOnlyList<IrFunction> functions = BuildFunctions(sectionName, blocks, labels, entryPoints, functionDeclarations);
            IReadOnlyList<IrBasicBlock> ownedBlocks = ApplyBlockOwnership(blocks, sectionName, functions);
            IReadOnlyList<IrProgramLabel> ownedLabels = ApplyLabelOwnership(labels, sectionName, functions);
            IReadOnlyList<IrEntryPointMetadata> ownedEntryPoints = ApplyEntryPointOwnership(entryPoints, sectionName, functions);
            IReadOnlyList<IrBasicBlock> labeledBlocks = ApplyBlockLabels(ownedBlocks, ownedLabels);
            IReadOnlyList<IrSection> sections = BuildSections(sectionName, virtualThreadId, labeledBlocks, ownedLabels, ownedEntryPoints, functions, sectionDeclarations);
            IrProgramSymbols symbols = BuildSymbols(labeledBlocks, ownedLabels, ownedEntryPoints, sections, functions);

            return new OwnedProgramArtifacts(labeledBlocks, ownedLabels, ownedEntryPoints, sections, functions, symbols);
        }

        private static IReadOnlyList<IrFunction> BuildFunctions(
            string sectionName,
            IReadOnlyList<IrBasicBlock> blocks,
            IReadOnlyList<IrProgramLabel> labels,
            IReadOnlyList<IrEntryPointMetadata> entryPoints,
            IReadOnlyList<IrFunctionDeclaration>? functionDeclarations)
        {
            List<EntryPointGroup> entryPointGroups = GroupEntryPointsByInstruction(entryPoints);
            if (entryPointGroups.Count == 0)
            {
                return Array.Empty<IrFunction>();
            }

            int programEndInstructionIndex = blocks[blocks.Count - 1].EndInstructionIndex;
            List<IrFunction> functions = new List<IrFunction>(entryPointGroups.Count);
            for (int index = 0; index < entryPointGroups.Count; index++)
            {
                EntryPointGroup group = entryPointGroups[index];
                int endInstructionIndex = index + 1 < entryPointGroups.Count
                    ? entryPointGroups[index + 1].InstructionIndex - 1
                    : programEndInstructionIndex;
                int entryBlockId = FindBlockIdForInstruction(blocks, group.InstructionIndex);
                IReadOnlyList<int> blockIds = CollectBlockIds(blocks, group.InstructionIndex, endInstructionIndex);
                IReadOnlyList<string> labelNames = CollectLabelNames(labels, group.InstructionIndex, endInstructionIndex);
                IReadOnlyList<string> entryPointNames = CollectEntryPointNames(group.EntryPoints);

                functions.Add(new IrFunction(
                    Name: SelectFunctionName(group.EntryPoints),
                    SectionName: sectionName,
                    EntryInstructionIndex: group.InstructionIndex,
                    EntryBlockId: entryBlockId,
                    EndInstructionIndex: endInstructionIndex,
                    BlockIds: blockIds,
                    LabelNames: labelNames,
                    EntryPointNames: entryPointNames,
                    IsSynthetic: AreAllSynthetic(group.EntryPoints),
                    SourceSpan: ResolveFunctionSourceSpan(functionDeclarations, sectionName, group.EntryPoints, group.InstructionIndex)));
            }

            return functions;
        }

        private static IReadOnlyList<IrBasicBlock> ApplyBlockOwnership(
            IReadOnlyList<IrBasicBlock> blocks,
            string sectionName,
            IReadOnlyList<IrFunction> functions)
        {
            List<IrBasicBlock> ownedBlocks = new List<IrBasicBlock>(blocks.Count);
            for (int index = 0; index < blocks.Count; index++)
            {
                string? functionName = FindOwningFunctionName(blocks[index].StartInstructionIndex, functions);
                ownedBlocks.Add(blocks[index] with
                {
                    SectionName = sectionName,
                    FunctionName = functionName
                });
            }

            return ownedBlocks;
        }

        private static IReadOnlyList<IrBasicBlock> ApplyBlockLabels(
            IReadOnlyList<IrBasicBlock> blocks,
            IReadOnlyList<IrProgramLabel> labels)
        {
            List<string>[] labelNamesByBlockId = new List<string>[blocks.Count];
            for (int index = 0; index < blocks.Count; index++)
            {
                labelNamesByBlockId[index] = new List<string>();
            }

            for (int index = 0; index < labels.Count; index++)
            {
                labelNamesByBlockId[labels[index].BlockId].Add(labels[index].Name);
            }

            List<IrBasicBlock> labeledBlocks = new List<IrBasicBlock>(blocks.Count);
            for (int index = 0; index < blocks.Count; index++)
            {
                IReadOnlyList<string> labelNames = labelNamesByBlockId[index].Count == 0
                    ? Array.Empty<string>()
                    : labelNamesByBlockId[index];
                labeledBlocks.Add(blocks[index] with { LabelNames = labelNames });
            }

            return labeledBlocks;
        }

        private static IReadOnlyList<IrProgramLabel> ApplyLabelOwnership(
            IReadOnlyList<IrProgramLabel> labels,
            string sectionName,
            IReadOnlyList<IrFunction> functions)
        {
            List<IrProgramLabel> ownedLabels = new List<IrProgramLabel>(labels.Count);
            for (int index = 0; index < labels.Count; index++)
            {
                string? functionName = FindOwningFunctionName(labels[index].InstructionIndex, functions);
                ownedLabels.Add(labels[index] with
                {
                    SectionName = sectionName,
                    FunctionName = functionName
                });
            }

            return ownedLabels;
        }

        private static IReadOnlyList<IrEntryPointMetadata> ApplyEntryPointOwnership(
            IReadOnlyList<IrEntryPointMetadata> entryPoints,
            string sectionName,
            IReadOnlyList<IrFunction> functions)
        {
            List<IrEntryPointMetadata> ownedEntryPoints = new List<IrEntryPointMetadata>(entryPoints.Count);
            for (int index = 0; index < entryPoints.Count; index++)
            {
                string? functionName = FindOwningFunctionName(entryPoints[index].InstructionIndex, functions);
                ownedEntryPoints.Add(entryPoints[index] with
                {
                    SectionName = sectionName,
                    FunctionName = functionName
                });
            }

            return ownedEntryPoints;
        }

        private static IReadOnlyList<IrSection> BuildSections(
            string sectionName,
            byte virtualThreadId,
            IReadOnlyList<IrBasicBlock> blocks,
            IReadOnlyList<IrProgramLabel> labels,
            IReadOnlyList<IrEntryPointMetadata> entryPoints,
            IReadOnlyList<IrFunction> functions,
            IReadOnlyList<IrSectionDeclaration>? sectionDeclarations)
        {
            List<int> blockIds = new List<int>(blocks.Count);
            for (int index = 0; index < blocks.Count; index++)
            {
                blockIds.Add(blocks[index].Id);
            }

            List<string> functionNames = new List<string>(functions.Count);
            for (int index = 0; index < functions.Count; index++)
            {
                functionNames.Add(functions[index].Name);
            }

            List<string> labelNames = new List<string>(labels.Count);
            for (int index = 0; index < labels.Count; index++)
            {
                labelNames.Add(labels[index].Name);
            }

            List<string> entryPointNames = new List<string>(entryPoints.Count);
            for (int index = 0; index < entryPoints.Count; index++)
            {
                entryPointNames.Add(entryPoints[index].Name);
            }

            return new[]
            {
                new IrSection(
                    Name: sectionName,
                    VirtualThreadId: virtualThreadId,
                    StartInstructionIndex: blocks[0].StartInstructionIndex,
                    EndInstructionIndex: blocks[blocks.Count - 1].EndInstructionIndex,
                    BlockIds: blockIds,
                    FunctionNames: functionNames,
                    LabelNames: labelNames,
                    EntryPointNames: entryPointNames,
                    IsSynthetic: true,
                    SourceSpan: ResolveSectionSourceSpan(sectionDeclarations, sectionName))
            };
        }

        private static IrSourceSpan? ResolveSectionSourceSpan(IReadOnlyList<IrSectionDeclaration>? sectionDeclarations, string sectionName)
        {
            if (sectionDeclarations is null)
            {
                return null;
            }

            for (int index = 0; index < sectionDeclarations.Count; index++)
            {
                if (string.Equals(sectionDeclarations[index].Name, sectionName, StringComparison.Ordinal))
                {
                    return sectionDeclarations[index].SourceSpan;
                }
            }

            return null;
        }

        private static IrSourceSpan? ResolveFunctionSourceSpan(
            IReadOnlyList<IrFunctionDeclaration>? functionDeclarations,
            string sectionName,
            IReadOnlyList<IrEntryPointMetadata> entryPoints,
            int entryInstructionIndex)
        {
            if (functionDeclarations is null)
            {
                return null;
            }

            string selectedName = SelectFunctionName(entryPoints);
            for (int index = 0; index < functionDeclarations.Count; index++)
            {
                IrFunctionDeclaration declaration = functionDeclarations[index];
                if (declaration.EntryInstructionIndex == entryInstructionIndex
                    && string.Equals(declaration.Name, selectedName, StringComparison.Ordinal)
                    && string.Equals(declaration.SectionName, sectionName, StringComparison.Ordinal))
                {
                    return declaration.SourceSpan;
                }
            }

            return null;
        }

        private static List<EntryPointGroup> GroupEntryPointsByInstruction(IReadOnlyList<IrEntryPointMetadata> entryPoints)
        {
            SortedDictionary<int, List<IrEntryPointMetadata>> groupsByInstruction = new SortedDictionary<int, List<IrEntryPointMetadata>>();
            for (int index = 0; index < entryPoints.Count; index++)
            {
                IrEntryPointMetadata entryPoint = entryPoints[index];
                if (!groupsByInstruction.TryGetValue(entryPoint.InstructionIndex, out List<IrEntryPointMetadata>? group))
                {
                    group = new List<IrEntryPointMetadata>();
                    groupsByInstruction.Add(entryPoint.InstructionIndex, group);
                }

                group.Add(entryPoint);
            }

            List<EntryPointGroup> groups = new List<EntryPointGroup>(groupsByInstruction.Count);
            foreach (KeyValuePair<int, List<IrEntryPointMetadata>> pair in groupsByInstruction)
            {
                groups.Add(new EntryPointGroup(pair.Key, pair.Value));
            }

            return groups;
        }

        private static int FindBlockIdForInstruction(IReadOnlyList<IrBasicBlock> blocks, int instructionIndex)
        {
            for (int index = 0; index < blocks.Count; index++)
            {
                if (instructionIndex >= blocks[index].StartInstructionIndex && instructionIndex <= blocks[index].EndInstructionIndex)
                {
                    return blocks[index].Id;
                }
            }

            throw new InvalidOperationException($"No IR basic block owns instruction index {instructionIndex}.");
        }

        private static IReadOnlyList<int> CollectBlockIds(IReadOnlyList<IrBasicBlock> blocks, int startInstructionIndex, int endInstructionIndex)
        {
            List<int> blockIds = new List<int>();
            for (int index = 0; index < blocks.Count; index++)
            {
                if (blocks[index].EndInstructionIndex < startInstructionIndex || blocks[index].StartInstructionIndex > endInstructionIndex)
                {
                    continue;
                }

                blockIds.Add(blocks[index].Id);
            }

            return blockIds;
        }

        private static IReadOnlyList<string> CollectLabelNames(IReadOnlyList<IrProgramLabel> labels, int startInstructionIndex, int endInstructionIndex)
        {
            List<string> labelNames = new List<string>();
            for (int index = 0; index < labels.Count; index++)
            {
                if (labels[index].InstructionIndex < startInstructionIndex || labels[index].InstructionIndex > endInstructionIndex)
                {
                    continue;
                }

                labelNames.Add(labels[index].Name);
            }

            return labelNames;
        }

        private static IReadOnlyList<string> CollectEntryPointNames(IReadOnlyList<IrEntryPointMetadata> entryPoints)
        {
            List<string> entryPointNames = new List<string>(entryPoints.Count);
            for (int index = 0; index < entryPoints.Count; index++)
            {
                entryPointNames.Add(entryPoints[index].Name);
            }

            return entryPointNames;
        }

        private static bool AreAllSynthetic(IReadOnlyList<IrEntryPointMetadata> entryPoints)
        {
            for (int index = 0; index < entryPoints.Count; index++)
            {
                if (!entryPoints[index].IsSynthetic)
                {
                    return false;
                }
            }

            return true;
        }

        private static string SelectFunctionName(IReadOnlyList<IrEntryPointMetadata> entryPoints)
        {
            for (int index = 0; index < entryPoints.Count; index++)
            {
                if (!entryPoints[index].IsSynthetic)
                {
                    return entryPoints[index].Name;
                }
            }

            return entryPoints[0].Name;
        }

        private static string? FindOwningFunctionName(int instructionIndex, IReadOnlyList<IrFunction> functions)
        {
            for (int index = functions.Count - 1; index >= 0; index--)
            {
                if (instructionIndex >= functions[index].EntryInstructionIndex && instructionIndex <= functions[index].EndInstructionIndex)
                {
                    return functions[index].Name;
                }
            }

            return null;
        }

        private sealed record OwnedProgramArtifacts(
            IReadOnlyList<IrBasicBlock> Blocks,
            IReadOnlyList<IrProgramLabel> Labels,
            IReadOnlyList<IrEntryPointMetadata> EntryPoints,
            IReadOnlyList<IrSection> Sections,
            IReadOnlyList<IrFunction> Functions,
            IrProgramSymbols Symbols);

        private sealed record EntryPointGroup(int InstructionIndex, IReadOnlyList<IrEntryPointMetadata> EntryPoints);
    }
}
