using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

/// <summary>
/// Selects the next innermost finally during exceptional unwind. x10 is a validated method row,
/// x11 the native fault PC, x12 the already-executed try-size lower bound (exclusive), and x13
/// the target-catch try-size upper bound (exclusive). It validates the entire canonical .hceh
/// payload and returns the selected clause address, zero if no eligible finally remains, or
/// ulong.MaxValue for malformed metadata so the walker cannot silently skip a required handler.
/// </summary>
public static class HybridCpuManagedEhFinallySelectorEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_eh_select_finally";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.eh-select-finally/v1";

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

        // Keep caller inputs in caller-saved registers not used as address scratch.
        Op(HybridCpuOpcode.ADDI, 14, 12); // minimum exclusive
        Op(HybridCpuOpcode.ADDI, 15, 13); // maximum exclusive
        Branch(HybridCpuOpcode.BLTU, 14, 15, "range_valid");
        Jump("invalid");
        Mark("range_valid");
        Load(HybridCpuOpcode.LD, 5, 10, HybridCpuManagedEhDispatchIndexV1.CodeAddressOffset);
        Load(HybridCpuOpcode.LD, 6, 10, HybridCpuManagedEhDispatchIndexV1.CodeSizeOffset);
        Branch(HybridCpuOpcode.BNE, 6, 0, "code_nonzero");
        Jump("invalid");
        Mark("code_nonzero");
        Branch(HybridCpuOpcode.BLTU, 11, 5, "invalid");
        Op(HybridCpuOpcode.ADD, 7, 5, 6);
        Branch(HybridCpuOpcode.BLTU, 11, 7, "pc_bounded");
        Jump("invalid");
        Mark("pc_bounded");
        Op(HybridCpuOpcode.SUB, 16, 11, 5); // native offset
        Load(HybridCpuOpcode.LD, 17, 10, HybridCpuManagedEhDispatchIndexV1.EhAddressOffset);
        Load(HybridCpuOpcode.LD, 7, 10, HybridCpuManagedEhDispatchIndexV1.EhSizeOffset);
        Branch(HybridCpuOpcode.BNE, 17, 0, "eh_nonzero");
        // A closed linked managed frame without handlers has an exact absent pair.
        // It still owns unwind metadata and must allow the caller walk to continue.
        Branch(HybridCpuOpcode.BNE, 7, 0, "invalid");
        Op(HybridCpuOpcode.ADDI, 10, 0);
        Jump("return");
        Mark("eh_nonzero");
        Load(HybridCpuOpcode.LD, 5, 17, 0);
        Constant(6, ((ulong)HybridCpuManagedEhSchemaV1.SchemaVersion << 32) | HybridCpuManagedEhSchemaV1.EhMagic);
        Branch(HybridCpuOpcode.BEQ, 5, 6, "eh_header_valid");
        Jump("invalid");
        Mark("eh_header_valid");
        Load(HybridCpuOpcode.LW, 8, 17, HybridCpuManagedEhClauseEncodingV1.CountOffset);
        Branch(HybridCpuOpcode.BNE, 8, 0, "count_nonzero");
        Jump("invalid");
        Mark("count_nonzero");
        Op(HybridCpuOpcode.ADDI, 5, 0, immediate: HybridCpuManagedEhSchemaV1.MaximumClausesPerMethod + 1);
        Branch(HybridCpuOpcode.BLTU, 8, 5, "count_bounded");
        Jump("invalid");
        Mark("count_bounded");
        Op(HybridCpuOpcode.ADDI, 5, 0, immediate: HybridCpuManagedEhClauseEncodingV1.ClauseSizeBytes);
        Op(HybridCpuOpcode.MUL, 5, 8, 5);
        Op(HybridCpuOpcode.ADDI, 5, 5, immediate: HybridCpuManagedEhClauseEncodingV1.HeaderSizeBytes);
        Branch(HybridCpuOpcode.BEQ, 5, 7, "size_exact");
        Jump("invalid");
        Mark("size_exact");
        Op(HybridCpuOpcode.ADDI, 17, 17, immediate: HybridCpuManagedEhClauseEncodingV1.HeaderSizeBytes);
        Op(HybridCpuOpcode.ADDI, 29, 0); // expected ordinal
        Op(HybridCpuOpcode.ADDI, 30, 0); // best clause
        Op(HybridCpuOpcode.ADDI, 31, 15); // best size sentinel

        Mark("row");
        Load(HybridCpuOpcode.LW, 5, 17, HybridCpuManagedEhClauseEncodingV1.KindOffset);
        Op(HybridCpuOpcode.ADDI, 6, 0, immediate: 2);
        Branch(HybridCpuOpcode.BLTU, 5, 6, "kind_valid");
        Jump("invalid");
        Mark("kind_valid");
        Load(HybridCpuOpcode.LW, 6, 17, HybridCpuManagedEhClauseEncodingV1.TryStartOffset);
        Load(HybridCpuOpcode.LW, 7, 17, HybridCpuManagedEhClauseEncodingV1.TrySizeOffset);
        Branch(HybridCpuOpcode.BNE, 7, 0, "try_nonzero");
        Jump("invalid");
        Mark("try_nonzero");
        Load(HybridCpuOpcode.LD, 12, 10, HybridCpuManagedEhDispatchIndexV1.CodeSizeOffset);
        Branch(HybridCpuOpcode.BLTU, 6, 12, "try_start_valid");
        Jump("invalid");
        Mark("try_start_valid");
        Op(HybridCpuOpcode.ADD, 13, 6, 7);
        Branch(HybridCpuOpcode.BLTU, 13, 6, "invalid");
        Branch(HybridCpuOpcode.BLTU, 12, 13, "invalid");
        Load(HybridCpuOpcode.LW, 12, 17, HybridCpuManagedEhClauseEncodingV1.HandlerStartOffset);
        Branch(HybridCpuOpcode.BLTU, 12, 13, "handler_start_maybe_valid");
        // Handler need not be inside the try, but it must be inside code; reload code size.
        Mark("handler_start_maybe_valid");
        Load(HybridCpuOpcode.LD, 13, 10, HybridCpuManagedEhDispatchIndexV1.CodeSizeOffset);
        Branch(HybridCpuOpcode.BLTU, 12, 13, "handler_start_valid");
        Jump("invalid");
        Mark("handler_start_valid");
        Load(HybridCpuOpcode.LW, 13, 17, HybridCpuManagedEhClauseEncodingV1.HandlerSizeOffset);
        Branch(HybridCpuOpcode.BNE, 13, 0, "handler_nonzero");
        Jump("invalid");
        Mark("handler_nonzero");
        Op(HybridCpuOpcode.ADD, 12, 12, 13);
        Load(HybridCpuOpcode.LD, 13, 10, HybridCpuManagedEhDispatchIndexV1.CodeSizeOffset);
        Branch(HybridCpuOpcode.BLTU, 13, 12, "invalid");
        Load(HybridCpuOpcode.LW, 12, 17, HybridCpuManagedEhClauseEncodingV1.OrdinalOffset);
        Branch(HybridCpuOpcode.BEQ, 12, 29, "ordinal_valid");
        Jump("invalid");
        Mark("ordinal_valid");
        Load(HybridCpuOpcode.LD, 12, 17, HybridCpuManagedEhClauseEncodingV1.CatchTypeIdOffset);
        Branch(HybridCpuOpcode.BNE, 5, 0, "finally_kind");
        Branch(HybridCpuOpcode.BNE, 12, 0, "next");
        Jump("invalid");
        Mark("finally_kind");
        Branch(HybridCpuOpcode.BEQ, 12, 0, "finally_type_valid");
        Jump("invalid");
        Mark("finally_type_valid");
        Branch(HybridCpuOpcode.BLTU, 16, 6, "next");
        Op(HybridCpuOpcode.ADD, 13, 6, 7);
        Branch(HybridCpuOpcode.BLTU, 16, 13, "inside_try");
        Jump("next");
        Mark("inside_try");
        Branch(HybridCpuOpcode.BLTU, 14, 7, "above_minimum");
        Jump("next");
        Mark("above_minimum");
        Branch(HybridCpuOpcode.BLTU, 7, 15, "below_maximum");
        Jump("next");
        Mark("below_maximum");
        Branch(HybridCpuOpcode.BLTU, 7, 31, "replace");
        Jump("next");
        Mark("replace");
        Op(HybridCpuOpcode.ADDI, 31, 7);
        Op(HybridCpuOpcode.ADDI, 30, 17);

        Mark("next");
        Load(HybridCpuOpcode.LD, 12, 17, 32);
        Branch(HybridCpuOpcode.BEQ, 12, 0, "reserved_valid");
        Jump("invalid");
        Mark("reserved_valid");
        Op(HybridCpuOpcode.ADDI, 17, 17, immediate: HybridCpuManagedEhClauseEncodingV1.ClauseSizeBytes);
        Op(HybridCpuOpcode.ADDI, 29, 29, immediate: 1);
        Op(HybridCpuOpcode.ADDI, 8, 8, immediate: -1);
        Branch(HybridCpuOpcode.BNE, 8, 0, "row");
        Op(HybridCpuOpcode.ADDI, 10, 30);
        Jump("return");
        Mark("invalid");
        Op(HybridCpuOpcode.ADDI, 10, 0, immediate: -1);
        Mark("return");
        Op(HybridCpuOpcode.JALR, 0, 1, immediate: HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);

        IReadOnlyList<HybridCpuInstructionWord> resolved = HybridCpuNativeBranchEncoderV1.Resolve(
            code, labels, branches, "finally-selector");
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
