using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

/// <summary>Exact allocating ArgumentException.Message ECALL thunk.</summary>
public static class HybridCpuManagedArgumentExceptionMessageEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_argument_exception_get_message";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.argument-exception-message/v1";

    public static byte[] Emit()
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

        // Preserve the managed receiver before a0/a1 become the service envelope.
        Op(HybridCpuOpcode.ADDI, 16, 10);
        Constant(17, HybridCpuExternalServiceEcallContractV1.EcallNumber);
        Constant(10, (ulong)HybridCpuHostServiceV1.ManagedRuntime);
        Constant(11, HybridCpuManagedRuntimeEcallContractV1.ArgumentExceptionGetMessageOperation);
        for (byte register = 12; register <= 14; register++) Op(HybridCpuOpcode.ADDI, register, 0);
        Op(HybridCpuOpcode.ADDI, 15, 0, immediate: 1);
        Op(HybridCpuOpcode.ECALL, HybridCpuInstructionWord.NoArchReg, HybridCpuInstructionWord.NoArchReg);
        int branch = code.Count;
        Op(HybridCpuOpcode.BEQ, HybridCpuInstructionWord.NoArchReg, 11, 0, 1);
        Op(HybridCpuOpcode.ADDI, 10, 0, immediate: 255);
        Op(HybridCpuOpcode.ADDI, 5, 3, immediate: HybridCpuManagedEhDispatchIndexV1.ProcessExitHelperOffset);
        Op(HybridCpuOpcode.LD, 5, 5);
        Op(HybridCpuOpcode.JALR, 0, 5);
        int success = code.Count;
        Op(HybridCpuOpcode.JALR, 0, 1,
            immediate: HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);
        HybridCpuInstructionWord branchWord = code[branch];
        branchWord.Immediate = unchecked((ushort)checked((short)((success - branch) * HybridCpuBundleSerializer.BundleSizeBytes)));
        code[branch] = branchWord;
        return new HybridCpuBundleSerializer().SerializeProgram(code.Select(word =>
        {
            HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
            bundle.SetInstruction(0, word);
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
