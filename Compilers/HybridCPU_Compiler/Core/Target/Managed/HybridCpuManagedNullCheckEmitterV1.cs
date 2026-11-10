using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

/// <summary>
/// Explicit managed null check. A non-null receiver is returned unchanged. Null allocates the
/// exact image-owned NullReferenceException through the production runtime ECALL and tail-enters
/// native throw dispatch while preserving the original managed caller return address.
/// </summary>
public static class HybridCpuManagedNullCheckEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_null_check";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.null-check/v1";

    public static byte[] Emit()
    {
        List<HybridCpuInstructionWord> code = new List<HybridCpuInstructionWord>();
        void Op(HybridCpuOpcode opcode, byte rd, byte rs1,
            byte rs2 = HybridCpuInstructionWord.NoArchReg, short immediate = 0) => code.Add(new()
        {
            OpCode = (uint)opcode,
            DataTypeValue = HybridCpuDataType.INT64,
            PredicateMask = byte.MaxValue,
            Word1 = HybridCpuInstructionWord.PackArchRegs(rd, rs1, rs2),
            Immediate = unchecked((ushort)immediate)
        });
        void Constant(byte register, ulong value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            int high = Array.FindLastIndex(bytes, static item => item != 0);
            Op(HybridCpuOpcode.ADDI, register, 0, immediate: high < 0 ? (short)0 : bytes[high]);
            for (int index = high - 1; index >= 0; index--)
            {
                Op(HybridCpuOpcode.SLLI, register, register, immediate: 8);
                if (bytes[index] != 0) Op(HybridCpuOpcode.ORI, register, register, immediate: bytes[index]);
            }
        }

        int nonNullBranch = code.Count;
        Op(HybridCpuOpcode.BNE, HybridCpuInstructionWord.NoArchReg, 10, 0, 1);
        Constant(17, HybridCpuExternalServiceEcallContractV1.EcallNumber);
        Constant(10, (ulong)HybridCpuHostServiceV1.ManagedRuntime);
        Constant(11, HybridCpuManagedRuntimeEcallContractV1.AllocateNullReferenceExceptionOperation);
        for (byte register = 12; register <= 16; register++) Op(HybridCpuOpcode.ADDI, register, 0);
        Op(HybridCpuOpcode.ADDI, 15, 0); // no scalar arguments
        Op(HybridCpuOpcode.ECALL, HybridCpuInstructionWord.NoArchReg, HybridCpuInstructionWord.NoArchReg);
        int successBranch = code.Count;
        Op(HybridCpuOpcode.BEQ, HybridCpuInstructionWord.NoArchReg, 11, 0, 1);
        Op(HybridCpuOpcode.ADDI, 10, 0, immediate: 255);
        Op(HybridCpuOpcode.ADDI, 5, 3, immediate: HybridCpuManagedEhDispatchIndexV1.ProcessExitHelperOffset);
        Op(HybridCpuOpcode.LD, 5, 5);
        Op(HybridCpuOpcode.JALR, 0, 5); // allocation/provider failure is unhandled process failure
        int dispatch = code.Count;
        Op(HybridCpuOpcode.ADDI, 5, 3, immediate: HybridCpuManagedEhDispatchIndexV1.CatchDispatchHelperOffset);
        Op(HybridCpuOpcode.LD, 5, 5);
        Op(HybridCpuOpcode.JALR, 0, 5); // tail call: keep managed caller RA for EH lookup
        int nonNull = code.Count;
        Op(HybridCpuOpcode.JALR, 0, 1,
            immediate: HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);

        Patch(nonNullBranch, nonNull);
        Patch(successBranch, dispatch);
        return new HybridCpuBundleSerializer().SerializeProgram(code.Select(word =>
        {
            HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
            bundle.SetInstruction(0, word);
            return bundle;
        }).ToArray());

        void Patch(int branch, int target)
        {
            int displacement = checked((target - branch) * HybridCpuBundleSerializer.BundleSizeBytes);
            HybridCpuInstructionWord word = code[branch];
            word.Immediate = unchecked((ushort)checked((short)displacement));
            code[branch] = word;
        }
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
