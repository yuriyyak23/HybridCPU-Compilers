using System;
using System.Collections.Generic;
using HybridCPU.Compiler.Core.IR;

namespace HybridCPU.Compiler.Core.Threading
{
    public partial class HybridCpuThreadCompilerContext
    {
        private readonly List<IrLabelDeclaration> _labelDeclarations = new List<IrLabelDeclaration>();
        private readonly List<IrEntryPointDeclaration> _entryPointDeclarations = new List<IrEntryPointDeclaration>();
        private readonly List<IrControlFlowTargetReference> _controlFlowTargetReferences = new List<IrControlFlowTargetReference>();

        /// <summary>
        /// Declares a named IR label for an instruction in the current VT-local stream.
        /// </summary>
        public void DeclareLabel(string name, int instructionIndex)
        {
            ValidateMetadataName(name, nameof(name));
            ValidateCompiledInstructionIndex(instructionIndex, nameof(instructionIndex));
            EnsureUniqueLabelName(name);

            _labelDeclarations.Add(new IrLabelDeclaration(name, instructionIndex));
            InvalidateCanonicalCompileCache();
        }

        /// <summary>
        /// Declares a named IR entry point for an instruction in the current VT-local stream.
        /// </summary>
        public void DeclareEntryPoint(string name, int instructionIndex, IrEntryPointKind kind = IrEntryPointKind.EntryPoint)
        {
            ValidateMetadataName(name, nameof(name));
            ValidateCompiledInstructionIndex(instructionIndex, nameof(instructionIndex));
            EnsureUniqueEntryPointName(name);

            _entryPointDeclarations.Add(new IrEntryPointDeclaration(name, kind, instructionIndex));
            InvalidateCanonicalCompileCache();
        }

        /// <summary>
        /// Declares a named IR label for the next instruction emitted at the current VT-local stream position.
        /// </summary>
        public void DeclareLabelAtCurrentPosition(string name)
        {
            ValidateMetadataName(name, nameof(name));
            EnsureUniqueLabelName(name);

            _labelDeclarations.Add(new IrLabelDeclaration(name, _instructionCount));
            InvalidateCanonicalCompileCache();
        }

        /// <summary>
        /// Declares a named IR entry point for the next instruction emitted at the current VT-local stream position.
        /// </summary>
        public void DeclareEntryPointAtCurrentPosition(string name, IrEntryPointKind kind = IrEntryPointKind.EntryPoint)
        {
            ValidateMetadataName(name, nameof(name));
            EnsureUniqueEntryPointName(name);

            _entryPointDeclarations.Add(new IrEntryPointDeclaration(name, kind, _instructionCount));
            InvalidateCanonicalCompileCache();
        }

        internal IReadOnlyList<IrLabelDeclaration> GetLabelDeclarations()
        {
            ValidateResolvedMetadataDeclarations();
            return _labelDeclarations;
        }

        internal IReadOnlyList<IrEntryPointDeclaration> GetEntryPointDeclarations()
        {
            ValidateResolvedMetadataDeclarations();
            return _entryPointDeclarations;
        }

        internal IReadOnlyList<IrControlFlowTargetReference> GetControlFlowTargetReferences()
        {
            return _controlFlowTargetReferences;
        }

        private void ResetIrMetadataDeclarations()
        {
            _labelDeclarations.Clear();
            _entryPointDeclarations.Clear();
            _controlFlowTargetReferences.Clear();
        }

        private void ShiftIrMetadataDeclarations(int startInstructionIndex, int oldInstructionCount, int delta)
        {
            if (delta == 0)
            {
                return;
            }

            for (int index = 0; index < _labelDeclarations.Count; index++)
            {
                IrLabelDeclaration declaration = _labelDeclarations[index];
                if (ShouldShiftMetadataDeclaration(declaration.InstructionIndex, startInstructionIndex, oldInstructionCount))
                {
                    _labelDeclarations[index] = declaration with { InstructionIndex = declaration.InstructionIndex + delta };
                }
            }

            for (int index = 0; index < _entryPointDeclarations.Count; index++)
            {
                IrEntryPointDeclaration declaration = _entryPointDeclarations[index];
                if (ShouldShiftMetadataDeclaration(declaration.InstructionIndex, startInstructionIndex, oldInstructionCount))
                {
                    _entryPointDeclarations[index] = declaration with { InstructionIndex = declaration.InstructionIndex + delta };
                }
            }

            for (int index = 0; index < _controlFlowTargetReferences.Count; index++)
            {
                IrControlFlowTargetReference reference = _controlFlowTargetReferences[index];
                if (ShouldShiftMetadataDeclaration(reference.InstructionIndex, startInstructionIndex, oldInstructionCount))
                {
                    _controlFlowTargetReferences[index] = reference with { InstructionIndex = reference.InstructionIndex + delta };
                }
            }
        }

        private void ValidateCompiledInstructionIndex(int instructionIndex, string parameterName)
        {
            if ((uint)instructionIndex >= (uint)_instructionCount)
            {
                throw new ArgumentOutOfRangeException(parameterName, $"Instruction index must reference an existing VT-local instruction (0-{_instructionCount - 1}).");
            }
        }

        private static void ValidateMetadataName(string name, string parameterName)
        {
            ArgumentNullException.ThrowIfNull(name);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Metadata name cannot be empty or whitespace.", parameterName);
            }
        }

        private void ValidateResolvedMetadataDeclarations()
        {
            for (int index = 0; index < _labelDeclarations.Count; index++)
            {
                IrLabelDeclaration declaration = _labelDeclarations[index];
                if (declaration.InstructionIndex >= _instructionCount)
                {
                    throw new InvalidOperationException(
                        $"Label '{declaration.Name}' is declared at the current end-of-stream boundary, but no instruction was emitted at that position.");
                }
            }

            for (int index = 0; index < _entryPointDeclarations.Count; index++)
            {
                IrEntryPointDeclaration declaration = _entryPointDeclarations[index];
                if (declaration.InstructionIndex >= _instructionCount)
                {
                    throw new InvalidOperationException(
                        $"Entry point '{declaration.Name}' is declared at the current end-of-stream boundary, but no instruction was emitted at that position.");
                }
            }
        }

        private void EnsureUniqueLabelName(string name)
        {
            for (int index = 0; index < _labelDeclarations.Count; index++)
            {
                if (string.Equals(_labelDeclarations[index].Name, name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Label '{name}' is already declared for this VT-local stream.");
                }
            }
        }

        private void EnsureUniqueEntryPointName(string name)
        {
            for (int index = 0; index < _entryPointDeclarations.Count; index++)
            {
                if (string.Equals(_entryPointDeclarations[index].Name, name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Entry point '{name}' is already declared for this VT-local stream.");
                }
            }
        }

        private static bool ShouldShiftMetadataDeclaration(int declarationInstructionIndex, int insertionInstructionIndex, int oldInstructionCount)
        {
            return declarationInstructionIndex > insertionInstructionIndex ||
                (declarationInstructionIndex == insertionInstructionIndex && declarationInstructionIndex < oldInstructionCount);
        }
    }
}
