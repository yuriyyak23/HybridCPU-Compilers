using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

/// <summary>
/// Native, non-returning normal-leave continuation for endfinally. x10 carries the non-zero
/// frame-owned continuation token and x1 identifies the endfinally call site. Every metadata
/// bound is checked before control is transferred inside the owning method.
/// Exceptional token zero is accepted only for an active phase-two unwind and tail-enters the
/// separately validated exceptional-resume state machine; this helper alone does not qualify EH.
/// </summary>
public static class HybridCpuManagedFinallyContinuationEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_endfinally";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.endfinally/v1";

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
        void Address(byte target, byte basis, int offset) =>
            Op(HybridCpuOpcode.ADDI, target, basis, immediate: checked((short)offset));
        void Load(HybridCpuOpcode opcode, byte destination, byte basis, int offset)
        {
            Address(28, basis, offset);
            Op(opcode, destination, 28);
        }
        void Store(byte source, byte basis, int offset)
        {
            Address(28, basis, offset);
            Op(HybridCpuOpcode.SD, HybridCpuInstructionWord.NoArchReg, 28, source);
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

        Branch(HybridCpuOpcode.BNE, 10, 0, "token_nonzero");
        Load(HybridCpuOpcode.LD, 27, 3, HybridCpuManagedEhDispatchIndexV1.ExceptionStateOffset);
        Branch(HybridCpuOpcode.BNE, 27, 0, "exception_state_nonzero"); Jump("invalid");
        Mark("exception_state_nonzero");
        Load(HybridCpuOpcode.LD, 5, 27, HybridCpuManagedEhStateObjectV1.DispatchActiveOffset);
        Op(HybridCpuOpcode.ADDI, 6, 0, immediate: (short)HybridCpuManagedEhExceptionalResumeEmitterV1.ActiveValue);
        Branch(HybridCpuOpcode.BEQ, 5, 6, "exception_active"); Jump("invalid");
        Mark("exception_active");
        Load(HybridCpuOpcode.LD, 5, 27, HybridCpuManagedEhStateObjectV1.DispatchPhaseOffset);
        Op(HybridCpuOpcode.ADDI, 6, 0, immediate: (short)HybridCpuManagedEhExceptionalResumeEmitterV1.UnwindPhase);
        Branch(HybridCpuOpcode.BEQ, 5, 6, "exception_unwind"); Jump("invalid");
        Mark("exception_unwind");
        Op(HybridCpuOpcode.ADDI, 10, 27, immediate: HybridCpuManagedEhStateObjectV1.UnwindTransferRecordOffset);
        Load(HybridCpuOpcode.LD, 5, 3, HybridCpuManagedEhDispatchIndexV1.FinallyResumeHelperOffset);
        Op(HybridCpuOpcode.JALR, 0, 5); // non-returning exceptional resume
        Mark("token_nonzero");
        Op(HybridCpuOpcode.ADDI, 16, 10); // token; find-frame does not clobber x16/x17
        Op(HybridCpuOpcode.ADDI, 17, 1, immediate: -HybridCpuNativeCallControlContractV1.LinkIncrementBytes);
        Op(HybridCpuOpcode.ADDI, 11, 17);
        Load(HybridCpuOpcode.LD, 5, 3, HybridCpuManagedEhDispatchIndexV1.FindFrameHelperOffset);
        Op(HybridCpuOpcode.JALR, 1, 5);
        Branch(HybridCpuOpcode.BNE, 10, 0, "frame_found");
        Jump("invalid");

        Mark("frame_found");
        Op(HybridCpuOpcode.ADDI, 15, 10); // dispatch row
        Load(HybridCpuOpcode.LD, 14, 15, HybridCpuManagedEhDispatchIndexV1.FinallyAddressOffset);
        Load(HybridCpuOpcode.LD, 9, 15, HybridCpuManagedEhDispatchIndexV1.FinallySizeOffset);
        Branch(HybridCpuOpcode.BNE, 14, 0, "metadata_nonzero");
        Jump("invalid");
        Mark("metadata_nonzero");
        Constant(8, HybridCpuManagedFinallyContinuationEncodingV1.HeaderSizeBytes);
        Branch(HybridCpuOpcode.BGEU, 9, 8, "header_bounded");
        Jump("invalid");
        Mark("header_bounded");
        Load(HybridCpuOpcode.LD, 7, 14, 0);
        Constant(8, ((ulong)HybridCpuManagedFinallyContinuationEncodingV1.Version << 32) |
            HybridCpuManagedFinallyContinuationEncodingV1.Magic);
        Branch(HybridCpuOpcode.BEQ, 7, 8, "header_valid");
        Jump("invalid");

        Mark("header_valid");
        Load(HybridCpuOpcode.LW, 5, 14, HybridCpuManagedFinallyContinuationEncodingV1.ContinuationCountOffset);
        Load(HybridCpuOpcode.LW, 6, 14, HybridCpuManagedFinallyContinuationEncodingV1.StepCountOffset);
        Branch(HybridCpuOpcode.BNE, 5, 0, "continuations_nonzero");
        Jump("invalid");
        Mark("continuations_nonzero");
        Constant(8, (ulong)HybridCpuManagedEhSchemaV1.MaximumOperationsPerMethod + 1);
        Branch(HybridCpuOpcode.BLTU, 5, 8, "continuations_bounded");
        Jump("invalid");
        Mark("continuations_bounded");
        Constant(8, checked((ulong)HybridCpuManagedEhSchemaV1.MaximumOperationsPerMethod *
            HybridCpuManagedEhSchemaV1.MaximumClausesPerMethod + 1));
        Branch(HybridCpuOpcode.BLTU, 6, 8, "steps_bounded");
        Jump("invalid");

        Mark("steps_bounded");
        Op(HybridCpuOpcode.ADD, 9, 14, 9); // one-past metadata
        Op(HybridCpuOpcode.ADDI, 7, 14, immediate: HybridCpuManagedFinallyContinuationEncodingV1.HeaderSizeBytes);
        Op(HybridCpuOpcode.ADDI, 8, 5);
        Mark("size_continuations");
        Branch(HybridCpuOpcode.BNE, 8, 0, "size_one_continuation");
        Jump("size_steps_begin");
        Mark("size_one_continuation");
        Op(HybridCpuOpcode.ADDI, 7, 7, immediate: HybridCpuManagedFinallyContinuationEncodingV1.ContinuationRowSizeBytes);
        Op(HybridCpuOpcode.ADDI, 8, 8, immediate: -1);
        Jump("size_continuations");
        Mark("size_steps_begin");
        Op(HybridCpuOpcode.ADDI, 8, 6);
        Mark("size_steps");
        Branch(HybridCpuOpcode.BNE, 8, 0, "size_one_step");
        Jump("size_ready");
        Mark("size_one_step");
        Op(HybridCpuOpcode.ADDI, 7, 7, immediate: HybridCpuManagedFinallyContinuationEncodingV1.StepRowSizeBytes);
        Op(HybridCpuOpcode.ADDI, 8, 8, immediate: -1);
        Jump("size_steps");
        Mark("size_ready");
        Branch(HybridCpuOpcode.BGEU, 9, 7, "metadata_rows_bounded");
        Jump("invalid");
        Mark("metadata_rows_bounded");
        Op(HybridCpuOpcode.ADDI, 11, 14, immediate: HybridCpuManagedFinallyContinuationEncodingV1.HeaderSizeBytes);
        Op(HybridCpuOpcode.ADDI, 12, 5); // retain continuation count
        Mark("find_token");
        Branch(HybridCpuOpcode.BNE, 12, 0, "test_token");
        Jump("invalid");
        Mark("test_token");
        Load(HybridCpuOpcode.LW, 7, 11, HybridCpuManagedFinallyContinuationEncodingV1.TokenOffset);
        Branch(HybridCpuOpcode.BEQ, 7, 16, "token_found");
        Op(HybridCpuOpcode.ADDI, 11, 11, immediate: HybridCpuManagedFinallyContinuationEncodingV1.ContinuationRowSizeBytes);
        Op(HybridCpuOpcode.ADDI, 12, 12, immediate: -1);
        Jump("find_token");

        Mark("token_found");
        Op(HybridCpuOpcode.ADDI, 13, 11); // selected continuation row
        // Establish the first step row without multiplication.
        Op(HybridCpuOpcode.ADDI, 11, 14, immediate: HybridCpuManagedFinallyContinuationEncodingV1.HeaderSizeBytes);
        Op(HybridCpuOpcode.ADDI, 12, 5);
        Mark("skip_continuations");
        Branch(HybridCpuOpcode.BNE, 12, 0, "skip_one_continuation");
        Jump("steps_base_ready");
        Mark("skip_one_continuation");
        Op(HybridCpuOpcode.ADDI, 11, 11, immediate: HybridCpuManagedFinallyContinuationEncodingV1.ContinuationRowSizeBytes);
        Op(HybridCpuOpcode.ADDI, 12, 12, immediate: -1);
        Jump("skip_continuations");
        Mark("steps_base_ready");
        Load(HybridCpuOpcode.LW, 7, 13, HybridCpuManagedFinallyContinuationEncodingV1.FirstStepOffset);
        Load(HybridCpuOpcode.LW, 8, 13, HybridCpuManagedFinallyContinuationEncodingV1.ContinuationStepCountOffset);
        Branch(HybridCpuOpcode.BNE, 8, 0, "continuation_steps_nonzero");
        Jump("invalid");
        Mark("continuation_steps_nonzero");
        Op(HybridCpuOpcode.ADD, 12, 7, 8);
        Branch(HybridCpuOpcode.BGEU, 6, 12, "step_range_bounded");
        Jump("invalid");
        Mark("step_range_bounded");
        Mark("skip_first_steps");
        Branch(HybridCpuOpcode.BNE, 7, 0, "skip_one_step");
        Jump("scan_steps");
        Mark("skip_one_step");
        Op(HybridCpuOpcode.ADDI, 11, 11, immediate: HybridCpuManagedFinallyContinuationEncodingV1.StepRowSizeBytes);
        Op(HybridCpuOpcode.ADDI, 7, 7, immediate: -1);
        Jump("skip_first_steps");

        Mark("scan_steps");
        Branch(HybridCpuOpcode.BNE, 8, 0, "test_step");
        Jump("invalid");
        Mark("test_step");
        Load(HybridCpuOpcode.LD, 12, 15, HybridCpuManagedEhDispatchIndexV1.CodeAddressOffset);
        Op(HybridCpuOpcode.SUB, 7, 17, 12); // current native PC relative to method
        Load(HybridCpuOpcode.LW, 5, 11, HybridCpuManagedFinallyContinuationEncodingV1.StepHandlerOffset);
        Branch(HybridCpuOpcode.BLTU, 7, 5, "next_step");
        Load(HybridCpuOpcode.LW, 6, 11, HybridCpuManagedFinallyContinuationEncodingV1.StepHandlerEndOffset);
        Branch(HybridCpuOpcode.BLTU, 7, 6, "current_step");
        Mark("next_step");
        Op(HybridCpuOpcode.ADDI, 11, 11, immediate: HybridCpuManagedFinallyContinuationEncodingV1.StepRowSizeBytes);
        Op(HybridCpuOpcode.ADDI, 8, 8, immediate: -1);
        Jump("scan_steps");

        Mark("current_step");
        Load(HybridCpuOpcode.LW, 5, 11, HybridCpuManagedFinallyContinuationEncodingV1.StepNextClauseOrdinalOffset);
        Op(HybridCpuOpcode.ADDI, 6, 0, immediate: -1);
        Branch(HybridCpuOpcode.BEQ, 5, 6, "final_target");
        Op(HybridCpuOpcode.ADDI, 7, 8, immediate: -1);
        Branch(HybridCpuOpcode.BNE, 7, 0, "has_next_step_slot");
        Jump("invalid");
        Mark("has_next_step_slot");
        Op(HybridCpuOpcode.ADDI, 7, 11, immediate: HybridCpuManagedFinallyContinuationEncodingV1.StepRowSizeBytes);
        Load(HybridCpuOpcode.LW, 6, 7, HybridCpuManagedFinallyContinuationEncodingV1.StepClauseOrdinalOffset);
        Branch(HybridCpuOpcode.BEQ, 5, 6, "next_step_matches");
        Jump("invalid");
        Mark("next_step_matches");
        Load(HybridCpuOpcode.LW, 5, 11, HybridCpuManagedFinallyContinuationEncodingV1.StepNextOffset);
        Load(HybridCpuOpcode.LW, 6, 7, HybridCpuManagedFinallyContinuationEncodingV1.StepHandlerOffset);
        Branch(HybridCpuOpcode.BEQ, 5, 6, "next_offset_matches");
        Jump("invalid");
        Mark("next_offset_matches");
        Load(HybridCpuOpcode.LW, 7, 7, HybridCpuManagedFinallyContinuationEncodingV1.StepReleaseBeforeEntryOffset);
        Jump("release_and_transfer");

        Mark("final_target");
        Load(HybridCpuOpcode.LW, 5, 13, HybridCpuManagedFinallyContinuationEncodingV1.TargetOffset);
        Load(HybridCpuOpcode.LW, 7, 13, HybridCpuManagedFinallyContinuationEncodingV1.ReleaseBeforeTargetOffset);
        Load(HybridCpuOpcode.LW, 6, 14, HybridCpuManagedFinallyContinuationEncodingV1.TokenSlotOffset);
        Op(HybridCpuOpcode.ADD, 6, 2, 6);
        Op(HybridCpuOpcode.SD, HybridCpuInstructionWord.NoArchReg, 6, 0); // clear normal token

        Mark("release_and_transfer");
        Branch(HybridCpuOpcode.BNE, 7, 0, "release_scope");
        Jump("transfer");
        Mark("release_scope");
        Load(HybridCpuOpcode.LD, 6, 3, HybridCpuManagedEhDispatchIndexV1.ExceptionStateOffset);
        Store(0, 6, HybridCpuManagedEhStateObjectV1.ExceptionReferenceOffset);
        Store(0, 6, HybridCpuManagedEhStateObjectV1.TypeHandleOffset);
        Mark("transfer");
        Load(HybridCpuOpcode.LD, 6, 15, HybridCpuManagedEhDispatchIndexV1.CodeSizeOffset);
        Branch(HybridCpuOpcode.BLTU, 5, 6, "target_bounded");
        Jump("invalid");
        Mark("target_bounded");
        Load(HybridCpuOpcode.LD, 6, 15, HybridCpuManagedEhDispatchIndexV1.CodeAddressOffset);
        Op(HybridCpuOpcode.ADD, 6, 6, 5);
        Op(HybridCpuOpcode.JALR, 0, 6);

        Mark("invalid");
        Op(HybridCpuOpcode.ADDI, 10, 0, immediate: 255);
        Mark("exit_retry");
        Load(HybridCpuOpcode.LD, 5, 3, HybridCpuManagedEhDispatchIndexV1.ProcessExitHelperOffset);
        Op(HybridCpuOpcode.JALR, 1, 5);
        Jump("exit_retry");

        IReadOnlyList<HybridCpuInstructionWord> resolved = HybridCpuNativeBranchEncoderV1.Resolve(
            code, labels, branches, "finally-continuation");
        return new HybridCpuBundleSerializer().SerializeProgram(resolved.Select(instruction =>
        {
            HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
            bundle.SetInstruction(0, instruction);
            return bundle;
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
