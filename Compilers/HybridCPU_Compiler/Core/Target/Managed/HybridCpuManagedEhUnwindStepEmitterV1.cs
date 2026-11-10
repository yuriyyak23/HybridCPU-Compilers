using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

/// <summary>
/// One image-native managed-frame unwind step. x10 is a validated immutable dispatch-index
/// row and x13 is a writable HCET snapshot. The helper updates snapshot PC/SP and saved
/// registers, returning 1 in x10; malformed stack ranges return zero without a memory access.
/// </summary>
public static class HybridCpuManagedEhUnwindStepEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_eh_unwind_step";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.eh-unwind-step/v1";

    public static byte[] Emit()
    {
        List<HybridCpuInstructionWord> code = new List<HybridCpuInstructionWord>();
        Dictionary<string, int> labels = new Dictionary<string, int>(StringComparer.Ordinal);
        List<(int Index, string Label)> branches = new List<(int Index, string Label)>();
        void Mark(string name) => labels.Add(name, code.Count);
        void Op(HybridCpuOpcode opcode, byte rd, byte rs1, byte rs2 = HybridCpuInstructionWord.NoArchReg,
            short immediate = 0) => code.Add(new()
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
        void Store(byte value, byte basis, int offset)
        {
            Address(28, basis, offset);
            Op(HybridCpuOpcode.SD, HybridCpuInstructionWord.NoArchReg, 28, value);
        }
        void ValidateStackAddress(byte address, string continuation)
        {
            Branch(HybridCpuOpcode.BLTU, address, 14, "invalid");
            Op(HybridCpuOpcode.ADDI, 30, address, immediate: 8);
            Branch(HybridCpuOpcode.BLTU, 15, 30, "invalid");
            Op(HybridCpuOpcode.ANDI, 30, address, immediate: 7);
            Branch(HybridCpuOpcode.BNE, 30, 0, "invalid");
            Jump(continuation);
        }
        void ValidateRegister(byte register, string continuation)
        {
            Branch(HybridCpuOpcode.BNE, register, 0, "register_nonzero_" + continuation);
            Jump("invalid");
            Mark("register_nonzero_" + continuation);
            Op(HybridCpuOpcode.ADDI, 30, 0, immediate: 32);
            Branch(HybridCpuOpcode.BLTU, register, 30, continuation);
            Jump("invalid");
        }

        Load(HybridCpuOpcode.LD, 14, 3, HybridCpuManagedEhDispatchIndexV1.StackBaseOffset);
        Load(HybridCpuOpcode.LD, 15, 3, HybridCpuManagedEhDispatchIndexV1.StackEndOffset);
        Load(HybridCpuOpcode.LW, 5, 10, HybridCpuManagedEhDispatchIndexV1.CfaBaseOffset);
        Op(HybridCpuOpcode.ADDI, 30, 0, immediate: 2);
        Branch(HybridCpuOpcode.BLTU, 5, 30, "cfa_kind_valid");
        Jump("invalid");
        Mark("cfa_kind_valid");
        Branch(HybridCpuOpcode.BNE, 5, 0, "cfa_fp");
        Load(HybridCpuOpcode.LD, 6, 13, HybridCpuManagedExceptionTransferV1.RegisterOffset(2));
        Jump("have_cfa_base");
        Mark("cfa_fp");
        Load(HybridCpuOpcode.LD, 6, 13, HybridCpuManagedExceptionTransferV1.RegisterOffset(8));
        Mark("have_cfa_base");
        Load(HybridCpuOpcode.LW, 7, 10, HybridCpuManagedEhDispatchIndexV1.CfaOffsetOffset);
        Op(HybridCpuOpcode.ADD, 12, 6, 7);
        Branch(HybridCpuOpcode.BLTU, 12, 14, "invalid");
        Branch(HybridCpuOpcode.BLTU, 15, 12, "invalid");
        Op(HybridCpuOpcode.ANDI, 30, 12, immediate: 15);
        Branch(HybridCpuOpcode.BNE, 30, 0, "invalid");

        Load(HybridCpuOpcode.LW, 31, 10, HybridCpuManagedEhDispatchIndexV1.ReturnRegisterOffset);
        Op(HybridCpuOpcode.ADDI, 29, 0, immediate: -1);
        Branch(HybridCpuOpcode.BNE, 31, 29, "return_register");
        Load(HybridCpuOpcode.LW, 7, 10, HybridCpuManagedEhDispatchIndexV1.ReturnStackOffset);
        Op(HybridCpuOpcode.ADD, 28, 12, 7);
        ValidateStackAddress(28, "return_stack_valid");
        Mark("return_stack_valid");
        Op(HybridCpuOpcode.LD, 11, 28);
        Jump("return_ready");
        Mark("return_register");
        ValidateRegister(31, "return_register_valid");
        Mark("return_register_valid");
        Op(HybridCpuOpcode.SLLI, 30, 31, immediate: 3);
        Op(HybridCpuOpcode.ADD, 30, 13, 30);
        Op(HybridCpuOpcode.ADDI, 30, 30, immediate: HybridCpuManagedExceptionTransferV1.RegistersOffset);
        Op(HybridCpuOpcode.LD, 11, 30);
        Mark("return_ready");

        Load(HybridCpuOpcode.LW, 5, 10, HybridCpuManagedEhDispatchIndexV1.SavedCountOffset);
        Op(HybridCpuOpcode.ADDI, 30, 0, immediate: HybridCpuManagedEhDispatchIndexV1.MaximumSavedRegisters + 1);
        Branch(HybridCpuOpcode.BLTU, 5, 30, "saved_count_valid");
        Jump("invalid");
        Mark("saved_count_valid");
        Address(16, 10, HybridCpuManagedEhDispatchIndexV1.SavedRowsOffset);
        Mark("saved_test");
        Branch(HybridCpuOpcode.BNE, 5, 0, "saved_body");
        Jump("finish");
        Mark("saved_body");
        Load(HybridCpuOpcode.LW, 17, 16, 0);
        ValidateRegister(17, "saved_register_valid");
        Mark("saved_register_valid");
        Load(HybridCpuOpcode.LW, 7, 16, 4);
        Op(HybridCpuOpcode.ADD, 28, 12, 7);
        ValidateStackAddress(28, "saved_stack_valid");
        Mark("saved_stack_valid");
        Op(HybridCpuOpcode.LD, 29, 28);
        Op(HybridCpuOpcode.SLLI, 30, 17, immediate: 3);
        Op(HybridCpuOpcode.ADD, 30, 13, 30);
        Op(HybridCpuOpcode.ADDI, 30, 30, immediate: HybridCpuManagedExceptionTransferV1.RegistersOffset);
        Op(HybridCpuOpcode.SD, HybridCpuInstructionWord.NoArchReg, 30, 29);
        Op(HybridCpuOpcode.ADDI, 16, 16, immediate: HybridCpuManagedEhDispatchIndexV1.SavedRowSizeBytes);
        Op(HybridCpuOpcode.ADDI, 5, 5, immediate: -1);
        Jump("saved_test");

        Mark("finish");
        Store(11, 13, HybridCpuManagedExceptionTransferV1.ProgramCounterOffset);
        Store(12, 13, HybridCpuManagedExceptionTransferV1.RegisterOffset(2));
        Op(HybridCpuOpcode.ADDI, 10, 0, immediate: 1);
        Jump("return");
        Mark("invalid");
        Op(HybridCpuOpcode.ADDI, 10, 0);
        Mark("return");
        Op(HybridCpuOpcode.JALR, 0, 1, immediate: HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);

        foreach ((int index, string label) in branches)
        {
            if (!labels.TryGetValue(label, out int target)) throw new InvalidOperationException("Undefined EH unwind label: " + label);
            int displacement = checked((target - index) * HybridCpuBundleSerializer.BundleSizeBytes);
            if (displacement is < short.MinValue or > short.MaxValue || displacement == 0)
                throw new InvalidOperationException("EH unwind branch displacement is outside the native encoding.");
            HybridCpuInstructionWord instruction = code[index];
            instruction.Immediate = unchecked((ushort)(short)displacement);
            code[index] = instruction;
        }
        return new HybridCpuBundleSerializer().SerializeProgram(code.Select(instruction =>
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
                ".text", 0, (ulong)code.Length, IsDefinition: true)], [],
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
    }
}
