using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

/// <summary>
/// Validates one final .hceh payload and selects its innermost catch for one exact
/// catch TypeId. x10 is the dispatch-index method row, x11 the native fault PC and
/// x12 the exact TypeId being tested. It returns the clause address or zero.
/// A dispatcher tests every TypeId in the exception's validated base chain and
/// compares the returned (try-size, ordinal) keys.
/// </summary>
public static class HybridCpuManagedEhClauseSelectorEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_eh_select_exact_catch";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.eh-select-exact-catch/v1";

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
        void Load(HybridCpuOpcode opcode, byte destination, byte basis, int offset)
        {
            Op(HybridCpuOpcode.ADDI, 28, basis, immediate: checked((short)offset));
            Op(opcode, destination, 28);
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

        Load(HybridCpuOpcode.LD, 5, 10, HybridCpuManagedEhDispatchIndexV1.CodeAddressOffset);
        Load(HybridCpuOpcode.LD, 15, 10, HybridCpuManagedEhDispatchIndexV1.CodeSizeOffset);
        Branch(HybridCpuOpcode.BNE, 15, 0, "code_size_nonzero");
        Jump("invalid");
        Mark("code_size_nonzero");
        Branch(HybridCpuOpcode.BLTU, 11, 5, "invalid");
        Op(HybridCpuOpcode.ADD, 6, 5, 15);
        Branch(HybridCpuOpcode.BLTU, 11, 6, "pc_valid");
        Jump("invalid");
        Mark("pc_valid");
        Op(HybridCpuOpcode.SUB, 16, 11, 5);
        Load(HybridCpuOpcode.LD, 13, 10, HybridCpuManagedEhDispatchIndexV1.EhAddressOffset);
        Load(HybridCpuOpcode.LD, 7, 10, HybridCpuManagedEhDispatchIndexV1.EhSizeOffset);
        Branch(HybridCpuOpcode.BNE, 13, 0, "eh_nonzero");
        Jump("invalid");
        Mark("eh_nonzero");
        Load(HybridCpuOpcode.LD, 5, 13, 0);
        Constant(6, ((ulong)HybridCpuManagedEhSchemaV1.SchemaVersion << 32) | HybridCpuManagedEhSchemaV1.EhMagic);
        Branch(HybridCpuOpcode.BNE, 5, 6, "invalid");
        Load(HybridCpuOpcode.LW, 17, 13, HybridCpuManagedEhClauseEncodingV1.CountOffset);
        Branch(HybridCpuOpcode.BNE, 17, 0, "count_nonzero");
        Jump("invalid");
        Mark("count_nonzero");
        Op(HybridCpuOpcode.ADDI, 6, 0, immediate: HybridCpuManagedEhSchemaV1.MaximumClausesPerMethod + 1);
        Branch(HybridCpuOpcode.BLTU, 17, 6, "count_bounded");
        Jump("invalid");
        Mark("count_bounded");
        Op(HybridCpuOpcode.ADDI, 6, 0, immediate: HybridCpuManagedEhClauseEncodingV1.ClauseSizeBytes);
        Op(HybridCpuOpcode.MUL, 6, 17, 6);
        Op(HybridCpuOpcode.ADDI, 6, 6, immediate: HybridCpuManagedEhClauseEncodingV1.HeaderSizeBytes);
        Branch(HybridCpuOpcode.BNE, 6, 7, "invalid");
        Op(HybridCpuOpcode.ADDI, 13, 13, immediate: HybridCpuManagedEhClauseEncodingV1.HeaderSizeBytes);
        Op(HybridCpuOpcode.ADDI, 14, 15, immediate: 1); // best try size sentinel
        Op(HybridCpuOpcode.ADDI, 29, 0); // canonical ordinal
        Op(HybridCpuOpcode.ADDI, 30, 0); // best clause

        Mark("row");
        // Reading the kind as a full word also requires reserved bytes 1..3 to be zero.
        Load(HybridCpuOpcode.LW, 5, 13, HybridCpuManagedEhClauseEncodingV1.KindOffset);
        Op(HybridCpuOpcode.ADDI, 6, 0, immediate: 2);
        Branch(HybridCpuOpcode.BLTU, 5, 6, "kind_valid");
        Jump("invalid");
        Mark("kind_valid");
        Load(HybridCpuOpcode.LW, 6, 13, HybridCpuManagedEhClauseEncodingV1.TryStartOffset);
        Load(HybridCpuOpcode.LW, 7, 13, HybridCpuManagedEhClauseEncodingV1.TrySizeOffset);
        Branch(HybridCpuOpcode.BNE, 7, 0, "try_size_nonzero");
        Jump("invalid");
        Mark("try_size_nonzero");
        Branch(HybridCpuOpcode.BLTU, 6, 15, "try_start_valid");
        Jump("invalid");
        Mark("try_start_valid");
        Op(HybridCpuOpcode.ADD, 8, 6, 7);
        Branch(HybridCpuOpcode.BLTU, 15, 8, "invalid");
        Branch(HybridCpuOpcode.BLTU, 8, 6, "invalid");
        Load(HybridCpuOpcode.LW, 31, 13, HybridCpuManagedEhClauseEncodingV1.HandlerStartOffset);
        Branch(HybridCpuOpcode.BLTU, 31, 15, "handler_start_valid");
        Jump("invalid");
        Mark("handler_start_valid");
        Op(HybridCpuOpcode.ADDI, 8, 31);
        Load(HybridCpuOpcode.LW, 28, 13, HybridCpuManagedEhClauseEncodingV1.HandlerSizeOffset);
        Branch(HybridCpuOpcode.BNE, 28, 0, "handler_size_nonzero");
        Jump("invalid");
        Mark("handler_size_nonzero");
        Op(HybridCpuOpcode.ADD, 31, 31, 28);
        Branch(HybridCpuOpcode.BLTU, 31, 8, "invalid");
        Branch(HybridCpuOpcode.BLTU, 15, 31, "invalid");
        Load(HybridCpuOpcode.LW, 31, 13, HybridCpuManagedEhClauseEncodingV1.OrdinalOffset);
        Branch(HybridCpuOpcode.BNE, 31, 29, "invalid");
        Load(HybridCpuOpcode.LD, 31, 13, HybridCpuManagedEhClauseEncodingV1.CatchTypeIdOffset);
        Op(HybridCpuOpcode.ADD, 8, 6, 7); // restore try end after handler validation scratch
        Branch(HybridCpuOpcode.BNE, 5, 0, "finally_row");
        Branch(HybridCpuOpcode.BNE, 31, 0, "catch_type_valid");
        Jump("invalid");
        Mark("catch_type_valid");
        Branch(HybridCpuOpcode.BNE, 31, 12, "next");
        Branch(HybridCpuOpcode.BLTU, 16, 6, "next");
        Branch(HybridCpuOpcode.BLTU, 16, 8, "candidate");
        Jump("next");
        Mark("candidate");
        Branch(HybridCpuOpcode.BLTU, 7, 14, "replace");
        Jump("next");
        Mark("replace");
        Op(HybridCpuOpcode.ADDI, 14, 7);
        Op(HybridCpuOpcode.ADDI, 30, 13);
        Jump("next");
        Mark("finally_row");
        Branch(HybridCpuOpcode.BNE, 31, 0, "invalid");
        Mark("next");
        // Reserved bytes 32..39 are part of the fail-closed canonical schema.
        Load(HybridCpuOpcode.LD, 31, 13, 32);
        Branch(HybridCpuOpcode.BNE, 31, 0, "invalid");
        Op(HybridCpuOpcode.ADDI, 13, 13, immediate: HybridCpuManagedEhClauseEncodingV1.ClauseSizeBytes);
        Op(HybridCpuOpcode.ADDI, 29, 29, immediate: 1);
        Op(HybridCpuOpcode.ADDI, 17, 17, immediate: -1);
        Branch(HybridCpuOpcode.BNE, 17, 0, "row");
        Op(HybridCpuOpcode.ADDI, 10, 30);
        Jump("return");
        Mark("invalid");
        Op(HybridCpuOpcode.ADDI, 10, 0);
        Mark("return");
        Op(HybridCpuOpcode.JALR, 0, 1, immediate: HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);

        foreach ((int index, string label) in branches)
        {
            int target = labels.TryGetValue(label, out int value) ? value : throw new InvalidOperationException("Undefined EH selector label.");
            int displacement = checked((target - index) * HybridCpuBundleSerializer.BundleSizeBytes);
            if (displacement is < short.MinValue or > short.MaxValue || displacement == 0)
                throw new InvalidOperationException("EH selector branch exceeds native encoding.");
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
            [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes, code, (ulong)code.Length)],
            [new(Symbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden, ".text", 0, (ulong)code.Length, true)], [],
            HybridCpuTargetPlatformContractV1.Default.ContractDigest, HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
    }
}
