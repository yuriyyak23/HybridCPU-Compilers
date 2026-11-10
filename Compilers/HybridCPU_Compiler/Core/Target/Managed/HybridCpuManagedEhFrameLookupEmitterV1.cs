using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;

namespace HybridCPU.Compiler.Core.Target.Managed;

public static class HybridCpuManagedEhDispatchIndexV1
{
    public const uint Magic = 0x58454348; // HCEX
    public const ushort Version = 8;
    public const int HeaderSizeBytes = 160;
    public const int CountOffset = 8;
    public const int TypeCountOffset = 12;
    public const int StackBaseOffset = 16;
    public const int StackEndOffset = 24;
    public const int FindFrameHelperOffset = 32;
    public const int CatchSelectorHelperOffset = 40;
    public const int UnwindStepHelperOffset = 48;
    public const int TransferHelperOffset = 56;
    public const int ProcessExitHelperOffset = 64;
    public const int ExceptionStateOffset = 72;
    public const int CatchDispatchHelperOffset = 80;
    public const int RethrowHelperOffset = 88;
    public const int LeaveCatchHelperOffset = 96;
    public const int StringLiteralTableOffset = 104;
    public const int LdstrHelperOffset = 112;
    public const int DispatchMetadataOffset = 120;
    public const int ResolveInterfaceHelperOffset = 128;
    public const int ResolveVirtualHelperOffset = 136;
    public const int FinallySelectorHelperOffset = 144;
    public const int FinallyResumeHelperOffset = 152;
    public const int CfaBaseOffset = 64;
    public const int CfaOffsetOffset = 68;
    public const int ReturnRegisterOffset = 72;
    public const int ReturnStackOffset = 76;
    public const int SavedCountOffset = 80;
    public const int SavedRowsOffset = 88;
    public const int SavedRowSizeBytes = 8;
    public const int MaximumSavedRegisters = 32;
    public const int FinallyAddressOffset = SavedRowsOffset + MaximumSavedRegisters * SavedRowSizeBytes;
    public const int FinallySizeOffset = FinallyAddressOffset + 8;
    public const int RowSizeBytes = FinallySizeOffset + 8;
    public const int TypeRowSizeBytes = 32;
    public const int TypeHandleOffset = 0;
    public const int TypeIdOffset = 8;
    public const int BaseTypeIdOffset = 16;
    public const int BaseTypeHandleOffset = 24;
    public const int CodeAddressOffset = 0;
    public const int CodeSizeOffset = 8;
    public const int EhAddressOffset = 16;
    public const int EhSizeOffset = 24;
    public const int UnwindAddressOffset = 32;
    public const int UnwindSizeOffset = 40;
    public const int GcAddressOffset = 48;
    public const int GcSizeOffset = 56;
}

public static class HybridCpuManagedEhClauseEncodingV1
{
    public const int HeaderSizeBytes = 12;
    public const int ClauseSizeBytes = 40;
    public const int CountOffset = 8;
    public const int KindOffset = 0;
    public const int TryStartOffset = 4;
    public const int TrySizeOffset = 8;
    public const int HandlerStartOffset = 12;
    public const int HandlerSizeOffset = 16;
    public const int CatchTypeIdOffset = 20;
    public const int OrdinalOffset = 28;
}

