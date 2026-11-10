using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

public static class HybridCpuManagedArrayCopyEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_array_copy";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.array-copy/v1";

    public static HybridCpuObjectArtifactV1 EmitObject()
    {
        List<HybridCpuInstructionWord> code = new List<HybridCpuInstructionWord>();
        void Op(HybridCpuOpcode opcode, byte rd, byte rs1,
            byte rs2 = HybridCpuInstructionWord.NoArchReg, short immediate = 0) => code.Add(new()
        {
            OpCode = (uint)opcode, DataTypeValue = HybridCpuDataType.INT64, PredicateMask = byte.MaxValue,
            Word1 = HybridCpuInstructionWord.PackArchRegs(rd, rs1, rs2), Immediate = unchecked((ushort)immediate)
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

        // The external-service envelope has only three scalar argument registers. Preserve the
        // five native ABI arguments in an exact, bounded, read-only stack block instead.
        Op(HybridCpuOpcode.ADDI, 2, 2, immediate: -48);
        Op(HybridCpuOpcode.SD, HybridCpuInstructionWord.NoArchReg, 2, 10, 0);
        Op(HybridCpuOpcode.SD, HybridCpuInstructionWord.NoArchReg, 2, 11, 8);
        Op(HybridCpuOpcode.SD, HybridCpuInstructionWord.NoArchReg, 2, 12, 16);
        Op(HybridCpuOpcode.SD, HybridCpuInstructionWord.NoArchReg, 2, 13, 24);
        Op(HybridCpuOpcode.SD, HybridCpuInstructionWord.NoArchReg, 2, 14, 32);
        Constant(17, HybridCpuExternalServiceEcallContractV1.EcallNumber);
        Constant(10, (ulong)HybridCpuHostServiceV1.ManagedRuntime);
        Constant(11, HybridCpuManagedRuntimeEcallContractV1.ArrayCopyOperation);
        Op(HybridCpuOpcode.ADDI, 12, 2);
        Op(HybridCpuOpcode.ADDI, 13, 0, immediate: HybridCpuManagedRuntimeEcallContractV1.ArrayCopyArgumentBlockBytes);
        Op(HybridCpuOpcode.ADDI, 14, 0, immediate: (short)HybridCpuHostBufferAccessV1.Read);
        Op(HybridCpuOpcode.ADDI, 15, 0);
        Op(HybridCpuOpcode.ECALL, HybridCpuInstructionWord.NoArchReg, HybridCpuInstructionWord.NoArchReg);
        Op(HybridCpuOpcode.ADDI, 2, 2, immediate: 48);
        int providerSuccess = code.Count;
        Op(HybridCpuOpcode.BEQ, HybridCpuInstructionWord.NoArchReg, 11, 0, 1);
        Op(HybridCpuOpcode.ADDI, 10, 0, immediate: 255);
        Op(HybridCpuOpcode.ADDI, 5, 3, immediate: HybridCpuManagedEhDispatchIndexV1.ProcessExitHelperOffset);
        Op(HybridCpuOpcode.LD, 5, 5);
        Op(HybridCpuOpcode.JALR, 0, 5);
        int success = code.Count;
        int managedThrow = code.Count;
        Op(HybridCpuOpcode.BNE, HybridCpuInstructionWord.NoArchReg, 12, 0, 1);
        Op(HybridCpuOpcode.JALR, 0, 1, immediate: HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);
        int dispatch = code.Count;
        Op(HybridCpuOpcode.ADDI, 5, 3, immediate: HybridCpuManagedEhDispatchIndexV1.CatchDispatchHelperOffset);
        Op(HybridCpuOpcode.LD, 5, 5);
        Op(HybridCpuOpcode.JALR, 0, 5);
        Patch(providerSuccess, success); Patch(managedThrow, dispatch);
        byte[] bytes = new HybridCpuBundleSerializer().SerializeProgram(code.Select(word =>
        { HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle(); bundle.SetInstruction(0, word); return bundle; }).ToArray());
        return new HybridCpuObjectWriterV1().Write(new(
            [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes, bytes, (ulong)bytes.Length)],
            [new(Symbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden, ".text", 0, (ulong)bytes.Length, true)], [],
            HybridCpuTargetPlatformContractV1.Default.ContractDigest, HybridCpuManagedAbiFamilyV1.Default.ContractDigest));

        void Patch(int branch, int target)
        {
            HybridCpuInstructionWord word = code[branch];
            word.Immediate = unchecked((ushort)checked((short)((target - branch) * HybridCpuBundleSerializer.BundleSizeBytes)));
            code[branch] = word;
        }
    }
}
