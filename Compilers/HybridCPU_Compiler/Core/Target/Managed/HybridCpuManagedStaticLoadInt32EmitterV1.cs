using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public static class HybridCpuManagedStaticLoadInt32EmitterV1
{
    public const string Symbol = "__hybridcpu_managed_static_load_i4";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.static-load-i4/v1";

    public static HybridCpuObjectArtifactV1 EmitObject() => EmitObject(Symbol,
        HybridCpuManagedRuntimeEcallContractV1.StaticLoadInt32Operation);

    internal static HybridCpuObjectArtifactV1 EmitObject(string symbol, ulong operation)
    {
        List<HybridCpuInstructionWord> code = new List<HybridCpuInstructionWord>();
        void Op(HybridCpuOpcode opcode, byte rd, byte rs1, byte rs2 = HybridCpuInstructionWord.NoArchReg,
            short immediate = 0) => code.Add(new()
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
            if (high < 0) { Op(HybridCpuOpcode.ADDI, rd, 0); return; }
            Op(HybridCpuOpcode.ADDI, rd, 0, immediate: bytes[high]);
            for (int index = high - 1; index >= 0; index--)
            {
                Op(HybridCpuOpcode.SLLI, rd, rd, immediate: 8);
                if (bytes[index] != 0) Op(HybridCpuOpcode.ORI, rd, rd, immediate: bytes[index]);
            }
        }

        // HCSV's second scalar argument is x18, which is callee-saved by the native
        // managed ABI. Preserve it in an aligned leaf frame before materializing the
        // service envelope and restore it before either success or failure transfer.
        Op(HybridCpuOpcode.ADDI, 2, 2, immediate: -16);
        Op(HybridCpuOpcode.SD, HybridCpuInstructionWord.NoArchReg, 2, 18, 0);
        Op(HybridCpuOpcode.ADDI, 16, 10);
        Op(HybridCpuOpcode.ADDI, 18, 11);
        Constant(17, HybridCpuExternalServiceEcallContractV1.EcallNumber);
        Constant(10, (ulong)HybridCpuHostServiceV1.ManagedRuntime);
        Constant(11, operation);
        for (byte register = 12; register <= 14; register++) Op(HybridCpuOpcode.ADDI, register, 0);
        Op(HybridCpuOpcode.ADDI, 15, 0, immediate: 2);
        Op(HybridCpuOpcode.ECALL, HybridCpuInstructionWord.NoArchReg, HybridCpuInstructionWord.NoArchReg);
        Op(HybridCpuOpcode.LD, 18, 2, immediate: 0);
        Op(HybridCpuOpcode.ADDI, 2, 2, immediate: 16);
        int branch = code.Count;
        Op(HybridCpuOpcode.BEQ, HybridCpuInstructionWord.NoArchReg, 11, 0, 1);
        Op(HybridCpuOpcode.ADDI, 10, 0, immediate: 255);
        Op(HybridCpuOpcode.ADDI, 5, 3, immediate: HybridCpuManagedEhDispatchIndexV1.ProcessExitHelperOffset);
        Op(HybridCpuOpcode.LD, 5, 5);
        Op(HybridCpuOpcode.JALR, 0, 5);
        int success = code.Count;
        Op(HybridCpuOpcode.JALR, 0, 1,
            immediate: HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);
        HybridCpuInstructionWord word = code[branch];
        word.Immediate = unchecked((ushort)checked((short)((success - branch) *
            HybridCpuBundleSerializer.BundleSizeBytes)));
        code[branch] = word;
        byte[] bytes = new HybridCpuBundleSerializer().SerializeProgram(code.Select(instruction =>
        {
            HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
            bundle.SetInstruction(0, instruction);
            return bundle;
        }).ToArray());
        return new HybridCpuObjectWriterV1().Write(new(
            [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes,
                bytes, (ulong)bytes.Length)],
            [new(symbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden,
                ".text", 0, (ulong)bytes.Length, true)], [],
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
    }
}
