using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    public sealed partial class HybridCpuIrBuilder
    {
        private static IrProgramSymbols BuildSymbols(
            IReadOnlyList<IrBasicBlock> blocks,
            IReadOnlyList<IrProgramLabel> labels,
            IReadOnlyList<IrEntryPointMetadata> entryPoints,
            IReadOnlyList<IrSection> sections,
            IReadOnlyList<IrFunction> functions)
        {
            IReadOnlyList<IrSectionSymbolGroup> sectionGroups = BuildSectionGroups(blocks, labels, entryPoints, sections, functions);
            IReadOnlyList<IrFunctionSymbolGroup> functionGroups = BuildFunctionGroups(blocks, labels, entryPoints, functions);
            return new IrProgramSymbols(labels, entryPoints, sections, functions, sectionGroups, functionGroups);
        }

        private static IReadOnlyList<IrSectionSymbolGroup> BuildSectionGroups(
            IReadOnlyList<IrBasicBlock> blocks,
            IReadOnlyList<IrProgramLabel> labels,
            IReadOnlyList<IrEntryPointMetadata> entryPoints,
            IReadOnlyList<IrSection> sections,
            IReadOnlyList<IrFunction> functions)
        {
            if (sections.Count == 0)
            {
                return Array.Empty<IrSectionSymbolGroup>();
            }

            List<IrSectionSymbolGroup> sectionGroups = new List<IrSectionSymbolGroup>(sections.Count);
            for (int index = 0; index < sections.Count; index++)
            {
                IrSection section = sections[index];
                sectionGroups.Add(new IrSectionSymbolGroup(
                    Section: section,
                    Blocks: CollectBlocksForSection(blocks, section.Name),
                    Functions: CollectFunctionsForSection(functions, section.Name),
                    Labels: CollectLabelsForSection(labels, section.Name),
                    EntryPoints: CollectEntryPointsForSection(entryPoints, section.Name)));
            }

            return sectionGroups;
        }

        private static IReadOnlyList<IrFunctionSymbolGroup> BuildFunctionGroups(
            IReadOnlyList<IrBasicBlock> blocks,
            IReadOnlyList<IrProgramLabel> labels,
            IReadOnlyList<IrEntryPointMetadata> entryPoints,
            IReadOnlyList<IrFunction> functions)
        {
            if (functions.Count == 0)
            {
                return Array.Empty<IrFunctionSymbolGroup>();
            }

            List<IrFunctionSymbolGroup> functionGroups = new List<IrFunctionSymbolGroup>(functions.Count);
            for (int index = 0; index < functions.Count; index++)
            {
                IrFunction function = functions[index];
                functionGroups.Add(new IrFunctionSymbolGroup(
                    Function: function,
                    Blocks: CollectBlocksForFunction(blocks, function.Name),
                    Labels: CollectLabelsForFunction(labels, function.Name),
                    EntryPoints: CollectEntryPointsForFunction(entryPoints, function.Name)));
            }

            return functionGroups;
        }

        private static IReadOnlyList<IrBasicBlock> CollectBlocksForSection(IReadOnlyList<IrBasicBlock> blocks, string sectionName)
        {
            List<IrBasicBlock> sectionBlocks = new List<IrBasicBlock>();
            for (int index = 0; index < blocks.Count; index++)
            {
                if (string.Equals(blocks[index].SectionName, sectionName, StringComparison.Ordinal))
                {
                    sectionBlocks.Add(blocks[index]);
                }
            }

            return sectionBlocks;
        }

        private static IReadOnlyList<IrFunction> CollectFunctionsForSection(IReadOnlyList<IrFunction> functions, string sectionName)
        {
            List<IrFunction> sectionFunctions = new List<IrFunction>();
            for (int index = 0; index < functions.Count; index++)
            {
                if (string.Equals(functions[index].SectionName, sectionName, StringComparison.Ordinal))
                {
                    sectionFunctions.Add(functions[index]);
                }
            }

            return sectionFunctions;
        }

        private static IReadOnlyList<IrProgramLabel> CollectLabelsForSection(IReadOnlyList<IrProgramLabel> labels, string sectionName)
        {
            List<IrProgramLabel> sectionLabels = new List<IrProgramLabel>();
            for (int index = 0; index < labels.Count; index++)
            {
                if (string.Equals(labels[index].SectionName, sectionName, StringComparison.Ordinal))
                {
                    sectionLabels.Add(labels[index]);
                }
            }

            return sectionLabels;
        }

        private static IReadOnlyList<IrEntryPointMetadata> CollectEntryPointsForSection(IReadOnlyList<IrEntryPointMetadata> entryPoints, string sectionName)
        {
            List<IrEntryPointMetadata> sectionEntryPoints = new List<IrEntryPointMetadata>();
            for (int index = 0; index < entryPoints.Count; index++)
            {
                if (string.Equals(entryPoints[index].SectionName, sectionName, StringComparison.Ordinal))
                {
                    sectionEntryPoints.Add(entryPoints[index]);
                }
            }

            return sectionEntryPoints;
        }

        private static IReadOnlyList<IrBasicBlock> CollectBlocksForFunction(IReadOnlyList<IrBasicBlock> blocks, string functionName)
        {
            List<IrBasicBlock> functionBlocks = new List<IrBasicBlock>();
            for (int index = 0; index < blocks.Count; index++)
            {
                if (string.Equals(blocks[index].FunctionName, functionName, StringComparison.Ordinal))
                {
                    functionBlocks.Add(blocks[index]);
                }
            }

            return functionBlocks;
        }

        private static IReadOnlyList<IrProgramLabel> CollectLabelsForFunction(IReadOnlyList<IrProgramLabel> labels, string functionName)
        {
            List<IrProgramLabel> functionLabels = new List<IrProgramLabel>();
            for (int index = 0; index < labels.Count; index++)
            {
                if (string.Equals(labels[index].FunctionName, functionName, StringComparison.Ordinal))
                {
                    functionLabels.Add(labels[index]);
                }
            }

            return functionLabels;
        }

        private static IReadOnlyList<IrEntryPointMetadata> CollectEntryPointsForFunction(IReadOnlyList<IrEntryPointMetadata> entryPoints, string functionName)
        {
            List<IrEntryPointMetadata> functionEntryPoints = new List<IrEntryPointMetadata>();
            for (int index = 0; index < entryPoints.Count; index++)
            {
                if (string.Equals(entryPoints[index].FunctionName, functionName, StringComparison.Ordinal))
                {
                    functionEntryPoints.Add(entryPoints[index]);
                }
            }

            return functionEntryPoints;
        }
    }
}
