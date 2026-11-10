using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

/// <summary>
/// Non-returning handled-catch frame walker. This is deliberately not yet exported as
/// __hybridcpu_managed_throw: finally continuation and the process-exit leaf are separate
/// authority gates. x10 is a non-null rooted exception reference with its TypeHandle at +0.
/// </summary>
public static class HybridCpuManagedEhCatchDispatchEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_eh_dispatch_catch";
    public const string ThrowSymbol = "__hybridcpu_managed_throw";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.eh-dispatch-catch/v1";
    public const string UnhandledProcessExitSymbol = HybridCpuManagedProcessExitEmitterV1.Symbol;
    public const int FrameBytes = 0;

    public static (byte[] Code, IReadOnlyList<(int Index, string Symbol)> Calls) Emit()
    {
        List<HybridCpuInstructionWord> code = new List<HybridCpuInstructionWord>();
        Dictionary<string, int> labels = new Dictionary<string, int>(StringComparer.Ordinal);
        List<(int Index, string Label)> branches = new List<(int Index, string Label)>();
        List<(int Index, string Symbol)> calls = new List<(int Index, string Symbol)>();
        void Mark(string label) => labels.Add(label, code.Count);
        void Op(HybridCpuOpcode opcode, byte rd, byte rs1, byte rs2 = HybridCpuInstructionWord.NoArchReg,
            short immediate = 0) => code.Add(new()
        {
            OpCode = (uint)opcode, DataTypeValue = HybridCpuDataType.INT64, PredicateMask = byte.MaxValue,
            Word1 = HybridCpuInstructionWord.PackArchRegs(rd, rs1, rs2), Immediate = unchecked((ushort)immediate)
        });
        void Branch(HybridCpuOpcode opcode, byte left, byte right, string label)
        {
            branches.Add((code.Count, label)); Op(opcode, HybridCpuInstructionWord.NoArchReg, left, right, 1);
        }
        void Jump(string label)
        {
            branches.Add((code.Count, label)); Op(HybridCpuOpcode.JAL, 0, HybridCpuInstructionWord.NoArchReg, immediate: 1);
        }
        void Call(string symbol, int helperOffset)
        {
            calls.Add((code.Count, symbol));
            Load(HybridCpuOpcode.LD, 5, 3, helperOffset);
            Op(HybridCpuOpcode.JALR, 1, 5);
        }
        void Address(byte target, byte basis, int offset) => Op(HybridCpuOpcode.ADDI, target, basis, immediate: checked((short)offset));
        void Load(HybridCpuOpcode opcode, byte destination, byte basis, int offset)
        {
            Address(28, basis, offset); Op(opcode, destination, 28);
        }
        void Store(byte source, byte basis, int offset)
        {
            Address(28, basis, offset); Op(HybridCpuOpcode.SD, HybridCpuInstructionWord.NoArchReg, 28, source);
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

        Branch(HybridCpuOpcode.BNE, 10, 0, "exception_nonzero"); Jump("unhandled");
        Mark("exception_nonzero");
        Op(HybridCpuOpcode.ADDI, 5, 2); // throwing frame SP
        Load(HybridCpuOpcode.LD, 6, 3, HybridCpuManagedEhDispatchIndexV1.StackBaseOffset);
        Branch(HybridCpuOpcode.BLTU, 5, 6, "unhandled");
        Load(HybridCpuOpcode.LD, 6, 3, HybridCpuManagedEhDispatchIndexV1.StackEndOffset);
        Branch(HybridCpuOpcode.BLTU, 6, 5, "unhandled");
        Op(HybridCpuOpcode.ANDI, 6, 5, immediate: HybridCpuNativeAbiContractV2.StackAlignmentBytes - 1);
        Branch(HybridCpuOpcode.BNE, 6, 0, "unhandled");

        Load(HybridCpuOpcode.LD, 29, 3, HybridCpuManagedEhDispatchIndexV1.ExceptionStateOffset);
        Branch(HybridCpuOpcode.BNE, 29, 0, "state_nonzero"); Jump("unhandled");
        Mark("state_nonzero");
        // x29..x31 are caller-saved. Keep every original callee-saved value intact until both
        // transfer records have captured it; x18/x25/x27 become working pointers only afterward.
        Op(HybridCpuOpcode.ADDI, 30, 29, immediate: HybridCpuManagedEhStateObjectV1.SearchTransferRecordOffset);
        Op(HybridCpuOpcode.ADDI, 31, 29, immediate: HybridCpuManagedEhStateObjectV1.UnwindTransferRecordOffset);
        Constant(6, ((ulong)HybridCpuManagedExceptionTransferV1.Version << 32) | HybridCpuManagedExceptionTransferV1.Magic);
        Store(6, 30, 0); Store(6, 31, 0);
        Op(HybridCpuOpcode.ADDI, 6, 1, immediate: -HybridCpuNativeCallControlContractV1.LinkIncrementBytes);
        Store(6, 30, HybridCpuManagedExceptionTransferV1.ProgramCounterOffset);
        Store(6, 31, HybridCpuManagedExceptionTransferV1.ProgramCounterOffset);
        Store(1, 30, HybridCpuManagedExceptionTransferV1.RegisterOffset(1));
        Store(1, 31, HybridCpuManagedExceptionTransferV1.RegisterOffset(1));
        Store(5, 30, HybridCpuManagedExceptionTransferV1.RegisterOffset(2));
        Store(5, 31, HybridCpuManagedExceptionTransferV1.RegisterOffset(2));
        foreach (int register in HybridCpuNativeAbiContractV2.Default.CalleeSavedRegisters)
        {
            Store((byte)register, 30, HybridCpuManagedExceptionTransferV1.RegisterOffset(register));
            Store((byte)register, 31, HybridCpuManagedExceptionTransferV1.RegisterOffset(register));
        }
        Op(HybridCpuOpcode.ADDI, 27, 29);
        Op(HybridCpuOpcode.ADDI, 18, 30);
        Op(HybridCpuOpcode.ADDI, 25, 31);
        Store(10, 18, HybridCpuManagedExceptionTransferV1.RegisterOffset(10));
        Store(10, 25, HybridCpuManagedExceptionTransferV1.RegisterOffset(10));
        Op(HybridCpuOpcode.ADDI, 19, 10); // rooted exception reference
        Load(HybridCpuOpcode.LD, 20, 19, 0); // compiler-owned object header TypeHandle
        Branch(HybridCpuOpcode.BNE, 20, 0, "type_nonzero"); Jump("unhandled");
        Mark("type_nonzero");
        Op(HybridCpuOpcode.ADDI, 21, 6); // exact initial throw-call PC
        Store(19, 27, HybridCpuManagedEhStateObjectV1.ExceptionReferenceOffset);
        Store(20, 27, HybridCpuManagedEhStateObjectV1.TypeHandleOffset);
        Store(21, 27, HybridCpuManagedEhStateObjectV1.WalkerProgramCounterOffset);
        Store(0, 27, HybridCpuManagedEhStateObjectV1.WalkerLastFinallyTrySizeOffset);

        Mark("frame_loop");
        Op(HybridCpuOpcode.ADDI, 22, 21);
        Op(HybridCpuOpcode.ADDI, 11, 22);
        Call(HybridCpuManagedEhFrameLookupEmitterV1.Symbol, HybridCpuManagedEhDispatchIndexV1.FindFrameHelperOffset);
        Branch(HybridCpuOpcode.BNE, 10, 0, "frame_found"); Jump("begin_unhandled_unwind");
        Mark("frame_found");
        Op(HybridCpuOpcode.ADDI, 23, 10);
        Op(HybridCpuOpcode.ADDI, 10, 23); Op(HybridCpuOpcode.ADDI, 11, 22); Op(HybridCpuOpcode.ADDI, 12, 20);
        Call(HybridCpuManagedEhCatchSelectorEmitterV1.Symbol, HybridCpuManagedEhDispatchIndexV1.CatchSelectorHelperOffset);
        Branch(HybridCpuOpcode.BNE, 10, 0, "catch_found");
        Op(HybridCpuOpcode.ADDI, 10, 23); Op(HybridCpuOpcode.ADDI, 13, 18);
        Call(HybridCpuManagedEhUnwindStepEmitterV1.Symbol, HybridCpuManagedEhDispatchIndexV1.UnwindStepHelperOffset);
        Branch(HybridCpuOpcode.BNE, 10, 0, "unwound"); Jump("begin_unhandled_unwind");
        Mark("unwound");
        Load(HybridCpuOpcode.LD, 21, 18, HybridCpuManagedExceptionTransferV1.ProgramCounterOffset);
        // The saved link is call-PC + four bytes, not the sequential bundle PC.
        Op(HybridCpuOpcode.ADDI, 21, 21, immediate: -HybridCpuNativeCallControlContractV1.LinkIncrementBytes);
        Jump("frame_loop");

        Mark("catch_found");
        Load(HybridCpuOpcode.LD, 27, 3, HybridCpuManagedEhDispatchIndexV1.ExceptionStateOffset);
        Store(23, 27, HybridCpuManagedEhStateObjectV1.TargetMethodRowOffset);
        Store(10, 27, HybridCpuManagedEhStateObjectV1.TargetClauseOffset);
        Op(HybridCpuOpcode.ADDI, 5, 0, immediate: (short)HybridCpuManagedEhExceptionalResumeEmitterV1.ActiveValue);
        Store(5, 27, HybridCpuManagedEhStateObjectV1.DispatchActiveOffset);
        Op(HybridCpuOpcode.ADDI, 5, 0, immediate: (short)HybridCpuManagedEhExceptionalResumeEmitterV1.UnwindPhase);
        Store(5, 27, HybridCpuManagedEhStateObjectV1.DispatchPhaseOffset);
        Op(HybridCpuOpcode.ADDI, 10, 25);
        Call(HybridCpuManagedEhExceptionalResumeEmitterV1.Symbol, HybridCpuManagedEhDispatchIndexV1.FinallyResumeHelperOffset);

        Mark("begin_unhandled_unwind");
        Load(HybridCpuOpcode.LD, 27, 3, HybridCpuManagedEhDispatchIndexV1.ExceptionStateOffset);
        Store(0, 27, HybridCpuManagedEhStateObjectV1.TargetMethodRowOffset);
        Store(0, 27, HybridCpuManagedEhStateObjectV1.TargetClauseOffset);
        Op(HybridCpuOpcode.ADDI, 5, 0, immediate: (short)HybridCpuManagedEhExceptionalResumeEmitterV1.ActiveValue);
        Store(5, 27, HybridCpuManagedEhStateObjectV1.DispatchActiveOffset);
        Op(HybridCpuOpcode.ADDI, 5, 0, immediate: (short)HybridCpuManagedEhExceptionalResumeEmitterV1.UnwindPhase);
        Store(5, 27, HybridCpuManagedEhStateObjectV1.DispatchPhaseOffset);
        Op(HybridCpuOpcode.ADDI, 10, 25);
        Call(HybridCpuManagedEhExceptionalResumeEmitterV1.Symbol, HybridCpuManagedEhDispatchIndexV1.FinallyResumeHelperOffset);

        Mark("unhandled");
        Op(HybridCpuOpcode.ADDI, 10, 0, immediate: 255);
        Mark("exit_retry");
        Call(UnhandledProcessExitSymbol, HybridCpuManagedEhDispatchIndexV1.ProcessExitHelperOffset);
        Jump("exit_retry"); // process_exit is non-returning; retry is fail-closed if a host violates it

        IReadOnlyList<HybridCpuInstructionWord> resolved = HybridCpuNativeBranchEncoderV1.Resolve(
            code, labels, branches, "catch-dispatch");
        byte[] bytes = new HybridCpuBundleSerializer().SerializeProgram(resolved.Select(instruction =>
        {
            HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle(); bundle.SetInstruction(0, instruction); return bundle;
        }).ToArray());
        return (bytes, calls.AsReadOnly());
    }

    public static HybridCpuObjectArtifactV1 EmitObject()
    {
        (byte[] code, IReadOnlyList<(int Index, string Symbol)> calls) = Emit();
        return new HybridCpuObjectWriterV1().Write(new(
            [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes, code, (ulong)code.Length)],
            [new(Symbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden, ".text", 0, (ulong)code.Length, true),
             new(ThrowSymbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default, ".text", 0, (ulong)code.Length, true)],
            [], HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
    }
}
