using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public static class HybridCpuManagedArrayStoreInt32EmitterV1
{
    public const string Symbol = "__hybridcpu_managed_array_store_i4";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.array-store-i4/v1";

    public static HybridCpuObjectArtifactV1 EmitObject() => EmitObject(Symbol,
        HybridCpuManagedRuntimeEcallContractV1.ArrayStoreInt32Operation, 3);

    internal static HybridCpuObjectArtifactV1 EmitObject(string symbol, ulong operation, int argumentCount)
    {
        List<HybridCpuInstructionWord> code = new List<HybridCpuInstructionWord>();
        void Op(HybridCpuOpcode opcode, byte rd, byte rs1,
            byte rs2 = HybridCpuInstructionWord.NoArchReg, short immediate = 0) => code.Add(new()
        {
            OpCode = (uint)opcode, DataTypeValue = HybridCpuDataType.INT64,
            PredicateMask = byte.MaxValue,
            Word1 = HybridCpuInstructionWord.PackArchRegs(rd, rs1, rs2),
            Immediate = unchecked((ushort)immediate)
        });
        void Constant(byte rd, ulong value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            int high = Array.FindLastIndex(bytes, static item => item != 0);
            Op(HybridCpuOpcode.ADDI, rd, 0, immediate: high < 0 ? (short)0 : bytes[high]);
            for (int index = high - 1; index >= 0; index--)
            {
                Op(HybridCpuOpcode.SLLI, rd, rd, immediate: 8);
                if (bytes[index] != 0) Op(HybridCpuOpcode.ORI, rd, rd, immediate: bytes[index]);
            }
        }

        // HCSV assigns its second and third scalar arguments to x18/x19, while the native
        // managed ABI classifies both registers as callee-saved. Preserve the incoming
        // values in an aligned leaf frame before forming the service envelope. Restore the
        // frame immediately after ECALL so every subsequent return or tail-transfer path
        // observes the caller's register state.
        Op(HybridCpuOpcode.ADDI, 2, 2, immediate: -16);
        Op(HybridCpuOpcode.SD, HybridCpuInstructionWord.NoArchReg, 2, 18, 0);
        Op(HybridCpuOpcode.SD, HybridCpuInstructionWord.NoArchReg, 2, 19, 8);
        Op(HybridCpuOpcode.ADDI, 16, 10);
        if (argumentCount >= 2) Op(HybridCpuOpcode.ADDI, 18, 11);
        if (argumentCount == 3) Op(HybridCpuOpcode.ADDI, 19, 12);
        Constant(17, HybridCpuExternalServiceEcallContractV1.EcallNumber);
        Constant(10, (ulong)HybridCpuHostServiceV1.ManagedRuntime);
        Constant(11, operation);
        for (byte register = 12; register <= 14; register++) Op(HybridCpuOpcode.ADDI, register, 0);
        Op(HybridCpuOpcode.ADDI, 15, 0, immediate: checked((short)argumentCount));
        Op(HybridCpuOpcode.ECALL, HybridCpuInstructionWord.NoArchReg, HybridCpuInstructionWord.NoArchReg);
        Op(HybridCpuOpcode.LD, 18, 2, immediate: 0);
        Op(HybridCpuOpcode.LD, 19, 2, immediate: 8);
        Op(HybridCpuOpcode.ADDI, 2, 2, immediate: 16);
        int providerSuccess = code.Count;
        Op(HybridCpuOpcode.BEQ, HybridCpuInstructionWord.NoArchReg, 11, 0, 1);
        Op(HybridCpuOpcode.ADDI, 10, 0, immediate: 255);
        Op(HybridCpuOpcode.ADDI, 5, 3, immediate: HybridCpuManagedEhDispatchIndexV1.ProcessExitHelperOffset);
        Op(HybridCpuOpcode.LD, 5, 5);
        Op(HybridCpuOpcode.JALR, 0, 5);
        int success = code.Count;
        int managedThrow = code.Count;
        Op(HybridCpuOpcode.BNE, HybridCpuInstructionWord.NoArchReg, 12, 0, 1);
        Op(HybridCpuOpcode.JALR, 0, 1,
            immediate: HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);
        int dispatch = code.Count;
        Op(HybridCpuOpcode.ADDI, 5, 3, immediate: HybridCpuManagedEhDispatchIndexV1.CatchDispatchHelperOffset);
        Op(HybridCpuOpcode.LD, 5, 5);
        Op(HybridCpuOpcode.JALR, 0, 5);
        Patch(providerSuccess, success);
        Patch(managedThrow, dispatch);
        byte[] bytes = new HybridCpuBundleSerializer().SerializeProgram(code.Select(word =>
        {
            HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle(); bundle.SetInstruction(0, word); return bundle;
        }).ToArray());
        return new HybridCpuObjectWriterV1().Write(new(
            [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes,
                bytes, (ulong)bytes.Length)],
            [new(symbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden,
                ".text", 0, (ulong)bytes.Length, true)], [],
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));

        void Patch(int branch, int target)
        {
            HybridCpuInstructionWord word = code[branch];
            word.Immediate = unchecked((ushort)checked((short)((target - branch) *
                HybridCpuBundleSerializer.BundleSizeBytes)));
            code[branch] = word;
        }
    }
}
