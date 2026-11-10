using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;

namespace HybridCPU.Compiler.Core.Target.Managed;

public static class HybridCpuManagedStringLiteralTableV1
{
    public const uint Magic = 0x52545348; // HSTR
    public const ushort Version = 1;
    public const int HeaderSizeBytes = 16;
    public const int RowSizeBytes = 16;
    public const int CountOffset = 8;
    public const int HandleOffset = 0;
    public const int ReferenceOffset = 8;
    public const string Symbol = "__hybridcpu_managed_string_literal_table";
}

/// <summary>Exact lookup of an image-preallocated, globally rooted UTF-16 literal.
/// x10 is its dense 1-based handle; zero denotes invalid metadata and returns null.</summary>
public static class HybridCpuManagedLdstrEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_ldstr";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.ldstr/v1";

    public static byte[] Emit()
    {
        List<HybridCpuInstructionWord> code = new List<HybridCpuInstructionWord>();
        Dictionary<string, int> labels = new Dictionary<string, int>(StringComparer.Ordinal);
        List<(int Index, string Label)> branches = new List<(int Index, string Label)>();
        void Mark(string label) => labels.Add(label, code.Count);
        void Op(HybridCpuOpcode opcode, byte rd, byte rs1, byte rs2 = HybridCpuInstructionWord.NoArchReg, short immediate = 0) => code.Add(new()
        {
            OpCode = (uint)opcode, DataTypeValue = HybridCpuDataType.INT64, PredicateMask = byte.MaxValue,
            Word1 = HybridCpuInstructionWord.PackArchRegs(rd, rs1, rs2), Immediate = unchecked((ushort)immediate)
        });
        void Branch(HybridCpuOpcode opcode, byte left, byte right, string label)
        { branches.Add((code.Count, label)); Op(opcode, HybridCpuInstructionWord.NoArchReg, left, right, 1); }

        Branch(HybridCpuOpcode.BNE, 10, 0, "nonzero"); Op(HybridCpuOpcode.JALR, 0, 1, immediate: HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);
        Mark("nonzero");
        Op(HybridCpuOpcode.ADDI, 5, 3, immediate: HybridCpuManagedEhDispatchIndexV1.StringLiteralTableOffset);
        Op(HybridCpuOpcode.LD, 5, 5);
        Branch(HybridCpuOpcode.BNE, 5, 0, "table"); Op(HybridCpuOpcode.ADDI, 10, 0); Op(HybridCpuOpcode.JALR, 0, 1, immediate: HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);
        Mark("table");
        Op(HybridCpuOpcode.ADDI, 6, 5, immediate: HybridCpuManagedStringLiteralTableV1.CountOffset);
        Op(HybridCpuOpcode.LW, 6, 6);
        Branch(HybridCpuOpcode.BLTU, 6, 10, "invalid");
        Op(HybridCpuOpcode.ADDI, 7, 10, immediate: -1);
        Op(HybridCpuOpcode.SLLI, 7, 7, immediate: 4);
        Op(HybridCpuOpcode.ADD, 7, 5, 7);
        Op(HybridCpuOpcode.ADDI, 7, 7, immediate: HybridCpuManagedStringLiteralTableV1.HeaderSizeBytes);
        Op(HybridCpuOpcode.LD, 6, 7);
        Branch(HybridCpuOpcode.BNE, 6, 10, "invalid");
        Op(HybridCpuOpcode.ADDI, 7, 7, immediate: HybridCpuManagedStringLiteralTableV1.ReferenceOffset);
        Op(HybridCpuOpcode.LD, 10, 7);
        Op(HybridCpuOpcode.JALR, 0, 1, immediate: HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);
        Mark("invalid"); Op(HybridCpuOpcode.ADDI, 10, 0); Op(HybridCpuOpcode.JALR, 0, 1, immediate: HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);
        foreach ((int index, string label) in branches)
        {
            int displacement = checked((labels[label] - index) * HybridCpuBundleSerializer.BundleSizeBytes);
            HybridCpuInstructionWord instruction = code[index]; instruction.Immediate = unchecked((ushort)checked((short)displacement)); code[index] = instruction;
        }
        return new HybridCpuBundleSerializer().SerializeProgram(code.Select(instruction =>
        { HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle(); bundle.SetInstruction(0, instruction); return bundle; }).ToArray());
    }

    public static HybridCpuObjectArtifactV1 EmitObject()
    {
        byte[] code = Emit();
        return new HybridCpuObjectWriterV1().Write(new(
            [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes, code, (ulong)code.Length)],
            [new(Symbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Default, ".text", 0, (ulong)code.Length, true)], [],
            HybridCpuTargetPlatformContractV1.Default.ContractDigest, HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
    }
}
