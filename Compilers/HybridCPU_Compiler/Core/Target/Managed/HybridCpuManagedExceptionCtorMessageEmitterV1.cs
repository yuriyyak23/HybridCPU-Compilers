using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

namespace HybridCPU.Compiler.Core.Target.Managed;

/// <summary>Exact native field store for the qualified System.Exception::_message slot.</summary>
public static class HybridCpuManagedExceptionCtorMessageEmitterV1
{
    public const string Symbol = "__hybridcpu_managed_exception_ctor_message";
    public const string ModuleIdentity = "hybridcpu.managed-runtime.exception-ctor-message/v1";

    public static HybridCpuObjectArtifactV1 EmitObject(int messageOffsetBytes)
    {
        if (messageOffsetBytes < HybridCpuPlatformContractV1.ManagedObjectHeaderSizeBytes ||
            messageOffsetBytes > short.MaxValue || messageOffsetBytes % sizeof(ulong) != 0)
            throw new ArgumentOutOfRangeException(nameof(messageOffsetBytes),
                "Exception message offset must be an aligned positive signed16 object-field displacement.");
        HybridCpuInstructionWord Word(HybridCpuOpcode opcode, byte rd, byte rs1,
            byte rs2 = HybridCpuInstructionWord.NoArchReg, short immediate = 0) => new()
        {
            OpCode = (uint)opcode, DataTypeValue = HybridCpuDataType.INT64, PredicateMask = byte.MaxValue,
            Word1 = HybridCpuInstructionWord.PackArchRegs(rd, rs1, rs2), Immediate = unchecked((ushort)immediate)
        };
        HybridCpuInstructionWord[] words =
        [
            Word(HybridCpuOpcode.SD, HybridCpuInstructionWord.NoArchReg, 10, 11,
                checked((short)messageOffsetBytes)),
            Word(HybridCpuOpcode.JALR, 0, 1,
                immediate: HybridCpuNativeCallControlContractV1.ManagedReturnAdjustmentBytes)
        ];
        byte[] code = new HybridCpuBundleSerializer().SerializeProgram(words.Select(word =>
        {
            HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
            bundle.SetInstruction(0, word);
            return bundle;
        }).ToArray());
        return new HybridCpuObjectWriterV1().Write(new(
            [new(".text", HybridCpuObjectSectionKind.Code, HybridCpuBundleSerializer.BundleSizeBytes,
                code, (ulong)code.Length)],
            [new(Symbol, HybridCpuSymbolBinding.Global, HybridCpuSymbolVisibility.Hidden,
                ".text", 0, (ulong)code.Length, true)], [],
            HybridCpuTargetPlatformContractV1.Default.ContractDigest,
            HybridCpuManagedAbiFamilyV1.Default.ContractDigest));
    }
}
