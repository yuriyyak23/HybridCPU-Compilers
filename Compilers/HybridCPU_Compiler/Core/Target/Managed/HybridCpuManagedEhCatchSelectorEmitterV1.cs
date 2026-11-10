using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;

namespace HybridCPU.Compiler.Core.Target.Managed;

/// <summary>
/// Ancestry-aware catch selector. x10 is a validated method row, x11 the native PC and
/// x12 a compiler-owned object-header TypeHandle. It preserves the native ABI callee set,
/// tests each exact TypeId through the canonical base-handle chain and returns the globally
/// innermost (try-size, ordinal) clause address or zero.
/// </summary>
public static class HybridCpuManagedEhCatchSelectorEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_eh_select_catch";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.eh-select-catch/v1";
    private const int FrameBytes = 96;
    private static readonly int[] SavedRegisters = [1, 8, 18, 19, 20, 21, 22, 23, 24, 25, 26];

    public static (byte[] Code, int CallInstructionIndex) Emit()
    {
        List<HybridCpuInstructionWord> code = new List<HybridCpuInstructionWord>();
        Dictionary<string, int> labels = new Dictionary<string, int>(StringComparer.Ordinal);
        List<(int Index, string Label)> branches = new List<(int Index, string Label)>();
        void Mark(string label) => labels.Add(label, code.Count);
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

        Op(HybridCpuOpcode.ADDI, 2, 2, immediate: -FrameBytes);
        for (int index = 0; index < SavedRegisters.Length; index++) Store((byte)SavedRegisters[index], 2, index * 8);
        Op(HybridCpuOpcode.ADDI, 18, 10); Op(HybridCpuOpcode.ADDI, 19, 11); Op(HybridCpuOpcode.ADDI, 20, 12);
        Op(HybridCpuOpcode.ADDI, 21, 0); // best clause
        Load(HybridCpuOpcode.LD, 22, 18, HybridCpuManagedEhDispatchIndexV1.CodeSizeOffset);
        Op(HybridCpuOpcode.ADDI, 22, 22, immediate: 1); // best size sentinel
        Op(HybridCpuOpcode.ADDI, 23, 0, immediate: -1); // best ordinal sentinel
        Load(HybridCpuOpcode.LW, 25, 3, HybridCpuManagedEhDispatchIndexV1.TypeCountOffset);
        Branch(HybridCpuOpcode.BNE, 20, 0, "handle_nonzero"); Jump("invalid");
        Mark("handle_nonzero");
        Branch(HybridCpuOpcode.BLTU, 25, 20, "invalid");
        Load(HybridCpuOpcode.LW, 5, 3, HybridCpuManagedEhDispatchIndexV1.CountOffset);
        Op(HybridCpuOpcode.ADDI, 6, 0, immediate: HybridCpuManagedEhDispatchIndexV1.RowSizeBytes);
        Op(HybridCpuOpcode.MUL, 6, 5, 6); Op(HybridCpuOpcode.ADD, 26, 3, 6);
        Op(HybridCpuOpcode.ADDI, 26, 26, immediate: HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes);

        Mark("type_loop");
        Op(HybridCpuOpcode.ADDI, 5, 20, immediate: -1); Op(HybridCpuOpcode.SLLI, 5, 5, immediate: 5);
        Op(HybridCpuOpcode.ADD, 24, 26, 5);
        Load(HybridCpuOpcode.LD, 6, 24, HybridCpuManagedEhDispatchIndexV1.TypeHandleOffset);
        Branch(HybridCpuOpcode.BNE, 6, 20, "invalid");
        Load(HybridCpuOpcode.LD, 12, 24, HybridCpuManagedEhDispatchIndexV1.TypeIdOffset);
        Op(HybridCpuOpcode.ADDI, 10, 18); Op(HybridCpuOpcode.ADDI, 11, 19);
        int callIndex = code.Count;
        Op(HybridCpuOpcode.JAL, 1, HybridCpuInstructionWord.NoArchReg, immediate: 0);
        Branch(HybridCpuOpcode.BNE, 10, 0, "candidate"); Jump("next_type");
        Mark("candidate");
        Load(HybridCpuOpcode.LW, 5, 10, HybridCpuManagedEhClauseEncodingV1.TrySizeOffset);
        Branch(HybridCpuOpcode.BLTU, 5, 22, "replace");
        Branch(HybridCpuOpcode.BNE, 5, 22, "next_type");
        Load(HybridCpuOpcode.LW, 6, 10, HybridCpuManagedEhClauseEncodingV1.OrdinalOffset);
        Branch(HybridCpuOpcode.BLTU, 6, 23, "replace_with_ordinal"); Jump("next_type");
        Mark("replace");
        Load(HybridCpuOpcode.LW, 6, 10, HybridCpuManagedEhClauseEncodingV1.OrdinalOffset);
        Mark("replace_with_ordinal");
        Op(HybridCpuOpcode.ADDI, 21, 10); Op(HybridCpuOpcode.ADDI, 22, 5); Op(HybridCpuOpcode.ADDI, 23, 6);
        Mark("next_type");
        Load(HybridCpuOpcode.LD, 20, 24, HybridCpuManagedEhDispatchIndexV1.BaseTypeHandleOffset);
        Branch(HybridCpuOpcode.BNE, 20, 0, "base_nonzero"); Jump("finish");
        Mark("base_nonzero");
        Op(HybridCpuOpcode.ADDI, 25, 25, immediate: -1);
        Branch(HybridCpuOpcode.BNE, 25, 0, "type_loop"); Jump("invalid");

        Mark("finish"); Op(HybridCpuOpcode.ADDI, 10, 21); Jump("restore");
        Mark("invalid"); Op(HybridCpuOpcode.ADDI, 10, 0);
        Mark("restore");
        for (int index = SavedRegisters.Length - 1; index >= 0; index--) Load(HybridCpuOpcode.LD, (byte)SavedRegisters[index], 2, index * 8);
        Op(HybridCpuOpcode.ADDI, 2, 2, immediate: FrameBytes);
        Op(HybridCpuOpcode.JALR, 0, 1, immediate: HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);

        foreach ((int index, string label) in branches)
        {
            int target = labels.TryGetValue(label, out int value) ? value : throw new InvalidOperationException("Undefined ancestry selector label.");
            int displacement = checked((target - index) * HybridCpuBundleSerializer.BundleSizeBytes);
            if (displacement is < short.MinValue or > short.MaxValue || displacement == 0)
                throw new InvalidOperationException("Ancestry selector branch exceeds native encoding.");
            HybridCpuInstructionWord instruction = code[index]; instruction.Immediate = unchecked((ushort)(short)displacement); code[index] = instruction;
        }
        byte[] bytes = new HybridCpuBundleSerializer().SerializeProgram(code.Select(instruction =>
        {
            HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle(); bundle.SetInstruction(0, instruction); return bundle;
        }).ToArray());
        return (bytes, callIndex);
    }

    public static HybridCpuObjectArtifactV1 EmitObject()
    {
        (byte[] code, int call) = Emit();
        ulong relocationOffset = checked((ulong)call * HybridCpuBundleSerializer.BundleSizeBytes +
            HybridCpuManagedCallRelocationContractV1.ImmediateFieldOffsetBytes);
        return new HybridCpuObjectWriterV1().Write(new(
            [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes, code, (ulong)code.Length)],
            [new(Symbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden, ".text", 0, (ulong)code.Length, true),
             new(HybridCpuManagedEhClauseSelectorEmitterV1.Symbol, HybridCpuSymbolBinding.Global,
                 HybridCpuSymbolVisibility.Hidden, null, 0, 0, false)],
            [new(".text", relocationOffset, HybridCpuRelocationKind.ManagedCallRelativeSigned16,
                HybridCpuManagedEhClauseSelectorEmitterV1.Symbol, HybridCpuManagedCallRelocationContractV1.RequiredAddend)],
            HybridCpuTargetPlatformContractV1.Default.ContractDigest, HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
    }
}
