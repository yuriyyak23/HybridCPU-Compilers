using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;

namespace HybridCPU.Compiler.Core.Target.Managed;

/// <summary>
/// Closed-world exception assignability leaf. x10 is the object-header TypeHandle, x11 is
/// the catch TypeId and x3 points to the immutable dispatch index. It follows only the
/// compiler-validated base-handle chain and returns 1/0 in x10.
/// </summary>
public static class HybridCpuManagedEhTypeMatchEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_eh_type_match";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.eh-type-match/v1";

    public static byte[] Emit()
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
        void Load(byte destination, byte basis, int offset)
        {
            Op(HybridCpuOpcode.ADDI, 28, basis, immediate: checked((short)offset));
            Op(HybridCpuOpcode.LD, destination, 28);
        }
        void LoadWord(byte destination, byte basis, int offset)
        {
            Op(HybridCpuOpcode.ADDI, 28, basis, immediate: checked((short)offset));
            Op(HybridCpuOpcode.LW, destination, 28);
        }

        LoadWord(5, 3, HybridCpuManagedEhDispatchIndexV1.CountOffset);
        LoadWord(6, 3, HybridCpuManagedEhDispatchIndexV1.TypeCountOffset);
        Branch(HybridCpuOpcode.BNE, 10, 0, "handle_nonzero");
        Jump("miss");
        Mark("handle_nonzero");
        Branch(HybridCpuOpcode.BLTU, 6, 10, "miss");
        // type table = x3 + header + methodCount * methodRowSize
        Op(HybridCpuOpcode.ADDI, 7, 0, immediate: HybridCpuManagedEhDispatchIndexV1.RowSizeBytes);
        Op(HybridCpuOpcode.MUL, 7, 5, 7);
        Op(HybridCpuOpcode.ADD, 7, 3, 7);
        Op(HybridCpuOpcode.ADDI, 7, 7, immediate: HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes);
        Op(HybridCpuOpcode.ADDI, 5, 10, immediate: -1); // zero-based handle
        Mark("loop");
        Op(HybridCpuOpcode.SLLI, 8, 5, immediate: 5); // 32-byte type row
        Op(HybridCpuOpcode.ADD, 8, 7, 8);
        // x29 is caller-saved; this leaf must not corrupt callee-saved x9.
        Load(29, 8, HybridCpuManagedEhDispatchIndexV1.TypeIdOffset);
        Branch(HybridCpuOpcode.BNE, 29, 11, "next_base");
        Op(HybridCpuOpcode.ADDI, 10, 0, immediate: 1);
        Jump("return");
        Mark("next_base");
        Load(5, 8, HybridCpuManagedEhDispatchIndexV1.BaseTypeHandleOffset);
        Branch(HybridCpuOpcode.BNE, 5, 0, "base_nonzero");
        Jump("miss");
        Mark("base_nonzero");
        Op(HybridCpuOpcode.ADDI, 5, 5, immediate: -1);
        Op(HybridCpuOpcode.ADDI, 6, 6, immediate: -1);
        Branch(HybridCpuOpcode.BNE, 6, 0, "loop");
        Mark("miss");
        Op(HybridCpuOpcode.ADDI, 10, 0);
        Mark("return");
        Op(HybridCpuOpcode.JALR, 0, 1, immediate: HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);

        foreach ((int index, string label) in branches)
        {
            int target = labels.TryGetValue(label, out int value) ? value : throw new InvalidOperationException("Undefined type-match label.");
            int displacement = checked((target - index) * HybridCpuBundleSerializer.BundleSizeBytes);
            if (displacement is < short.MinValue or > short.MaxValue || displacement == 0)
                throw new InvalidOperationException("Type-match branch exceeds native encoding.");
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
