using HybridCPU.Compiler.Core.IR;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;
using static YAKSys_Hybrid_CPU.Processor.CPU_Core;

namespace HybridCPU.Compiler.Core.Threading
{
    public partial class HybridCpuThreadCompilerContext
    {
        /// <summary>
        /// Emits a published branch/call opcode with a compiler-owned symbolic target relocation.
        /// </summary>
        internal void CompileSymbolicControlFlow(
            InstructionsEnum opcode,
            string targetName,
            byte rd,
            byte rs1,
            byte rs2,
            IrControlTransferKind transferKind,
            StealabilityPolicy stealabilityPolicy = StealabilityPolicy.NotStealable)
        {
            ValidateSymbolicControlFlowOpcode(opcode, transferKind);
            ValidateMetadataName(targetName, nameof(targetName));
            EnsureInstructionCapacity();

            int instructionIndex = _instructionCount;
            _instructions[instructionIndex] = new VLIW_Instruction
            {
                OpCode = (uint)opcode,
                DataTypeValue = DataTypeEnum.INT32,
                PredicateMask = 0,
                Immediate = 0,
                Word1 = VLIW_Instruction.PackArchRegs(rd, rs1, rs2),
                Src2Pointer = 0,
                StreamLength = 0,
                Stride = 0,
                VirtualThreadId = _virtualThreadId.Value
            };
            _instructionSlotMetadata[instructionIndex] = new InstructionSlotMetadata(
                _virtualThreadId,
                BuildSlotMetadata((uint)opcode, stealabilityPolicy, _domainTag));
            _controlFlowTargetReferences.Add(new IrControlFlowTargetReference(
                instructionIndex,
                targetName,
                transferKind));
            _instructionCount++;
            InvalidateCanonicalCompileCache();
        }

        /// <summary>
        /// Emits a canonical register-indirect return using JALR rd=x0, rs1=base, imm=0.
        /// </summary>
        internal void CompileRegisterReturn(
            byte baseRegister,
            StealabilityPolicy stealabilityPolicy = StealabilityPolicy.NotStealable)
        {
            EnsureInstructionCapacity();

            const InstructionsEnum opcode = InstructionsEnum.JALR;
            _instructions[_instructionCount] = new VLIW_Instruction
            {
                OpCode = (uint)opcode,
                DataTypeValue = DataTypeEnum.INT32,
                PredicateMask = 0,
                Immediate = 0,
                Word1 = VLIW_Instruction.PackArchRegs(
                    0,
                    baseRegister,
                    VLIW_Instruction.NoArchReg),
                Src2Pointer = 0,
                StreamLength = 0,
                Stride = 0,
                VirtualThreadId = _virtualThreadId.Value
            };
            _instructionSlotMetadata[_instructionCount] = new InstructionSlotMetadata(
                _virtualThreadId,
                BuildSlotMetadata((uint)opcode, stealabilityPolicy, _domainTag));
            _instructionCount++;
            InvalidateCanonicalCompileCache();
        }

        private static void ValidateSymbolicControlFlowOpcode(
            InstructionsEnum opcode,
            IrControlTransferKind transferKind)
        {
            bool isValid = transferKind switch
            {
                IrControlTransferKind.Branch => opcode == InstructionsEnum.JAL,
                IrControlTransferKind.Call => opcode == InstructionsEnum.JAL,
                IrControlTransferKind.ConditionalBranch => opcode is
                    InstructionsEnum.BNE or
                    InstructionsEnum.BLTU,
                _ => false
            };

            if (!isValid)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(opcode),
                    opcode,
                    $"Opcode is not valid for symbolic {transferKind} control-flow emission.");
            }
        }
    }
}