/// <summary>
/// Native leaf used by the EH dispatcher. x3 points at the image-owned dispatch index and
/// x11 contains a native PC. It returns the matching row address in x10, or zero. It reads
/// only the fixed index header/rows and returns normally to x1.
/// </summary>
public static class HybridCpuManagedEhFrameLookupEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_eh_find_frame";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.eh-find-frame/v1";

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
        void Load(byte destination, byte addressBase, int offset)
        {
            Op(HybridCpuOpcode.ADDI, 28, addressBase, immediate: checked((short)offset));
            Op(HybridCpuOpcode.LD, destination, 28);
        }
        void LoadWord(byte destination, byte addressBase, int offset)
        {
            Op(HybridCpuOpcode.ADDI, 28, addressBase, immediate: checked((short)offset));
            Op(HybridCpuOpcode.LW, destination, 28);
        }
        void Constant(byte destination, ulong value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            int high = Array.FindLastIndex(bytes, static value => value != 0);
            Op(HybridCpuOpcode.ADDI, destination, 0, immediate: high < 0 ? (short)0 : bytes[high]);
            for (int index = high - 1; index >= 0; index--)
            {
                Op(HybridCpuOpcode.SLLI, destination, destination, immediate: 8);
                if (bytes[index] != 0) Op(HybridCpuOpcode.ORI, destination, destination, immediate: bytes[index]);
            }
        }

        Load(7, 3, 0);
        Constant(8, ((ulong)HybridCpuManagedEhDispatchIndexV1.Version << 32) |
            HybridCpuManagedEhDispatchIndexV1.Magic);
        Branch(HybridCpuOpcode.BNE, 7, 8, "invalid");
        LoadWord(5, 3, HybridCpuManagedEhDispatchIndexV1.CountOffset); // count
        Branch(HybridCpuOpcode.BNE, 5, 0, "count_nonzero");
        Jump("invalid");
        Mark("count_nonzero");
        Constant(8, HybridCPU.Platform.Contracts.HybridCpuPlatformContractV1.MaximumCodeManagerRecords + 1UL);
        Branch(HybridCpuOpcode.BLTU, 5, 8, "bounds");
        Jump("invalid");
        Mark("bounds");
        Load(7, 3, HybridCpuManagedEhDispatchIndexV1.StackBaseOffset);
        Load(8, 3, HybridCpuManagedEhDispatchIndexV1.StackEndOffset);
        Branch(HybridCpuOpcode.BLTU, 7, 8, "aligned_base");
        Jump("invalid");
        Mark("aligned_base");
        Op(HybridCpuOpcode.ANDI, 29, 7, immediate: 15);
        Branch(HybridCpuOpcode.BNE, 29, 0, "invalid");
        Op(HybridCpuOpcode.ANDI, 29, 8, immediate: 15);
        Branch(HybridCpuOpcode.BNE, 29, 0, "invalid");
        Op(HybridCpuOpcode.ADDI, 6, 3, immediate: HybridCpuManagedEhDispatchIndexV1.HeaderSizeBytes); // row
        Mark("test");
        Branch(HybridCpuOpcode.BNE, 5, 0, "body");
        Op(HybridCpuOpcode.ADDI, 10, 0);
        Jump("return");
        Mark("body");
        Load(7, 6, HybridCpuManagedEhDispatchIndexV1.CodeAddressOffset);
        Branch(HybridCpuOpcode.BLTU, 11, 7, "next");
        Load(8, 6, HybridCpuManagedEhDispatchIndexV1.CodeSizeOffset);
        Op(HybridCpuOpcode.ADD, 8, 7, 8);
        Branch(HybridCpuOpcode.BLTU, 11, 8, "found");
        Mark("next");
        Op(HybridCpuOpcode.ADDI, 6, 6, immediate: HybridCpuManagedEhDispatchIndexV1.RowSizeBytes);
        Op(HybridCpuOpcode.ADDI, 5, 5, immediate: -1);
        Jump("test");
        Mark("found");
        Op(HybridCpuOpcode.ADDI, 10, 6);
        Jump("return");
        Mark("invalid");
        Op(HybridCpuOpcode.ADDI, 10, 0);
        Mark("return");
        Op(HybridCpuOpcode.JALR, 0, 1, immediate: HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);

        foreach ((int index, string label) in branches)
        {
            if (!labels.TryGetValue(label, out int target)) throw new InvalidOperationException("Undefined EH lookup label.");
            int displacement = checked((target - index) * HybridCpuBundleSerializer.BundleSizeBytes);
            if (displacement is < short.MinValue or > short.MaxValue || displacement == 0)
                throw new InvalidOperationException("EH lookup branch displacement is outside the native encoding.");
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
