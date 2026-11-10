using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

/// <summary>Exact non-throwing signed Int32 maximum helper.</summary>
public static class HybridCpuManagedMathMaxInt32EmitterV1
{
    public const string Symbol = "__hybridcpu_managed_math_max_i4";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.math-max-i4/v1";

    public static HybridCpuObjectArtifactV1 EmitObject()
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

        // Managed scalar ABI: x10=left, x11=right, x10=result. Int32 carriers are
        // canonical sign-extended values, so the signed branch implements Math.Max.
        int chooseRightBranch = code.Count;
        Op(HybridCpuOpcode.BLT, HybridCpuInstructionWord.NoArchReg, 10, 11, 1);
        Op(HybridCpuOpcode.ADDIW, 10, 10);
        Op(HybridCpuOpcode.JALR, 0, 1,
            immediate: HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);
        int chooseRight = code.Count;
        Op(HybridCpuOpcode.ADDIW, 10, 11);
        Op(HybridCpuOpcode.JALR, 0, 1,
            immediate: HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes);

        HybridCpuInstructionWord branch = code[chooseRightBranch];
        branch.Immediate = unchecked((ushort)checked((short)((chooseRight - chooseRightBranch) *
            HybridCpuBundleSerializer.BundleSizeBytes)));
        code[chooseRightBranch] = branch;

        byte[] bytes = new HybridCpuBundleSerializer().SerializeProgram(code.Select(word =>
        {
            HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
            bundle.SetInstruction(0, word);
            return bundle;
        }).ToArray());
        return new HybridCpuObjectWriterV1().Write(new(
            [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes,
                bytes, (ulong)bytes.Length)],
            [new(Symbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden,
                ".text", 0, (ulong)bytes.Length, true)], [],
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
    }
}
