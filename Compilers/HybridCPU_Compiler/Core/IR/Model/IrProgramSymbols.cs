using System;
using System.Collections.Generic;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Groups flat IR symbol collections into explicit section and function ownership views.
    /// </summary>
    public sealed record IrProgramSymbols(
        IReadOnlyList<IrProgramLabel> Labels,
        IReadOnlyList<IrEntryPointMetadata> EntryPoints,
        IReadOnlyList<IrSection> Sections,
        IReadOnlyList<IrFunction> Functions,
        IReadOnlyList<IrSectionSymbolGroup> SectionGroups,
        IReadOnlyList<IrFunctionSymbolGroup> FunctionGroups)
    {
        /// <summary>
        /// Tries to find a label by its exact symbol name.
        /// </summary>
        public bool TryGetLabel(string name, out IrProgramLabel? label)
        {
            ValidateSymbolName(name, nameof(name));
            for (int index = 0; index < Labels.Count; index++)
            {
                if (string.Equals(Labels[index].Name, name, StringComparison.Ordinal))
                {
                    label = Labels[index];
                    return true;
                }
            }

            label = null;
            return false;
        }

        /// <summary>
        /// Tries to find an entry point by its exact symbol name.
        /// </summary>
        public bool TryGetEntryPoint(string name, out IrEntryPointMetadata? entryPoint)
        {
            ValidateSymbolName(name, nameof(name));
            for (int index = 0; index < EntryPoints.Count; index++)
            {
                if (string.Equals(EntryPoints[index].Name, name, StringComparison.Ordinal))
                {
                    entryPoint = EntryPoints[index];
                    return true;
                }
            }

            entryPoint = null;
            return false;
        }

        /// <summary>
        /// Tries to find a grouped section view by its exact section name.
        /// </summary>
        public bool TryGetSection(string name, out IrSectionSymbolGroup? sectionGroup)
        {
            ValidateSymbolName(name, nameof(name));
            for (int index = 0; index < SectionGroups.Count; index++)
            {
                if (string.Equals(SectionGroups[index].Section.Name, name, StringComparison.Ordinal))
                {
                    sectionGroup = SectionGroups[index];
                    return true;
                }
            }

            sectionGroup = null;
            return false;
        }

        /// <summary>
        /// Tries to find a grouped function view by its exact function name.
        /// </summary>
        public bool TryGetFunction(string name, out IrFunctionSymbolGroup? functionGroup)
        {
            ValidateSymbolName(name, nameof(name));
            for (int index = 0; index < FunctionGroups.Count; index++)
            {
                if (string.Equals(FunctionGroups[index].Function.Name, name, StringComparison.Ordinal))
                {
                    functionGroup = FunctionGroups[index];
                    return true;
                }
            }

            functionGroup = null;
            return false;
        }

        /// <summary>
        /// Tries to resolve a label reference within optional source section/function qualifiers.
        /// </summary>
        public bool TryResolveLabelReference(IrSourceSymbolReference reference, out IrProgramLabel? label)
        {
            ValidateReference(reference, nameof(reference));
            return TryResolveScopedSymbol(Labels, reference, static candidate => candidate.Name, static candidate => candidate.SectionName, static candidate => candidate.FunctionName, out label);
        }

        /// <summary>
        /// Tries to resolve an entry-point reference within optional source section/function qualifiers.
        /// </summary>
        public bool TryResolveEntryPointReference(IrSourceSymbolReference reference, out IrEntryPointMetadata? entryPoint)
        {
            ValidateReference(reference, nameof(reference));
            return TryResolveScopedSymbol(EntryPoints, reference, static candidate => candidate.Name, static candidate => candidate.SectionName, static candidate => candidate.FunctionName, out entryPoint);
        }

        /// <summary>
        /// Tries to resolve a section reference within an optional source section qualifier.
        /// </summary>
        public bool TryResolveSectionReference(IrSourceSymbolReference reference, out IrSectionSymbolGroup? sectionGroup)
        {
            ValidateReference(reference, nameof(reference));
            return TryResolveScopedSymbol(SectionGroups, reference, static candidate => candidate.Section.Name, static candidate => candidate.Section.Name, static _ => null, out sectionGroup);
        }

        /// <summary>
        /// Tries to resolve a function reference within an optional source section qualifier.
        /// </summary>
        public bool TryResolveFunctionReference(IrSourceSymbolReference reference, out IrFunctionSymbolGroup? functionGroup)
        {
            ValidateReference(reference, nameof(reference));
            return TryResolveScopedSymbol(FunctionGroups, reference, static candidate => candidate.Function.Name, static candidate => candidate.Function.SectionName, static candidate => candidate.Function.Name, out functionGroup);
        }

        private static void ValidateSymbolName(string name, string parameterName)
        {
            ArgumentNullException.ThrowIfNull(name);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Symbol name cannot be empty or whitespace.", parameterName);
            }
        }

        private static void ValidateReference(IrSourceSymbolReference reference, string parameterName)
        {
            ArgumentNullException.ThrowIfNull(reference);
            ValidateSymbolName(reference.Name, parameterName);
        }

        private static bool TryResolveScopedSymbol<TSymbol>(
            IReadOnlyList<TSymbol> symbols,
            IrSourceSymbolReference reference,
            Func<TSymbol, string> getName,
            Func<TSymbol, string?> getSectionName,
            Func<TSymbol, string?> getFunctionName,
            out TSymbol? resolvedSymbol)
            where TSymbol : class
        {
            TSymbol? match = null;
            for (int index = 0; index < symbols.Count; index++)
            {
                TSymbol candidate = symbols[index];
                if (!string.Equals(getName(candidate), reference.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                if (reference.SectionName is not null && !string.Equals(getSectionName(candidate), reference.SectionName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (reference.FunctionName is not null && !string.Equals(getFunctionName(candidate), reference.FunctionName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (match is not null)
                {
                    resolvedSymbol = null;
                    return false;
                }

                match = candidate;
            }

            resolvedSymbol = match;
            return resolvedSymbol is not null;
        }
    }
}
