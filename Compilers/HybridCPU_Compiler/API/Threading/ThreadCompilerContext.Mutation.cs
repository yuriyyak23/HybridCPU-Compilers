using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU.Compiler.Core.Threading
{
    public partial class HybridCpuThreadCompilerContext
    {
        /// <summary>
        /// Inserts an instruction into the VT-local stream before the specified instruction index.
        /// Existing IR label and entry-point declarations are shifted so they continue to point to
        /// the same logical instructions after insertion.
        /// </summary>
        public void InsertInstruction(
            int instructionIndex,
            uint opCode,
            byte dataType,
            byte predicate,
            ushort immediate,
            ulong destSrc1,
            ulong src2,
            ulong streamLength,
            ushort stride,
            StealabilityPolicy stealabilityPolicy)
        {
            ValidateNoDirectSystemDeviceCommandEmission(opCode);
            ValidateNoDirectMatrixTileEmission(opCode);
            ValidateNoDirectVectorTransferEmission(opCode);
            ValidateInsertionIndex(instructionIndex, nameof(instructionIndex));
            EnsureInstructionCapacity();
            uint encodedStreamLength = RequireVliwUInt32(streamLength, nameof(streamLength));
            int oldInstructionCount = _instructionCount;

            for (int index = _instructionCount; index > instructionIndex; index--)
            {
                _instructions[index] = _instructions[index - 1];
                _instructionSlotMetadata[index] = _instructionSlotMetadata[index - 1];
                _externalOperationIntents[index] = _externalOperationIntents[index - 1];
                _externalOperationDescriptorIdentities[index] = _externalOperationDescriptorIdentities[index - 1];
            }

            _instructions[instructionIndex] = new VLIW_Instruction
            {
                OpCode = opCode,
                DataType = dataType,
                PredicateMask = predicate,
                Immediate = immediate,
                DestSrc1Pointer = destSrc1,
                Src2Pointer = src2,
                StreamLength = encodedStreamLength,
                Stride = stride
            };
            _instructionSlotMetadata[instructionIndex] = new InstructionSlotMetadata(
                _virtualThreadId,
                BuildSlotMetadata(opCode, stealabilityPolicy, _domainTag));
            _externalOperationIntents[instructionIndex] = null;
            _externalOperationDescriptorIdentities[instructionIndex] = null;
            _instructionCount++;

            ShiftIrMetadataDeclarations(instructionIndex, oldInstructionCount, 1);
            InvalidateCanonicalCompileCache();
        }

        private void EnsureInstructionCapacity()
        {
            if (_instructionCount >= MAX_INSTRUCTIONS_PER_THREAD)
            {
                throw new InvalidOperationException($"VT-{_virtualThreadId.Value}: Instruction buffer overflow (max {MAX_INSTRUCTIONS_PER_THREAD})");
            }
        }

        private void ValidateInsertionIndex(int instructionIndex, string parameterName)
        {
            if (instructionIndex < 0 || instructionIndex > _instructionCount)
            {
                throw new ArgumentOutOfRangeException(parameterName, $"Insertion index must be in range 0-{_instructionCount}.");
            }
        }

        internal HybridCpuThreadCompilerContext CreateDetachedCopy()
        {
            HybridCpuThreadCompilerContext copy = new HybridCpuThreadCompilerContext(_virtualThreadId.Value)
            {
                DomainTag = DomainTag,
                FrontendMode = FrontendMode
            };

            Array.Copy(_instructions, copy._instructions, _instructionCount);
            Array.Copy(_instructionSlotMetadata, copy._instructionSlotMetadata, _instructionCount);
            Array.Copy(_externalOperationIntents, copy._externalOperationIntents, _instructionCount);
            Array.Copy(_externalOperationDescriptorIdentities, copy._externalOperationDescriptorIdentities, _instructionCount);
            copy._instructionCount = _instructionCount;

            copy._labelDeclarations.AddRange(_labelDeclarations);
            copy._entryPointDeclarations.AddRange(_entryPointDeclarations);
            copy._controlFlowTargetReferences.AddRange(_controlFlowTargetReferences);

            return copy;
        }
    }
}
