using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

/// <summary>
/// Resumes phase-two exceptional unwind from the image-owned unwind HCET. It executes every
/// eligible finally before transferring to the catch selected during phase one. Entry x10 must
/// equal the canonical unwind-record address. Transfers are non-returning; malformed state exits.
/// </summary>
public static class HybridCpuManagedEhExceptionalResumeEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_eh_resume_unwind";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.eh-resume-unwind/v1";
    public const ulong ActiveValue = 1;
    public const ulong UnwindPhase = 1;
    public const ulong CatchPhase = 2;

    public static byte[] Emit()
    {
        List<HybridCpuInstructionWord> code = new List<HybridCpuInstructionWord>();
        Dictionary<string, int> labels = new Dictionary<string, int>(StringComparer.Ordinal);
        List<(int Index, string Label)> branches = new List<(int Index, string Label)>();
        void Mark(string label) => labels.Add(label, code.Count);
        void Op(HybridCpuOpcode opcode, byte rd, byte rs1,
            byte rs2 = HybridCpuInstructionWord.NoArchReg, short immediate = 0) => code.Add(new()
        {
            OpCode = (uint)opcode, DataTypeValue = HybridCpuDataType.INT64, PredicateMask = byte.MaxValue,
            Word1 = HybridCpuInstructionWord.PackArchRegs(rd, rs1, rs2), Immediate = unchecked((ushort)immediate)
        });
        void Branch(HybridCpuOpcode opcode, byte left, byte right, string label)
        {
            branches.Add((code.Count, label));
            Op(opcode, HybridCpuInstructionWord.NoArchReg, left, right, 1);
        }
        void Jump(string label)
        {
            branches.Add((code.Count, label));
            Op(HybridCpuOpcode.JAL, 0, HybridCpuInstructionWord.NoArchReg, immediate: 1);
        }
        void Load(HybridCpuOpcode opcode, byte destination, byte basis, int offset)
        {
            Op(HybridCpuOpcode.ADDI, 28, basis, immediate: checked((short)offset));
            Op(opcode, destination, 28);
        }
        void Store(byte source, byte basis, int offset)
        {
            Op(HybridCpuOpcode.ADDI, 28, basis, immediate: checked((short)offset));
            Op(HybridCpuOpcode.SD, HybridCpuInstructionWord.NoArchReg, 28, source);
        }
        void Call(int helperOffset)
        {
            Load(HybridCpuOpcode.LD, 5, 3, helperOffset);
            Op(HybridCpuOpcode.JALR, 1, 5);
        }
        void Constant(byte destination, ulong value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            int high = Array.FindLastIndex(bytes, static item => item != 0);
            Op(HybridCpuOpcode.ADDI, destination, 0, immediate: high < 0 ? (short)0 : bytes[high]);
            for (int index = high - 1; index >= 0; index--)
            {
                Op(HybridCpuOpcode.SLLI, destination, destination, immediate: 8);
                if (bytes[index] != 0) Op(HybridCpuOpcode.ORI, destination, destination, immediate: bytes[index]);
            }
        }

        Load(HybridCpuOpcode.LD, 27, 3, HybridCpuManagedEhDispatchIndexV1.ExceptionStateOffset);
        Branch(HybridCpuOpcode.BNE, 27, 0, "state_nonzero"); Jump("invalid");
        Mark("state_nonzero");
        Op(HybridCpuOpcode.ADDI, 18, 27, immediate: HybridCpuManagedEhStateObjectV1.UnwindTransferRecordOffset);
        Branch(HybridCpuOpcode.BEQ, 10, 18, "record_exact"); Jump("invalid");
        Mark("record_exact");
        Load(HybridCpuOpcode.LD, 5, 27, HybridCpuManagedEhStateObjectV1.DispatchActiveOffset);
        Op(HybridCpuOpcode.ADDI, 6, 0, immediate: (short)ActiveValue);
        Branch(HybridCpuOpcode.BEQ, 5, 6, "active"); Jump("invalid");
        Mark("active");
        Load(HybridCpuOpcode.LD, 5, 27, HybridCpuManagedEhStateObjectV1.DispatchPhaseOffset);
        Op(HybridCpuOpcode.ADDI, 6, 0, immediate: (short)UnwindPhase);
        Branch(HybridCpuOpcode.BEQ, 5, 6, "phase_valid"); Jump("invalid");
        Mark("phase_valid");

        Mark("frame_loop");
        Load(HybridCpuOpcode.LD, 21, 27, HybridCpuManagedEhStateObjectV1.WalkerProgramCounterOffset);
        Op(HybridCpuOpcode.ADDI, 11, 21);
        Call(HybridCpuManagedEhDispatchIndexV1.FindFrameHelperOffset);
        Branch(HybridCpuOpcode.BNE, 10, 0, "frame_found"); Jump("invalid");
        Mark("frame_found");
        Op(HybridCpuOpcode.ADDI, 23, 10);
        Load(HybridCpuOpcode.LD, 24, 27, HybridCpuManagedEhStateObjectV1.TargetMethodRowOffset);
        Load(HybridCpuOpcode.LD, 26, 27, HybridCpuManagedEhStateObjectV1.WalkerLastFinallyTrySizeOffset);
        Branch(HybridCpuOpcode.BEQ, 23, 24, "target_frame");
        Load(HybridCpuOpcode.LD, 13, 23, HybridCpuManagedEhDispatchIndexV1.CodeSizeOffset);
        Op(HybridCpuOpcode.ADDI, 13, 13, immediate: 1);
        Jump("select_finally");
        Mark("target_frame");
        Load(HybridCpuOpcode.LD, 25, 27, HybridCpuManagedEhStateObjectV1.TargetClauseOffset);
        Branch(HybridCpuOpcode.BNE, 25, 0, "target_clause_nonzero"); Jump("invalid");
        Mark("target_clause_nonzero");
        Load(HybridCpuOpcode.LW, 13, 25, HybridCpuManagedEhClauseEncodingV1.TrySizeOffset);
        Mark("select_finally");
        Op(HybridCpuOpcode.ADDI, 10, 23); Op(HybridCpuOpcode.ADDI, 11, 21); Op(HybridCpuOpcode.ADDI, 12, 26);
        Call(HybridCpuManagedEhDispatchIndexV1.FinallySelectorHelperOffset);
        Op(HybridCpuOpcode.ADDI, 6, 0, immediate: -1);
        Branch(HybridCpuOpcode.BEQ, 10, 6, "invalid");
        Branch(HybridCpuOpcode.BNE, 10, 0, "finally_found");
        Branch(HybridCpuOpcode.BEQ, 23, 24, "transfer_catch");

        // No finally in this frame: unwind the image-owned record and continue at its caller PC.
        Op(HybridCpuOpcode.ADDI, 10, 23); Op(HybridCpuOpcode.ADDI, 13, 18);
        Call(HybridCpuManagedEhDispatchIndexV1.UnwindStepHelperOffset);
        Branch(HybridCpuOpcode.BNE, 10, 0, "unwound"); Jump("invalid");
        Mark("unwound");
        Load(HybridCpuOpcode.LD, 5, 18, HybridCpuManagedExceptionTransferV1.ProgramCounterOffset);
        // Recover the throwing call site from the ISA-visible saved link.
        Op(HybridCpuOpcode.ADDI, 5, 5, immediate: -HybridCpuNativeCallControlContractV1.LinkIncrementBytes);
        Store(5, 27, HybridCpuManagedEhStateObjectV1.WalkerProgramCounterOffset);
        Store(0, 27, HybridCpuManagedEhStateObjectV1.WalkerLastFinallyTrySizeOffset);
        Jump("frame_loop");

        Mark("finally_found");
        Op(HybridCpuOpcode.ADDI, 22, 10); // selected, fully validated clause
        Load(HybridCpuOpcode.LW, 5, 22, HybridCpuManagedEhClauseEncodingV1.TrySizeOffset);
        Store(5, 27, HybridCpuManagedEhStateObjectV1.WalkerLastFinallyTrySizeOffset);
        Load(HybridCpuOpcode.LD, 14, 23, HybridCpuManagedEhDispatchIndexV1.FinallyAddressOffset);
        Load(HybridCpuOpcode.LD, 15, 23, HybridCpuManagedEhDispatchIndexV1.FinallySizeOffset);
        Branch(HybridCpuOpcode.BNE, 14, 0, "finally_metadata_nonzero"); Jump("invalid");
        Mark("finally_metadata_nonzero");
        Constant(5, HybridCpuManagedFinallyContinuationEncodingV1.HeaderSizeBytes);
        Branch(HybridCpuOpcode.BGEU, 15, 5, "finally_header_bounded"); Jump("invalid");
        Mark("finally_header_bounded");
        Load(HybridCpuOpcode.LD, 5, 14, 0);
        Constant(6, ((ulong)HybridCpuManagedFinallyContinuationEncodingV1.Version << 32) |
            HybridCpuManagedFinallyContinuationEncodingV1.Magic);
        Branch(HybridCpuOpcode.BEQ, 5, 6, "finally_header_valid"); Jump("invalid");
        Mark("finally_header_valid");
        Load(HybridCpuOpcode.LW, 5, 14, HybridCpuManagedFinallyContinuationEncodingV1.TokenSlotOffset);
        Op(HybridCpuOpcode.ANDI, 6, 5, immediate: 7);
        Branch(HybridCpuOpcode.BEQ, 6, 0, "token_slot_aligned"); Jump("invalid");
        Mark("token_slot_aligned");
        Load(HybridCpuOpcode.LD, 6, 18, HybridCpuManagedExceptionTransferV1.RegisterOffset(2));
        Op(HybridCpuOpcode.ADD, 5, 6, 5);
        Load(HybridCpuOpcode.LD, 7, 3, HybridCpuManagedEhDispatchIndexV1.StackBaseOffset);
        Branch(HybridCpuOpcode.BLTU, 5, 7, "invalid");
        Load(HybridCpuOpcode.LD, 7, 3, HybridCpuManagedEhDispatchIndexV1.StackEndOffset);
        Op(HybridCpuOpcode.ADDI, 6, 5, immediate: 8);
        Branch(HybridCpuOpcode.BGEU, 7, 6, "token_slot_bounded"); Jump("invalid");
        Mark("token_slot_bounded");
        Op(HybridCpuOpcode.SD, HybridCpuInstructionWord.NoArchReg, 5, 0); // exceptional token zero
        Load(HybridCpuOpcode.LW, 5, 22, HybridCpuManagedEhClauseEncodingV1.HandlerStartOffset);
        Load(HybridCpuOpcode.LD, 6, 23, HybridCpuManagedEhDispatchIndexV1.CodeAddressOffset);
        Op(HybridCpuOpcode.ADD, 6, 6, 5);
        Store(6, 18, HybridCpuManagedExceptionTransferV1.ProgramCounterOffset);
        Store(0, 18, HybridCpuManagedExceptionTransferV1.RegisterOffset(10));
        Op(HybridCpuOpcode.ADDI, 10, 18);
        Call(HybridCpuManagedEhDispatchIndexV1.TransferHelperOffset);
        Jump("invalid");

        Mark("transfer_catch");
        Load(HybridCpuOpcode.LD, 25, 27, HybridCpuManagedEhStateObjectV1.TargetClauseOffset);
        Load(HybridCpuOpcode.LD, 19, 27, HybridCpuManagedEhStateObjectV1.ExceptionReferenceOffset);
        Load(HybridCpuOpcode.LW, 5, 25, HybridCpuManagedEhClauseEncodingV1.HandlerStartOffset);
        Load(HybridCpuOpcode.LD, 6, 23, HybridCpuManagedEhDispatchIndexV1.CodeAddressOffset);
        Op(HybridCpuOpcode.ADD, 6, 6, 5);
        Store(6, 18, HybridCpuManagedExceptionTransferV1.ProgramCounterOffset);
        Store(19, 18, HybridCpuManagedExceptionTransferV1.RegisterOffset(10));
        Op(HybridCpuOpcode.ADDI, 5, 0, immediate: (short)CatchPhase);
        Store(5, 27, HybridCpuManagedEhStateObjectV1.DispatchPhaseOffset);
        Store(0, 27, HybridCpuManagedEhStateObjectV1.DispatchActiveOffset);
        Op(HybridCpuOpcode.ADDI, 10, 18);
        Call(HybridCpuManagedEhDispatchIndexV1.TransferHelperOffset);

        Mark("invalid");
        Op(HybridCpuOpcode.ADDI, 10, 0, immediate: 255);
        Mark("exit_retry");
        Call(HybridCpuManagedEhDispatchIndexV1.ProcessExitHelperOffset);
        Jump("exit_retry");

        IReadOnlyList<HybridCpuInstructionWord> resolved = HybridCpuNativeBranchEncoderV1.Resolve(
            code, labels, branches, "exceptional-resume");
        return new HybridCpuBundleSerializer().SerializeProgram(resolved.Select(instruction =>
        {
            HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle(); bundle.SetInstruction(0, instruction); return bundle;
        }).ToArray());
    }

    public static HybridCpuObjectArtifactV1 EmitObject()
    {
        byte[] code = Emit();
        return new HybridCpuObjectWriterV1().Write(new(
            [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes,
                code, (ulong)code.Length)],
            [new(Symbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden,
                ".text", 0, (ulong)code.Length, true)], [],
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
    }
}
